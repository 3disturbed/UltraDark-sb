// -----------------------------------------------------------------------------
// GameMode — the rules: who spawns, where, and when the match ends.
//
// It waits for PlayMode.isActive before doing anything, so opening a scene in
// the editor does not populate it with controllers and pawns that then get
// serialised into the file.
// -----------------------------------------------------------------------------

import { Actor } from '../core/Actor.js';
import { registerActor } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { SBEvent } from '../core/SBEvent.js';
import { PlayMode } from '../core/PlayMode.js';
import { Vector3 } from '../math/index.js';
import { Character } from './Character.js';
import { PlayerController } from './PlayerController.js';
import { PlayerState } from './PlayerState.js';
import { GameState, MatchState } from './GameState.js';
import { PlayerStart } from './PlayerStart.js';

/** The match rules. One per scene. */
export class GameMode extends Actor {
    static schema = {
        spawnLayer:          { type: P.String, default: 'default' },
        respawnDelay:        { type: P.Number, default: 3 },
        scoreToWin:          { type: P.Int, default: 0 },
        timeLimit:           { type: P.Number, default: 0 },
        autoStartLocalPlayer:{ type: P.Bool, default: true },
    };

    /** The game mode running in the active scene. */
    static current = null;

    constructor(name = 'GameMode') {
        super(name);

        /** Factories, so a subclass can swap in its own classes without overriding spawn. */
        this.pawnFactory = () => new Character();
        this.playerControllerFactory = () => new PlayerController();
        this.playerStateFactory = () => new PlayerState();
        this.gameStateFactory = () => new GameState();

        this.spawnLayer = 'default';
        this.respawnDelay = 3;
        /** Zero means no score limit. */
        this.scoreToWin = 0;
        /** Zero means no time limit, in seconds. */
        this.timeLimit = 0;
        this.autoStartLocalPlayer = true;

        /** @type {?GameState} */
        this.gameState = null;

        /** @type {PlayerController[]} */
        this.controllers = [];

        this.matchStarted = new SBEvent();
        this.matchEnded = new SBEvent();

        this._started = false;
    }

    onStart() {
        // Edit mode: the scene is being authored, not played. Spawning here would
        // put controllers and pawns into the file the editor is about to save.
        if (!PlayMode.isActive) return;
        this._beginPlay();
    }

    update(dt) {
        if (!PlayMode.isActive) return;
        if (!this._started) { this._beginPlay(); return; }

        if (this.gameState?.matchState !== MatchState.InProgress) return;

        if (this.timeLimit > 0 && this.gameState.elapsedTime >= this.timeLimit) {
            this.endMatch(this.gameState.getScoreboard()[0] ?? null);
        }
    }

    _beginPlay() {
        this._started = true;
        GameMode.current = this;

        this.gameState = this.gameStateFactory();
        this.scene?.addActor(this.gameState, this.spawnLayer);

        this.onPreStart();

        if (this.autoStartLocalPlayer) this.spawnPlayer(0);

        this.startMatch();
    }

    /** Moves the match into progress. */
    startMatch() {
        if (!this.gameState) return;
        this.gameState.matchState = MatchState.InProgress;
        this.onMatchStart();
        this.matchStarted.broadcast();
    }

    /** Ends the match, optionally naming a winner. */
    endMatch(winner = null) {
        if (!this.gameState || this.gameState.matchState === MatchState.Ended) return;
        this.gameState.matchState = MatchState.Ended;
        this.onMatchEnd(winner);
        this.matchEnded.broadcast(winner);
    }

    /** Creates a controller, its state and its pawn, and possesses. */
    spawnPlayer(playerIndex = 0) {
        const controller = this.playerControllerFactory();
        controller.playerIndex = playerIndex;
        controller.name = `PlayerController ${playerIndex}`;

        const playerState = this.playerStateFactory();
        playerState.playerId = playerIndex;
        playerState.playerName = `Player ${playerIndex + 1}`;
        controller.playerState = playerState;

        this.scene?.addActor(playerState, this.spawnLayer);
        this.scene?.addActor(controller, this.spawnLayer);
        this.gameState?.addPlayer(playerState);
        this.controllers.push(controller);

        // The actors are queued; flushing now means the pawn can be possessed in
        // this call rather than a frame later, when a caller expects a live pawn.
        this.scene?.flushPendingActors();

        const pawn = this.spawnDefaultPawnFor(controller);
        if (pawn) controller.possess(pawn);

        this.onPlayerJoined(controller);
        return controller;
    }

    /** Creates the pawn for a controller and places it at a spawn point. */
    spawnDefaultPawnFor(controller) {
        const pawn = this.pawnFactory();
        if (!pawn) return null;

        pawn.name = `Pawn ${controller.playerIndex}`;
        this.scene?.addActor(pawn, this.spawnLayer);
        this.scene?.flushPendingActors();

        const start = this.choosePlayerStart(controller);
        const t = pawn.transform3D;
        if (t && start) {
            t.position = start.position;
            pawn.controlRotation = new Vector3(0, start.yaw, 0);
            t.eulerAngles = new Vector3(0, start.yaw, 0);
        }

        return pawn;
    }

    /** Picks a spawn point for a controller. */
    choosePlayerStart(controller) {
        const starts = PlayerStart.all.filter((s) => s.actor?.scene === this.scene);
        if (starts.length === 0) return null;

        const team = controller.playerState?.teamId ?? -1;
        const matching = starts.filter((s) => s.teamId === -1 || s.teamId === team);
        const pool = matching.length > 0 ? matching : starts;

        // Round-robin rather than random: two players joining together should not
        // land on top of each other.
        return pool[controller.playerIndex % pool.length];
    }

    /** Destroys and re-spawns a controller's pawn. */
    restartPlayer(controller) {
        controller.controlledPawn?.destroy();
        controller.unPossess();

        const pawn = this.spawnDefaultPawnFor(controller);
        if (pawn) controller.possess(pawn);
    }

    /** Removes a player from the match entirely. */
    removePlayer(controller) {
        const i = this.controllers.indexOf(controller);
        if (i >= 0) this.controllers.splice(i, 1);

        if (controller.playerState) this.gameState?.removePlayer(controller.playerState);
        controller.controlledPawn?.destroy();
        controller.playerState?.destroy();
        controller.destroy();
    }

    /** Scores the kill and schedules a respawn. */
    onPlayerDied(victim, killer = null) {
        if (killer && killer !== victim) {
            killer.playerState?.addScore(1);
            if (this.scoreToWin > 0 && (killer.playerState?.score ?? 0) >= this.scoreToWin) {
                this.endMatch(killer.playerState);
                return;
            }
        }

        victim.controlledPawn?.destroy();
        victim.unPossess();

        const timers = this.scene?.engine?.timers;
        if (timers) {
            timers.setTimer(this.respawnDelay, () => {
                if (!victim.isDestroyed) this.restartPlayer(victim);
            });
        } else {
            this.restartPlayer(victim);
        }
    }

    onDestroy() {
        if (GameMode.current === this) GameMode.current = null;
    }

    // ---- Hooks for subclasses ------------------------------------------------

    /** Runs before any player is spawned. */
    onPreStart() {}
    onMatchStart() {}
    onMatchEnd(winner) {}
    onPlayerJoined(controller) {}
}
registerActor(GameMode, { category: 'Gameplay', summary: 'The match rules.' });
