// -----------------------------------------------------------------------------
// InputManager — keyboard, mouse, touch, gamepads and the action map.
//
// Mirrors `Input/InputManager.cs`, which is an instance class reached through
// the engine host rather than a static. Everything that is a MonoGame concept
// there becomes a DOM one here: `Mouse.SetPosition` warping becomes the Pointer
// Lock API, `TouchPanel.GetState` becomes pointer events.
// -----------------------------------------------------------------------------

import { Vector2 } from '../math/index.js';
import { canonicalKey, normalizeKeyCode, MouseButton } from './Keys.js';
import { TouchManager } from './TouchState.js';
import { GamepadState } from './GamepadState.js';
import { ActionMap, InputBinding } from './ActionMap.js';

/** The engine's input. One instance lives on the engine host. */
export class InputManager {
    /**
     * @param {object} [options]
     * @param {HTMLCanvasElement} [options.canvas] Element pointer events are read from.
     * @param {ActionMap} [options.actionMap] Defaults to {@link ActionMap.default}.
     * @param {boolean} [options.preventDefaults=true] Swallow browser gestures over the canvas.
     */
    constructor({ canvas = null, actionMap = null, preventDefaults = true } = {}) {
        this.canvas = canvas;
        this.actionMap = actionMap ?? ActionMap.default();
        this.preventDefaults = preventDefaults;

        // ---- Keyboard ----
        this._keys = new Set();
        this._keysPressed = new Set();
        this._keysReleased = new Set();

        // ---- Mouse ----
        this.mousePosition = new Vector2(0, 0);
        this.mouseDelta = new Vector2(0, 0);
        this.scrollDelta = 0;
        this._mouseButtons = new Set();
        this._mouseButtonsPressed = new Set();
        this._mouseButtonsReleased = new Set();
        this._pendingMouseDelta = new Vector2(0, 0);
        this._pendingScroll = 0;

        // ---- Touch ----
        this.touch = new TouchManager();

        // ---- Gamepads ----
        this.gamepads = [0, 1, 2, 3].map((i) => new GamepadState(i));

        // ---- Cursor ----
        this.isCursorVisible = true;
        this.isCursorLocked = false;

        /** Fired when show/hide is requested, so a host can restyle its canvas. */
        this.cursorVisibilityChanged = null;

        /** Text typed since the last frame, for text fields. */
        this.typedText = '';
        this._pendingText = '';

        this._listeners = [];
        this._attached = false;
    }

    // -------------------------------------------------------------------------
    // Attachment
    // -------------------------------------------------------------------------

    /**
     * Starts listening. Keyboard goes on the window, pointer events on the
     * canvas, so a page can host the game beside other interactive content.
     */
    attach(canvas = this.canvas) {
        if (this._attached) this.detach();
        if (typeof window === 'undefined') return;   // headless: tests drive the manager directly

        this.canvas = canvas ?? this.canvas;
        const target = this.canvas ?? window;

        this._on(window, 'keydown', (e) => this._onKeyDown(e));
        this._on(window, 'keyup', (e) => this._onKeyUp(e));
        this._on(window, 'blur', () => this._onBlur());

        this._on(target, 'pointerdown', (e) => this._onPointerDown(e));
        this._on(window, 'pointermove', (e) => this._onPointerMove(e));
        this._on(window, 'pointerup', (e) => this._onPointerUp(e));
        this._on(window, 'pointercancel', (e) => this._onPointerUp(e, true));
        this._on(target, 'wheel', (e) => this._onWheel(e), { passive: false });
        this._on(target, 'contextmenu', (e) => { if (this.preventDefaults) e.preventDefault(); });

        // Safari on iOS still fires these alongside pointer events, and they are
        // what actually scrolls and zooms the page.
        if (this.preventDefaults && this.canvas) {
            for (const name of ['touchstart', 'touchmove', 'touchend']) {
                this._on(this.canvas, name, (e) => e.preventDefault(), { passive: false });
            }
        }

        this._on(document, 'pointerlockchange', () => {
            this.isCursorLocked = document.pointerLockElement === this.canvas;
        });

        this._attached = true;
    }

    /** Stops listening and releases every held input. */
    detach() {
        for (const [target, type, handler, options] of this._listeners) {
            target.removeEventListener(type, handler, options);
        }
        this._listeners.length = 0;
        this._attached = false;
        this._onBlur();
    }

    _on(target, type, handler, options) {
        target.addEventListener(type, handler, options);
        this._listeners.push([target, type, handler, options]);
    }

    // -------------------------------------------------------------------------
    // Frame
    // -------------------------------------------------------------------------

    /**
     * Rolls the edge-triggered state forward. Called once per frame by the host,
     * on unscaled time so menus stay responsive while the game is paused.
     */
    update(dt) {
        this._keysPressed.clear();
        this._keysReleased.clear();
        this._mouseButtonsPressed.clear();
        this._mouseButtonsReleased.clear();

        for (const key of this._pendingKeysPressed) this._keysPressed.add(key);
        for (const key of this._pendingKeysReleased) this._keysReleased.add(key);
        this._pendingKeysPressed.clear();
        this._pendingKeysReleased.clear();

        for (const b of this._pendingButtonsPressed) this._mouseButtonsPressed.add(b);
        for (const b of this._pendingButtonsReleased) this._mouseButtonsReleased.add(b);
        this._pendingButtonsPressed.clear();
        this._pendingButtonsReleased.clear();

        this.mouseDelta.copy(this._pendingMouseDelta);
        this._pendingMouseDelta.set(0, 0);

        this.scrollDelta = this._pendingScroll;
        this._pendingScroll = 0;

        this.typedText = this._pendingText;
        this._pendingText = '';

        this.touch.update();
        this._updateGamepads();
    }

    _updateGamepads() {
        const pads = typeof navigator !== 'undefined' && navigator.getGamepads
            ? navigator.getGamepads()
            : [];
        for (let i = 0; i < this.gamepads.length; i++) this.gamepads[i].update(pads[i] ?? null);
    }

    // -------------------------------------------------------------------------
    // Keyboard
    // -------------------------------------------------------------------------

    /** True while the key is held. @param {string} key An XNA-style name, e.g. 'Space'. */
    isKeyDown(key) { return this._keys.has(canonicalKey(key)); }

    /** True on the frame the key went down. */
    isKeyPressed(key) { return this._keysPressed.has(canonicalKey(key)); }

    /** True on the frame the key came up. */
    isKeyReleased(key) { return this._keysReleased.has(canonicalKey(key)); }

    /** Every key currently held, for a rebinding UI. */
    get heldKeys() { return [...this._keys]; }

    // -------------------------------------------------------------------------
    // Mouse
    // -------------------------------------------------------------------------

    isMouseButtonDown(button = MouseButton.Left) { return this._mouseButtons.has(button); }
    isMouseButtonPressed(button = MouseButton.Left) { return this._mouseButtonsPressed.has(button); }
    isMouseButtonReleased(button = MouseButton.Left) { return this._mouseButtonsReleased.has(button); }

    get mouseX() { return this.mousePosition.x; }
    get mouseY() { return this.mousePosition.y; }

    // -------------------------------------------------------------------------
    // Cursor
    // -------------------------------------------------------------------------

    showCursor() {
        this.isCursorVisible = true;
        if (this.canvas) this.canvas.style.cursor = '';
        this.cursorVisibilityChanged?.(true);
    }

    hideCursor() {
        this.isCursorVisible = false;
        if (this.canvas) this.canvas.style.cursor = 'none';
        this.cursorVisibilityChanged?.(false);
    }

    /**
     * Locks the pointer to the canvas, so `mouseDelta` keeps reporting movement
     * past the window edge — what a first-person camera needs.
     *
     * The browser only grants this from inside a user gesture, so call it from a
     * click handler rather than at start-up.
     */
    lockCursor() {
        this.canvas?.requestPointerLock?.();
    }

    unlockCursor() {
        if (typeof document !== 'undefined') document.exitPointerLock?.();
        this.isCursorLocked = false;
    }

    // -------------------------------------------------------------------------
    // Gamepads
    // -------------------------------------------------------------------------

    getGamepad(playerIndex = 0) { return this.gamepads[playerIndex] ?? this.gamepads[0]; }

    isGamepadConnected(playerIndex = 0) { return this.getGamepad(playerIndex).isConnected; }

    setRumble(playerIndex, lowFrequency, highFrequency, durationSeconds) {
        const pads = typeof navigator !== 'undefined' && navigator.getGamepads
            ? navigator.getGamepads()
            : [];
        return this.getGamepad(playerIndex)
            .setRumble(lowFrequency, highFrequency, durationSeconds, pads[playerIndex] ?? null);
    }

    // -------------------------------------------------------------------------
    // Action map
    // -------------------------------------------------------------------------

    /** True on the frame any of an action's bindings became active. */
    isPressed(action) { return this._evaluateDigital(action, 'pressed'); }

    /** True while any of an action's bindings is active. */
    isHeld(action) { return this._evaluateDigital(action, 'held'); }

    /** True on the frame every binding for an action stopped being active. */
    isReleased(action) { return this._evaluateDigital(action, 'released'); }

    /**
     * The action's value in -1..1. Bindings are tried in order and the first
     * non-zero one wins, so a keyboard binding does not fight a resting stick.
     */
    getAxis(action) {
        const bound = this.actionMap.get(action);
        if (!bound) return 0;

        for (const binding of bound.bindings) {
            const value = this._evaluateAxis(binding);
            if (value !== 0) return binding.invert ? -value * binding.scale : value * binding.scale;
        }
        return 0;
    }

    /** Replaces an action's bindings, for a rebinding screen. */
    rebindAction(action, binding) { return this.actionMap.rebind(action, binding); }

    /** Loads an action map from parsed JSON or a JSON string. */
    loadActionMap(json) { this.actionMap = ActionMap.fromJson(json); return this.actionMap; }

    /** The current bindings, ready to persist. */
    saveBindings() { return JSON.stringify(this.actionMap.toJSON(), null, 2); }

    resetBindings() { this.actionMap = ActionMap.default(); }

    _evaluateDigital(action, mode) {
        const bound = this.actionMap.get(action);
        if (!bound) return false;

        let anyHeld = false;
        let anyReleased = false;

        for (const binding of bound.bindings) {
            switch (binding.device) {
                case 'keyboard': {
                    if (!binding.key) break;
                    if (mode === 'pressed' && this.isKeyPressed(binding.key)) return true;
                    if (this.isKeyDown(binding.key)) anyHeld = true;
                    if (this.isKeyReleased(binding.key)) anyReleased = true;
                    break;
                }
                case 'mouse': {
                    const button = Number(binding.button ?? binding.key ?? 0);
                    if (mode === 'pressed' && this.isMouseButtonPressed(button)) return true;
                    if (this.isMouseButtonDown(button)) anyHeld = true;
                    if (this.isMouseButtonReleased(button)) anyReleased = true;
                    break;
                }
                case 'gamepad': {
                    if (!binding.button) break;
                    const pad = this.getGamepad(0);
                    if (mode === 'pressed' && pad.isButtonPressed(binding.button)) return true;
                    if (pad.isButtonDown(binding.button)) anyHeld = true;
                    if (pad.isButtonReleased(binding.button)) anyReleased = true;
                    break;
                }
                case 'touch': {
                    // A touch binding with no axis is a screen tap.
                    if (binding.axis) break;
                    const touching = this.touch.isTouching;
                    if (mode === 'pressed' && touching
                        && this.touch.touches.some((t) => t.phase === 'Began')) return true;
                    if (touching) anyHeld = true;
                    break;
                }
                default:
                    break;
            }
        }

        if (mode === 'held') return anyHeld;
        if (mode === 'released') return anyReleased && !anyHeld;
        return false;
    }

    _evaluateAxis(binding) {
        switch (binding.device) {
            case 'keyboard': {
                if (binding.negKey || binding.posKey) {
                    return (this.isKeyDown(binding.posKey) ? 1 : 0)
                         - (this.isKeyDown(binding.negKey) ? 1 : 0);
                }
                return this.isKeyDown(binding.key) ? 1 : 0;
            }
            case 'mouse': {
                if (binding.axis === 'MouseX') return this.mouseDelta.x;
                if (binding.axis === 'MouseY') return this.mouseDelta.y;
                if (binding.axis === 'ScrollDelta') return this.scrollDelta;
                return this.isMouseButtonDown(Number(binding.button ?? 0)) ? 1 : 0;
            }
            case 'gamepad':
                return binding.axis ? this.getGamepad(0).getAxis(binding.axis) : 0;
            case 'touch': {
                switch (binding.axis) {
                    case 'LeftJoystickX':  return this.touch.leftJoystick.value.x;
                    case 'LeftJoystickY':  return this.touch.leftJoystick.value.y;
                    case 'RightJoystickX': return this.touch.rightJoystick.value.x;
                    case 'RightJoystickY': return this.touch.rightJoystick.value.y;
                    case 'PinchDelta':     return this.touch.pinchDelta;
                    default: return 0;
                }
            }
            default:
                return 0;
        }
    }

    // -------------------------------------------------------------------------
    // Event handlers
    // -------------------------------------------------------------------------

    _pendingKeysPressed = new Set();
    _pendingKeysReleased = new Set();
    _pendingButtonsPressed = new Set();
    _pendingButtonsReleased = new Set();

    _onKeyDown(event) {
        const key = normalizeKeyCode(event.code) || canonicalKey(event.key);

        // The browser repeats a held key; the engine's "pressed" means the first
        // frame only, so a repeat must not re-trigger it.
        if (!event.repeat && !this._keys.has(key)) this._pendingKeysPressed.add(key);
        this._keys.add(key);

        if (event.key && event.key.length === 1 && !event.ctrlKey && !event.metaKey) {
            this._pendingText += event.key;
        } else if (event.key === 'Backspace') {
            this._pendingText += '\b';
        }

        // Keys the page would otherwise act on. Function keys and the modifiers
        // stay with the browser so refresh and devtools keep working.
        if (this.preventDefaults && SWALLOWED_KEYS.has(key)) event.preventDefault();
    }

    _onKeyUp(event) {
        const key = normalizeKeyCode(event.code) || canonicalKey(event.key);
        if (this._keys.delete(key)) this._pendingKeysReleased.add(key);
    }

    _onBlur() {
        // Without this, a key held while the window loses focus stays held forever:
        // the keyup lands on whatever the user switched to.
        for (const key of this._keys) this._pendingKeysReleased.add(key);
        this._keys.clear();
        for (const b of this._mouseButtons) this._pendingButtonsReleased.add(b);
        this._mouseButtons.clear();
        this.touch.clear();
    }

    _onPointerDown(event) {
        if (event.pointerType === 'touch') {
            const p = this._toCanvas(event);
            this.touch.onTouchStart(event.pointerId, p.x, p.y);
            this.mousePosition.copy(p);   // a tap also reads as a click, as on desktop
        }

        if (!this._mouseButtons.has(event.button)) this._pendingButtonsPressed.add(event.button);
        this._mouseButtons.add(event.button);

        this.mousePosition.copy(this._toCanvas(event));
        this.canvas?.setPointerCapture?.(event.pointerId);
        if (this.preventDefaults) event.preventDefault();
    }

    _onPointerMove(event) {
        if (event.pointerType === 'touch') {
            const p = this._toCanvas(event);
            this.touch.onTouchMove(event.pointerId, p.x, p.y);
        }

        const position = this._toCanvas(event);

        // Under pointer lock the reported position is meaningless; movementX/Y is
        // the only signal, and it is what a look control wants anyway.
        if (this.isCursorLocked) {
            this._pendingMouseDelta.x += event.movementX ?? 0;
            this._pendingMouseDelta.y += event.movementY ?? 0;
        } else {
            this._pendingMouseDelta.x += position.x - this.mousePosition.x;
            this._pendingMouseDelta.y += position.y - this.mousePosition.y;
            this.mousePosition.copy(position);
        }
    }

    _onPointerUp(event, cancelled = false) {
        if (event.pointerType === 'touch') this.touch.onTouchEnd(event.pointerId, cancelled);

        if (this._mouseButtons.delete(event.button)) this._pendingButtonsReleased.add(event.button);
        this.canvas?.releasePointerCapture?.(event.pointerId);
    }

    _onWheel(event) {
        // deltaMode 1 is lines, 2 is pages; normalise everything to notches so a
        // trackpad and a mouse wheel feel comparable.
        const scale = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? 100 : 1;
        this._pendingScroll -= (event.deltaY * scale) / 100;
        if (this.preventDefaults) event.preventDefault();
    }

    /** Converts a pointer event into canvas pixels, accounting for CSS scaling. */
    _toCanvas(event) {
        if (!this.canvas) return new Vector2(event.clientX, event.clientY);

        const rect = this.canvas.getBoundingClientRect();
        const scaleX = rect.width > 0 ? this.canvas.width / rect.width : 1;
        const scaleY = rect.height > 0 ? this.canvas.height / rect.height : 1;
        return new Vector2(
            (event.clientX - rect.left) * scaleX,
            (event.clientY - rect.top) * scaleY);
    }
}

/** Keys whose default page behaviour would interfere with a game. */
const SWALLOWED_KEYS = new Set([
    'Space', 'Up', 'Down', 'Left', 'Right', 'Tab',
    'PageUp', 'PageDown', 'Home', 'End', 'Back',
]);

export { MouseButton };
