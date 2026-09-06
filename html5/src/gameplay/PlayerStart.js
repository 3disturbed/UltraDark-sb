// -----------------------------------------------------------------------------
// PlayerStart — where the game mode spawns a player.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Transform3D } from '../core/Transform3D.js';
import { Vector3 } from '../math/index.js';

/** Marks a spawn point. A scene needs at least one for Play to place a player. */
export class PlayerStart extends Component {
    static schema = {
        teamId: { type: P.Int, default: -1 },
    };

    /** Every live spawn point. */
    static all = [];

    constructor() {
        super();
        /** -1 means any team may spawn here. */
        this.teamId = -1;
    }

    awake() {
        PlayerStart.all.push(this);
        if (!this.actor.getComponent(Transform3D)) this.actor.addComponent(Transform3D);
    }

    onDestroy() {
        const i = PlayerStart.all.indexOf(this);
        if (i >= 0) PlayerStart.all.splice(i, 1);
    }

    get position() { return this.actor.transform3D?.position ?? new Vector3(0, 0, 0); }

    /** The yaw a spawned pawn faces, in degrees. */
    get yaw() { return this.actor.transform3D?.eulerAngles.y ?? 0; }
}
registerComponent(PlayerStart, { category: 'Gameplay', summary: 'A spawn point.' });
