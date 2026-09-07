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

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const gameDir = path.resolve(here, '..');
export const repoRoot = path.resolve(gameDir, '../..');

const engine = await import(path.join(repoRoot, 'html5/src/index.js'));
export const { deserialize, ScriptComponent, PhysicsSystem2D, Vector2 } = engine;

export const DT = 1 / 60;

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

    const scene = deserialize(fs.readFileSync(path.join(gameDir, sceneName), 'utf8'),
                              { onWarning: () => {} });

    scene.engine = {
        input,
        audio: null,
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

    async function step(frames = 1) {
        for (let i = 0; i < frames; i++) {
            scene.update(DT);
            scene.physics2D.fixedStep(DT);
            scene.fixedUpdate(DT);
            scene.lateUpdate(DT);
            scene.flushPendingActors();
            pressed.clear();
            // Let a runtime-attached script's loadText settle.
            if (i % 4 === 0) { await new Promise((r) => setImmediate(r)); }
        }
    }

    function restore() {
        console.error = realError;
        console.warn = realWarn;
        console.log = realLog;
    }

    return {
        scene, input, errors, logs, find, byTag, scriptOn, step, restore,
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
