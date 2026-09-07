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
import { NetworkManager as NetworkManagerClass } from '../net/NetworkManager.js';
import { DarksGames as DarksGamesClass } from '../dg/DarksGames.js';

/** The events `Network.on` accepts. The Jint bridge accepts exactly these. */
const NETWORK_EVENTS = ['message', 'playerJoined', 'playerLeft', 'connected', 'disconnected'];

/**
 * Releases everything a script's globals still hold — its network handlers and its
 * UI elements. Keyed by a symbol so it is not a global a script can see.
 */
export const DISPOSE = Symbol('sb.script.dispose');

/** The events `DG.on` accepts. The Jint bridge accepts exactly these. */
const DG_EVENTS = ['user', 'save', 'saveConflict', 'achievement'];

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

        // ---- hierarchy -------------------------------------------------------
        get parent() { return wrapActor(actor.parent); },
        get children() { return actor.children.map(wrapActor); },
        // `keep` defaults to true: the common case is "hold this pickup where it is
        // and make it follow the player", not "snap it to the player's origin".
        attachTo(other, keep = true) {
            actor.attachTo(unwrapActor(other), keep !== false);
        },
        detach(keep = true) { actor.attachTo(null, keep !== false); },
        findChild(name, recursive = false) {
            return wrapActor(actor.findChild(String(name), Boolean(recursive)));
        },
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
    const transform3DProxy = makeTransform3DAccessor(() => actor.transform3D);

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

    // ---- Network -------------------------------------------------------------
    //
    // Member for member the same proxy the Jint bridge installs, over the same
    // wire. A multiplayer script running with no session is the single-player
    // case, not an error: `ensureNet()` starts a loopback session rather than
    // warning and doing nothing, so the same script drives the same code path
    // alone as it does in a lobby.

    const net = () => NetworkManagerClass.instance;

    const ensureNet = () => {
        const running = NetworkManagerClass.instance;
        if (running?.isRunning) return running;

        const manager = running ?? new NetworkManagerClass();
        if (!manager.isRunning) manager.startSolo();
        return manager;
    };

    /** Handlers this script registered, so they go away with the script. */
    const networkUnsubscribes = [];

    const networkProxy = {
        // Player 0 is the local player before a server has said otherwise, so a lobby
        // script that asks `Network.isLocalPlayer(id)` behaves as a one-player game
        // rather than a dead one.
        get localId() { const id = net()?.localClientId ?? -1; return id >= 0 ? id : 0; },
        get isServer() { return net()?.isServer ?? false; },
        get isHost() { return net()?.isHost ?? false; },
        get isConnected() { return net()?.isConnected ?? false; },
        get ping() { return net()?.ping ?? 0; },
        get room() { return net()?.room ?? ''; },

        get playerName() { return net()?.playerName ?? 'Player'; },
        set playerName(value) {
            const manager = NetworkManagerClass.instance ?? new NetworkManagerClass();
            manager.playerName = String(value);
        },

        // A fresh array each read, so a script cannot mutate the engine's roster.
        get players() {
            return [...(net()?.players ?? new Map())].map(([id, name]) => ({ id, name }));
        },

        isLocalPlayer(id) {
            const local = net()?.localClientId ?? -1;
            return Number(id) === (local >= 0 ? local : 0);
        },

        startServer(port = 7777) {
            try {
                const manager = NetworkManagerClass.instance ?? new NetworkManagerClass();
                if (manager.isRunning) return true;
                manager.startServer(port);
                return true;
            } catch (err) {
                log('warn', `Network.startServer: ${err.message}`);
                return false;
            }
        },

        startSolo() {
            try { ensureNet(); return true; } catch (err) {
                log('warn', `Network.startSolo: ${err.message}`);
                return false;
            }
        },

        connect(address, port) {
            try {
                const manager = NetworkManagerClass.instance ?? new NetworkManagerClass();
                if (manager.isRunning) manager.disconnect();

                const target = String(address ?? '');
                if (target.startsWith('ws://') || target.startsWith('wss://')) {
                    manager.connectToUrl(target, { room: typeof port === 'string' ? port : '' });
                } else {
                    manager.connect(target, Number(port) || 7777);
                }
                return true;
            } catch (err) {
                log('warn', `Network.connect: ${err.message}`);
                return false;
            }
        },

        disconnect() { net()?.disconnect(); },

        sendToAll(type, data) { ensureNet().sendMessageToAll(String(type), data ?? null); },
        broadcast(type, data) { ensureNet().sendMessageToAll(String(type), data ?? null); },
        sendTo(clientId, type, data) {
            ensureNet().sendMessageTo(Number(clientId), String(type), data ?? null);
        },

        on(event, handler) {
            const name = String(event);
            if (typeof handler !== 'function') {
                log('warn', `Network.on('${name}'): the second argument must be a function.`);
                return () => {};
            }
            if (!NETWORK_EVENTS.includes(name)) {
                log('warn', `Network.on: unknown event '${name}'. Try ${NETWORK_EVENTS.join(', ')}.`);
                return () => {};
            }

            const unsubscribe = ensureNet().on(name, handler);
            networkUnsubscribes.push(unsubscribe);
            return () => {
                unsubscribe();
                const i = networkUnsubscribes.indexOf(unsubscribe);
                if (i >= 0) networkUnsubscribes.splice(i, 1);
            };
        },
    };

    // ---- DG — the Darks Games account and social layer ------------------------
    //
    // Member for member the same proxy the Jint bridge installs. Every read is
    // safe signed out and safe with no runtime at all, so a game that never ships
    // to DarksGames still runs every line of a script that uses this.

    const dg = () => DarksGamesClass.instance;

    /** Handlers this script registered with the DG runtime. */
    const dgUnsubscribes = [];

    const dgProxy = {
        get available() { return dg() != null; },
        get signedIn() { return dg()?.signedIn ?? false; },
        get userId() { return dg()?.user?.id ?? null; },
        get userName() { return dg()?.user?.name ?? null; },
        get handle() { return dg()?.user?.handle ?? null; },
        get displayName() { return dg()?.user?.displayName ?? 'Player'; },
        get game() { return dg()?.game ?? ''; },

        presence(fields) {
            const runtime = dg();
            if (!runtime) return;
            if (typeof fields !== 'object' || fields === null) {
                log('warn', "DG.presence: pass an object, e.g. { state: 'lobby', joinCode: room }.");
                return;
            }
            runtime.presence(fields);
        },

        clearPresence() { dg()?.clearPresence(); },

        achievement(key, increment) {
            dg()?.reportAchievement(String(key), increment == null ? undefined : Number(increment));
        },

        // The save arrives on the "save" event, not as a return value: the Jint bridge
        // cannot await, and one contract has to describe both engines.
        loadSave() { dg()?.requestSave(); },

        saveCloud(data, version = 1) {
            dg()?.writeSave(data ?? null, { version: Number(version) || 1 });
        },

        on(event, handler) {
            const name = String(event);
            const runtime = dg();
            if (!runtime) return () => {};
            if (typeof handler !== 'function') {
                log('warn', `DG.on('${name}'): the second argument must be a function.`);
                return () => {};
            }
            if (!DG_EVENTS.includes(name)) {
                log('warn', `DG.on: unknown event '${name}'. Try ${DG_EVENTS.join(', ')}.`);
                return () => {};
            }

            // `user` reports the same three arguments the Jint bridge reports, rather than
            // the user object: a script written against one engine has to read on the other.
            const wrapped = name === 'user'
                ? (user) => handler(Boolean(user?.signedIn), user?.id ?? null, user?.displayName ?? 'Player')
                : handler;

            const unsubscribe = runtime.on(name, wrapped);
            dgUnsubscribes.push(unsubscribe);
            return () => {
                unsubscribe();
                const i = dgUnsubscribes.indexOf(unsubscribe);
                if (i >= 0) dgUnsubscribes.splice(i, 1);
            };
        },
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

    const globals = {
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
        DG: dgProxy,

        // Bare logging functions. The templates call `log(...)` with no namespace.
        log: debugProxy.log,
        warn: debugProxy.warn,
        error: debugProxy.error,
    };

    // Teardown hangs off a symbol, not a name: the parity test compares this object's
    // own property names against the Jint bridge's globals, and a `__dispose` key would
    // be a global a script could see and the other engine does not have.
    Object.defineProperty(globals, DISPOSE, {
        value: () => {
            for (const unsubscribe of networkUnsubscribes) unsubscribe();
            networkUnsubscribes.length = 0;
            for (const unsubscribe of dgUnsubscribes) unsubscribe();
            dgUnsubscribes.length = 0;
            uiProxy.clear();
        },
    });

    return globals;
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

/**
 * Builds the `transform3d` accessor a script sees: x/y/z, euler rotation, scale
 * and `lookAt`, or null until the actor has a Transform3D.
 *
 * One factory rather than two copies, because the script's own actor and an
 * actor it created must expose exactly the same thing -- they did not, and the
 * difference was invisible until a script tried to build a world.
 *
 * @param {() => object|null} read Fetches the live Transform3D each time.
 */
function makeTransform3DAccessor(read) {
    let cached = null;
    let proxy = null;

    return function transform3d() {
        const t = read();
        if (!t) return null;
        if (t !== cached) {
            cached = t;
            proxy = {
                get x() { return t.position.x; }, set x(v) { t.x = Number(v); },
                get y() { return t.position.y; }, set y(v) { t.y = Number(v); },
                get z() { return t.position.z; }, set z(v) { t.z = Number(v); },
                get rotX() { return t.eulerAngles.x; },
                set rotX(v) { const e = t.eulerAngles; t.eulerAngles = new Vec3Class(Number(v), e.y, e.z); },
                get rotY() { return t.eulerAngles.y; },
                set rotY(v) { const e = t.eulerAngles; t.eulerAngles = new Vec3Class(e.x, Number(v), e.z); },
                get rotZ() { return t.eulerAngles.z; },
                set rotZ(v) { const e = t.eulerAngles; t.eulerAngles = new Vec3Class(e.x, e.y, Number(v)); },

                // Scale, which the 2D transform has always had and this one did
                // not. Every mesh in the contract is a UNIT primitive, so without
                // this a script could place a cube but never make a wall of one.
                get scaleX() { return t.localScale.x; },
                set scaleX(v) { const c = t.localScale; t.localScale = new Vec3Class(Number(v), c.y, c.z); },
                get scaleY() { return t.localScale.y; },
                set scaleY(v) { const c = t.localScale; t.localScale = new Vec3Class(c.x, Number(v), c.z); },
                get scaleZ() { return t.localScale.z; },
                set scaleZ(v) { const c = t.localScale; t.localScale = new Vec3Class(c.x, c.y, Number(v)); },

                lookAt(x, y, z) { t.lookAt(new Vec3Class(Number(x), Number(y), Number(z))); },

                /** Position and scale at once — one matrix rebuild, not six. */
                set(x, y, z, sx = 1, sy = 1, sz = 1) {
                    t.position = new Vec3Class(Number(x), Number(y), Number(z));
                    t.localScale = new Vec3Class(Number(sx), Number(sy), Number(sz));
                },
            };
        }
        return proxy;
    };
}

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
        get transform3d() { return read3D(); },
        getComponent(name) { return wrapComponent(target.getComponent(name)); },
        destroy() { target.destroy(); },
    };
    const read3D = makeTransform3DAccessor(() => target.transform3D);
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
