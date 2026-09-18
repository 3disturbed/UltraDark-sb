// -----------------------------------------------------------------------------
// boot — the game headless, on the real engine host, the way the dedicated
// server runs it: the project read from disk through the DiskAssetManager,
// PlayMode on, a tick that pumps the UI canvases, the vector canvases, the
// network session and the scenes in the host's own order.
//
// Two things are added for a check to hold on to. The entry script arrives
// with a PROBE appended -- a few top-level functions in its own program, so a
// test can press SOLO RUN, read the world model, read the stage and jump the
// authority's simulation to a wave -- and the host's InputManager is proxied,
// so a test can hold keys and the mouse without a DOM.
// -----------------------------------------------------------------------------

import fs from "node:fs";
import path from "node:path";
import { projectRoot, engineImport } from "./engine.mjs";

const { EngineHost, EngineConfig } = await engineImport("src/EngineHost.js");
const { PlayMode } = await engineImport("src/core/PlayMode.js");
const { NetworkManager } = await engineImport("src/net/NetworkManager.js");
const { DiskAssetManager, createDiskFetch } = await engineImport("tools/lib/diskassets.js");
const { ScriptComponent } = await engineImport("src/scripting/ScriptComponent.js");
const { Time } = await engineImport("src/core/Time.js");
const { UiCanvas } = await engineImport("src/ui/UiCanvas.js");
const { VectorCanvas } = await engineImport("src/rendering/VectorCanvas.js");
const { DarksGames } = await engineImport("src/dg/DarksGames.js");

export const ENTRY = "Scripts/DarkShapes.js";
export const DT = 1 / 60;

/** What a check may ask the running game, in the entry's own program. */
export const PROBE = `
import * as probeScreens from "./client/screens.js";
import * as probeGame from "./client/game.js";
import * as probeNet from "./client/net.js";
import * as probeRender from "./client/render.js";
import * as probeStage from "./stage/stage.js";

function probeAction(json) {
  const fn = probeScreens.ui.onAction;
  if (!fn) return false;
  fn(JSON.parse(json));
  return true;
}
function probeWorld() {
  const w = probeGame.world;
  return JSON.stringify({
    phase: w.phase, wave: w.wave, myId: w.myId, code: w.code, connected: probeNet.net.connected,
    players: w.players.map((p) => ({ id: p.id, pilot: p.pilot, state: p.state, x: p.x, y: p.y, hp: p.hp, flags: p.flags })),
    enemies: w.enemies.length, bullets: w.bullets.length, eBullets: w.eBullets.length, tracers: (w.tracers || []).length,
    zones: w.zones.length, pickups: (w.pickups || []).length,
    myHp: w.myHp, myState: w.myState, myPilot: w.myPilot, me: { x: w.me.x, y: w.me.y, aim: w.me.aim },
    screen: probeScreens.screenShown(),
  });
}
function probeStats() { return JSON.stringify(probeStage.stats()); }
function probeFighterActorId(id) { const a = probeStage.fighterActor(id); return a ? a.id : -1; }
function probeCameraActorId() { const a = probeStage.cameraActor(); return a ? a.id : -1; }
function probeStartWave(n) {
  if (authority === null) return false;
  const rooms = [...authority.rooms.map.values()];
  if (!rooms.length) return false;
  rooms[0].sim.startWave(n);
  return true;
}
function probeSim() {
  if (authority === null) return "null";
  const room = [...authority.rooms.map.values()][0];
  if (!room) return "null";
  const s = room.sim;
  return JSON.stringify({ phase: s.phase, wave: s.wave, enemies: s.enemies.size, players: s.players.size, tick: s.tick });
}
function probeSetView(v) { probeRender.settings.view = v; return probeRender.settings.view; }
function probeProject(px, py, h, W, H) { return JSON.stringify(probeStage.project(px, py, h, W, H)); }
function probeRoster(json) { probeRender.setNames(JSON.parse(json)); return true; }
// Bots in the authority's own simulation: seated as players, driven towards the nearest enemy each tick.
function probeAddBots(n) {
  if (authority === null) return 0;
  const room = [...authority.rooms.map.values()][0];
  if (!room) return 0;
  let added = 0;
  for (let i = 0; i < n; i++) { room.sim.addPlayer(100 + i, "BOT" + i, i % 8); added++; }
  return added;
}
function probeBotTick(t) {
  if (authority === null) return 0;
  const room = [...authority.rooms.map.values()][0];
  if (!room) return 0;
  const sim = room.sim;
  let n = 0;
  for (const p of sim.players.values()) {
    if (p.id < 100) continue;
    let nearest = null, nd = Infinity;
    for (const e of sim.enemies.values()) { const d = Math.hypot(e.x - p.x, e.y - p.y); if (d < nd) { nd = d; nearest = e; } }
    const ang = t * 0.03 + p.id * 1.7;
    let mx = 1024 + Math.cos(ang) * 420 - p.x, my = 576 + Math.sin(ang) * 280 - p.y;
    if (nearest && nd < 170) { mx = p.x - nearest.x; my = p.y - nearest.y; }
    const ml = Math.hypot(mx, my) || 1;
    let ax = 1, ay = 0;
    if (nearest) { ax = (nearest.x - p.x) / (nd || 1); ay = (nearest.y - p.y) / (nd || 1); }
    p.input = { seq: t, mx: mx / ml, my: my / ml, ax, ay, buttons: 1 };
    n++;
  }
  return n;
}
`;

/**
 * Boots the project headless.
 *
 * @param {object} [options]
 * @param {Array<[string, string]>} [options.launch] Launch parameters, as `?name=value` would be.
 * @param {string} [options.probe] What to append to the entry script.
 */
export async function boot({ launch = [], probe = PROBE } = {}) {
  const wasPlaying = PlayMode.isActive;
  PlayMode.isActive = true;
  Time.reset();
  UiCanvas.clearAll();
  VectorCanvas.clearAll();
  const priorDg = DarksGames.instance;
  DarksGames.instance = null;

  const errors = [];
  const warnings = [];
  const logs = [];
  const realError = console.error, realWarn = console.warn, realLog = console.log;
  console.error = (...a) => errors.push(a.join(" "));
  console.warn = (...a) => warnings.push(a.join(" "));
  console.log = (...a) => logs.push(a.join(" "));

  // The entry arrives with the probe on its end; everything else is the project's own file.
  const baseFetch = createDiskFetch(projectRoot);
  const fetchImpl = async (url) => {
    const rel = String(url).split(/[?#]/, 1)[0].replace(/^\.\//, "");
    if (rel === ENTRY || rel.endsWith("/" + ENTRY)) {
      const text = fs.readFileSync(path.join(projectRoot, ENTRY), "utf8") + "\n" + probe;
      return new Response(text, { status: 200, headers: { "content-type": "text/javascript" } });
    }
    return baseFetch(url);
  };
  const assets = new DiskAssetManager(projectRoot, { fetchImpl });
  const settings = await assets.loadJson("ProjectSettings.json");
  const config = new EngineConfig({
    gameInstanceClass: EngineConfig.fromProjectSettings(settings).gameInstanceClass,
    launchParameters: launch,
  });
  const engine = new EngineHost({ config, assets });

  // The host's InputManager, with a check's keys and mouse laid over it.
  const held = new Set();
  const pressed = new Set();
  const mouse = { x: 800, y: 450, down: new Set(), pressed: new Set() };
  const spell = (k) => String(k).toLowerCase().replace(/^key|^digit|^arrow/, "");
  const overrides = {
    isKeyDown: (k) => held.has(spell(k)),
    isKeyPressed: (k) => pressed.has(spell(k)),
    isKeyReleased: () => false,
    isMouseButtonDown: (b) => mouse.down.has(Number(b)),
    isMouseButtonPressed: (b) => mouse.pressed.has(Number(b)),
    isMouseButtonReleased: () => false,
    isMouseButtonTouch: () => false,
    get mousePosition() { return { x: mouse.x, y: mouse.y }; },
  };
  const real = engine.input;
  engine.input = new Proxy(real, {
    get(target, key) {
      if (key in overrides) return overrides[key];
      const value = target[key];
      return typeof value === "function" ? value.bind(target) : value;
    },
    set(target, key, value) { target[key] = value; return true; },
  });

  const opened = await engine.loadProject({});

  const entry = () => {
    const scene = engine.scene;
    if (!scene) return null;
    for (const actor of scene.allActors) {
      for (const script of actor.getComponents(ScriptComponent)) {
        if (script.scriptPath === ENTRY) return script;
      }
    }
    return null;
  };

  async function step(frames = 1) {
    for (let i = 0; i < frames; i++) {
      engine.tick(DT, true);
      pressed.clear();
      mouse.pressed.clear();
      // Assets and modules arrive on promises; the host is fetched through, so give them the loop.
      if (i % 2 === 0) await new Promise((r) => setImmediate(r));
    }
  }

  /** Steps until `until()` is truthy or `frames` have passed; returns the frames it took, or -1. */
  async function stepUntil(until, frames = 600) {
    for (let i = 0; i < frames; i++) {
      await step(1);
      if (until()) return i + 1;
    }
    return -1;
  }

  const invoke = (name, ...args) => {
    const script = entry();
    if (!script) throw new Error("the entry script is not in the scene yet");
    return script.invoke(name, ...args);
  };
  const json = (name, ...args) => JSON.parse(invoke(name, ...args));

  function stop() {
    try { engine.dispose(); } catch { /* already gone */ }
    try { NetworkManager.instance?.dispose?.(); } catch { /* none */ }
    if (NetworkManager.instance && typeof NetworkManager.instance.dispose !== "function") NetworkManager.instance = null;
    PlayMode.isActive = wasPlaying;
    DarksGames.instance = priorDg;
    Time.reset();
    console.error = realError; console.warn = realWarn; console.log = realLog;
  }

  return {
    engine, opened, errors, warnings, logs, step, stepUntil, invoke, json, stop, entry,
    press: (k) => { held.add(spell(k)); pressed.add(spell(k)); },
    release: (k) => held.delete(spell(k)),
    mouseDown: (b = 0) => { mouse.down.add(b); mouse.pressed.add(b); },
    mouseUp: (b = 0) => mouse.down.delete(b),
    mouseTo: (x, y) => { mouse.x = x; mouse.y = y; },
    actors: () => (engine.scene ? [...engine.scene.allActors] : []),
    find: (name) => (engine.scene ? [...engine.scene.allActors].find((a) => a.name === name && !a.isDestroyed) : null),
    findAll: (name) => (engine.scene ? [...engine.scene.allActors].filter((a) => a.name === name && !a.isDestroyed) : []),
  };
}
