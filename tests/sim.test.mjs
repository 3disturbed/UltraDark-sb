// -----------------------------------------------------------------------------
// The simulation, as the original's harness held it: bots play, solo and in a
// squad, the deep waves run, and every one of the EIGHT bosses spawns -- through
// the program this project's entry links, so what is tested is what ships.
// -----------------------------------------------------------------------------

import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { projectRoot, engineRoot, engineImport } from "./helpers/engine.mjs";

const { Actor } = await engineImport("src/core/Actor.js");
const { createScriptGlobals } = await engineImport("src/scripting/ScriptBridge.js");
const { createScriptMath } = await engineImport("src/scripting/ScriptMath.js");
const { linkScript } = await engineImport("src/scripting/ScriptModules.js");

const ENTRY = "Scripts/DarkShapes.js";
const readFile = (rel) => {
  const file = path.join(projectRoot, rel);
  return fs.existsSync(file) && fs.statSync(file).isFile() ? fs.readFileSync(file, "utf8") : null;
};

/** Links the entry over this project's files, as the engine does, and hands back its modules. */
function linkProject() {
  const globals = createScriptGlobals(new Actor("DarkShapes"));
  const names = Object.keys(globals);
  const linked = linkScript(ENTRY, readFile(ENTRY), readFile, names);
  if (linked.program === null) throw new Error(`${ENTRY} does not link:\n${linked.errors.join("\n")}`);
  // eslint-disable-next-line no-new-func
  const factory = new Function(...names, "Math", `${linked.program}\n\nreturn { importModule: typeof __sbImport === 'function' ? __sbImport : null };`);
  const { importModule } = factory(...names.map((n) => globals[n]), createScriptMath());
  return (rel) => {
    const index = linked.modules.indexOf(`Scripts/${rel}`);
    if (index < 0) throw new Error(`the program holds no Scripts/${rel}`);
    return importModule(index);
  };
}

/** The original's harness, from the engine's DarkShapes fixture. */
function harness(module, minutes = 3) {
  const text = fs.readFileSync(path.join(engineRoot, "html5/tests/fixtures/darkshapes-template/harness.js"), "utf8");
  // eslint-disable-next-line no-new-func
  const { darkShapesHarness } = new Function(`${text}\nreturn { darkShapesHarness };`)();
  return darkShapesHarness(module, { now: () => performance.now(), minutes, seed: 0x5eed });
}

const module = linkProject();

test("bots play three simulated minutes solo and as a squad of four inside every invariant", (t) => {
  const h = harness(module);
  for (const players of [1, 4]) {
    const r = h.runScenario(players);
    t.diagnostic(`${players}p: wave ${r.maxWave}, ${r.kills} kills, avg tick ${r.avgMs} ms`);
    h.checkScenario(r);
  }
});

test("the deep waves run, and the first five bosses come in order", (t) => {
  const h = harness(module);
  h.checkDeep(h.runDeep());
  const { bosses } = h.runEveryBoss();
  const { EK } = module("shared/constants.js");
  t.diagnostic(`bosses 5..25: ${bosses.join(", ")}`);
  assert.deepEqual(bosses, [EK.BRUTE_PRIME, EK.HEX_PRIME, EK.FOUNDRY, EK.SHEPHERD, EK.ULTRADARK]);
});

test("the eight-boss cycle: waves 30, 35 and 40 bring the raid bosses, and 45 starts again", () => {
  const { Sim } = module("server/sim.js");
  const { EK, TICK_DT, ARENA_W, ARENA_H } = module("shared/constants.js");
  const { BTN } = module("shared/protocol.js");
  const sim = new Sim();
  sim.seedAll(0xb055);
  for (let i = 1; i <= 4; i++) sim.addPlayer(i, `BOT${i}`, (i - 1) % 8);
  sim.startRun();
  const bosses = [];
  for (const wave of [30, 35, 40, 45]) {
    sim.startWave(wave);
    for (let t = 0; t < Math.round(20 / TICK_DT); t++) {
      for (const p of sim.players.values()) {
        const ang = t * 0.03 + p.id * 1.7;
        const mx = ARENA_W / 2 + Math.cos(ang) * 420 - p.x, my = ARENA_H / 2 + Math.sin(ang) * 280 - p.y;
        const ml = Math.hypot(mx, my) || 1;
        p.input = { seq: t, mx: mx / ml, my: my / ml, ax: 1, ay: 0, buttons: BTN.FIRE };
      }
      sim.step(TICK_DT);
      for (const ev of sim.events) if (ev.t === "boss") bosses.push(ev.kind);
      sim.events.length = 0;
      for (const p of sim.players.values()) assert.ok(Number.isFinite(p.x) && Number.isFinite(p.y), `NaN player at wave ${wave}`);
      for (const e of sim.enemies.values()) assert.ok(Number.isFinite(e.x) && Number.isFinite(e.y), `NaN enemy at wave ${wave}`);
      if (sim.phase !== 1) break;
    }
  }
  assert.deepEqual(bosses, [EK.PATCHWORK, EK.THADDIUS, EK.BROODMOTHER, EK.BRUTE_PRIME]);
});
