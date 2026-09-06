// -----------------------------------------------------------------------------
// Touch — touch points, virtual joysticks and pinch, mirroring Input/TouchState.cs.
//
// This is the part that makes a game playable on a phone. The C# engine already
// has the same shapes; the only difference here is that the events come from the
// DOM rather than from MonoGame's TouchPanel.
// -----------------------------------------------------------------------------

import { Vector2 } from '../math/index.js';

/** The lifecycle stage of a touch point. */
export const TouchPhase = Object.freeze({
    Began: 'Began',
    Moved: 'Moved',
    Stationary: 'Stationary',
    Ended: 'Ended',
    Cancelled: 'Cancelled',
});

/** One finger on the screen for one frame. */
export class TouchPoint {
    constructor(id, position, phase = TouchPhase.Began) {
        this.id = id;
        this.position = position;
        this.delta = new Vector2(0, 0);
        this.startPosition = position.clone();
        this.phase = phase;
    }
}

/**
 * An on-screen thumbstick.
 *
 * Claims the first touch that starts inside its half of the screen and follows
 * it until release, so two joysticks — movement left, aim right — never fight
 * over the same finger.
 */
export class VirtualJoystick {
    /**
     * @param {object} [options]
     * @param {'left'|'right'|'any'} [options.side='left'] Which half of the screen it claims.
     * @param {number} [options.radius=80] Travel, in CSS pixels, for a full-deflection value.
     * @param {boolean} [options.dynamic=true] Re-centre on the touch that starts it.
     */
    constructor({ side = 'left', radius = 80, dynamic = true } = {}) {
        this.side = side;
        this.radius = radius;
        this.dynamic = dynamic;

        /** Where the stick is anchored, in screen pixels. */
        this.center = new Vector2(0, 0);

        /** Deflection in -1..1 on each axis. Y is positive downwards, as on screen. */
        this.value = new Vector2(0, 0);

        this.isActive = false;
        this._touchId = null;
    }

    /** Rebuilds `value` from this frame's touches. Called by the TouchManager. */
    update(touches, screenWidth) {
        const claimed = this._touchId !== null
            ? touches.find((t) => t.id === this._touchId)
            : null;

        if (claimed && claimed.phase !== TouchPhase.Ended && claimed.phase !== TouchPhase.Cancelled) {
            const dx = claimed.position.x - this.center.x;
            const dy = claimed.position.y - this.center.y;
            const dist = Math.hypot(dx, dy);

            if (dist > this.radius) {
                this.value.set(dx / dist, dy / dist);
            } else {
                this.value.set(dx / this.radius, dy / this.radius);
            }
            this.isActive = true;
            return;
        }

        // The finger went away, or we never had one: look for a new claim.
        this._touchId = null;
        this.isActive = false;
        this.value.set(0, 0);

        for (const touch of touches) {
            if (touch.phase !== TouchPhase.Began) continue;
            if (!this._ownsPosition(touch.position, screenWidth)) continue;

            this._touchId = touch.id;
            if (this.dynamic) this.center.copy(touch.startPosition);
            this.isActive = true;
            break;
        }
    }

    _ownsPosition(position, screenWidth) {
        if (this.side === 'any') return true;
        const half = screenWidth / 2;
        return this.side === 'left' ? position.x < half : position.x >= half;
    }
}

/**
 * Collects touch points for the frame and derives joystick and pinch values.
 *
 * The manager is fed by the browser's pointer events; it does not attach
 * listeners itself, so the same class works in a test with synthetic input.
 */
export class TouchManager {
    constructor() {
        /** This frame's touch points. */
        this.touches = [];

        this.leftJoystick = new VirtualJoystick({ side: 'left' });
        this.rightJoystick = new VirtualJoystick({ side: 'right' });

        /** Change in two-finger distance since last frame, in pixels. */
        this.pinchDelta = 0;

        this._screenWidth = 1;
        this._screenHeight = 1;
        this._active = new Map();       // pointerId -> TouchPoint
        this._lastPinchDistance = 0;
    }

    /** True while at least one finger is down. */
    get isTouching() { return this.touches.length > 0; }

    /** How many fingers are down. */
    get touchCount() { return this.touches.length; }

    setScreenSize(width, height) {
        this._screenWidth = width;
        this._screenHeight = height;
    }

    // ---- Event intake --------------------------------------------------------
    // Driven by the InputManager's pointer listeners.

    onTouchStart(id, x, y) {
        const point = new TouchPoint(id, new Vector2(x, y), TouchPhase.Began);
        this._active.set(id, point);
    }

    onTouchMove(id, x, y) {
        const point = this._active.get(id);
        if (!point) return;
        point.delta.set(x - point.position.x, y - point.position.y);
        point.position.set(x, y);
        // A Began touch stays Began for its first frame even if it moves, so a
        // joystick sees the press before the drag.
        if (point.phase !== TouchPhase.Began) point.phase = TouchPhase.Moved;
    }

    onTouchEnd(id, cancelled = false) {
        const point = this._active.get(id);
        if (!point) return;
        point.phase = cancelled ? TouchPhase.Cancelled : TouchPhase.Ended;
    }

    /** Advances every touch by a frame. Called by the InputManager. */
    update() {
        this.touches = [...this._active.values()];

        this.leftJoystick.update(this.touches, this._screenWidth);
        this.rightJoystick.update(this.touches, this._screenWidth);
        this._updatePinch();

        // Retire finished touches and settle the rest, after everything has seen
        // this frame's phases.
        for (const [id, point] of [...this._active]) {
            if (point.phase === TouchPhase.Ended || point.phase === TouchPhase.Cancelled) {
                this._active.delete(id);
                continue;
            }
            point.phase = point.delta.lengthSquared > 0 ? TouchPhase.Moved : TouchPhase.Stationary;
            point.delta.set(0, 0);
        }
    }

    _updatePinch() {
        const live = this.touches.filter(
            (t) => t.phase !== TouchPhase.Ended && t.phase !== TouchPhase.Cancelled);

        if (live.length < 2) {
            this.pinchDelta = 0;
            this._lastPinchDistance = 0;
            return;
        }

        const distance = Vector2.distance(live[0].position, live[1].position);
        this.pinchDelta = this._lastPinchDistance > 0 ? distance - this._lastPinchDistance : 0;
        this._lastPinchDistance = distance;
    }

    /** Drops every touch. Used when the page loses focus mid-gesture. */
    clear() {
        this._active.clear();
        this.touches = [];
        this.pinchDelta = 0;
        this._lastPinchDistance = 0;
    }
}
