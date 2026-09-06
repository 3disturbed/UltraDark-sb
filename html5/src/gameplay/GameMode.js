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
import { Vector3, Color } from '../math/index.js';
import { Transform3D } from '../core/Transform3D.js';
import { MeshRenderer } from '../rendering/MeshRenderer.js';
import { MeshPrimitive } from '../rendering/PrimitiveMesh.js';
import { Camera3D } from '../rendering/Camera3D.js';
import { Character } from './Character.js';
import { PlayerController } from './PlayerController.js';
import { PlayerState } from './PlayerState.js';
import { GameState, MatchState } from './GameState.js';
import { PlayerStart } from './PlayerStart.js';
import { CharacterController3D } from '../physics/CharacterController3D.js';

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
            // A Player Start marks where the pawn's *feet* go. A capsule's
            // transform sits at its middle, so dropping it straight onto the
            // marker buries it up to the waist — and a character that starts
            // inside the floor never finds ground to stand on, because the floor
            // is above its feet. It falls forever, which is what pressing Play on
            // the default scene did.
            const footOffset = pawn.getComponent(CharacterController3D)?.footOffset ?? 0;
            const feet = start.position;

            t.position = new Vector3(feet.x, feet.y + footOffset, feet.z);
            pawn.controlRotation = new Vector3(0, start.yaw, 0);
            t.eulerAngles = new Vector3(0, start.yaw, 0);
        }

        this.equipDefaultPawn(pawn);
        return pawn;
    }

    /**
     * Gives a pawn a body and a view, if it arrived with neither.
     *
     * The default pawn is a bare `Character`: no renderer, no camera. It walks,
     * falls and collides correctly, and none of that is visible — pressing Play
     * on a fresh scene looked exactly like not pressing it, which is a reasonable
     * reason to conclude that Play is broken.
     *
     * Both additions are conditional, so a game that builds its own pawn through
     * `pawnFactory` keeps whatever rig it supplied. Both hang off the pawn as
     * child actors rather than sitting on it: the body has to be scaled to match
     * the capsule the controller moves, and scaling the pawn itself would drag
     * the camera in with it.
     */
    equipDefaultPawn(pawn) {
        const root = pawn.transform3D;
        if (!root) return;

        const movement = pawn.getComponent(CharacterController3D);

        if (!pawn.getComponent(MeshRenderer) && !this._findInChildren(root, MeshRenderer)) {
            const body = new Actor(`${pawn.name} Body`);
            const bodyTransform = body.addComponent(Transform3D);

            const renderer = body.addComponent(MeshRenderer);
            renderer.setPrimitive(MeshPrimitive.Capsule);
            renderer.albedoColor = Color.from('#D98C4A');
            renderer.roughness = 0.55;

            this._addChild(body, bodyTransform, root);

            // The capsule primitive is a metre across and two tall; scale it to
            // whatever the controller is actually sweeping.
            if (movement) {
                bodyTransform.localScale = new Vector3(
                    movement.radius * 2, movement.height / 2, movement.radius * 2);
            }
        }

        if (PlayerController.findPawnCamera(pawn)) return;

        // Over the shoulder, behind the pawn. Behind is +Z, because the engine's
        // forward is -Z; with no yaw of its own the camera inherits the pawn's
        // facing and so looks along -Z, back across the pawn and out in front of
        // it. Yawing it 180 here would point it at the horizon behind the player.
        const view = new Actor(`${pawn.name} Camera`);
        const viewTransform = view.addComponent(Transform3D);
        view.addComponent(Camera3D);

        this._addChild(view, viewTransform, root);

        const eye = movement ? movement.footOffset + 0.7 : 1.6;
        viewTransform.localPosition = new Vector3(0, eye, 4.5);
        viewTransform.localEulerAngles = new Vector3(-10, 0, 0);
    }

    /** Adds an actor to the scene and parents it, keeping its local placement. */
    _addChild(actor, transform, parent) {
        this.scene?.addActor(actor, this.spawnLayer);
        // Flush before parenting: an actor still queued has not run `start`, and
        // its components have nothing to attach to yet.
        this.scene?.flushPendingActors();
        transform.setParent(parent, false);
    }

    /** The first component of a type anywhere beneath a transform. */
    _findInChildren(root, type) {
        const stack = [...root.children];
        while (stack.length > 0) {
            const transform = stack.pop();
            const found = transform.actor?.getComponent(type);
            if (found) return found;
            stack.push(...transform.children);
        }
        return null;
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
