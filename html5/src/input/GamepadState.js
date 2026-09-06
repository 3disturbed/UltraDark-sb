// -----------------------------------------------------------------------------
// GamepadState — one pad, read through the browser's Gamepad API.
//
// Button names follow XNA's `Buttons` enum so that action maps and code port
// across; the standard gamepad mapping puts them at fixed indices.
// -----------------------------------------------------------------------------

import { Vector2 } from '../math/index.js';

/** Standard-mapping button index for each XNA button name. */
const BUTTON_INDEX = {
    A: 0, B: 1, X: 2, Y: 3,
    LeftShoulder: 4, RightShoulder: 5,
    LeftTrigger: 6, RightTrigger: 7,
    Back: 8, Start: 9,
    LeftStick: 10, RightStick: 11,
    DPadUp: 12, DPadDown: 13, DPadLeft: 14, DPadRight: 15,
    BigButton: 16,
};

/** A snapshot of one gamepad, refreshed each frame. */
export class GamepadState {
    constructor(playerIndex = 0) {
        this.playerIndex = playerIndex;
        this.isConnected = false;

        /** Stick deflection in -1..1. Y is negated so up is positive, as in XNA. */
        this.leftStick = new Vector2(0, 0);
        this.rightStick = new Vector2(0, 0);

        this.leftTrigger = 0;
        this.rightTrigger = 0;

        /** Below this, a stick reads as centred. */
        this.deadZone = 0.15;

        this._buttons = new Set();
        this._previous = new Set();
        this._rumbleUntil = 0;
    }

    /** Rebuilds from a browser Gamepad object, or marks the pad disconnected. */
    update(pad) {
        this._previous = this._buttons;
        this._buttons = new Set();

        if (!pad) {
            this.isConnected = false;
            this.leftStick.set(0, 0);
            this.rightStick.set(0, 0);
            this.leftTrigger = 0;
            this.rightTrigger = 0;
            return;
        }

        this.isConnected = true;

        const axes = pad.axes ?? [];
        this.leftStick.set(this._applyDeadZone(axes[0] ?? 0), -this._applyDeadZone(axes[1] ?? 0));
        this.rightStick.set(this._applyDeadZone(axes[2] ?? 0), -this._applyDeadZone(axes[3] ?? 0));

        const buttons = pad.buttons ?? [];
        this.leftTrigger = buttons[6]?.value ?? 0;
        this.rightTrigger = buttons[7]?.value ?? 0;

        for (const [name, index] of Object.entries(BUTTON_INDEX)) {
            if (buttons[index]?.pressed) this._buttons.add(name);
        }
    }

    _applyDeadZone(value) {
        if (Math.abs(value) < this.deadZone) return 0;
        // Rescale so the value still reaches 1 at full deflection rather than
        // jumping from 0 to the dead-zone edge.
        const sign = value < 0 ? -1 : 1;
        return sign * (Math.abs(value) - this.deadZone) / (1 - this.deadZone);
    }

    isButtonDown(name) { return this._buttons.has(name); }
    isButtonPressed(name) { return this._buttons.has(name) && !this._previous.has(name); }
    isButtonReleased(name) { return !this._buttons.has(name) && this._previous.has(name); }

    /**
     * Reads a named axis.
     *
     * `LeftX`, `LeftY`, `RightX`, `RightY`, `LeftTrigger` and `RightTrigger` are the names the C#
     * engine's action maps use, so a bindings file written there works here unchanged. The longer
     * `LeftStickX` spellings are accepted as aliases.
     */
    getAxis(name) {
        switch (name) {
            case 'LeftX':
            case 'LeftStickX':   return this.leftStick.x;
            case 'LeftY':
            case 'LeftStickY':   return this.leftStick.y;
            case 'RightX':
            case 'RightStickX':  return this.rightStick.x;
            case 'RightY':
            case 'RightStickY':  return this.rightStick.y;
            case 'LeftTrigger':  return this.leftTrigger;
            case 'RightTrigger': return this.rightTrigger;
            default: return 0;
        }
    }

    /**
     * Plays a rumble effect where the browser supports one.
     * Silently does nothing elsewhere, which is most of Safari.
     */
    setRumble(lowFrequency, highFrequency, durationSeconds, pad) {
        const actuator = pad?.vibrationActuator;
        if (!actuator?.playEffect) return false;

        actuator.playEffect('dual-rumble', {
            duration: durationSeconds * 1000,
            weakMagnitude: Math.max(0, Math.min(1, highFrequency)),
            strongMagnitude: Math.max(0, Math.min(1, lowFrequency)),
        }).catch(() => { /* a rejected effect is not worth reporting */ });
        return true;
    }
}

export { BUTTON_INDEX };
