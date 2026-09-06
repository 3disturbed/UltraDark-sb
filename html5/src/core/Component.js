// -----------------------------------------------------------------------------
// Component — base class for everything you attach to an Actor.
//
// The lifecycle is the C# one with JavaScript casing: awake, start, update,
// fixedUpdate, lateUpdate, draw, onDestroy, plus the six collision and trigger
// callbacks. Porting a C# component is a matter of lowering the first letter of
// each override.
// -----------------------------------------------------------------------------

import { PropertyType } from './PropertyTypes.js';

/** Base class for all components. Attach to an Actor to add behaviour. */
export class Component {
    /**
     * Saveable, inspectable state. Subclasses declare their own; the merged
     * result is what the serialiser writes and the inspector draws.
     *
     * `enabled` lives here because the C# serialiser writes it for every
     * component — it is a public get/set bool like any other.
     */
    static schema = {
        enabled: { type: PropertyType.Bool, default: true, hidden: true },
    };

    /** Component classes this one cannot work without; added automatically. */
    static requires = [];

    constructor() {
        /** @type {import('./Actor.js').Actor} The actor this is attached to. */
        this.actor = null;

        /** When false the component is skipped by every per-frame callback. */
        this.enabled = true;
    }

    // ---- Lifecycle ----------------------------------------------------------
    // Called by the Actor, which is driven by its Layer.

    /** Runs the moment the component is attached, before the actor is in a scene. */
    awake() {}

    /**
     * Runs once, when the actor first joins a running scene.
     *
     * A component that is disabled at that moment never receives it, and does not
     * get it retroactively on being re-enabled — the same rule as the C# engine.
     */
    start() {}

    /** Runs every frame. @param {number} dt Scaled seconds since the last frame. */
    update(dt) {}

    /** Runs at the fixed timestep, before `update`. Physics-facing logic goes here. */
    fixedUpdate(dt) {}

    /** Runs every frame after every `update`. Camera follow goes here. */
    lateUpdate(dt) {}

    /** Draws through the 2D renderer. @param {object} batch The active sprite batch. */
    draw(batch) {}

    /** Runs when the component or its actor is destroyed. Deregister here. */
    onDestroy() {}

    // ---- Collision and trigger callbacks ------------------------------------
    // Populated by the physics system.

    onCollisionEnter(data) {}
    onCollisionStay(data) {}
    onCollisionExit(data) {}
    onTriggerEnter(other) {}
    onTriggerStay(other) {}
    onTriggerExit(other) {}

    // ---- Convenience --------------------------------------------------------

    /** The actor's 2D transform. */
    get transform() { return this.actor?.transform; }

    /** The actor's 3D transform, if it has one. */
    get transform3D() { return this.actor?.transform3D; }

    /** @see Actor#getComponent */
    getComponent(type) { return this.actor?.getComponent(type) ?? null; }

    /** @see Actor#getComponents */
    getComponents(type) { return this.actor?.getComponents(type) ?? []; }

    /** @see Actor#addComponent */
    addComponent(type) { return this.actor?.addComponent(type); }

    toString() { return `${this.constructor.componentName ?? this.constructor.name}`; }
}

/**
 * Contact data handed to the collision callbacks.
 *
 * `tag` and `name` forward to `other` so that the shorthand the bundled template
 * scripts use — `onCollisionEnter(c) { if (c.tag === 'Ground') … }` — works
 * alongside the documented `data.other.tag`. The C# bridge offers only the
 * latter, and scripts written against the templates have never run because of it.
 */
export class CollisionData {
    constructor({ other = null, contactPoint = null, normal = null, relativeVelocity = 0 } = {}) {
        this.other = other;
        this.contactPoint = contactPoint;
        this.normal = normal;
        this.relativeVelocity = relativeVelocity;
    }

    get tag() { return this.other?.tag; }
    get name() { return this.other?.name; }
}
