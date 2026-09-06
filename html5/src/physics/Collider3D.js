// -----------------------------------------------------------------------------
// Collider3D — the 3D collision shapes.
//
// Shapes are reported as world-space axis-aligned boxes plus a kind, which is
// what the solver works with. It is a coarser model than BEPU's, and the
// trade-off is deliberate: an oriented-box solver is a large amount of code and
// download for a browser runtime, and a character walking on level geometry does
// not notice. Rotated colliders are reported as the box that contains them.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Transform3D } from '../core/Transform3D.js';
import { Vector3, Bounds } from '../math/index.js';

/** Base for the 3D colliders. */
export class Collider3D extends Component {
    static schema = {
        isTrigger:   { type: P.Bool, default: false },
        center:      { type: P.Vector3, default: [0, 0, 0] },
        friction:    { type: P.Number, default: 0.5, min: 0 },
        restitution: { type: P.Number, default: 0, min: 0, max: 1 },
    };

    /** Every live 3D collider, filtered per scene by the physics system. */
    static all = [];

    constructor() {
        super();
        this.isTrigger = false;
        this.center = new Vector3(0, 0, 0);
        this.friction = 0.5;
        this.restitution = 0;
    }

    awake() {
        Collider3D.all.push(this);
        if (!this.actor.getComponent(Transform3D)) this.actor.addComponent(Transform3D);
    }

    onDestroy() {
        const i = Collider3D.all.indexOf(this);
        if (i >= 0) Collider3D.all.splice(i, 1);
    }

    /** World position of the shape's centre. */
    get worldCenter() {
        const t = this.actor.transform3D;
        if (!t) return this.center.clone();
        return Vector3.add(t.position,
            Vector3.transform(Vector3.multiply(this.center, t.scale), t.rotation));
    }

    /**
     * The world-space bounds the solver uses.
     * @returns {Bounds}
     * @abstract
     */
    getWorldBounds() { throw new Error('Collider3D subclasses must implement getWorldBounds().'); }
}

/** A box. */
export class BoxCollider3D extends Collider3D {
    static schema = {
        size: { type: P.Vector3, default: [1, 1, 1] },
    };

    constructor() {
        super();
        /** Full size in local units, before the actor's scale. */
        this.size = new Vector3(1, 1, 1);
    }

    getWorldBounds() {
        const t = this.actor.transform3D;
        const scale = t?.scale ?? Vector3.one;
        let size = new Vector3(
            Math.abs(this.size.x * scale.x),
            Math.abs(this.size.y * scale.y),
            Math.abs(this.size.z * scale.z));

        // A rotated box is reported as the axis-aligned box that contains it, so
        // it never lets a character through a wall it should have hit.
        const euler = t?.eulerAngles;
        if (euler && (Math.abs(euler.x) > 0.01 || Math.abs(euler.y) > 0.01 || Math.abs(euler.z) > 0.01)) {
            const diagonal = size.length;
            size = new Vector3(
                Math.max(size.x, diagonal * 0.5),
                Math.max(size.y, diagonal * 0.5),
                Math.max(size.z, diagonal * 0.5));
        }

        return new Bounds(this.worldCenter, size);
    }
}
registerComponent(BoxCollider3D, { category: 'Physics', summary: 'A 3D box collider.' });

/** A sphere. */
export class SphereCollider3D extends Collider3D {
    static schema = {
        radius: { type: P.Number, default: 0.5, min: 0 },
    };

    constructor() {
        super();
        this.radius = 0.5;
    }

    /** World radius: a sphere cannot be squashed, so the largest axis wins. */
    get worldRadius() {
        const scale = this.actor.transform3D?.scale ?? Vector3.one;
        return this.radius * Math.max(Math.abs(scale.x), Math.abs(scale.y), Math.abs(scale.z));
    }

    getWorldBounds() {
        const d = this.worldRadius * 2;
        return new Bounds(this.worldCenter, new Vector3(d, d, d));
    }
}
registerComponent(SphereCollider3D, { category: 'Physics', summary: 'A 3D sphere collider.' });

/** An upright capsule — the shape a character occupies. */
export class CapsuleCollider3D extends Collider3D {
    static schema = {
        radius: { type: P.Number, default: 0.5, min: 0 },
        height: { type: P.Number, default: 2, min: 0 },
    };

    constructor() {
        super();
        this.radius = 0.5;
        /** Total height, caps included. */
        this.height = 2;
    }

    getWorldBounds() {
        const scale = this.actor.transform3D?.scale ?? Vector3.one;
        const radius = this.radius * Math.max(Math.abs(scale.x), Math.abs(scale.z));
        const height = Math.max(this.height * Math.abs(scale.y), radius * 2);
        return new Bounds(this.worldCenter, new Vector3(radius * 2, height, radius * 2));
    }
}
registerComponent(CapsuleCollider3D, { category: 'Physics', summary: 'A 3D capsule collider.' });

/**
 * A collider derived from the actor's MeshRenderer bounds.
 *
 * Not a true mesh collider — the browser runtime has no triangle-level 3D
 * narrow phase — but it means a model dropped into a scene is solid without
 * anyone sizing a box by hand.
 */
export class MeshCollider3D extends Collider3D {
    static schema = {
        padding: { type: P.Number, default: 0 },
    };

    constructor() {
        super();
        this.padding = 0;
    }

    getWorldBounds() {
        const renderer = this.actor.getAllComponents().find((c) => c.constructor.name === 'MeshRenderer');
        if (!renderer) return new Bounds(this.worldCenter, new Vector3(1, 1, 1));

        const bounds = renderer.worldBounds;
        const padded = new Vector3(
            bounds.size.x + this.padding * 2,
            bounds.size.y + this.padding * 2,
            bounds.size.z + this.padding * 2);
        return new Bounds(bounds.center, padded);
    }
}
registerComponent(MeshCollider3D, { category: 'Physics', summary: 'A box fitted to the actor’s mesh.' });
