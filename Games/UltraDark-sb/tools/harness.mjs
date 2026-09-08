// harness.mjs -- boot the game headlessly, the way the real engine does.
// Shared by soak.mjs and checks.mjs so both run against the same host.
//
// Three things the bundled smoke test does not give a scene, each of which
// silently removes a whole system here:
//
//   physics2D   without it Physics.overlapCircle returns nothing, so no shot
//               can ever hit anything and the game still "runs"
//   assets      without it a ScriptComponent attached at runtime never gets
//               its source, so the boss is an actor with no behaviour
//   input.touch without it Input.joystickX throws, because the bridge guards
//               only the first hop of input()?.touch.leftJoystick.value.x
//   ui          without it every UI.panel/label/bar/button returns null and
//               warns, so the HUD, the draft board and every screen effect are
//               absent -- and a game built on them looks fine until you run it
//   Time        the clock is a module singleton, and a harness that steps a
//               fixed dt ignores Time.timeScale entirely. Pause and hit-stop
//               both work by setting it, so a harness that does not honour it
//               reports that neither does anything

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const gameDir = path.resolve(here, '..');
export const repoRoot = path.resolve(gameDir, '../..');

const engine = await import(path.join(repoRoot, 'html5/src/index.js'));
export const { deserialize, ScriptComponent, PhysicsSystem2D, Vector2 } = engine;

// The UI is a retained tree now, and the host does not hand the scene a UI object
// at all: the bridge makes one UiCanvas per script on its first `UI` call and
// registers it in UiCanvas.all. So there is nothing to construct here -- there is
// a per-frame duty instead, and skipping it is silent. A canvas that is never
// laid out has every node at a zero rect, which reads as "the HUD is missing"
// rather than as "the harness forgot", and nothing hit-tests.
const { UiCanvas } = await import(path.join(repoRoot, 'html5/src/ui/UiCanvas.js'));
const { Time } = await import(path.join(repoRoot, 'html5/src/core/Time.js'));
const { DarksGames } = await import(path.join(repoRoot, 'html5/src/dg/DarksGames.js'));

export const DT = 1 / 60;

export const VIEWPORT = { width: 1280, height: 720 };

/**
 * A recording stand-in for the Darks Games runtime.
 *
 * The `DG` global is installed by the bridge whether or not a hub is reachable,
 * and reads it from `DarksGames.instance` -- so a null instance is the signed-out
 * case a real player has, and this is the signed-in one. Without it there is no
 * way to assert on what the game TOLD the hub, only that it did not crash, and
 * the interesting bugs here are all in what was sent: a counter reported as a
 * total instead of a difference, a save that replaces instead of merging.
 */
export function makeDgStub({ game = 'ultradark-sb', signedIn = true } = {}) {
    const handlers = new Map();
    const stub = {
        game,
        available: true,
        get signedIn() { return stub.user.signedIn; },
        user: {
            id: signedIn ? 'u_test' : null,
            name: signedIn ? 'Tester' : null,
            handle: signedIn ? 'Tester#0001' : null,
            get signedIn() { return this.id != null; },
            get displayName() { return this.name ?? this.handle ?? 'Player'; },
        },

        // What was said, in order, for a test to read back.
        presences: [],
        achievements: [],
        saves: [],
        saveRequests: 0,

        presence(fields) { stub.presences.push({ ...fields }); },
        clearPresence() { stub.presences.push(null); },
        reportAchievement(key, increment) { stub.achievements.push({ key, increment }); },
        requestSave() { stub.saveRequests++; },
        writeSave(data, options) { stub.saves.push({ data, options }); },
        on(event, handler) {
            if (!handlers.has(event)) handlers.set(event, []);
            handlers.get(event).push(handler);
            return () => {};
        },

        /** Delivers an event to whatever the game registered, as the runtime would. */
        emit(event, ...args) { for (const h of handlers.get(event) ?? []) h(...args); },

        /** Totals per key, which is what the hub is actually accumulating. */
        totalFor(key) {
            return stub.achievements
                .filter((a) => a.key === key)
                .reduce((sum, a) => sum + (a.increment ?? 1), 0);
        },
        countOf(key) { return stub.achievements.filter((a) => a.key === key).length; },
    };
    return stub;
}

/**
 * @param {object} [options]
 * @param {object|boolean} [options.dg] A Darks Games runtime to install for this
 *   boot; `true` builds a signed-in stub. Omitted means signed out with no hub,
 *   which is what every other test in this file runs as and what a player who
 *   never signs in gets.
 */
export function boot({ scene: sceneName = 'Scenes/Ultradark.scene', dg = null } = {}) {
    const held = new Set();
    const pressed = new Set();
    const mouseHeld = new Set();

    const input = {
        mousePosition: { x: 640, y: 360 },
        mouseDelta: { x: 0, y: 0 },
        scrollDelta: 0,
        touch: { touchCount: 0, touches: [], leftJoystick: { value: { x: 0, y: 0 } } },

        isPressed: () => false,
        isHeld: () => false,
        isReleased: () => false,
        getAxis: () => 0,

        isKeyDown: (k) => held.has(String(k)),
        isKeyPressed: (k) => pressed.has(String(k)),
        isKeyReleased: () => false,

        isMouseButtonDown: (b) => mouseHeld.has(Number(b)),
        isMouseButtonPressed: (b) => mouseHeld.has(Number(b)),
        isMouseButtonReleased: () => false,
    };

    const errors = [];
    const logs = [];
    const realError = console.error;
    const realWarn = console.warn;
    const realLog = console.log;

    console.error = (...a) => errors.push(a.join(' '));
    console.warn = (...a) => logs.push(a.join(' '));
    console.log = (...a) => logs.push(a.join(' '));

    // The clock is a module singleton shared by every boot in a process, so a
    // test that left timeScale at 0 would freeze the next one.
    Time.reset();

    // So is DarksGames.instance. It is restored in restore() for the same reason.
    const priorDg = DarksGames.instance;
    const dgRuntime = dg === true ? makeDgStub() : (dg || null);
    DarksGames.instance = dgRuntime;

    const scene = deserialize(fs.readFileSync(path.join(gameDir, sceneName), 'utf8'),
                              { onWarning: () => {} });

    // Canvases are process-wide, like Time and DarksGames.instance: one boot's
    // HUD would otherwise still be laid out and hit-tested during the next one.
    UiCanvas.clearAll();

    scene.engine = {
        input,
        audio: null,
        Time,
        assets: { loadText: async (p) => fs.readFileSync(path.join(gameDir, p), 'utf8') },
    };
    scene.physics2D = new PhysicsSystem2D({ scene, gravity: new Vector2(0, 0) });

    scene.flushPendingActors();

    for (const actor of scene.allActors) {
        for (const script of actor.getComponents(ScriptComponent)) {
            const file = path.join(gameDir, script.scriptPath);
            if (!fs.existsSync(file)) { errors.push(`missing script ${script.scriptPath}`); continue; }
            script.setSource(fs.readFileSync(file, 'utf8'));
        }
    }
    scene.flushPendingActors();

    const find = (name) => scene.allActors.find((a) => a.name === name && !a.isDestroyed);
    const byTag = (tag) => scene.allActors.filter((a) => a.tag === tag && !a.isDestroyed);
    const scriptOn = (name) => {
        const a = find(name);
        return a ? a.getComponents(ScriptComponent)[0] : null;
    };

    // Where the pointer is, and whether it is down, for UI hover and clicks.
    const pointer = { x: -1, y: -1, down: false };
    let lastPointer = { x: -1, y: -1 };

    // Per boot, not the module constant: the size sweep changes it.
    const viewport = { width: VIEWPORT.width, height: VIEWPORT.height };

    /**
     * What EngineHost._updateUiCanvases does: size every canvas, lay it out, then
     * feed it the frame's input.
     *
     * Layout before input, not after. Hit-testing reads the rectangles this pass
     * produces, so a canvas whose tree changed this frame -- a draft board that
     * just opened -- would otherwise be picked against last frame's shape, and a
     * card would be clickable where it used to be.
     */
    function pumpUi() {
        if (UiCanvas.all.length === 0) return;

        const frame = {
            deltaTime: DT,
            pointer: { x: pointer.x, y: pointer.y },
            pointerDelta: { x: pointer.x - lastPointer.x, y: pointer.y - lastPointer.y },
            pointerDown: pointer.down,
            pointerIsTouch: false,
            wheel: 0,
            navAxis: { x: 0, y: 0 },
            navUp: false, navDown: false, navLeft: false, navRight: false,
            confirm: false, cancel: false,
            typed: '', backspace: false,
        };
        lastPointer = { x: pointer.x, y: pointer.y };

        // A copy, because a script reacting to a click may destroy a canvas.
        for (const canvas of [...UiCanvas.all]) {
            canvas.setViewport(viewport.width, viewport.height);
            canvas.layout();
            canvas.input.update(frame);
        }
    }

    async function step(frames = 1) {
        for (let i = 0; i < frames; i++) {
            pumpUi();

            // Advance the real clock, then hand the scene the SCALED delta, the
            // same as the engine host does. A pause is timeScale 0, and a
            // harness that passes a fixed dt would step straight through it.
            Time.advance(DT);
            const dt = Time.deltaTime;

            scene.update(dt);
            scene.physics2D.fixedStep(dt);
            scene.fixedUpdate(dt);
            scene.lateUpdate(dt);
            scene.flushPendingActors();
            pressed.clear();
            // Let a runtime-attached script's loadText settle.
            if (i % 4 === 0) { await new Promise((r) => setImmediate(r)); }
        }
    }

    function restore() {
        Time.reset();          // timeScale is a module singleton; a paused test would leak it
        DarksGames.instance = priorDg;
        console.error = realError;
        console.warn = realWarn;
        console.log = realLog;
    }

    /**
     * Every node on every canvas, laid out, as a flat list.
     *
     * The flat UI handed out a single `elements` array; a tree has to be walked,
     * and every tool that used to read that array wants the same thing from it --
     * what is on screen, where, and what it says. `rect` is in CANVAS space and
     * `screen` is where it actually lands, which differ the moment a canvas is
     * not ConstantPixel; the overflow sweep has to judge the second.
     */
    function uiNodes({ visibleOnly = true } = {}) {
        const out = [];
        for (const canvas of UiCanvas.all) {
            for (const node of canvas.root.descendants()) {
                if (visibleOnly && !visibleInTree(node)) continue;
                out.push({
                    node,
                    canvas,
                    kind: String(node.kind).toLowerCase(),
                    name: node.name ?? '',
                    text: String(node.text ?? ''),
                    value: node.value,
                    visible: node.visible,
                    rect: node.rect,
                    screen: canvas.canvasRectToScreen(node.rect),
                });
            }
        }
        return out;
    }

    /** A node is only on screen if every ancestor is too. */
    function visibleInTree(node) {
        for (let n = node; n; n = n.parent) { if (!n.visible) return false; }
        return true;
    }

    return {
        scene, input, errors, logs, find, byTag, scriptOn, step, restore,
        Time,
        ui: {
            nodes: uiNodes,
            find: (name) => {
                for (const canvas of UiCanvas.all) {
                    const hit = canvas.find(String(name));
                    if (hit) return hit;
                }
                return null;
            },
            get canvases() { return [...UiCanvas.all]; },
            setViewport(width, height) {
                viewport.width = width;
                viewport.height = height;
                pumpUi();
            },
        },
        dg: dgRuntime,
        social: () => scriptOn('Social'),
        // Drive the UI pointer: `click(x, y)` presses this frame and releases
        // next, because a click is a release inside the element that also went
        // down inside it.
        pointAt: (x, y) => { pointer.x = x; pointer.y = y; },
        pointerDown: (on) => { pointer.down = !!on; },
        press: (k) => pressed.add(k),
        hold: (k, on) => { if (on) held.add(k); else held.delete(k); },
        mouse: (b, on) => { if (on) mouseHeld.add(b); else mouseHeld.delete(b); },
        director: () => scriptOn('Director'),
        swarm: () => scriptOn('Swarm'),
        bullets: () => scriptOn('Bullets'),
        pilot: () => scriptOn('Player'),
        board: () => scriptOn('Draft'),
        // The log carries the words the game has no font to draw.
        said: (needle) => logs.filter((l) => l.indexOf(needle) >= 0).length,
    };
}
