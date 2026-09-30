// -----------------------------------------------------------------------------
// The lobby's class picker.
//
// A join link drops a friend straight into the lobby, past the menu where the class is chosen, so
// they fought as whatever they last were -- the owner's report. The lobby now has the eight classes
// on it. This presses one the way a player does: a pointer over the chip, a button down and up, and
// the engine's own UI deciding that was a click.
//
//     node --test tests/lobby.test.mjs
// -----------------------------------------------------------------------------

import test from "node:test";
import assert from "node:assert/strict";
import { boot } from "./helpers/boot.mjs";

const answers = (h) => () => { try { return h.invoke("probeStats") !== undefined; } catch { return false; } };
const realErrors = (h) => h.errors.filter((e) => !/UiTextMeasure|is not a usable font/.test(e));

test("a class is chosen in the lobby, by pressing it", async (t) => {
  const h = await boot();
  t.after(() => h.stop());
  assert.ok((await h.stepUntil(answers(h), 400)) > 0, "the entry script never started");

  // The lobby as a joiner finds it: two in the room, this pilot the second, flying DAVE from last time.
  const roster = [{ id: 1, name: "HOST", pilot: 0, state: 0 }, { id: 2, name: "FRIEND", pilot: 3, state: 0 }];
  h.invoke("probeShowLobby", JSON.stringify({ code: "ABCDEF", roster, myId: 2 }));
  await h.step(6);

  const shown = () => h.json("probeLobbyPilot");
  const info = () => shown().info;
  assert.match(info(), /^DAVE — /, `the picker opens on the class the room has for this pilot (${info()})`);

  // All eight are there, laid out with room to press, inside the screen.
  const chips = Array.from({ length: 8 }, (_, i) => h.ui(`lobby-pilot-${i}`));
  for (const [i, chip] of chips.entries()) {
    assert.ok(chip, `chip ${i} is on the lobby screen`);
    // No font is measured headless, so a chip is as tall as its padding here and taller on a screen.
    assert.ok(chip.rect.width >= 40 && chip.rect.height >= 24, `chip ${i} is big enough to press (${chip.rect.width} x ${chip.rect.height})`);
  }
  const lobby = h.ui("screen-lobby").rect;
  for (const [i, chip] of chips.entries()) {
    assert.ok(chip.rect.x >= lobby.x && chip.rect.x + chip.rect.width <= lobby.x + lobby.width, `chip ${i} is inside the lobby panel`);
  }

  // Press RIGG.
  const rigg = chips[5].rect;
  h.mouseTo(rigg.x + rigg.width / 2, rigg.y + rigg.height / 2);
  await h.step(2);
  h.mouseDown(0);
  await h.step(2);
  h.mouseUp(0);
  await h.step(4);

  assert.equal(shown().pilot, 5, "the chip pressed is the one shown chosen");
  assert.match(info(), /^RIGG — .*Auto-Turret/, `the line under the chips says what was chosen (${info()})`);
  assert.equal(h.json("probeWorld").myPilot, 5, "and the game took it: the same action the menu's cards send");

  // The room's word is final: a roster that says otherwise moves the picker back.
  h.invoke("probeShowLobby", JSON.stringify({ code: "ABCDEF", roster: [roster[0], { ...roster[1], pilot: 7 }], myId: 2 }));
  await h.step(3);
  assert.match(info(), /^HAWK — /, "the picker follows the roster");
  assert.deepEqual(realErrors(h), [], "no script errors");
});
