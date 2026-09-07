// -----------------------------------------------------------------------------
// ScriptBridge — the globals a project `.js` script sees.
//
// This is the shared scripting contract: the C# engine's Jint bridge
// (`SexyBiscuit.Engine/Scripting/ScriptBridge.cs`) installs the same globals
// with the same members and the same shapes, and `bridge-api.json` beside this
// file lists every one of them. A test on each side holds its bridge to that
// list, so a script that runs here runs under Jint unchanged, and the other way
// round. Add to both sides or neither.
// -----------------------------------------------------------------------------

import { Vector2 as Vec2Class, Vector3 as Vec3Class } from '../math/index.js';
import { Time } from '../core/Time.js';
import { Actor } from '../core/Actor.js';
import { schemaOf } from '../core/TypeRegistry.js';
import { coerce } from '../core/PropertyTypes.js';
import { applyProperties } from '../scene/SceneSerializer.js';
import { widestLine } from '../ui/UiCanvas.js';

/** Proxies handed to scripts, mapped back to the actor each stands for. */
const proxyToActor = new WeakMap();

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
    const scene = () => actor.scene ?? null;

    // ---- actor ---------------------------------------------------------------
    const actorProxy = {
        get id() { return actor.id; },
        get name() { return actor.name; },
        set name(value) { actor.name = String(value); },
        get tag() { return actor.tag; },
        set tag(value) { actor.tag = String(value); },
        get active() { return actor.isActive; },
        set active(value) { actor.isActive = Boolean(value); },
        get transform() { return transformProxy; },
        get transform3d() { return transform3DProxy(); },
        getComponent(name) { return wrapComponent(actor.getComponent(name)); },
        addComponent(name) { return wrapComponent(actor.addComponent(name)); },
        destroy() { actor.destroy(); },
    };
    proxyToActor.set(actorProxy, actor);

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

    // ---- transform3d — null until the actor has a Transform3D -----------------
    let cachedTransform3D = null;
    let cachedTransform3DProxy = null;
    function transform3DProxy() {
        const t = actor.transform3D;
        if (!t) return null;
        if (t !== cachedTransform3D) {
            cachedTransform3D = t;
            cachedTransform3DProxy = {
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
        return cachedTransform3DProxy;
    }

    // ---- Input ---------------------------------------------------------------
    const inputProxy = {
        isPressed(action) { return input()?.isPressed(action) ?? false; },
        isHeld(action) { return input()?.isHeld(action) ?? false; },
        isReleased(action) { return input()?.isReleased(action) ?? false; },
        getAxis(action) { return input()?.getAxis(action) ?? 0; },
        get mouseX() { return input()?.mousePosition.x ?? 0; },
        get mouseY() { return input()?.mousePosition.y ?? 0; },
        get mouseDeltaX() { return input()?.mouseDelta.x ?? 0; },
        get mouseDeltaY() { return input()?.mouseDelta.y ?? 0; },
        get scrollDelta() { return input()?.scrollDelta ?? 0; },

        isKeyDown(key) { return input()?.isKeyDown(key) ?? false; },
        isKeyHeld(key) { return input()?.isKeyDown(key) ?? false; },
        isKeyPressed(key) { return input()?.isKeyPressed(key) ?? false; },
        isKeyReleased(key) { return input()?.isKeyReleased(key) ?? false; },

        isMouseDown(button) { return input()?.isMouseButtonDown(button) ?? false; },
        isMouseHeld(button) { return input()?.isMouseButtonDown(button) ?? false; },
        isMousePressed(button) { return input()?.isMouseButtonPressed(button) ?? false; },
        isMouseReleased(button) { return input()?.isMouseButtonReleased(button) ?? false; },

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
        /** Returns a handle whose id is 0 when nothing could play; never null. */
        play(path, loop = false) {
            const handle = audioManager()?.play(path, { loop });
            return { id: handle?.id ?? 0 };
        },
        playOneShot(path, volume = 1) { audioManager()?.playOneShot(path, { volume }); },
        stop(handle) {
            const id = typeof handle === 'number' ? handle : handle?.id;
            if (id) audioManager()?.stopById(id);
        },
        setVolume(volume) { audioManager()?.setMasterVolume(volume); },
    };

    // ---- Scene ---------------------------------------------------------------
    const sceneProxy = {
        get name() { return scene()?.name ?? ''; },

        find(name) { return wrapActor(scene()?.findByName(name)); },

        /** Every actor with the name; the bundled scripts use it for a batch of enemies. */
        findAll(name) {
            return (scene()?.allActors ?? [])
                .filter((a) => a.name === name && !a.isDestroyed)
                .map(wrapActor);
        },

        /** An array, as the C# bridge returns. An empty one is still truthy. */
        findByTag(tag) { return (scene()?.findByTag(tag) ?? []).map(wrapActor); },

        /** The single-actor form the bundled scripts actually want. */
        findFirstByTag(tag) { return wrapActor((scene()?.findByTag(tag) ?? [])[0]); },

        createActor(name = 'Actor', x = 0, y = 0) {
            const target = scene();
            if (!target) return null;
            const created = new Actor(String(name));
            created.transform.x = Number(x);
            created.transform.y = Number(y);
            target.addActor(created);
            return wrapActor(created);
        },

        addComponent(actorLike, typeName, properties) {
            const target = unwrapActor(actorLike);
            if (!target) {
                log('warn', 'Scene.addComponent: the first argument is not an actor.');
                return null;
            }
            let component;
            try {
                component = target.addComponent(typeName);
            } catch (err) {
                log('warn', err.message);
                return null;
            }
            if (properties && typeof properties === 'object') {
                applyProperties(component, properties, target.name, (message) => log('warn', message));
            }
            return wrapComponent(component);
        },

        destroy(actorLike) {
            const target = unwrapActor(actorLike);
            if (!target) log('warn', 'Scene.destroy: the argument is not an actor.');
            else target.destroy();
        },
        destroyActor(actorLike) { sceneProxy.destroy(actorLike); },

        instantiate(prefabPath, x = 0, y = 0) {
            const spawned = engine()?.instantiate?.(prefabPath, new Vec2Class(x, y));
            return wrapActor(spawned);
        },

        load(sceneName) { engine()?.sceneManager?.loadScene(sceneName); },
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

    // ---- Physics -------------------------------------------------------------
    const physicsProxy = {
        raycast(originX, originY, dirX, dirY, maxDistance = Infinity) {
            const hit = scene()?.physics2D?.raycast(
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
            return (scene()?.physics2D?.overlapCircle(new Vec2Class(x, y), radius) ?? [])
                .map((c) => wrapActor(c.actor));
        },
        overlapBox(x, y, width, height) {
            return (scene()?.physics2D
                ?.overlapBox(new Vec2Class(x, y), new Vec2Class(width, height)) ?? [])
                .map((c) => wrapActor(c.actor));
        },
    };

    // ---- Time ----------------------------------------------------------------
    const timeProxy = {
        get deltaTime() { return Time.deltaTime; },
        get unscaledDeltaTime() { return Time.unscaledDeltaTime; },
        get time() { return Time.timeSinceStartup; },
        get frameCount() { return Time.frameCount; },
        get fps() { return Time.fps; },
        get timeScale() { return Time.timeScale; },
        set timeScale(value) { Time.timeScale = Number(value); },
    };

    // ---- Network — a stub that lets a multiplayer script run solo -------------
    // Player 0 is the local player when there is no network, so a lobby script that asks
    // `Network.isLocalPlayer(id)` behaves as a one-player game rather than a dead one.
    let networkWarned = false;
    const unavailable = () => {
        if (!networkWarned) {
            networkWarned = true;
            log('warn', 'Network is not available in this build; the script is running solo.');
        }
        return false;
    };
    const networkProxy = {
        get localId() { return 0; },
        get isServer() { return false; },
        get isConnected() { return false; },
        isLocalPlayer(id) { return Number(id) === 0; },
        startServer() { return unavailable(); },
        connect() { return unavailable(); },
        sendToAll() {},
        broadcast() {},
    };

    // ---- UI — screen space, which the world-space globals above cannot reach ---
    //
    // Every element the script makes is owned by the host's canvas, and every
    // element it makes is remembered here so `UI.clear()` and a scene change can
    // take them away again. A script that leaks a label into the next scene is
    // the failure this avoids.
    const owned = new Set();

    const wrapElement = (element) => {
        if (!element) return null;
        owned.add(element);

        const proxy = {
            get x() { return element.x; },                 set x(v) { element.x = Number(v); },
            get y() { return element.y; },                 set y(v) { element.y = Number(v); },
            get width() { return element.width; },         set width(v) { element.width = Number(v); },
            get height() { return element.height; },       set height(v) { element.height = Number(v); },
            get text() { return element.text; },           set text(v) { element.text = String(v ?? ''); },
            get value() { return element.value; },         set value(v) { element.value = Number(v); },
            get visible() { return element.visible; },     set visible(v) { element.visible = Boolean(v); },
            get tint() { return element.tint; },           set tint(v) { element.tint = toCss(v); },
            get background() { return element.background; }, set background(v) { element.background = v == null ? null : toCss(v); },
            get scale() { return element.scale; },         set scale(v) { element.scale = Number(v); },
            get anchor() { return element.anchor; },       set anchor(v) { element.anchor = String(v); },
            get align() { return element.align; },         set align(v) { element.align = String(v); },
            get padding() { return element.padding; },     set padding(v) { element.padding = Number(v); },
            get texturePath() { return element.texturePath; }, set texturePath(v) { element.texturePath = String(v ?? ''); },
            get hovered() { return element.hovered; },
            get clicked() { return element.clicked; },
            destroy() { owned.delete(element); canvas()?.remove(element); },
        };
        return proxy;
    };

    const canvas = () => engine()?.ui ?? null;

    const make = (kind, options) => {
        const target = canvas();
        if (!target) { log('warn', `UI.${kind}: there is no UI canvas in this host.`); return null; }
        return wrapElement(target.add(kind, options));
    };

    const uiProxy = {
        get width() { return canvas()?.width ?? 0; },
        get height() { return canvas()?.height ?? 0; },

        panel(x, y, width, height, options) {
            return make('panel', { ...options, x: Number(x), y: Number(y), width: Number(width), height: Number(height) });
        },
        label(x, y, text, options) {
            return make('label', { ...options, x: Number(x), y: Number(y), text: String(text ?? ''), width: 0, height: 0 });
        },
        bar(x, y, width, height, value, options) {
            return make('bar', { ...options, x: Number(x), y: Number(y), width: Number(width), height: Number(height), value: Number(value) });
        },
        button(x, y, width, height, text, options) {
            return make('button', { ...options, x: Number(x), y: Number(y), width: Number(width), height: Number(height), text: String(text ?? '') });
        },
        image(x, y, width, height, path, options) {
            return make('image', { ...options, x: Number(x), y: Number(y), width: Number(width), height: Number(height), texturePath: String(path ?? '') });
        },

        /** Only this script's elements, so one script cannot wipe another's HUD. */
        clear() {
            const target = canvas();
            for (const element of owned) target?.remove(element);
            owned.clear();
        },

        /** The width one line of text will occupy, for laying a panel out around it. */
        measure(text, scale) { return measureText(String(text ?? ''), Number(scale) || 1); },
    };

    return {
        actor: actorProxy,
        UI: uiProxy,
        transform: transformProxy,
        get transform3d() { return transform3DProxy(); },
        Input: inputProxy,
        Audio: audioProxy,
        Scene: sceneProxy,
        Debug: debugProxy,
        Vector2: vector2Proxy,
        Physics: physicsProxy,
        Time: timeProxy,
        Network: networkProxy,

        // Bare logging functions. The templates call `log(...)` with no namespace.
        log: debugProxy.log,
        warn: debugProxy.warn,
        error: debugProxy.error,
    };
}

/**
 * The proxy shape a script gets for a *found* actor: from `Scene.find`, from a
 * collision, from `Scene.createActor`. Returns null for a missing or destroyed actor.
 */
/**
 * A colour a script wrote, as CSS.
 *
 * Scene files and component properties already accept `"#ff8040"`, `[r, g, b]`
 * and `{R, G, B, A}`, so UI takes the same forms rather than inventing a
 * twelfth spelling of the colour red.
 */
function toCss(value) {
    if (value == null) return null;
    if (typeof value === 'string') return value;

    const channel = (v) => Math.max(0, Math.min(255, Math.round(Number(v) || 0)));
    if (Array.isArray(value)) {
        const [r, g, b, a] = value;
        return a == null ? `rgb(${channel(r)},${channel(g)},${channel(b)})`
                         : `rgba(${channel(r)},${channel(g)},${channel(b)},${Number(a) > 1 ? Number(a) / 255 : Number(a)})`;
    }
    if (typeof value === 'object') {
        const r = value.R ?? value.r, g = value.G ?? value.g, b = value.B ?? value.b;
        const a = value.A ?? value.a;
        if (r === undefined) return String(value);
        return a === undefined ? `rgb(${channel(r)},${channel(g)},${channel(b)})`
                               : `rgba(${channel(r)},${channel(g)},${channel(b)},${Number(a) > 1 ? Number(a) / 255 : Number(a)})`;
    }
    return String(value);
}

/** The width of the widest line, which is what a caller laying out a panel needs. */
function measureText(text, scale) { return widestLine(text, scale); }

export function wrapActor(target) {
    if (!target || target.isDestroyed) return null;

    const proxy = {
        get id() { return target.id; },
        get name() { return target.name; },
        set name(value) { target.name = String(value); },
        get tag() { return target.tag; },
        set tag(value) { target.tag = String(value); },
        get active() { return target.isActive; },
        set active(value) { target.isActive = Boolean(value); },
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
    proxyToActor.set(proxy, target);
    return proxy;
}

/**
 * The object a flat-function collision hook receives:
 * `{ other, contactPoint: {x, y}, normal: {x, y}, relativeVelocity, tag, name, getComponent() }`.
 *
 * `other` is a wrapped actor, as it is under Jint, and `tag`, `name` and
 * `getComponent` forward to it — the bundled scripts name the parameter `other`
 * and write `other.tag` and `other.getComponent("ScriptComponent")`.
 */
export function wrapCollisionData(data) {
    const other = wrapActor(data?.other);
    const point = data?.contactPoint;
    const normal = data?.normal;
    return {
        other,
        contactPoint: { x: point?.x ?? 0, y: point?.y ?? 0 },
        normal: { x: normal?.x ?? 0, y: normal?.y ?? 0 },
        relativeVelocity: data?.relativeVelocity ?? 0,
        get tag() { return other?.tag; },
        get name() { return other?.name; },
        getComponent(name) { return other ? other.getComponent(name) : null; },
    };
}

/** The actor behind a script-facing proxy (or a raw actor), else null. */
export function unwrapActor(value) {
    if (value instanceof Actor) return value;
    return (value && proxyToActor.get(value)) ?? null;
}

/**
 * Exposes a component's declared properties to a script.
 *
 * Only declared properties are reachable, which keeps a script from reaching
 * into engine internals — the same intent as Jint's "no CLR access by default".
 * A member is found under its camelCase name or its C# PascalCase name
 * (`rb.gravityScale` and `rb.GravityScale` are one property), and a value
 * assigned in a file-friendly form — `"#FF0000"`, `[1, 2]` — is coerced the way
 * a scene file's would be.
 */
export function wrapComponent(component) {
    if (!component) return null;

    const schema = schemaOf(component.constructor) ?? {};

    const resolveKey = (target, key) => {
        if (typeof key !== 'string' || key in target) return key;
        const lower = lowerFirst(key);
        return lower in target ? lower : key;
    };

    return new Proxy(component, {
        get(target, key) {
            const resolved = resolveKey(target, key);
            const value = target[resolved];
            return typeof value === 'function' ? value.bind(target) : value;
        },
        set(target, key, value) {
            const resolved = resolveKey(target, key);
            const descriptor = schema[resolved];
            target[resolved] = descriptor && isPlainValue(value) ? coerce(value, descriptor) : value;
            return true;
        },
        has(target, key) { return resolveKey(target, key) in target; },
    });
}

function isPlainValue(value) {
    return typeof value === 'string'
        || Array.isArray(value)
        || (value !== null && typeof value === 'object' && value.constructor === Object);
}

function lowerFirst(name) {
    return name.length > 0 && name[0] !== name[0].toLowerCase()
        ? name[0].toLowerCase() + name.slice(1)
        : name;
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

/** The lifecycle functions a script may define. Nothing else is ever called by the engine. */
export const SCRIPT_HOOKS = Object.freeze([
    'onAwake', 'onStart', 'onUpdate', 'onFixedUpdate', 'onLateUpdate', 'onDestroy',
    'onCollisionEnter', 'onCollisionStay', 'onCollisionExit',
    'onTriggerEnter', 'onTriggerStay', 'onTriggerExit',
]);
