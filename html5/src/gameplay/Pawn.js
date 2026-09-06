// -----------------------------------------------------------------------------
// Pawn — an actor a Controller can possess.
// -----------------------------------------------------------------------------

import { Actor } from '../core/Actor.js';
import { registerActor } from '../core/TypeRegistry.js';
import { Transform3D } from '../core/Transform3D.js';
import { Vector3, SBMath } from '../math/index.js';

/** Something a player or an AI can drive. */
export class Pawn extends Actor {
    constructor(name = 'Pawn') {
        super(name);

        /** @type {?import('./Controller.js').Controller} */
        this.controller = null;

        /** Where the controller is looking: pitch, yaw and roll in degrees. */
        this.controlRotation = new Vector3(0, 0, 0);

        /** Turn the pawn to face the controller's yaw each frame. */
        this.useControllerRotationYaw = true;

        this.pendingMovementInput = new Vector3(0, 0, 0);

        if (!this.getComponent(Transform3D)) this.addComponent(Transform3D);
    }

    /** True when a PlayerController — rather than an AI — holds this pawn. */
    get isPlayerControlled() { return this.controller?.isPlayerController === true; }

    /** True when any controller holds this pawn. */
    get isControlled() { return this.controller != null; }

    /**
     * Adds a world-space movement request for this frame. Movement components
     * consume it; calling twice in a frame accumulates.
     */
    addMovementInput(worldDirection, scale = 1) {
        const d = Vector3.from(worldDirection);
        this.pendingMovementInput.x += d.x * scale;
        this.pendingMovementInput.y += d.y * scale;
        this.pendingMovementInput.z += d.z * scale;
    }

    /** Takes this frame's accumulated movement request and clears it. */
    consumeMovementInput() {
        const consumed = this.pendingMovementInput.clone();
        this.pendingMovementInput.set(0, 0, 0);
        return consumed;
    }

    /** Turns the controller's view. */
    addControllerYawInput(degrees) {
        this.controlRotation.y = SBMath.wrapAngle((this.controlRotation.y + degrees) * SBMath.DEG2RAD)
            * SBMath.RAD2DEG;
    }

    /** Pitches the controller's view, clamped so it cannot roll over the top. */
    addControllerPitchInput(degrees, minPitch = -89, maxPitch = 89) {
        this.controlRotation.x = SBMath.clamp(this.controlRotation.x + degrees, minPitch, maxPitch);
    }

    lateUpdate(_dt) {
        if (!this.useControllerRotationYaw) return;
        const t = this.transform3D;
        if (!t) return;

        // Only yaw: pitching the body as well would tip the whole character over
        // when the player looks up.
        const euler = t.eulerAngles;
        t.eulerAngles = new Vector3(euler.x, this.controlRotation.y, euler.z);
    }

    /** Where a camera attached to this pawn should sit. */
    getViewLocation() {
        return this.transform3D?.position ?? new Vector3(0, 0, 0);
    }

    /** Called when a controller takes this pawn. */
    onPossessed(controller) {}

    /** Called when a controller lets it go. */
    onUnPossessed(controller) {}
}
registerActor(Pawn, { category: 'Gameplay', summary: 'An actor a controller can possess.' });
