// Client world model: snapshot interpolation for remote entities,
// local prediction for the own ship, and locally-simulated pattern
// bullets (SDD §3.3 — the server sends seeds, not bullets).
//
// DarkShapes: TwinStickTron's client/js/game.js at 7eee633, ported onto the engine. The
// world model is the original's, statement for statement: snapshot interpolation for
// every remote entity, prediction and gentle reconciliation for the own ship and each
// couch seat, locally simulated pattern bullets, lasers and the stats the mods make.
// What moved is what it stood on:
//   - the simulation's modules are imported relatively, from the template's shared/;
//   - performance.now() is the client's clock, which the flow advances once a frame on
//     both engines (engine/clock.js) and hands over in initGame, so interpolation and a
//     laser's life are measured the same way in a tab and natively.
// Each change is written down, with why, in html5/tests/fixtures/darkshapes-template/ports.json.

import {
  ARENA_W, ARENA_H, PHASE, PS, PILOTS, PLAYER, TICK_RATE, SNAPSHOT_EVERY, clamp,
} from "../shared/constants.js";
import { stepPlayerMovement, startDash } from "../shared/movement.js";
import { spawnPattern } from "../shared/patterns.js";
import { computeStats } from "../shared/mods.js";
import { BTN, PF } from "../shared/protocol.js";

const SNAP_MS = 1000 * SNAPSHOT_EVERY / TICK_RATE; // 66.7ms between snapshots

// DarkShapes: the clock this model reads in place of performance.now(), in milliseconds.
let clockNow = () => 0;

/** DarkShapes: hands the world model the client's clock; the flow calls it once, at start. */
export function initGame({ now }) {
  clockNow = now;
}

export const world = {
  myId: 0, code: "", joinUrl: "", resumeKey: "",
  phase: PHASE.LOBBY, wave: 0, phaseT: 0,
  mult: 1, unbanked: 0, banked: 0, enemiesLeft: 0,
  players: [], enemies: [], bullets: [], zones: [], // interpolated view
  eBullets: [],                                      // client-simmed pattern bullets
  tracers: [],                                       // HAWK rails (event-spawned, client-integrated)
  predRails: [],                                     // timestamps of own predicted rails awaiting their event
  lasers: [],                                        // sniper telegraphs & beams
  pickups: [], myCons: [], stasis: 0,                // consumables
  myCores: 0, shopOffer: null, shopDoneUi: false, intermissionS: 20,    // the Core Shop
  charges: {}, myFlags: 0, // THADDIUS polarity marks / own snapshot flags
  locals: [],  // couch co-op seats beyond P1 (managed by main.js)
  challenge: null, // {n, s, w, seed} when playing someone's challenge link
  me: { x: ARENA_W / 2, y: ARENA_H / 2, vx: 0, vy: 0, dashT: 0, aim: 0, alive: true },
  myPilot: 0, myMods: [], myStats: computeStats(PILOTS[0], []),
  myHp: 3, myBombs: 1, myDashCd: 0, myAbilCd: 0, myState: PS.ALIVE,
  overdrive: false,
  prevSnap: null, currSnap: null, currAt: 0,
  serverTick: 0,
  dashPressedPrev: false,
};

export function resetForRun() {
  world.myMods = [];
  world.myStats = computeStats(PILOTS[world.myPilot], []);
  world.eBullets.length = 0;
  world.tracers.length = 0;
  world.predRails.length = 0;
}

// Own-rail prediction: spawn the tracer the instant the trigger is pulled,
// mirroring the server's fire() for the rail kind. The server's "rail" event
// (~40-100ms later) is then consumed as this shot's confirmation instead of
// spawning a duplicate. Damage stays fully server-side.
export function predictRail() {
  const w = PILOTS[world.myPilot].weapon, s = world.myStats;
  const count = 1 + (s.split | 0);
  const spread = count > 1 ? 0.14 : 0;
  const speed = PLAYER.BULLET_SPEED * (w.speedMul ?? 1) * s.bulletSpeed;
  const life = (w.life ?? PLAYER.BULLET_LIFE) * s.bulletLife;
  for (let i = 0; i < count; i++) {
    const a = world.me.aim + (count === 1 ? 0 : (i - (count - 1) / 2) * spread);
    world.tracers.push({
      x: world.me.x + Math.cos(a) * 20, y: world.me.y + Math.sin(a) * 20,
      vx: Math.cos(a) * speed, vy: Math.sin(a) * speed,
      life, rc: s.ricochet | 0,
    });
  }
  world.predRails.push(clockNow()); // DarkShapes: the client's clock
}

function consumePredRail() {
  const cutoff = clockNow() - 400; // stale predictions never eat fresh events (DarkShapes: the client's clock)
  while (world.predRails.length && world.predRails[0] < cutoff) world.predRails.shift();
  if (!world.predRails.length) return false;
  world.predRails.shift();
  return true;
}

export function onSnapshot(s) {
  world.prevSnap = world.currSnap;
  world.currSnap = s;
  world.currAt = clockNow(); // DarkShapes: the client's clock
  world.serverTick = s.tick;
  world.phase = s.phase; world.wave = s.wave; world.phaseT = s.phaseT;
  world.mult = s.mult; world.unbanked = s.unbanked; world.banked = s.banked;
  world.enemiesLeft = s.enemiesLeft;

  world.stasis = s.stasis ?? 0;
  world.pickups = s.pickups ?? [];

  const meS = s.players.find(p => p.id === world.myId);
  if (meS) {
    world.myHp = meS.hp; world.myBombs = meS.bombs;
    world.myDashCd = meS.dashCd; world.myAbilCd = meS.abilCd;
    world.myState = meS.state;
    world.myCons = (meS.cons ?? []).filter(Boolean);
    world.myCores = meS.cores ?? 0;
    world.overdrive = !!(meS.flags & PF.OVERDRIVE);
    world.myFlags = meS.flags;
    // reconcile prediction: gentle blend, hard snap on big error
    const err = Math.hypot(meS.x - world.me.x, meS.y - world.me.y);
    if (err > 64 || world.myState !== PS.ALIVE) {
      world.me.x = meS.x; world.me.y = meS.y; world.me.vx = 0; world.me.vy = 0;
    } else {
      world.me.x += (meS.x - world.me.x) * 0.18;
      world.me.y += (meS.y - world.me.y) * 0.18;
    }
  }

  // reconcile couch seats the same way
  for (const seat of world.locals) {
    if (!seat.id) continue;
    const sS = s.players.find(p => p.id === seat.id);
    if (!sS) continue;
    seat.hud = { hp: sS.hp, bombs: sS.bombs, cons: (sS.cons ?? []).filter(Boolean), dashCd: sS.dashCd, abilCd: sS.abilCd, state: sS.state, cores: sS.cores ?? 0 };
    const err = Math.hypot(sS.x - seat.pred.x, sS.y - seat.pred.y);
    if (err > 64 || sS.state !== PS.ALIVE) {
      seat.pred.x = sS.x; seat.pred.y = sS.y; seat.pred.vx = 0; seat.pred.vy = 0;
    } else {
      seat.pred.x += (sS.x - seat.pred.x) * 0.18;
      seat.pred.y += (sS.y - seat.pred.y) * 0.18;
    }
  }
}

// Called every render frame: advance prediction + local bullets, produce
// the interpolated view arrays.
export function frame(dt, input) {
  // --- own ship prediction (skipped while cocooned: the server roots us) ---
  if (world.myState === PS.ALIVE && !uiBlocking() && !(world.myFlags & PF.WRAPPED)) {
    if (Math.hypot(input.ax, input.ay) > 0.25) world.me.aim = Math.atan2(input.ay, input.ax);
    const dashPressed = !!(input.buttons & BTN.DASH);
    if (dashPressed && !world.dashPressedPrev && world.myDashCd <= 0.05 && world.me.dashT <= 0) {
      startDash(world.me, { mx: input.mx, my: input.my }); // visual-instant dash
    }
    world.dashPressedPrev = dashPressed;
    stepPlayerMovement(world.me, { mx: input.mx, my: input.my }, world.myStats, dt);
  }

  // --- couch seat prediction (input polled by main.js into seat.lastInput) ---
  for (const seat of world.locals) {
    if (!seat.id || seat.hud?.state !== PS.ALIVE) continue;
    const inp = seat.lastInput;
    if (!inp) continue;
    if (Math.hypot(inp.ax, inp.ay) > 0.25) seat.pred.aim = Math.atan2(inp.ay, inp.ax);
    const dashP = !!(inp.buttons & BTN.DASH);
    if (dashP && !seat.dashPrev && (seat.hud?.dashCd ?? 1) <= 0.05 && seat.pred.dashT <= 0) {
      startDash(seat.pred, { mx: inp.mx, my: inp.my });
    }
    seat.dashPrev = dashP;
    stepPlayerMovement(seat.pred, { mx: inp.mx, my: inp.my }, seat.stats, dt);
  }

  // --- lasers expire ---
  const now = clockNow(); // DarkShapes: the client's clock
  world.lasers = world.lasers.filter(l => l.until > now);

  // --- pattern bullets (deterministic, visual) ---
  const eb = world.eBullets;
  for (let i = eb.length - 1; i >= 0; i--) {
    const b = eb[i];
    b.x += b.vx * dt; b.y += b.vy * dt;
    if (b.x < 0 || b.x > ARENA_W || b.y < 0 || b.y > ARENA_H) eb.splice(i, 1);
  }

  // --- rail tracers: same wall/life rules the server applies to the real rail ---
  const tr = world.tracers;
  for (let i = tr.length - 1; i >= 0; i--) {
    const b = tr[i];
    b.x += b.vx * dt; b.y += b.vy * dt; b.life -= dt;
    if (b.life <= 0) { tr.splice(i, 1); continue; }
    if (b.x < 4 || b.x > ARENA_W - 4) {
      if (b.rc > 0) { b.rc--; b.vx = -b.vx; b.x = clamp(b.x, 4, ARENA_W - 4); }
      else { tr.splice(i, 1); continue; }
    }
    if (b.y < 4 || b.y > ARENA_H - 4) {
      if (b.rc > 0) { b.rc--; b.vy = -b.vy; b.y = clamp(b.y, 4, ARENA_H - 4); }
      else tr.splice(i, 1);
    }
  }

  // --- interpolate remote entities ---
  const curr = world.currSnap;
  if (!curr) return;
  const prev = world.prevSnap;
  const alpha = prev ? clamp((clockNow() - world.currAt) / SNAP_MS, 0, 1) : 1; // DarkShapes: the client's clock
  // local (predicted) ships skip interpolation — theirs is the predicted pos
  world._localIds = new Set([world.myId, ...world.locals.map(l => l.id)]);
  world.players = lerpById(prev?.players, curr.players, alpha, world._localIds);
  world.enemies = lerpById(prev?.enemies, curr.enemies, alpha, -1);
  world.bullets = lerpById(prev?.bullets, curr.bullets, alpha, -1);
  world.zones = curr.zones;
}

function lerpById(prevArr, currArr, a, skip) {
  if (!prevArr) return currArr;
  const skipSet = skip instanceof Set ? skip : new Set([skip]);
  const prevMap = new Map();
  for (const e of prevArr) prevMap.set(e.id, e);
  const out = new Array(currArr.length);
  for (let i = 0; i < currArr.length; i++) {
    const c = currArr[i];
    const p = prevMap.get(c.id);
    if (!p || skipSet.has(c.id)) { out[i] = c; continue; }
    out[i] = { ...c, x: p.x + (c.x - p.x) * a, y: p.y + (c.y - p.y) * a };
  }
  return out;
}

export function handleEvent(ev) {
  if (ev.t === "pattern") {
    const bullets = spawnPattern(ev.pid, ev.seed, ev.x, ev.y, ev.angle);
    for (const b of bullets) world.eBullets.push(b);
    if (world.eBullets.length > 1400) world.eBullets.splice(0, world.eBullets.length - 1400);
  } else if (ev.t === "rail") {
    // HAWK's shots can live and die between two snapshots — the spawn event,
    // not the snapshot, is what guarantees every rail is seen. Own primary
    // rails were already drawn at press time (predictRail); their event only
    // confirms. Ability rails (ab) and remote rails spawn here.
    if (ev.who === world.myId && !ev.ab && consumePredRail()) return;
    for (const a of ev.a) {
      world.tracers.push({
        x: ev.x + Math.cos(a) * 20, y: ev.y + Math.sin(a) * 20,
        vx: Math.cos(a) * ev.sp, vy: Math.sin(a) * ev.sp,
        life: ev.tl, rc: ev.rc || 0,
      });
    }
    if (world.tracers.length > 240) world.tracers.splice(0, world.tracers.length - 240);
  } else if (ev.t === "laser_warn") {
    world.lasers.push({
      id: ev.id, sx: ev.sx, sy: ev.sy, tx: ev.tx, ty: ev.ty,
      firing: false, until: clockNow() + ev.s * 1000 + 60, // DarkShapes: the client's clock
    });
  } else if (ev.t === "laser_fire") {
    world.lasers = world.lasers.filter(l => l.id !== ev.id);
    world.lasers.push({
      id: ev.id, sx: ev.sx, sy: ev.sy, tx: ev.tx, ty: ev.ty,
      firing: true, until: clockNow() + 170, // DarkShapes: the client's clock
    });
  } else if (ev.t === "bomb") {
    world.eBullets.length = 0;
  } else if ((ev.t === "picked" || ev.t === "bought") && ev.who === world.myId) {
    world.myMods.push(ev.mod);
    world.myStats = computeStats(PILOTS[world.myPilot], world.myMods);
  } else if (ev.t === "class_grant") {
    // personal event — the free pilot-signature upgrade this intermission
    world.myMods.push(ev.mod);
    world.myStats = computeStats(PILOTS[world.myPilot], world.myMods);
    world.lastGrant = ev;
  } else if (ev.t === "wave_start" || ev.t === "gameover" || ev.t === "victory") {
    world.eBullets.length = 0;
    world.tracers.length = 0;
    world.predRails.length = 0;
    world.lasers.length = 0;
  }
}

export function myHpMax() { return Math.max(1, 3 + (world.myStats.maxHp | 0)); }

// Estimated current server tick — keeps client-drawn orbitals in phase with
// where the server actually deals blade damage.
export function serverTickNow() {
  if (!world.currSnap) return 0;
  return world.serverTick + (clockNow() - world.currAt) / 1000 * TICK_RATE; // DarkShapes: the client's clock
}

let uiBlock = false;
export function setUiBlocking(b) { uiBlock = b; }
function uiBlocking() { return uiBlock; }

export function inGame() {
  return world.phase === PHASE.WAVE || world.phase === PHASE.INTERMISSION;
}
