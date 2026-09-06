// -----------------------------------------------------------------------------
// TouchControls — the on-screen joysticks and buttons a phone needs.
//
// A DOM overlay rather than something drawn into the canvas: it stays crisp at
// any pixel ratio, costs the game loop nothing, and can sit inside the safe area
// on a notched phone without the engine knowing what a notch is.
//
// The sticks are only a *visual*. Input still flows through the InputManager's
// TouchManager, so a game reads `Input.getAxis('MoveX')` and neither knows nor
// cares that a thumb is driving it.
// -----------------------------------------------------------------------------

/** Draws thumbsticks and action buttons over the game. */
export class TouchControls {
    /**
     * @param {import('./InputManager.js').InputManager} input
     * @param {object} [options]
     * @param {HTMLElement} [options.mount]
     * @param {boolean} [options.leftStick=true]
     * @param {boolean} [options.rightStick=false]
     * @param {Array<{action: string, label: string}>} [options.buttons]
     */
    constructor(input, {
        mount = null,
        leftStick = true,
        rightStick = false,
        buttons = [{ action: 'Jump', label: 'A' }],
    } = {}) {
        this.input = input;
        this.mount = mount;
        this.showLeftStick = leftStick;
        this.showRightStick = rightStick;
        this.buttonSpecs = buttons;

        this.root = null;
        this._sticks = [];
        this._buttons = [];
        this._visible = false;
    }

    /** True when a touch screen is present. Desktop keeps its keyboard. */
    static get isTouchDevice() {
        if (typeof window === 'undefined') return false;
        return ('ontouchstart' in window)
            || (navigator.maxTouchPoints ?? 0) > 0;
    }

    /** Builds the overlay and attaches it. Does nothing without a document. */
    attach(mount = this.mount) {
        if (typeof document === 'undefined') return this;
        this.mount = mount ?? document.body;

        this.root = document.createElement('div');
        this.root.className = 'sb-touch-controls';
        this.root.innerHTML = '';
        Object.assign(this.root.style, {
            position: 'absolute',
            inset: '0',
            // The overlay must not eat the pointer events the canvas beneath is
            // reading; the sticks are drawn from those same events.
            pointerEvents: 'none',
            zIndex: '10',
            userSelect: 'none',
            touchAction: 'none',
        });

        if (this.showLeftStick) this._sticks.push(this._makeStick('left'));
        if (this.showRightStick) this._sticks.push(this._makeStick('right'));
        for (const spec of this.buttonSpecs) this._makeButton(spec);

        this.mount.appendChild(this.root);
        this.setVisible(TouchControls.isTouchDevice);
        return this;
    }

    _makeStick(side) {
        const base = document.createElement('div');
        Object.assign(base.style, {
            position: 'absolute',
            width: '120px',
            height: '120px',
            marginLeft: '-60px',
            marginTop: '-60px',
            borderRadius: '50%',
            border: '2px solid rgba(255,255,255,0.28)',
            background: 'rgba(255,255,255,0.06)',
            opacity: '0',
            transition: 'opacity 120ms ease',
        });

        const knob = document.createElement('div');
        Object.assign(knob.style, {
            position: 'absolute',
            left: '50%',
            top: '50%',
            width: '52px',
            height: '52px',
            marginLeft: '-26px',
            marginTop: '-26px',
            borderRadius: '50%',
            background: 'rgba(255,255,255,0.42)',
            boxShadow: '0 2px 10px rgba(0,0,0,0.35)',
        });

        base.appendChild(knob);
        this.root.appendChild(base);
        return { side, base, knob };
    }

    _makeButton({ action, label }) {
        const button = document.createElement('div');
        button.textContent = label;
        Object.assign(button.style, {
            position: 'absolute',
            right: 'calc(28px + env(safe-area-inset-right, 0px))',
            bottom: 'calc(40px + env(safe-area-inset-bottom, 0px))',
            width: '76px',
            height: '76px',
            borderRadius: '50%',
            border: '2px solid rgba(255,255,255,0.3)',
            background: 'rgba(255,255,255,0.12)',
            color: 'rgba(255,255,255,0.85)',
            font: '600 22px system-ui, sans-serif',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            // Buttons are the one part that does take pointer events: unlike the
            // sticks, there is no canvas gesture to derive them from.
            pointerEvents: 'auto',
            touchAction: 'none',
        });

        // Stack extra buttons up the right edge.
        const index = this._buttons.length;
        button.style.bottom = `calc(${40 + index * 92}px + env(safe-area-inset-bottom, 0px))`;

        const press = (down) => {
            button.style.background = down ? 'rgba(255,255,255,0.32)' : 'rgba(255,255,255,0.12)';
            this._setVirtualAction(action, down);
        };

        button.addEventListener('pointerdown', (e) => { e.preventDefault(); press(true); });
        button.addEventListener('pointerup', (e) => { e.preventDefault(); press(false); });
        button.addEventListener('pointercancel', () => press(false));
        button.addEventListener('pointerleave', () => press(false));

        this.root.appendChild(button);
        this._buttons.push({ action, button });
        return button;
    }

    /**
     * Feeds a synthetic key press into the input manager.
     *
     * An on-screen button binds to an *action*, so it goes in through the same
     * action map a keyboard does. The action's first keyboard binding is the key
     * pressed, which means rebinding Jump moves the button with it.
     */
    _setVirtualAction(action, down) {
        const bound = this.input.actionMap.get(action);
        const key = bound?.bindings.find((b) => b.device === 'keyboard' && b.key)?.key;
        if (!key) return;

        if (down) this.input._onKeyDown({ code: key, key, repeat: false });
        else this.input._onKeyUp({ code: key, key });
    }

    /** Shows or hides the whole overlay. */
    setVisible(visible) {
        this._visible = visible;
        if (this.root) this.root.style.display = visible ? 'block' : 'none';
    }

    get isVisible() { return this._visible; }

    /** Redraws the sticks from this frame's touch state. Call once per frame. */
    update() {
        if (!this._visible || !this.root) return;

        for (const stick of this._sticks) {
            const source = stick.side === 'left'
                ? this.input.touch.leftJoystick
                : this.input.touch.rightJoystick;

            if (!source.isActive) {
                stick.base.style.opacity = '0';
                continue;
            }

            // The touch positions are in canvas pixels; the overlay is in CSS
            // pixels, so a high-density screen needs the ratio taken back out.
            const ratio = this._pixelRatio();
            stick.base.style.opacity = '1';
            stick.base.style.left = `${source.center.x / ratio}px`;
            stick.base.style.top = `${source.center.y / ratio}px`;

            const travel = source.radius / ratio * 0.55;
            stick.knob.style.transform =
                `translate(${source.value.x * travel}px, ${source.value.y * travel}px)`;
        }
    }

    _pixelRatio() {
        const canvas = this.input.canvas;
        if (!canvas) return 1;
        const rect = canvas.getBoundingClientRect();
        return rect.width > 0 ? canvas.width / rect.width : 1;
    }

    /** Removes the overlay. */
    detach() {
        this.root?.remove();
        this.root = null;
        this._sticks.length = 0;
        this._buttons.length = 0;
    }
}
