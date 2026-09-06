// -----------------------------------------------------------------------------
// CharacterController3D — a kinematic capsule mover.
//
// Deliberately not a rigid body: a character that is simulated slides down
// slopes, tips over and fights the player for control. This integrates gravity
// itself and resolves against the 3D colliders by sweeping, which is what the
// C# controller does on top of BEPU.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Transform3D } from '../core/Transform3D.js';
import { Vector3, SBMath } from '../math/index.js';

/** Moves an actor as a capsule, with ground detection and step-up. */
export class CharacterController3D extends Component {
    static schema = {
        radius:         { type: P.Number, default: 0.35, min: 0.01 },
        height:         { type: P.Number, default: 1.8, min: 0.1 },
        moveSpeed:      { type: P.Number, default: 5 },
        jumpSpeed:      { type: P.Number, default: 8 },
        gravity:        { type: P.Number, default: -20 },
        stepUpHeight:   { type: P.Number, default: 0.3 },
        slopeLimit:     { type: P.Number, default: 45, min: 0, max: 89 },
        snapDistance:   { type: P.Number, default: 0.1 },
        airControl:     { type: P.Number, default: 0.4, min: 0, max: 1 },
    };

    constructor() {
        super();
        this.radius = 0.35;
        this.height = 1.8;
        this.moveSpeed = 5;
        this.jumpSpeed = 8;

        /** Metres per second squared. Negative points down. */
        this.gravity = -20;

        this.stepUpHeight = 0.3;
        this.slopeLimit = 45;
        this.snapDistance = 0.1;

        /** How much of the requested speed applies while airborne, 0..1. */
        this.airControl = 0.4;

        this.isGrounded = false;
        this.isOnSlope = false;
        this.slopeAngle = 0;

        /** Vertical speed, integrated here rather than by a rigid body. */
        this.verticalVelocity = 0;

        this._wantedVelocity = new Vector3(0, 0, 0);
    }

    awake() {
        if (!this.actor.getComponent(Transform3D)) this.actor.addComponent(Transform3D);
    }

    /** Requests horizontal movement for this frame, in world units per second. */
    move(velocity) { this._wantedVelocity = Vector3.from(velocity); }

    /** Starts a jump if grounded. */
    jump() {
        if (!this.isGrounded) return false;
        this.verticalVelocity = this.jumpSpeed;
        this.isGrounded = false;
        return true;
    }

    fixedUpdate(dt) {
        const t = this.actor.transform3D;
        if (!t) return;

        const physics = this.actor.scene?.physics3D;

        // Airborne movement is damped, so a player cannot change direction in
        // mid-air as freely as on the ground.
        const control = this.isGrounded ? 1 : this.airControl;
        const horizontal = Vector3.scale(this._wantedVelocity, control);

        this.verticalVelocity += this.gravity * dt;

        const delta = new Vector3(
            horizontal.x * dt,
            this.verticalVelocity * dt,
            horizontal.z * dt);

        const position = t.position;
        let next = Vector3.add(position, delta);

        if (physics) next = this._resolve(physics, position, next);

        t.position = next;
        this._wantedVelocity.set(0, 0, 0);
    }

    _resolve(physics, from, to) {
        const halfHeight = Math.max(this.height / 2, this.radius);
        const feetOffset = halfHeight;

        // Horizontal first, so a wall stops sideways motion without also
        // cancelling the fall; then vertical, so landing is not blocked by the
        // wall the character is pressed against.
        let resolved = new Vector3(to.x, from.y, to.z);
        resolved = physics.resolveCapsule(resolved, this.radius, halfHeight, this.actor);

        resolved.y = to.y;
        const grounded = physics.groundCheck(
            resolved, this.radius, feetOffset,
            this.snapDistance + Math.max(0, -this.verticalVelocity) * 0.02,
            this.actor);

        if (grounded && this.verticalVelocity <= 0) {
            resolved.y = grounded.y + feetOffset;
            this.verticalVelocity = 0;
            this.isGrounded = true;

            this.slopeAngle = Math.acos(SBMath.clamp(grounded.normal.y, -1, 1)) * SBMath.RAD2DEG;
            this.isOnSlope = this.slopeAngle > 1;

            // Too steep to stand on: slide rather than stick.
            if (this.slopeAngle > this.slopeLimit) {
                this.isGrounded = false;
                this.verticalVelocity = Math.min(this.verticalVelocity, -0.1);
            }
        } else {
            this.isGrounded = false;
            this.isOnSlope = false;
            this.slopeAngle = 0;
            resolved = physics.resolveCapsule(resolved, this.radius, halfHeight, this.actor);
        }

        return resolved;
    }
}
registerComponent(CharacterController3D, {
    category: 'Physics',
    summary: 'Moves an actor as a kinematic capsule.',
});
