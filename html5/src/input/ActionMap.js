// -----------------------------------------------------------------------------
// ActionMap — named actions bound to devices, loaded from the same JSON the C#
// engine uses (`Input/ActionMap.cs`).
//
// One addition: a "touch" device, so an action can be driven by a virtual
// joystick. The C# ActionMap has no touch binding — touch is only reachable
// through `Input.Touch` directly — which makes every action-map-driven game
// unplayable on a phone. Files stay compatible either way: a C# engine reading a
// file with touch bindings ignores the device it does not know, and this engine
// reads a file without them unchanged.
// -----------------------------------------------------------------------------

/** One device binding for an action. */
export class InputBinding {
    constructor(init = {}) {
        /** 'keyboard' | 'mouse' | 'gamepad' | 'touch' */
        this.device = init.device ?? 'keyboard';

        /** Key or button name for a digital binding. */
        this.key = init.key ?? null;

        /** For an axis built from two keys. */
        this.negKey = init.negKey ?? null;
        this.posKey = init.posKey ?? null;

        /** Gamepad button name, or mouse button index. */
        this.button = init.button ?? null;

        /** Analogue axis name: 'LeftStickX', 'LeftJoystickY', 'MouseX', … */
        this.axis = init.axis ?? null;

        this.invert = init.invert ?? false;
        this.scale = init.scale ?? 1;
    }

    toJSON() {
        const out = { device: this.device };
        for (const key of ['key', 'negKey', 'posKey', 'button', 'axis']) {
            if (this[key] != null) out[key] = this[key];
        }
        if (this.invert) out.invert = true;
        if (this.scale !== 1) out.scale = this.scale;
        return out;
    }
}

/** A named action and the bindings that can trigger it. */
export class InputAction {
    constructor(name, bindings = []) {
        this.name = name;
        this.bindings = bindings.map((b) => (b instanceof InputBinding ? b : new InputBinding(b)));
    }

    toJSON() { return { name: this.name, bindings: this.bindings.map((b) => b.toJSON()) }; }
}

/** The set of actions a game responds to. */
export class ActionMap {
    constructor(actions = {}) {
        /** @type {Map<string, InputAction>} Keyed by lower-case name, looked up case-insensitively. */
        this.actions = new Map();
        for (const [name, action] of Object.entries(actions)) this.add(name, action);
    }

    add(name, action) {
        const built = action instanceof InputAction
            ? action
            : new InputAction(name, action?.bindings ?? action ?? []);
        this.actions.set(name.toLowerCase(), built);
        return built;
    }

    get(name) { return this.actions.get(String(name).toLowerCase()) ?? null; }

    remove(name) { return this.actions.delete(String(name).toLowerCase()); }

    get names() { return [...this.actions.values()].map((a) => a.name); }

    /** Replaces every binding on an action. */
    rebind(name, binding) {
        const action = this.get(name) ?? this.add(name, []);
        action.bindings = [binding instanceof InputBinding ? binding : new InputBinding(binding)];
        return action;
    }

    toJSON() {
        return { actions: [...this.actions.values()].map((a) => a.toJSON()) };
    }

    /** Parses the `{ "actions": [ … ] }` document the C# engine writes. */
    static fromJson(json) {
        const dto = typeof json === 'string' ? JSON.parse(json) : json;
        const map = new ActionMap();

        // Both shapes turn up: an array of named actions, or an object keyed by name.
        const entries = Array.isArray(dto?.actions)
            ? dto.actions.map((a) => [a.name, a])
            : Object.entries(dto?.actions ?? dto ?? {});

        for (const [name, action] of entries) {
            if (!name) continue;
            map.add(name, new InputAction(name, action?.bindings ?? []));
        }
        return map;
    }

    /**
     * A sensible starting map: WASD and arrows for movement, space to jump, mouse
     * buttons to fire, with the left virtual joystick bound to the movement axes
     * and gamepad sticks alongside them.
     */
    static default() {
        return ActionMap.fromJson({
            actions: [
                {
                    name: 'MoveX',
                    bindings: [
                        { device: 'keyboard', negKey: 'A', posKey: 'D' },
                        { device: 'keyboard', negKey: 'Left', posKey: 'Right' },
                        { device: 'gamepad', axis: 'LeftStickX' },
                        { device: 'touch', axis: 'LeftJoystickX' },
                    ],
                },
                {
                    name: 'MoveY',
                    bindings: [
                        { device: 'keyboard', negKey: 'W', posKey: 'S' },
                        { device: 'keyboard', negKey: 'Up', posKey: 'Down' },
                        { device: 'gamepad', axis: 'LeftStickY' },
                        { device: 'touch', axis: 'LeftJoystickY' },
                    ],
                },
                {
                    name: 'LookX',
                    bindings: [
                        { device: 'mouse', axis: 'MouseX' },
                        { device: 'gamepad', axis: 'RightStickX' },
                        { device: 'touch', axis: 'RightJoystickX' },
                    ],
                },
                {
                    name: 'LookY',
                    bindings: [
                        { device: 'mouse', axis: 'MouseY' },
                        { device: 'gamepad', axis: 'RightStickY' },
                        { device: 'touch', axis: 'RightJoystickY' },
                    ],
                },
                { name: 'Jump',   bindings: [{ device: 'keyboard', key: 'Space' }, { device: 'gamepad', button: 'A' }] },
                { name: 'Sprint', bindings: [{ device: 'keyboard', key: 'LeftShift' }, { device: 'gamepad', button: 'LeftStick' }] },
                { name: 'Fire',   bindings: [{ device: 'mouse', button: 0 }, { device: 'gamepad', button: 'RightTrigger' }] },
                { name: 'AltFire',bindings: [{ device: 'mouse', button: 1 }, { device: 'gamepad', button: 'LeftTrigger' }] },
                { name: 'Interact', bindings: [{ device: 'keyboard', key: 'E' }, { device: 'gamepad', button: 'X' }] },
                { name: 'Pause',  bindings: [{ device: 'keyboard', key: 'Escape' }, { device: 'gamepad', button: 'Start' }] },
                // The four directions the bundled template scripts ask for by name.
                { name: 'Left',  bindings: [{ device: 'keyboard', key: 'Left' }, { device: 'keyboard', key: 'A' }] },
                { name: 'Right', bindings: [{ device: 'keyboard', key: 'Right' }, { device: 'keyboard', key: 'D' }] },
                { name: 'Up',    bindings: [{ device: 'keyboard', key: 'Up' }, { device: 'keyboard', key: 'W' }] },
                { name: 'Down',  bindings: [{ device: 'keyboard', key: 'Down' }, { device: 'keyboard', key: 'S' }] },
            ],
        });
    }
}
