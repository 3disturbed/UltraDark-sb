// -----------------------------------------------------------------------------
// stage — UltraDark in three dimensions, without touching UltraDark.
//
// The client's world model (client/game.js) is the truth: interpolated enemies,
// predicted pilots, zones, pickups, bullets. This reads it once a frame, after
// the model has advanced, and keeps a 3D scene in step with it -- chibi pilots
// and bosses, glowing geometry for the swarm, discs on the floor for the zones,
// a lit deck, a camera that frames the squad -- and hands render.js a projection
// so the neon 2D layer (bullets, the dark, popups) lands where the 3D things
// are. It reads; it never writes anything the simulation or the client owns.
//
// Between runs the same deck is a hangar: the lobby's pilots stand in a row on
// it, waving as they arrive, and the local pilot changes class in front of you.
// -----------------------------------------------------------------------------

import { PHASE, PS, PILOTS, ARENA_W, ARENA_H } from "../shared/constants.js";
import { ENEMIES } from "../shared/enemies.js";
import { world, serverTickNow } from "../client/game.js";
import { U, ARENA_UW, ARENA_UH, num } from "./units.js";
import { Rig } from "./camera.js";
import { Arena } from "./arena.js";
import { Swarm } from "./swarm.js";
import { Zones } from "./zones.js";
import { Fighter } from "./fighter.js";
import * as overlay from "./overlay.js";

export const stage = {
  on: false,          // the 3D stage is what the player sees
  built: false,
  hangar: true,       // between runs: the row of pilots
  pitch: 60,
};

const PITCH = 54;             // steep enough to read the fight as the 2D game laid it out, shallow enough to see faces
const HANGAR_PITCH = 24;      // above 21 the horizon leaves a 40-degree frame, and the sky with it
const SOLO_VIEW = 27;         // the narrowest view, world units across: a solo pilot reads at about a hundred pixels
const SQUAD_VIEW = 32;

let rig = null, arena = null, swarm = null, zones = null;
const fighters = new Map();   // player id -> Fighter (in the arena), roster slot -> Fighter (in the hangar)
let roster = [];              // [{ id, name, pilot, state }] as the room last said
let radar = null;
let seenBullets = new Set();
let seenBulletsNext = new Set();
let wasHangar = true;
let framePoints = [];
let screenW = 1280, screenH = 720;
let frames = 0, elapsed = 0;   // what the stage has been given, for the debug readout

// -----------------------------------------------------------------------------
// Setup
// -----------------------------------------------------------------------------

export function initStage() {
  if (stage.built) return;
  rig = new Rig();
  rig.init();
  arena = new Arena();
  arena.init();
  swarm = new Swarm();
  zones = new Zones();
  buildRadar();
  swarm.radar = radar;
  stage.built = true;
  setOn(true);
}

function buildRadar() {
  const root = UI.root;
  if (!root || !root.add) return;
  radar = root.add({
    name: "ud-radar", kind: "minimap", absolute: true, anchor: "right", x: -14, y: 0,
    width: 144, height: 81, mapRect: [0, 0, ARENA_UW, ARENA_UH],
    markerTag: "Enemy", markerColour: "#ff5b6e", markerSize: 3,
    background: "#05020c80", borderColour: "#39f0ff40", borderWidth: 1, cornerRadius: 4, visible: false,
  });
}

/** The 3D stage on or off (the settings screen's VIEW). Off, the classic 2D picture is drawn. */
export function setOn(on) {
  on = !!on;
  if (on === stage.on) return;
  stage.on = on;
  if (!stage.built) return;
  arena.setActive(on);
  swarm.setActive(on);
  zones.setActive(on);
  for (const f of fighters.values()) f.show(on);
  if (radar) radar.visible = on && !stage.hangar;
  if (rig.actor) rig.actor.active = on;
}

/** The room's roster, from WELCOME and every roster event: who stands in the hangar. */
export function setRoster(list) {
  roster = Array.isArray(list) ? list.map((r) => ({ id: r.id, name: r.name, pilot: r.pilot | 0, state: r.state })) : [];
}

// -----------------------------------------------------------------------------
// The frame
// -----------------------------------------------------------------------------

/**
 * Once a frame, after game.frame(): `f` is what render.js knows -- the screen in
 * CSS pixels, the darkness level, the trauma shake, the client clock, whether a
 * room is connected.
 */
export function update(dt, f) {
  if (!stage.built || !stage.on) return;
  frames++; elapsed += dt;
  screenW = f.W; screenH = f.H;
  const now = f.clockNow();
  const hangar = !f.connected || world.phase === PHASE.LOBBY;
  if (hangar !== wasHangar) {
    wasHangar = hangar;
    clearFighters();
    if (radar) radar.visible = !hangar;
  }
  stage.hangar = hangar;
  if (hangar) syncHangar(dt, now, f); else syncArena(dt, now, f);
  const dark = hangar ? 0 : f.dark;
  arena.setDark(dark);
  swarm.setDark(dark);
  for (const fi of fighters.values()) fi.setDark(dark);
  arena.update(dt);
  rig.update(dt, f.shakeX * U * 2.5, f.shakeY * U * 2.5);
  arena.setViewPitch(rig.pitch);   // after the rig has eased: the rim light follows the pitch it has now
}

function clearFighters() {
  for (const fi of fighters.values()) { if (radar) radar.unmark(fi.actor); fi.dispose(); }
  fighters.clear();
  swarm.sync([], 0, 0, () => null);
  zones.sync([], [], 0, 0, null);
  seenBullets.clear();
}

/** A pilot's predicted position for a local seat, or null for a remote one. */
function localPred(id) {
  for (const seat of world.locals) if (seat.id === id) return seat.pred;
  return null;
}

function syncArena(dt, now, f) {
  // ---- the pilots ----
  const alive = [];
  framePoints.length = 0;
  for (const p of world.players) {
    let fi = fighters.get(p.id);
    if (!fi) {
      fi = new Fighter(p.id, p.pilot, f.nameOf(p.id));
      fighters.set(p.id, fi);
      if (radar && fi.actor) radar.mark(fi.actor, (PILOTS[p.pilot] || PILOTS[0]).color);
    }
    const mine = p.id === world.myId;
    const pred = mine ? world.me : localPred(p.id);
    const x = pred ? pred.x : p.x, y = pred ? pred.y : p.y;
    const aim = pred ? pred.aim : p.aim;
    const nearest = p.state === PS.ALIVE ? swarm.nearestActor(x, y, 320) : null;
    fi.update(dt, now, p, x, y, aim, nearest);
    if (p.state === PS.ALIVE || p.state === PS.DOWNED) { alive.push(p.id); framePoints.push({ x: x * U, z: y * U }); }
  }
  for (const [id, fi] of fighters) {
    if (world.players.some((p) => p.id === id)) continue;
    if (radar) radar.unmark(fi.actor);
    fi.dispose();
    fighters.delete(id);
  }

  // ---- the swarm, the floor ----
  swarm.sync(world.enemies, dt, now, nearestPilotActor);
  zones.sync(world.zones, world.pickups ?? [], dt, now, arena);

  // ---- muzzle flashes for shots the snapshot just showed leaving a pilot ----
  seenBulletsNext.clear();
  for (const b of world.bullets) {
    seenBulletsNext.add(b.id);
    if (seenBullets.has(b.id)) continue;
    const fi = fighters.get(b.owner);
    if (!fi || b.owner === world.myId) continue;           // the local gun flashes at its own cadence
    const dx = b.x - fi.x / U, dy = b.y - fi.z / U;
    if (dx * dx + dy * dy < 120 * 120) fi.shoot();
  }
  const swap = seenBullets; seenBullets = seenBulletsNext; seenBulletsNext = swap;

  // ---- the camera frames the living and the boss ----
  swarm.bossPoints(framePoints);
  if (framePoints.length === 0) framePoints.push({ x: ARENA_UW / 2, z: ARENA_UH / 2 });
  rig.frame(framePoints, { pad: 7, minW: alive.length > 1 ? SQUAD_VIEW : SOLO_VIEW, aspect: f.W / f.H, pitch: PITCH });
}

/** The nearest living pilot's actor to an arena point, for a boss to look at. */
function nearestPilotActor(px, py) {
  let best = null, bd = Infinity;
  for (const fi of fighters.values()) {
    if (fi.dead || !fi.active || fi.x !== fi.x) continue;
    const dx = fi.x / U - px, dy = fi.z / U - py;
    const d = dx * dx + dy * dy;
    if (d < bd) { bd = d; best = fi.actor; }
  }
  return best;
}

/** Between runs: the roster in a row on the deck, the local pilot's class live. */
function syncHangar(dt, now, f) {
  const row = roster.length ? roster : [{ id: -1, name: f.myName, pilot: world.myPilot, state: PS.ALIVE }];
  const n = row.length;
  const gap = n > 4 ? 1.25 : 1.6;
  const dist = 4.6 + 0.5 * n;
  // The menu covers the middle of the screen, so the row stands in the left third: the camera looks
  // at a point to the right of it by a third of what it sees at that distance.
  const viewW = 2 * dist * Math.tan(rig.fov * Math.PI / 360) * ((screenW || 16) / (screenH || 9));
  const rowX = ARENA_UW / 2 - viewW * 0.31;
  const x0 = rowX - (n - 1) * gap / 2;
  const seen = new Set();
  for (let i = 0; i < n; i++) {
    const r = row[i];
    const mine = r.id === world.myId || r.id === -1;
    const pilot = mine ? world.myPilot : r.pilot;
    seen.add(r.id);
    let fi = fighters.get(r.id);
    if (!fi) {
      fi = new Fighter(r.id, pilot, r.name);
      fighters.set(r.id, fi);
      fi.arrived = now;
    }
    const px = (x0 + i * gap) / U, py = (ARENA_UH / 2 + 1.5) / U;
    const snapshotRow = { pilot, state: PS.ALIVE, x: px, y: py, aim: Math.PI / 2, hp: fi.lastHp < 0 ? 3 : fi.lastHp, flags: 0 };
    fi.update(dt, now, snapshotRow, px, py, Math.PI / 2, rig.actor);
    // A wave on arrival, and the one being chosen keeps looking pleased about it.
    if (fi.arrived && now - fi.arrived < 120 && fi.ready && !fi.waved) { fi.waved = true; fi.act("wave", 1.4, 0.1); }
    if (mine) fi.settle("happy"); else fi.settle("neutral");
  }
  for (const [id, fi] of fighters) {
    if (seen.has(id)) continue;
    fi.dispose();
    fighters.delete(id);
  }
  rig.lookAt(ARENA_UW / 2, ARENA_UH / 2 + 1.5, dist, HANGAR_PITCH, 0.7);
}

// -----------------------------------------------------------------------------
// What the client tells the stage
// -----------------------------------------------------------------------------

/** The local gun fired at its predicted cadence (render.js's muzzle feel). */
export function muzzle(id) {
  const fi = fighters.get(id);
  if (fi) fi.shoot();
}

/** The kill-multiplier milestone. */
export function flare() { if (arena) arena.flare(); }

/** A server event, after the world model has taken it. Reactions only; nothing here decides. */
export function event(ev) {
  if (!stage.built) return;
  const who = fighters.get(ev.who);
  switch (ev.t) {
    case "kill": {
      const def = ENEMIES[ev.kind];
      if (def?.boss) { for (const fi of fighters.values()) { fi.act("cheer", 1.3, 0.1); fi.emote("happy", 2.5); } arena.blast(ev.x, ev.y, 260, def.color); }
      else if (who && !who.dead) who.emote("happy", 0.45);
      break;
    }
    case "rail": if (who) who.shoot(); break;
    case "ability": if (who) { who.act("wave", 0.6, 0.06); who.emote("determined", 1); } break;
    case "nova": case "bomb": for (const fi of fighters.values()) fi.emote("surprised", 0.8); break;
    case "revived": if (who) who.emote("happy", 1.5); break;
    case "hateful": if (who) who.emote("surprised", 1.6); break;
    case "charge": for (const fi of fighters.values()) fi.emote("determined", 1.2); break;
    case "shock": { const fa = fighters.get(ev.a), fb = fighters.get(ev.b); if (fa) fa.emote("hurt", 0.8); if (fb) fb.emote("hurt", 0.8); break; }
    case "unwrapped": if (who) who.emote("happy", 1); break;
    case "boss": for (const fi of fighters.values()) fi.emote("surprised", 1.6); break;
    case "boss_down": for (const fi of fighters.values()) { fi.act("cheer", 1.2, 0.1); fi.emote("happy", 2); } break;
    case "victory": for (const fi of fighters.values()) { fi.act("cheer", 2, 0.1); fi.emote("happy", 5); } break;
    case "gameover": for (const fi of fighters.values()) fi.emote("sad", 6); break;
    case "hole_burst": arena.blast(ev.x, ev.y, 240, "#a56bff"); break;
    default: break;
  }
}

// -----------------------------------------------------------------------------
// Projection, for input and the 2D layer
// -----------------------------------------------------------------------------

/** A screen point (CSS px) on the arena floor, in arena pixels: what mouse aim reads. */
export function screenToWorld(sx, sy, W, H) {
  if (!rig) return { x: sx, y: sy };
  const g = rig.ground(sx, sy, W, H, 0.4);
  return { x: g.x / U, y: g.z / U };
}

/** An arena point at a height on the screen: {x, y, s} with s in screen px per arena px, or null. */
export function project(px, py, h, W, H) {
  if (!rig) return null;
  const q = rig.project(px * U, h, py * U, W, H);
  if (!q) return null;
  q.s *= U;
  return q;
}

/** The 2D layers over the 3D frame: render.js hands its canvases and its lists. */
export function drawOverlay(layer, ctx, args) {
  const proj = (px, py, h) => project(px, py, h, args.W, args.H);
  const a = { ...args, world, proj, localPred, serverTickNow, squash: Math.sin(rig.pitch * Math.PI / 180) };
  if (layer === "world") overlay.drawWorldLayer(ctx, a);
  else if (layer === "dark") overlay.drawDark(ctx, a);
  else if (layer === "tags") overlay.drawTags(ctx, a);
}

/** What the headless checks read. */
export function stats() {
  const out = {
    on: stage.on, built: stage.built, hangar: stage.hangar, frames, elapsed: Math.round(elapsed * 100) / 100,
    fighters: fighters.size, ready: 0, armed: 0, enemies: swarm ? swarm.count : 0, bosses: swarm ? swarm.bosses.size : 0,
    camera: rig ? { x: rig.px, y: rig.py, z: rig.pz, pitch: rig.pitch, dist: rig.dist, cx: rig.cx, cz: rig.cz } : null,
    rim: arena ? arena.rim : null,
    clips: {}, faces: {},
  };
  for (const fi of fighters.values()) {
    if (fi.ready) out.ready++;
    if (fi.armed) out.armed++;
    out.clips[fi.id] = fi.clip;
    out.faces[fi.id] = fi.face;
  }
  return out;
}

/** The actor the rig's camera is on, for a check that it is the scene's own and not a second. */
export function cameraActor() { return rig ? rig.actor : null; }

/** The fighter actor for a player id, for a check to read its transform. */
export function fighterActor(id) {
  const fi = fighters.get(id);
  return fi ? fi.actor : null;
}
