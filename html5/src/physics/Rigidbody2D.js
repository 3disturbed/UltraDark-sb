// -----------------------------------------------------------------------------
// Rigidbody2D — mass, velocity and forces, mirroring Physics/Rigidbody2D.cs.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Vector2 } from '../math/index.js';

/** How a body responds to forces and collisions. */
export const BodyType = Object.freeze({
    /** Never moves and has infinite mass. Walls and floors. */
    Static: 'Static',
    /** Moves only when code moves it, and pushes dynamic bodies aside. Platforms. */
    Kinematic: 'Kinematic',
    /** Fully simulated. */
    Dynamic: 'Dynamic',
});

/** Gives an actor mass and velocity. Pair it with a Collider2D to be solid. */
export class Rigidbody2D extends Component {
    static schema = {
        bodyType:       { type: P.Enum, values: ['Static', 'Kinematic', 'Dynamic'], default: 'Dynamic' },
        mass:           { type: P.Number, default: 1, min: 0 },
        gravityScale:   { type: P.Number, default: 1 },
        linearDamping:  { type: P.Number, default: 0, min: 0 },
        angularDamping: { type: P.Number, default: 0.05, min: 0 },
        freezeRotation: { type: P.Bool, default: false },
        isKinematic:    { type: P.Bool, default: false },
    };

    constructor() {
        super();
        this.bodyType = BodyType.Dynamic;
        this.mass = 1;
        this.gravityScale = 1;
        this.linearDamping = 0;
        this.angularDamping = 0.05;
        this.freezeRotation = false;

        /** Metres — or pixels, in a 2D game — per second. */
        this.linearVelocity = new Vector2(0, 0);

        /** Radians per second. */
        this.angularVelocity = 0;

        this._force = new Vector2(0, 0);
        this._torque = 0;
    }

    /** Alias kept for parity with the C# component, which exposes both. */
    get isKinematic() { return this.bodyType === BodyType.Kinematic; }
    set isKinematic(value) {
        if (value) this.bodyType = BodyType.Kinematic;
        else if (this.bodyType === BodyType.Kinematic) this.bodyType = BodyType.Dynamic;
    }

    /** The reciprocal of mass; zero for anything that cannot be pushed. */
    get inverseMass() {
        if (this.bodyType !== BodyType.Dynamic || this.mass <= 0) return 0;
        return 1 / this.mass;
    }

    /** Reciprocal moment of inertia, approximated from the collider's extent. */
    get inverseInertia() {
        if (this.bodyType !== BodyType.Dynamic || this.freezeRotation || this.mass <= 0) return 0;
        const extent = this._boundingExtent();
        const inertia = this.mass * (extent * extent) / 6;
        return inertia > 0 ? 1 / inertia : 0;
    }

    _boundingExtent() {
        const collider = this.actor?.getAllComponents()
            .find((c) => typeof c.getWorldShape === 'function');
        if (!collider) return 1;

        const box = collider.getWorldShape()?.aabb;
        if (!box) return 1;
        return Math.max(box.maxX - box.minX, box.maxY - box.minY);
    }

    // ---- Component-friendly velocity accessors -------------------------------
    // The script bridge and the bundled templates set `rb.velocityX` directly.

    get velocityX() { return this.linearVelocity.x; }
    set velocityX(value) { this.linearVelocity.x = value; }

    get velocityY() { return this.linearVelocity.y; }
    set velocityY(value) { this.linearVelocity.y = value; }

    /** Alias for `linearVelocity`, the name most engines use. */
    get velocity() { return this.linearVelocity; }
    set velocity(value) { this.linearVelocity = Vector2.from(value); }

    // ---- Forces --------------------------------------------------------------

    /** Applies a continuous force. Accumulates until the next fixed step. */
    addForce(force) {
        const f = Vector2.from(force);
        this._force.x += f.x;
        this._force.y += f.y;
    }

    /** Applies a force at a world point, producing torque as well as motion. */
    addForceAtPoint(force, worldPoint) {
        const f = Vector2.from(force);
        this.addForce(f);

        const centre = this.transform.position;
        const rx = worldPoint.x - centre.x;
        const ry = worldPoint.y - centre.y;
        this._torque += rx * f.y - ry * f.x;
    }

    /** Applies an instantaneous change in momentum. */
    addImpulse(impulse) {
        const j = Vector2.from(impulse);
        const inverseMass = this.inverseMass;
        this.linearVelocity.x += j.x * inverseMass;
        this.linearVelocity.y += j.y * inverseMass;
    }

    addTorque(torque) { this._torque += torque; }

    /** Zeroes velocity and any pending forces. */
    sleep() {
        this.linearVelocity.set(0, 0);
        this.angularVelocity = 0;
        this._force.set(0, 0);
        this._torque = 0;
    }

    /** Integrates one fixed step. Called by the physics system, not by game code. */
    integrate(dt, gravity) {
        if (this.bodyType !== BodyType.Dynamic) {
            this._force.set(0, 0);
            this._torque = 0;
            return;
        }

        const inverseMass = this.inverseMass;

        this.linearVelocity.x += (this._force.x * inverseMass + gravity.x * this.gravityScale) * dt;
        this.linearVelocity.y += (this._force.y * inverseMass + gravity.y * this.gravityScale) * dt;

        if (!this.freezeRotation) {
            this.angularVelocity += this._torque * this.inverseInertia * dt;
        }

        // Exponential damping stays stable at any step size, unlike the naive
        // `v *= 1 - damping * dt`, which flips sign once dt gets large.
        if (this.linearDamping > 0) {
            const factor = Math.exp(-this.linearDamping * dt);
            this.linearVelocity.x *= factor;
            this.linearVelocity.y *= factor;
        }
        if (this.angularDamping > 0) this.angularVelocity *= Math.exp(-this.angularDamping * dt);

        this._force.set(0, 0);
        this._torque = 0;
    }

    /** Moves the transform by this step's velocity. */
    applyVelocity(dt) {
        if (this.bodyType === BodyType.Static) return;

        const position = this.transform.position;
        this.transform.position = new Vector2(
            position.x + this.linearVelocity.x * dt,
            position.y + this.linearVelocity.y * dt);

        if (!this.freezeRotation && this.angularVelocity !== 0) {
            this.transform.rotation += this.angularVelocity * dt;
        }
    }
}
registerComponent(Rigidbody2D, { category: 'Physics', summary: 'Mass, velocity and forces in 2D.' });
