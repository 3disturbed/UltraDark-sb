// -----------------------------------------------------------------------------
// PlayerController — turns input into pawn movement and camera aim.
// -----------------------------------------------------------------------------

import { Controller } from './Controller.js';
import { registerActor } from '../core/TypeRegistry.js';
import { Camera3D } from '../rendering/Camera3D.js';
import { Vector3 } from '../math/index.js';

/** The controller a human drives. */
export class PlayerController extends Controller {
    constructor(name = 'PlayerController') {
        super(name);

        this.playerIndex = 0;

        /** @type {?import('./PlayerState.js').PlayerState} */
        this.playerState = null;

        /** @type {?Camera3D} The view this player sees through. */
        this.viewCamera = null;

        this.inputEnabled = true;

        /** Degrees of yaw per pixel of mouse movement. */
        this.lookSensitivity = 0.15;

        /** Degrees per second at full gamepad stick deflection. */
        this.gamepadLookSpeed = 180;

        this.invertLookY = false;
    }

    get isPlayerController() { return true; }

    /** The engine's input manager, or null outside a host. */
    get input() { return this.scene?.engine?.input ?? null; }

    onPossess(pawn) {
        // A camera the pawn owns is the view the player should get: a spring-arm
        // rig or a first-person head belongs to the pawn, not to the controller.
        const pawnCamera = PlayerController.findPawnCamera(pawn);
        if (pawnCamera) this.viewCamera = pawnCamera;

        // Claiming the view is what makes Play show the game's camera rather than
        // whichever camera the editor happened to leave as `main`.
        if (this.viewCamera) Camera3D.playerView = this.viewCamera;
    }

    onUnPossess(_pawn) {
        if (Camera3D.playerView === this.viewCamera) Camera3D.playerView = null;
    }

    /** The first Camera3D on a pawn or anywhere beneath it. */
    static findPawnCamera(pawn) {
        if (!pawn) return null;

        const own = pawn.getComponent(Camera3D);
        if (own) return own;

        const root = pawn.transform3D;
        if (!root) return null;

        // Depth-first over the transform hierarchy: a spring arm puts the camera
        // one or two levels down.
        const stack = [...root.children];
        while (stack.length > 0) {
            const transform = stack.pop();
            const camera = transform.actor?.getComponent(Camera3D);
            if (camera) return camera;
            stack.push(...transform.children);
        }

        return null;
    }

    update(dt) {
        const pawn = this.controlledPawn;
        const input = this.input;
        if (!pawn || !input || !this.inputEnabled) return;

        this.applyLookInput(pawn, input, dt);
        this.onPlayerTick(pawn, input, dt);
    }

    /**
     * Turns mouse, stick and touch movement into controller rotation.
     *
     * Mouse deltas are already per-frame pixel counts, so they are not scaled by
     * dt; stick and touch deflection is a rate and is.
     */
    applyLookInput(pawn, input, dt) {
        const invert = this.invertLookY ? -1 : 1;

        const mouseX = input.mouseDelta.x * this.lookSensitivity;
        const mouseY = input.mouseDelta.y * this.lookSensitivity * invert;

        const pad = input.getGamepad(this.playerIndex);
        const touch = input.touch.rightJoystick.value;

        const stickX = (pad.rightStick.x + touch.x) * this.gamepadLookSpeed * dt;
        const stickY = (-pad.rightStick.y + touch.y) * this.gamepadLookSpeed * dt * invert;

        const yaw = mouseX + stickX;
        const pitch = mouseY + stickY;

        if (yaw !== 0) pawn.addControllerYawInput(yaw);
        if (pitch !== 0) pawn.addControllerPitchInput(pitch);
    }

    /**
     * Default movement: the action map's MoveX and MoveY, projected onto the
     * pawn's own facing so forward means where the player is looking.
     */
    onPlayerTick(pawn, input, _dt) {
        const moveX = input.getAxis('MoveX');
        const moveY = input.getAxis('MoveY');
        if (moveX === 0 && moveY === 0) return;

        const t = pawn.transform3D;
        if (!t) return;

        // Flatten the basis: a pawn looking at the floor should still walk
        // forwards rather than into it.
        const forward = new Vector3(t.forward.x, 0, t.forward.z).normalize();
        const right = new Vector3(t.right.x, 0, t.right.z).normalize();

        // MoveY is positive downwards on a screen and on a stick, and forwards is
        // the negative direction of both.
        pawn.addMovementInput(Vector3.add(
            Vector3.scale(right, moveX),
            Vector3.scale(forward, -moveY)));
    }

    /** A ray from the view camera through the cursor, for click-to-move and picking. */
    getCursorRay() {
        const camera = this.viewCamera ?? Camera3D.main;
        const input = this.input;
        if (!camera || !input) return null;

        const canvas = input.canvas;
        const width = canvas?.width ?? 1280;
        const height = canvas?.height ?? 720;
        return camera.screenToWorldRay(input.mousePosition, width, height);
    }
}
registerActor(PlayerController, { category: 'Gameplay', summary: 'Turns input into pawn movement.' });
