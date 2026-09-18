// -----------------------------------------------------------------------------
// The stage, on a real run: the hangar, a solo start, a wave, the bosses, the dark.
//
// Everything a screenshot would show is asserted here as numbers instead: that the
// pilot's chibi is built and armed, that the swarm has one body per enemy in the
// snapshot, that the body faces the aim, that a boss wave puts a boss on the deck
// (a chibi for the people, geometry for the machines), that the sun goes down
// from wave sixteen, and that nothing leaks across waves.
//
//     node --test tests/stage.test.mjs
// -----------------------------------------------------------------------------

import test from "node:test";
import assert from "node:assert/strict";
import { boot } from "./helpers/boot.mjs";
import { engineImport } from "./helpers/engine.mjs";

const { Vector3 } = await engineImport("src/math/Vector3.js");
const { Transform3D } = await engineImport("src/core/Transform3D.js");

const PHASE_LOBBY = 0, PHASE_WAVE = 1;
const answers = (h) => () => { try { return h.invoke("probeStats") !== undefined; } catch { return false; } };
const realErrors = (h) => h.errors.filter((e) => !/UiTextMeasure|is not a usable font/.test(e));

test("the hangar, the run, the swarm, the bosses and the dark, on one solo run", async (t) => {
  const h = await boot();
  t.after(() => h.stop());

  // ---- the hangar: the local pilot on the deck, before any room exists ----
  assert.ok((await h.stepUntil(answers(h), 400)) > 0, "the entry script never started");
  // The body arrives on a promise; the weapon is hung on it on the frame after.
  await h.stepUntil(() => { const s = h.json("probeStats"); return s.ready === 1 && s.armed === 1; }, 300);
  let stats = h.json("probeStats");
  assert.equal(stats.built, true);
  assert.equal(stats.on, true);
  assert.equal(stats.hangar, true, "before a room, the stage is the hangar");
  assert.equal(stats.fighters, 1, "the local pilot stands in the hangar");
  assert.equal(stats.ready, 1, "the chibi's body has been built from its recipe");
  assert.equal(stats.armed, 1, "the pilot holds the class's weapon");
  assert.ok(h.find("Deck") && h.find("StageCamera") && h.find("Sun"), "the deck, the camera and the sun exist");
  const deck = h.find("Deck");

  // Choosing another class rebuilds the body in that class's kit.
  h.invoke("probeAction", JSON.stringify({ t: "ui_pilot", pilot: 7 }));
  await h.stepUntil(() => h.find("Pilot -1") && h.json("probeStats").ready === 1 && h.findAll("GunPart").length === 5, 300);
  assert.equal(h.findAll("GunPart").length, 5, "HAWK's railgun is five parts");
  h.invoke("probeAction", JSON.stringify({ t: "ui_pilot", pilot: 0 }));
  await h.stepUntil(() => h.json("probeStats").ready === 1 && h.findAll("GunPart").length === 3, 300);

  // ---- SOLO RUN: a loopback session, a room, a welcome, wave one ----
  h.invoke("probeAction", JSON.stringify({ t: "ui_solo" }));
  const toWave = await h.stepUntil(() => h.json("probeWorld").phase === PHASE_WAVE, 900);
  assert.ok(toWave > 0, "the solo run never reached its first wave");
  let world = h.json("probeWorld");
  assert.equal(world.connected, true);
  assert.equal(world.players.length, 1);
  assert.match(world.code, /^[A-Z0-9]{4,8}$/);

  // Fire and move for five seconds: the swarm arrives, the mirror keeps up.
  h.mouseDown(0); h.press("W"); h.press("D");
  await h.stepUntil(() => h.json("probeWorld").enemies >= 3, 900);
  await h.step(60);
  world = h.json("probeWorld");
  stats = h.json("probeStats");
  assert.equal(stats.hangar, false, "in a run the stage is the arena");
  assert.equal(stats.fighters, 1);
  assert.equal(stats.ready, 1);
  assert.equal(stats.armed, 1);
  assert.ok(world.enemies >= 3, `enemies arrived (${world.enemies})`);
  assert.equal(stats.enemies, world.enemies, "one body per enemy in the snapshot");
  assert.ok(world.me.x > 1024 && world.me.y < 576, "W and D carried the pilot up and right");
  assert.deepEqual(realErrors(h), [], "no script errors");

  // The camera frames the pilot: it stands over the arena, looking down, near the pilot's column.
  const cam = stats.camera;
  assert.ok(cam.y > 10 && cam.y < 60, `the camera is above the deck (${cam.y})`);
  assert.ok(cam.pitch > 45 && cam.pitch < 65, `the camera has settled to the arena's pitch (${cam.pitch})`);
  assert.ok(Math.abs(cam.cx - world.me.x / 32) < 6, `the camera's centre followed the pilot (${cam.cx} vs ${world.me.x / 32})`);

  // ---- the body faces the aim: its local +Z lands along (cos aim, 0, sin aim) ----
  h.release("W"); h.release("D");
  h.mouseTo(1500, 450);
  await h.step(30);
  world = h.json("probeWorld");
  const actorId = h.invoke("probeFighterActorId", world.myId);
  const body = h.actors().find((a) => a.id === actorId);
  assert.ok(body, "the local pilot's chibi actor is in the scene");
  const rotation = body.getComponent(Transform3D).rotation;
  const face = Vector3.transform(new Vector3(0, 0, 1), rotation);
  const aim = world.me.aim;
  assert.ok(Math.abs(face.x - Math.cos(aim)) < 0.02 && Math.abs(face.z - Math.sin(aim)) < 0.02,
    `the chibi faces the aim: face (${face.x.toFixed(3)}, ${face.z.toFixed(3)}) vs aim (${Math.cos(aim).toFixed(3)}, ${Math.sin(aim).toFixed(3)})`);

  // ---- a boss that is a person, one that is a machine, and the raid bosses ----
  // A jumped wave keeps the last boss alive beside the new one, so what is asserted is the arrival:
  // the named chibi for a person, a growing count of parts for a machine.
  const bossOn = async (wave, expectActor, expectParts) => {
    const partsBefore = h.findAll("BossPart").length;
    const bossesBefore = h.json("probeStats").bosses;
    assert.equal(h.invoke("probeStartWave", wave), true, `the authority jumped to wave ${wave}`);
    const found = await h.stepUntil(() => h.json("probeStats").bosses > bossesBefore, 600);
    assert.ok(found > 0, `wave ${wave} put a boss on the deck`);
    await h.step(30);
    if (expectActor) assert.ok(h.find(expectActor), `${expectActor} stands on the deck (wave ${wave})`);
    if (expectParts) assert.ok(h.findAll("BossPart").length >= partsBefore + expectParts, `the machine boss has its parts (wave ${wave})`);
    assert.deepEqual(realErrors(h), [], `no script errors through wave ${wave}`);
  };
  const sun = h.find("Sun").getComponent("Light3D");
  const lit = sun.intensity;
  assert.ok(lit > 1.0, `in the light the sun is up (${lit})`);
  await bossOn(5, "Boss BRUTE PRIME");
  await bossOn(10, null, 8);
  await bossOn(15, null, 6);

  // ---- the dark: from wave sixteen the sun goes down with the simulation's darkness ----
  await bossOn(20, "Boss NULL SHEPHERD");
  assert.ok(sun.intensity < lit * 0.4, `at wave 20 the sun is down (${sun.intensity} from ${lit})`);

  await bossOn(30, "Boss THE PATCHWORK");
  await bossOn(35, "Boss THADDIUS PRIME");
  await bossOn(40, "Boss THE BROODMOTHER");
  await bossOn(25, "Boss THE ULTRADARK");
  assert.ok(sun.intensity < 0.1, `under THE ULTRADARK the sun is all but out (${sun.intensity})`);

  // ---- nothing leaks: seven waves in, the actor count is bounded by the pools ----
  const total = h.actors().length;
  assert.ok(total < 1600, `actor count stays bounded (${total})`);

  // ---- the classic picture is a setting away, and back ----
  h.invoke("probeSetView", "2d");
  await h.step(3);
  assert.equal(h.json("probeStats").on, false);
  assert.equal(deck.isActive, false, "the deck is switched off in the classic view");
  h.invoke("probeSetView", "3d");
  await h.step(3);
  assert.equal(h.json("probeStats").on, true);
  assert.equal(deck.isActive, true);
  assert.deepEqual(realErrors(h), []);
});
