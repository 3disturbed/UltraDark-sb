// -----------------------------------------------------------------------------
// PlayerState — one player's persistent data, surviving respawns.
// -----------------------------------------------------------------------------

import { Actor } from '../core/Actor.js';
import { registerActor } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { SBEvent } from '../core/SBEvent.js';

/** Score, name and team for one player. */
export class PlayerState extends Actor {
    static schema = {
        playerName: { type: P.String, default: 'Player' },
        teamId:     { type: P.Int, default: -1 },
        isBot:      { type: P.Bool, default: false },
    };

    constructor(name = 'PlayerState') {
        super(name);
        this.playerName = 'Player';
        this.playerId = 0;
        this.score = 0;
        this.teamId = -1;
        this.pingMs = 0;
        this.isBot = false;

        this.scoreChanged = new SBEvent();
    }

    /** Adds to the score and broadcasts the new total. */
    addScore(points) {
        this.score += points;
        this.scoreChanged.broadcast(this.score);
        return this.score;
    }
}
registerActor(PlayerState, { category: 'Gameplay', summary: 'One player’s persistent data.' });
