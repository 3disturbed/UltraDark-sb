// -----------------------------------------------------------------------------
// The phone's controls, on a real solo run: the game draws its own two sticks and
// four buttons on the HUD canvas, exactly where input.js hit-tests them, and the
// engine's generic overlay is declared away.
//
// The original page drew #touch-ui as DOM over the canvas; the engine has no page,
// so until 2.0.3 nothing drew the game's controls and a phone player saw only the
// engine's default overlay -- a stick and two round buttons the game never reads,
// over the menus and the match. This holds the fix: the ProjectSettings.json block
// that turns the overlay off, a drawn button where a tap on it registers, and two
// thumbs that move and aim the survivor.
//
//     node --test tests/touch.test.mjs
// -----------------------------------------------------------------------------

import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { boot, PROBE } from "./helpers/boot.mjs";
import { projectRoot, engineImport } from "./helpers/engine.mjs";

const { VectorCanvas } = await engineImport("src/rendering/VectorCanvas.js");

const PHASE_WAVE = 1;
const answers = (h) => () => { try { return h.invoke("probeStats") !== undefined; } catch { return false; } };
const realErrors = (h) => h.errors.filter((e) => !/UiTextMeasure|is not a usable font/.test(e));

const TOUCH_PROBE = PROBE + `
import * as probeInput from "./client/input.js";
function probeTouch() {
  return JSON.stringify({ active: probeInput.touchActive(), sticks: probeInput.touchSticks(), buttons: probeInput.touchButtons() });
}
`;

/** The HUD canvas's commands this tick, and the transform it was drawn under (render.js's dpr). */
function hud() {
  const canvas = VectorCanvas.all.find((c) => c.name === "darkshapes-hud");
  const commands = canvas ? canvas.decode() : [];
  const transform = commands.find((c) => c[0] === "setTransform");
  return { commands, dpr: transform ? transform[1] : 1, arcs: commands.filter((c) => c[0] === "arc") };
}

/** Whether an arc of radius `r` centred on (x, y) was drawn, to a hair. */
const drew = (arcs, x, y, r) => arcs.some((a) => Math.abs(a[1] - x) < 1e-3 && Math.abs(a[2] - y) < 1e-3 && Math.abs(a[3] - r) < 1e-3);

test("ProjectSettings.json turns the engine's generic touch overlay off", () => {
  const settings = JSON.parse(fs.readFileSync(path.join(projectRoot, "ProjectSettings.json"), "utf8"));
  assert.deepEqual(settings.TouchControls, { leftStick: false, rightStick: false, buttons: [] });
});

test("the game's own sticks and buttons are drawn where they hit-test, and two thumbs play", async (t) => {
  // A canvas records nothing with no renderer to hand it to; headless, the check is the renderer.
  VectorCanvas.attachRenderer();
  const h = await boot({ probe: TOUCH_PROBE });
  t.after(() => { h.stop(); VectorCanvas.detachRenderer(); });
  const touch = h.engine.input.touch;

  assert.ok((await h.stepUntil(answers(h), 400)) > 0, "the entry script never started");
  h.invoke("probeAction", JSON.stringify({ t: "ui_solo" }));
  assert.ok((await h.stepUntil(() => h.json("probeWorld").phase === PHASE_WAVE, 900)) > 0, "the solo run never reached its first wave");
  await h.step(2);

  // No finger yet, on a desk: nothing of the phone's is drawn.
  let state = h.json("probeTouch");
  assert.equal(state.active, false);
  for (const b of state.buttons) assert.ok(!drew(hud().arcs, b.x, b.y, b.r), `${b.id} drawn before any touch`);

  // A thumb on the left half: the move stick, based where it landed.
  const { dpr } = hud();
  const at = (css) => css * dpr; // input.js's CSS pixels -> the screen pixels a touch is read in
  const size = state.sticks.l; // the resting place of the left stick
  const lx = size.ox, ly = size.oy;
  touch.onTouchStart(101, at(lx), at(ly));
  await h.step(2);
  touch.onTouchMove(101, at(lx + 60), at(ly));   // hard right
  await h.step(2);
  state = h.json("probeTouch");
  assert.equal(state.active, true);
  assert.equal(state.sticks.l.active, true);
  assert.ok(state.sticks.l.dx > 0.95, `left stick deflected right (${state.sticks.l.dx})`);

  // Every button is drawn on the HUD canvas at the very centre and radius input.js hit-tests.
  let frame = hud();
  for (const b of state.buttons) assert.ok(drew(frame.arcs, b.x, b.y, b.r), `${b.id} drawn at its hit-test circle`);
  // Both sticks: a base (55) and a knob (22), the left knob pushed 30px right.
  assert.ok(drew(frame.arcs, state.sticks.l.ox, state.sticks.l.oy, 55), "left stick base drawn where the thumb landed");
  assert.ok(drew(frame.arcs, state.sticks.l.ox + state.sticks.l.dx * 30, state.sticks.l.oy + state.sticks.l.dy * 30, 22), "left knob drawn deflected");
  assert.ok(drew(frame.arcs, state.sticks.r.ox, state.sticks.r.oy, 55), "right stick base drawn at rest");

  // A tap at each button's centre is that button's -- it lights, and no stick takes the finger.
  let id = 200;
  for (const b of state.buttons) {
    touch.onTouchStart(++id, at(b.x), at(b.y));
    await h.step(1);
    const now = h.json("probeTouch");
    assert.equal(now.buttons.find((x) => x.id === b.id).held, true, `a tap on ${b.id}'s drawing is ${b.id}'s`);
    assert.equal(now.sticks.r.active, false, `a tap on ${b.id} never becomes the aim stick`);
    touch.onTouchEnd(id);
    await h.step(8);
  }

  // The second thumb on the right half, off every button: the aim stick. Aim down, and the survivor
  // turns to it while the left thumb carries it right.
  const before = h.json("probeWorld").me;
  const rx = state.sticks.r.ox, ry = state.sticks.r.oy;
  touch.onTouchStart(301, at(rx), at(ry));
  await h.step(2);
  touch.onTouchMove(301, at(rx), at(ry + 60));
  await h.step(40);
  state = h.json("probeTouch");
  assert.equal(state.sticks.r.active, true);
  assert.ok(state.sticks.r.dy > 0.95, `right stick deflected down (${state.sticks.r.dy})`);
  frame = hud();
  assert.ok(drew(frame.arcs, state.sticks.r.ox, state.sticks.r.oy + 30, 22), "right knob drawn deflected");
  const after = h.json("probeWorld").me;
  assert.ok(after.x > before.x + 20, `the left thumb moved the survivor right (${before.x} -> ${after.x})`);
  assert.ok(Math.abs(after.aim - Math.PI / 2) < 0.3, `the right thumb aimed it down (${after.aim})`);

  touch.onTouchEnd(101);
  touch.onTouchEnd(301);
  await h.step(4);
  state = h.json("probeTouch");
  assert.equal(state.sticks.l.active, false);
  assert.equal(state.sticks.r.active, false);
  assert.deepEqual(realErrors(h), [], "no script errors");
});
