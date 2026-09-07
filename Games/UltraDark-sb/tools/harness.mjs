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

// UiCanvas is not re-exported from the engine index, so it is imported by path.
const { UiCanvas } = await import(path.join(repoRoot, 'html5/src/ui/UiCanvas.js'));
const { Time } = await import(path.join(repoRoot, 'html5/src/core/Time.js'));

export const DT = 1 / 60;

export const VIEWPORT = { width: 1280, height: 720 };

export function boot({ scene: sceneName = 'Scenes/Ultradark.scene' } = {}) {
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

    const scene = deserialize(fs.readFileSync(path.join(gameDir, sceneName), 'utf8'),
                              { onWarning: () => {} });

    const ui = new UiCanvas({ width: VIEWPORT.width, height: VIEWPORT.height });

    scene.engine = {
        input,
        audio: null,
        ui,
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

    async function step(frames = 1) {
        for (let i = 0; i < frames; i++) {
            ui.setPointer(pointer.x, pointer.y, pointer.down);
            ui.update();

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
        console.error = realError;
        console.warn = realWarn;
        console.log = realLog;
    }

    return {
        scene, input, errors, logs, find, byTag, scriptOn, step, restore,
        ui,
        Time,
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
