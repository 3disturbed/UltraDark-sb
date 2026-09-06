// -----------------------------------------------------------------------------
// Rigidbody3D — mass, velocity and forces in 3D.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Transform3D } from '../core/Transform3D.js';
import { Vector3 } from '../math/index.js';

/** Gives a 3D actor mass and velocity. */
export class Rigidbody3D extends Component {
    static schema = {
        mass:           { type: P.Number, default: 1, min: 0 },
        useGravity:     { type: P.Bool, default: true },
        linearDamping:  { type: P.Number, default: 0.02, min: 0 },
        angularDamping: { type: P.Number, default: 0.05, min: 0 },
        isKinematic:    { type: P.Bool, default: false },
        freezeRotation: { type: P.Bool, default: false },
    };

    constructor() {
        super();
        this.mass = 1;
        this.useGravity = true;
        this.linearDamping = 0.02;
        this.angularDamping = 0.05;

        /** A kinematic body moves only when code moves it, and is never pushed. */
        this.isKinematic = false;
        this.freezeRotation = false;

        this.linearVelocity = new Vector3(0, 0, 0);
        this.angularVelocity = new Vector3(0, 0, 0);

        this._force = new Vector3(0, 0, 0);
        this._torque = new Vector3(0, 0, 0);
    }

    awake() {
        if (!this.actor.getComponent(Transform3D)) this.actor.addComponent(Transform3D);
    }

    get inverseMass() {
        if (this.isKinematic || this.mass <= 0) return 0;
        return 1 / this.mass;
    }

    /** Alias, matching the name most engines use. */
    get velocity() { return this.linearVelocity; }
    set velocity(value) { this.linearVelocity = Vector3.from(value); }

    addForce(force) { this._force.add(Vector3.from(force)); }

    addImpulse(impulse) {
        this.linearVelocity.add(Vector3.scale(Vector3.from(impulse), this.inverseMass));
    }

    addTorque(torque) { this._torque.add(Vector3.from(torque)); }

    /** Zeroes velocity and any pending forces. */
    sleep() {
        this.linearVelocity.set(0, 0, 0);
        this.angularVelocity.set(0, 0, 0);
        this._force.set(0, 0, 0);
        this._torque.set(0, 0, 0);
    }

    integrate(dt, gravity) {
        if (this.isKinematic) {
            this._force.set(0, 0, 0);
            this._torque.set(0, 0, 0);
            return;
        }

        const inverseMass = this.inverseMass;
        this.linearVelocity.add(Vector3.scale(this._force, inverseMass * dt));
        if (this.useGravity) this.linearVelocity.add(Vector3.scale(gravity, dt));

        if (!this.freezeRotation) {
            this.angularVelocity.add(Vector3.scale(this._torque, inverseMass * dt));
        }

        // Exponential damping stays stable at any step size.
        if (this.linearDamping > 0) this.linearVelocity.scale(Math.exp(-this.linearDamping * dt));
        if (this.angularDamping > 0) this.angularVelocity.scale(Math.exp(-this.angularDamping * dt));

        this._force.set(0, 0, 0);
        this._torque.set(0, 0, 0);
    }

    applyVelocity(dt) {
        const t = this.actor.transform3D;
        if (!t) return;

        t.position = Vector3.add(t.position, Vector3.scale(this.linearVelocity, dt));

        if (!this.freezeRotation && this.angularVelocity.lengthSquared > 1e-9) {
            const euler = t.eulerAngles;
            t.eulerAngles = new Vector3(
                euler.x + this.angularVelocity.x * dt,
                euler.y + this.angularVelocity.y * dt,
                euler.z + this.angularVelocity.z * dt);
        }
    }
}
registerComponent(Rigidbody3D, { category: 'Physics', summary: 'Mass, velocity and forces in 3D.' });
