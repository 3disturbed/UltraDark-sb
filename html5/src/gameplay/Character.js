// -----------------------------------------------------------------------------
// Character — a Pawn that walks, with a capsule mover and gravity.
// -----------------------------------------------------------------------------

import { Pawn } from './Pawn.js';
import { registerActor } from '../core/TypeRegistry.js';
import { SBEvent } from '../core/SBEvent.js';
import { CharacterController3D } from '../physics/CharacterController3D.js';
import { Vector3, SBMath } from '../math/index.js';

/** A walking, jumping pawn. */
export class Character extends Pawn {
    constructor(name = 'Character') {
        super(name);

        this.walkSpeed = 5;
        this.sprintMultiplier = 1.8;
        this.isSprinting = false;

        /** Face the direction of travel rather than the controller's yaw. */
        this.orientToMovement = false;
        this.rotationSpeed = 720;   // degrees per second

        this.eyeHeight = 1.7;

        this.jumped = new SBEvent();
        this.landed = new SBEvent();

        /** @type {CharacterController3D} */
        this.movement = this.addComponent(CharacterController3D);

        this._wasGrounded = true;
    }

    get isGrounded() { return this.movement.isGrounded; }

    /** Where a first-person camera sits. */
    getViewLocation() {
        const position = this.transform3D?.position ?? new Vector3(0, 0, 0);
        return new Vector3(position.x, position.y + this.eyeHeight, position.z);
    }

    /** Launches upwards if the character is on the ground. */
    jump() {
        if (!this.movement.isGrounded) return;
        this.movement.jump();
        this.jumped.broadcast();
    }

    update(dt) {
        const input = this.consumeMovementInput();
        const speed = this.walkSpeed * (this.isSprinting ? this.sprintMultiplier : 1);

        // Normalise only when over-long, so a half-deflected stick still walks
        // slowly rather than snapping to full speed.
        const lengthSq = input.lengthSquared;
        const direction = lengthSq > 1 ? input.normalize() : input;

        this.movement.move(Vector3.scale(direction, speed));

        if (this.orientToMovement && lengthSq > 1e-4) this._faceMovement(direction, dt);

        if (this.movement.isGrounded && !this._wasGrounded) this.landed.broadcast();
        this._wasGrounded = this.movement.isGrounded;
    }

    _faceMovement(direction, dt) {
        const t = this.transform3D;
        if (!t) return;

        // Yaw is measured from -Z, matching the engine's forward axis.
        const wantedYaw = Math.atan2(direction.x, -direction.z) * SBMath.RAD2DEG;
        const euler = t.eulerAngles;
        const step = this.rotationSpeed * dt;

        t.eulerAngles = new Vector3(
            euler.x,
            SBMath.moveTowards(euler.y, euler.y + SBMath.deltaAngle(
                euler.y * SBMath.DEG2RAD, wantedYaw * SBMath.DEG2RAD) * SBMath.RAD2DEG, step),
            euler.z);
    }

    lateUpdate(dt) {
        // Orienting to movement and yawing to the controller are mutually
        // exclusive; letting both run makes the character twitch between them.
        if (!this.orientToMovement) super.lateUpdate(dt);
    }
}
registerActor(Character, { category: 'Gameplay', summary: 'A walking, jumping pawn.' });
