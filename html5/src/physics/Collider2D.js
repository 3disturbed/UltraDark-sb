// -----------------------------------------------------------------------------
// Collider2D — the shapes a Rigidbody2D collides with.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Vector2 } from '../math/index.js';
import { Circle, Polygon } from './Shapes2D.js';

/** Surface response, shared between colliders. */
export class PhysicsMaterial2D {
    constructor({ friction = 0.3, restitution = 0, density = 1 } = {}) {
        this.friction = friction;
        /** Bounciness, 0..1. */
        this.restitution = restitution;
        this.density = density;
    }
}

/** Base for the 2D collision shapes. */
export class Collider2D extends Component {
    /**
     * Every live collider, across every scene.
     *
     * Registration happens in `awake`, which runs the moment a component is
     * attached — before the actor is in a scene — so a per-scene list cannot be
     * built here. The physics system filters this list by scene each step, which
     * also means enabling or disabling a collider takes effect immediately
     * without any registration bookkeeping.
     */
    static all = [];

    static schema = {
        isTrigger:   { type: P.Bool, default: false },
        offset:      { type: P.Vector2, default: [0, 0] },
        friction:    { type: P.Number, default: 0.3, min: 0 },
        restitution: { type: P.Number, default: 0, min: 0, max: 1 },
        density:     { type: P.Number, default: 1, min: 0 },
    };

    constructor() {
        super();
        /** A trigger reports overlaps but never pushes anything. */
        this.isTrigger = false;
        this.offset = new Vector2(0, 0);
        this.friction = 0.3;
        this.restitution = 0;
        this.density = 1;
    }

    /** Where the shape sits in world space, offset included. */
    get worldCenter() {
        const position = this.transform.position;
        if (this.offset.x === 0 && this.offset.y === 0) return position;

        // The offset is in the actor's local frame, so it rotates and scales with it.
        const rotation = this.transform.rotation;
        const scale = this.transform.scale;
        const ox = this.offset.x * scale.x;
        const oy = this.offset.y * scale.y;
        const cos = Math.cos(rotation), sin = Math.sin(rotation);
        return new Vector2(position.x + ox * cos - oy * sin, position.y + ox * sin + oy * cos);
    }

    /**
     * The shape in world space, for the narrow phase.
     * @returns {Circle|Polygon}
     * @abstract
     */
    getWorldShape() { throw new Error('Collider2D subclasses must implement getWorldShape().'); }

    awake() { Collider2D.all.push(this); }

    onDestroy() {
        const i = Collider2D.all.indexOf(this);
        if (i >= 0) Collider2D.all.splice(i, 1);
    }
}

/** An axis-aligned or rotated rectangle. */
export class BoxCollider2D extends Collider2D {
    static schema = {
        size: { type: P.Vector2, default: [1, 1] },
    };

    constructor() {
        super();
        /** Full size in local units, before the actor's scale. */
        this.size = new Vector2(1, 1);
    }

    getWorldShape() {
        const scale = this.transform.scale;
        const worldSize = new Vector2(Math.abs(this.size.x * scale.x), Math.abs(this.size.y * scale.y));
        return Polygon.box(this.worldCenter, worldSize, this.transform.rotation);
    }
}
registerComponent(BoxCollider2D, { category: 'Physics', summary: 'A rectangular 2D collider.' });

/** A circle. Scaled by the larger of the actor's two axes. */
export class CircleCollider2D extends Collider2D {
    static schema = {
        radius: { type: P.Number, default: 0.5, min: 0 },
    };

    constructor() {
        super();
        this.radius = 0.5;
    }

    getWorldShape() {
        const scale = this.transform.scale;
        // A circle cannot be squashed, so the larger axis wins and the collider
        // fully contains the sprite rather than cutting into it.
        const worldRadius = this.radius * Math.max(Math.abs(scale.x), Math.abs(scale.y));
        return new Circle(this.worldCenter, worldRadius);
    }
}
registerComponent(CircleCollider2D, { category: 'Physics', summary: 'A circular 2D collider.' });

/** An arbitrary convex polygon, in local units. */
export class PolygonCollider2D extends Collider2D {
    static schema = {
        points: { type: P.List, of: { type: P.Vector2 }, default: () => [] },
    };

    constructor() {
        super();
        /** @type {Vector2[]} Local-space vertices, in order. */
        this.points = [];
    }

    getWorldShape() {
        if (this.points.length < 3) {
            // Not a polygon yet; a unit box keeps the solver from choking on it.
            return Polygon.box(this.worldCenter, new Vector2(1, 1), this.transform.rotation);
        }

        const centre = this.worldCenter;
        const rotation = this.transform.rotation;
        const scale = this.transform.scale;
        const cos = Math.cos(rotation), sin = Math.sin(rotation);

        return new Polygon(this.points.map((p) => {
            const x = p.x * scale.x;
            const y = p.y * scale.y;
            return new Vector2(centre.x + x * cos - y * sin, centre.y + x * sin + y * cos);
        }));
    }
}
registerComponent(PolygonCollider2D, { category: 'Physics', summary: 'A convex polygon 2D collider.' });
