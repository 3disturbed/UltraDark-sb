// -----------------------------------------------------------------------------
// GameState — match-wide state every client can see.
// -----------------------------------------------------------------------------

import { Actor } from '../core/Actor.js';
import { registerActor } from '../core/TypeRegistry.js';
import { SBEvent } from '../core/SBEvent.js';

/** Where a match is in its lifecycle. */
export const MatchState = Object.freeze({
    WaitingToStart: 'WaitingToStart',
    InProgress: 'InProgress',
    Paused: 'Paused',          // the C# enum has it; a state file crosses engines with it
    Ended: 'Ended',
});

/** The shared state of the match in progress. */
export class GameState extends Actor {
    constructor(name = 'GameState') {
        super(name);
        this.elapsedTime = 0;
        this._matchState = MatchState.WaitingToStart;

        /** @type {import('./PlayerState.js').PlayerState[]} */
        this.players = [];

        this.matchStateChanged = new SBEvent();
    }

    get matchState() { return this._matchState; }
    set matchState(value) {
        if (this._matchState === value) return;
        this._matchState = value;
        this.matchStateChanged.broadcast(value);
    }

    addPlayer(playerState) {
        if (!playerState || this.players.includes(playerState)) return;
        this.players.push(playerState);
    }

    removePlayer(playerState) {
        const i = this.players.indexOf(playerState);
        if (i >= 0) this.players.splice(i, 1);
    }

    /** Players sorted by score, highest first. */
    getScoreboard() { return [...this.players].sort((a, b) => b.score - a.score); }

    update(dt) {
        if (this._matchState === MatchState.InProgress) this.elapsedTime += dt;
    }
}
registerActor(GameState, { category: 'Gameplay', summary: 'Match-wide shared state.' });
