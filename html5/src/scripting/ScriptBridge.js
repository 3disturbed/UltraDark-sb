// -----------------------------------------------------------------------------
// ScriptBridge — the globals a project `.js` script sees.
//
// This is the compatibility layer for scripts written against the C# engine's
// Jint bridge. Everything `Scripting/TypeScriptDefinitions.cs` declares is here
// with the same names and the same shapes, so a script that runs under Jint runs
// here unchanged.
//
// It is deliberately a superset. The C# bridge exposes seven globals and no
// `getComponent`, which is why none of the forty-five bundled template scripts
// have ever run — they call `log()`, `Input.isKeyHeld`, `actor.getComponent` and
// `actor.transform`, none of which exist there. Those are all provided here.
// Adding them cannot break a script written against the narrower surface, and it
// makes the shipped templates work. Each addition is marked below.
// -----------------------------------------------------------------------------

import { Vector2 as Vec2Class, Vector3 as Vec3Class } from '../math/index.js';
import { Time } from '../core/Time.js';

/**
 * Builds the global object a script executes against.
 *
 * @param {import('../core/Actor.js').Actor} actor The actor the script is attached to.
 * @param {object} [services]
 * @param {(level: string, message: string) => void} [services.log]
 * @returns {object} A map of global name to value.
 */
export function createScriptGlobals(actor, services = {}) {
    const log = services.log ?? ((level, message) => {
        const line = `[Script${level === 'log' ? '' : ` ${level.toUpperCase()}`}] ${message}`;
        (level === 'error' ? console.error : level === 'warn' ? console.warn : console.log)(line);
    });

    const engine = () => actor.scene?.engine ?? null;
    const input = () => engine()?.input ?? null;
    const audioManager = () => engine()?.audio ?? null;

    // ---- actor ---------------------------------------------------------------
    // The C# `actor` proxy has name/tag/active/destroy only. `transform`,
    // `transform3d`, `getComponent` and `addComponent` are additions.
    const actorProxy = {
        get name() { return actor.name; },
        set name(value) { actor.name = String(value); },
        get tag() { return actor.tag; },
        set tag(value) { actor.tag = String(value); },
        get active() { return actor.isActive; },
        set active(value) { actor.isActive = Boolean(value); },
        get id() { return actor.id; },
        destroy() { actor.destroy(); },

        // Additions:
        get transform() { return transformProxy; },
        get transform3d() { return transform3DProxy(); },
        getComponent(name) { return wrapComponent(actor.getComponent(name)); },
        addComponent(name) { return wrapComponent(actor.addComponent(name)); },
    };

    // ---- transform -----------------------------------------------------------
    const transformProxy = {
        get x() { return actor.transform.position.x; },
        set x(value) { actor.transform.x = Number(value); },
        get y() { return actor.transform.position.y; },
        set y(value) { actor.transform.y = Number(value); },
        get rotation() { return actor.transform.rotation; },
        set rotation(value) { actor.transform.rotation = Number(value); },
        get scaleX() { return actor.transform.scale.x; },
        set scaleX(value) { actor.transform.scaleX = Number(value); },
        get scaleY() { return actor.transform.scale.y; },
        set scaleY(value) { actor.transform.scaleY = Number(value); },
        lookAt(x, y) { actor.transform.lookAt(new Vec2Class(Number(x), Number(y))); },
        distanceTo(other) {
            if (!other) return 0;
            const dx = (other.x ?? 0) - actor.transform.position.x;
            const dy = (other.y ?? 0) - actor.transform.position.y;
            return Math.hypot(dx, dy);
        },
    };

    // ---- transform3d (an addition; the 3D templates ask for it) --------------
    function transform3DProxy() {
        const t = actor.transform3D;
        if (!t) return null;
        return {
            get x() { return t.position.x; }, set x(v) { t.x = Number(v); },
            get y() { return t.position.y; }, set y(v) { t.y = Number(v); },
            get z() { return t.position.z; }, set z(v) { t.z = Number(v); },
            get rotX() { return t.eulerAngles.x; },
            set rotX(v) { const e = t.eulerAngles; t.eulerAngles = new Vec3Class(Number(v), e.y, e.z); },
            get rotY() { return t.eulerAngles.y; },
            set rotY(v) { const e = t.eulerAngles; t.eulerAngles = new Vec3Class(e.x, Number(v), e.z); },
            get rotZ() { return t.eulerAngles.z; },
            set rotZ(v) { const e = t.eulerAngles; t.eulerAngles = new Vec3Class(e.x, e.y, Number(v)); },
            lookAt(x, y, z) { t.lookAt(new Vec3Class(Number(x), Number(y), Number(z))); },
        };
    }

    // ---- Input ---------------------------------------------------------------
    // isPressed/isHeld/isReleased/getAxis/mouseX/mouseY are the documented set.
    // Everything after them is an addition the templates depend on.
    const inputProxy = {
        isPressed(action) { return input()?.isPressed(action) ?? false; },
        isHeld(action) { return input()?.isHeld(action) ?? false; },
        isReleased(action) { return input()?.isReleased(action) ?? false; },
        getAxis(action) { return input()?.getAxis(action) ?? 0; },
        get mouseX() { return input()?.mousePosition.x ?? 0; },
        get mouseY() { return input()?.mousePosition.y ?? 0; },

        // Additions:
        isKeyDown(key) { return input()?.isKeyDown(key) ?? false; },
        isKeyHeld(key) { return input()?.isKeyDown(key) ?? false; },
        isKeyPressed(key) { return input()?.isKeyPressed(key) ?? false; },
        isKeyReleased(key) { return input()?.isKeyReleased(key) ?? false; },
        isMouseDown(button) { return input()?.isMouseButtonDown(button) ?? false; },
        isMouseHeld(button) { return input()?.isMouseButtonDown(button) ?? false; },
        isMousePressed(button) { return input()?.isMouseButtonPressed(button) ?? false; },
        isMouseReleased(button) { return input()?.isMouseButtonReleased(button) ?? false; },
        get mouseDeltaX() { return input()?.mouseDelta.x ?? 0; },
        get mouseDeltaY() { return input()?.mouseDelta.y ?? 0; },
        get scrollDelta() { return input()?.scrollDelta ?? 0; },
        get touchCount() { return input()?.touch.touchCount ?? 0; },
        getTouch(index) {
            const touch = input()?.touch.touches[index];
            if (!touch) return null;
            return { id: touch.id, x: touch.position.x, y: touch.position.y, phase: touch.phase };
        },
        get joystickX() { return input()?.touch.leftJoystick.value.x ?? 0; },
        get joystickY() { return input()?.touch.leftJoystick.value.y ?? 0; },
    };

    // ---- Audio ---------------------------------------------------------------
    const audioProxy = {
        play(path, loop = false) {
            const handle = audioManager()?.play(path, { loop });
            return { id: handle?.id ?? 0 };
        },
        playOneShot(path, volume = 1) { audioManager()?.playOneShot(path, { volume }); },
        stop(handle) {
            const id = typeof handle === 'number' ? handle : handle?.id;
            if (id) audioManager()?.stopById(id);
        },
        setVolume(volume) { audioManager()?.setMasterVolume(volume); },   // addition
    };

    // ---- Scene ---------------------------------------------------------------
    const sceneProxy = {
        find(name) { return wrapActor(actor.scene?.findByName(name)); },

        /** An array, as the C# bridge returns. An empty one is still truthy. */
        findByTag(tag) { return (actor.scene?.findByTag(tag) ?? []).map(wrapActor); },

        /** Addition: the single-actor form the bundled scripts actually want. */
        findFirstByTag(tag) { return wrapActor((actor.scene?.findByTag(tag) ?? [])[0]); },

        instantiate(prefabPath, x = 0, y = 0) {
            const spawned = engine()?.instantiate?.(prefabPath, new Vec2Class(x, y));
            return wrapActor(spawned);
        },

        load(sceneName) { engine()?.sceneManager?.loadScene(sceneName); },

        get name() { return actor.scene?.name ?? ''; },   // addition
    };

    // ---- Debug ---------------------------------------------------------------
    const debugProxy = {
        log: (...args) => log('log', args.map(stringify).join(' ')),
        warn: (...args) => log('warn', args.map(stringify).join(' ')),
        error: (...args) => log('error', args.map(stringify).join(' ')),
    };

    // ---- Vector2 -------------------------------------------------------------
    // Static helpers over plain {x, y} objects, exactly as the C# bridge has them.
    const vector2Proxy = {
        create: (x, y) => ({ x, y }),
        add: (a, b) => ({ x: a.x + b.x, y: a.y + b.y }),
        sub: (a, b) => ({ x: a.x - b.x, y: a.y - b.y }),
        scale: (v, s) => ({ x: v.x * s, y: v.y * s }),
        normalize: (v) => {
            const length = Math.hypot(v.x, v.y);
            return length < 1e-10 ? { x: 0, y: 0 } : { x: v.x / length, y: v.y / length };
        },
        dot: (a, b) => a.x * b.x + a.y * b.y,
        distance: (a, b) => Math.hypot(b.x - a.x, b.y - a.y),
        length: (v) => Math.hypot(v.x, v.y),
    };

    // ---- Physics (an addition; README documents it, the C# bridge lacks it) ---
    const physicsProxy = {
        raycast(originX, originY, dirX, dirY, maxDistance = Infinity) {
            const hit = actor.scene?.physics2D?.raycast(
                new Vec2Class(originX, originY), new Vec2Class(dirX, dirY), maxDistance);
            if (!hit) return null;
            return {
                actor: wrapActor(hit.actor),
                x: hit.point.x, y: hit.point.y,
                normalX: hit.normal.x, normalY: hit.normal.y,
                distance: hit.distance,
            };
        },
        overlapCircle(x, y, radius) {
            return (actor.scene?.physics2D?.overlapCircle(new Vec2Class(x, y), radius) ?? [])
                .map((c) => wrapActor(c.actor));
        },
        overlapBox(x, y, width, height) {
            return (actor.scene?.physics2D
                ?.overlapBox(new Vec2Class(x, y), new Vec2Class(width, height)) ?? [])
                .map((c) => wrapActor(c.actor));
        },
    };

    // ---- Time (an addition) --------------------------------------------------
    const timeProxy = {
        get deltaTime() { return Time.deltaTime; },
        get unscaledDeltaTime() { return Time.unscaledDeltaTime; },
        get time() { return Time.timeSinceStartup; },
        get frameCount() { return Time.frameCount; },
        get fps() { return Time.fps; },
        get timeScale() { return Time.timeScale; },
        set timeScale(value) { Time.timeScale = Number(value); },
    };

    return {
        actor: actorProxy,
        transform: transformProxy,
        get transform3d() { return transform3DProxy(); },
        Input: inputProxy,
        Audio: audioProxy,
        Scene: sceneProxy,
        Debug: debugProxy,
        Vector2: vector2Proxy,
        Physics: physicsProxy,
        Time: timeProxy,

        // Bare logging functions. The templates call `log(...)` with no namespace.
        log: debugProxy.log,
        warn: debugProxy.warn,
        error: debugProxy.error,
    };
}

/**
 * The proxy shape the C# bridge hands back for a *found* actor.
 *
 * Note the asymmetry, which is part of the contract: this has a `transform`
 * sub-object, whereas the `actor` global in C# does not. Scripts written for the
 * C# engine rely on it.
 */
export function wrapActor(target) {
    if (!target || target.isDestroyed) return null;

    return {
        get name() { return target.name; },
        set name(value) { target.name = String(value); },
        get tag() { return target.tag; },
        set tag(value) { target.tag = String(value); },
        get active() { return target.isActive; },
        set active(value) { target.isActive = Boolean(value); },
        get id() { return target.id; },
        transform: {
            get x() { return target.transform.position.x; },
            set x(value) { target.transform.x = Number(value); },
            get y() { return target.transform.position.y; },
            set y(value) { target.transform.y = Number(value); },
            get rotation() { return target.transform.rotation; },
            set rotation(value) { target.transform.rotation = Number(value); },
        },
        getComponent(name) { return wrapComponent(target.getComponent(name)); },
        destroy() { target.destroy(); },
    };
}

/**
 * Exposes a component's schema-declared properties to a script.
 *
 * Only declared properties are reachable, which keeps a script from reaching
 * into engine internals — the same intent as Jint's "no CLR access by default".
 */
export function wrapComponent(component) {
    if (!component) return null;

    // The velocity shorthands the bundled scripts use live on Rigidbody2D itself,
    // so a plain property forward covers them along with everything else.
    return new Proxy(component, {
        get(target, key) {
            const value = target[key];
            return typeof value === 'function' ? value.bind(target) : value;
        },
        set(target, key, value) {
            target[key] = value;
            return true;
        },
        has(target, key) { return key in target; },
    });
}

function stringify(value) {
    if (typeof value === 'string') return value;
    if (value === null) return 'null';
    if (value === undefined) return 'undefined';
    if (typeof value === 'object') {
        try { return JSON.stringify(value); } catch { return String(value); }
    }
    return String(value);
}

/** The lifecycle functions a script may define. Nothing else is ever called. */
export const SCRIPT_HOOKS = Object.freeze([
    'onAwake', 'onStart', 'onUpdate', 'onFixedUpdate', 'onLateUpdate', 'onDestroy',
    'onCollisionEnter', 'onCollisionStay', 'onCollisionExit',
    'onTriggerEnter', 'onTriggerStay', 'onTriggerExit',
]);
