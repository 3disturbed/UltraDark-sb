// Canvas renderer — neon per SDD §2.10/§3.7: pre-baked glow sprites (no
// runtime shadowBlur), additive compositing, pooled particles, trauma
// shake, hitstop. Whole arena fits on screen (letterboxed).
//
// DarkShapes: TwinStickTron's client/js/render.js at 7eee633, ported onto the engine's
// Draw canvases, which speak Canvas 2D. Every draw is the original's, call for call;
// what moved is what it drew on:
//   - the page's one canvas is three: the arena, the dark's half-resolution lightmap,
//     which the engine composites over it, and the popups and HUD over both, all under
//     the screens. A frame measures them as a resize event did, in the units the screens
//     are laid out in, as the page's CSS pixels were;
//   - a baked glow sprite is the canvas's glow(), and a pixel sprite its pixels(), which
//     bake the same pictures once each; the zombies' ground, which a canvas cannot keep
//     between frames, is drawn again each frame from its seed;
//   - the context's composite operation and image smoothing are `composite` and `smoothing`;
//   - text is drawn in the engine's mono faces, with a stand-in for each symbol no face
//     draws (engine/glyphs.js), where the browser fell back to an emoji font;
//   - clockNow() is the client's clock, handed over in initRender.
// Each change is written down, with why, in html5/tests/fixtures/darkshapes-template/ports.json.

import { ARENA_W, ARENA_H, PILOTS, ZK, PS, PHASE, PLAYER, WAVE, EK, ORBITAL, TICK_DT } from "../shared/constants.js";
import { ENEMIES } from "../shared/enemies.js";
import { CONSUMABLES } from "../shared/consumables.js";
import { PF, EF } from "../shared/protocol.js";
import { world, myHpMax, serverTickNow } from "./game.js";
import { net } from "./net.js";
import { mulberry32 } from "../shared/rng.js";
import { substitute } from "../engine/glyphs.js";
// UltraDark-sb: the 3D stage over the same world model; `settings.view` decides which picture is drawn.
import * as Stage from "../stage/stage.js";

// player-tunable accessibility settings (SDD §2.11) — main.js loads/saves.
// Themes are three INDEPENDENT cosmetic channels: mix a mech pilot with
// retro enemies in a post-apocalyptic world if that's your thing. Rendering
// only — hitboxes and the server never change.
export const settings = {
  shake: 1, flash: true, floor: 0, volume: 0.25,
  themeWorld: "retro", themePlayers: "retro", themeEnemies: "retro",
  view: "3d",   // UltraDark-sb: "3d" is the stage, "2d" the original's picture
};
export const THEME_OPTS = ["retro", "bots", "zombies", "geom"];

let ctx, W = 0, H = 0, dpr = 1; // DarkShapes: no canvas element; ctx is the Draw canvas being painted
let scale = 1, offX = 0, offY = 0;
let trauma = 0, hitstopT = 0, gridPulse = 0, bombFlashT = 0;
let shakeX = 0, shakeY = 0;
let worldCtx = null, hudCtx = null, lightCtx = null; // DarkShapes: the Draw canvases a frame is painted on
let clockNow = () => 0, pixelRatio = () => 1;       // DarkShapes: the client's clock, and the screen's density

const MAX_PARTICLES = 1500;
const particles = [];
const popups = [];

// DarkShapes: `draw` is the engine's Draw global, `now` the client clock in milliseconds, and `ratio`
// the drawing buffer's pixels to one unit of the screens' layout, the page's devicePixelRatio. The
// canvases paint below every screen, as the page's canvas lay under its DOM.
export function initRender({ draw, now, ratio }) {
  worldCtx = draw.canvas("darkshapes-arena", { space: "screen", order: -30 });
  lightCtx = draw.canvas("darkshapes-dark", { space: "screen", order: -20, resolution: 0.5 });
  hudCtx = draw.canvas("darkshapes-hud", { space: "screen", order: -10 });
  ctx = worldCtx;
  clockNow = now;
  pixelRatio = ratio;
  resize();
  Stage.initStage();   // UltraDark-sb: the deck, the camera and the lights exist from the first frame
}

export function resize() {
  // DarkShapes: measured every frame from the canvas, as the page measured its window on resize; a
  // canvas nothing paints reports no size, and is taken to be the window the project opens.
  dpr = pixelRatio();
  W = (worldCtx.width || 1280) / dpr; H = (worldCtx.height || 720) / dpr;
  const margin = 0.98;
  scale = Math.min(W / ARENA_W, H / ARENA_H) * margin;
  offX = (W - ARENA_W * scale) / 2;
  offY = (H - ARENA_H * scale) / 2;
}

export function screenToWorld(sx, sy) {
  if (Stage.stage.on) return Stage.screenToWorld(sx, sy, W, H);   // UltraDark-sb: a ray to the deck
  return { x: (sx - offX - shakeX) / scale, y: (sy - offY - shakeY) / scale };
}

// ---------- effects API (called from main's event handling) ----------
export function addTrauma(t) { trauma = Math.min(1, trauma + t); }
export function hitstop(ms) { hitstopT = Math.max(hitstopT, ms / 1000); }
export function isHitstopped() { return hitstopT > 0; }
export function fxBomb() { bombFlashT = 0.25; addTrauma(0.5); }
export function gridMilestone() { gridPulse = 1; Stage.flare(); }

// GEOM world: kills send ripples through the grid, Geometry Wars style
const geomRipples = [];

export function fxKill(x, y, color, big = false) {
  if (settings.themeWorld === "geom" && geomRipples.length < 40) {
    geomRipples.push({ x, y, t: 0, big });
  }
  const n = big ? 46 : 14;
  for (let i = 0; i < n && particles.length < MAX_PARTICLES; i++) {
    const a = Math.random() * Math.PI * 2;
    const sp = (big ? 340 : 220) * (0.3 + Math.random() * 0.7);
    particles.push({
      x, y, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp,
      life: 0.35 + Math.random() * (big ? 0.5 : 0.25),
      t: 0, color, size: big ? 5 : 3.2,
    });
  }
}

export function fxMuzzle(x, y, angle, color) {
  Stage.muzzle(world.myId);   // UltraDark-sb: the flash at the chibi's barrel
  for (let i = 0; i < 3 && particles.length < MAX_PARTICLES; i++) {
    const a = angle + (Math.random() - 0.5) * 0.6;
    particles.push({
      x: x + Math.cos(angle) * 20, y: y + Math.sin(angle) * 20,
      vx: Math.cos(a) * 300, vy: Math.sin(a) * 300,
      life: 0.1, t: 0, color, size: 2.5,
    });
  }
}

const cleaves = [];
export function fxCleave(x, y, aim, r, full) {
  cleaves.push({ x, y, aim, r: r ?? 95, full: !!full, t: 0, life: 0.22 });
  if (cleaves.length > 12) cleaves.shift();
}

export function fxPopup(x, y, text, color) {
  popups.push({ x, y, text, color, life: 0.9, t: 0 });
  if (popups.length > 40) popups.shift();
}

// ---------- glow sprites ----------
// DarkShapes: the sprite is the canvas's own glow(), which bakes this one -- solid in the middle,
// two thirds at a quarter out, gone at the edge -- once a colour; a glow here is its colour.
function glow(color) {
  return color;
}

function blit(color, x, y, size) {
  ctx.glow(color, x, y, size);
}

// ---------- main draw ----------
export function draw(dt) {
  if (hitstopT > 0) { hitstopT -= dt; dt = 0; } // freeze world visuals, still render
  trauma = Math.max(0, trauma - 1.4 * (dt || 0.016));
  gridPulse = Math.max(0, gridPulse - 2 * (dt || 0.016));
  bombFlashT = Math.max(0, bombFlashT - (dt || 0.016));

  resize(); // DarkShapes: the canvas is measured each frame
  // UltraDark-sb: on the stage the deck is the backdrop and the 2D layers are projected onto it.
  Stage.setOn(settings.view !== "2d");
  if (Stage.stage.on) {
    const sh3 = trauma * trauma * 22 * settings.shake;
    shakeX = (Math.random() - 0.5) * sh3; shakeY = (Math.random() - 0.5) * sh3;
    drawStage(dt);
    return;
  }
  ctx = worldCtx;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.fillStyle = "#05020c";
  ctx.fillRect(0, 0, W, H);

  const sh = trauma * trauma * 22 * settings.shake;
  shakeX = (Math.random() - 0.5) * sh; shakeY = (Math.random() - 0.5) * sh;
  ctx.translate(offX + shakeX, offY + shakeY);
  ctx.scale(scale, scale);

  drawGrid();
  drawZones(dt);
  drawPickups();
  drawBullets(dt);
  drawEnemies();
  drawLasers();
  drawPlayers();
  drawCleaves(dt);
  drawParticles(dt);

  ctx = hudCtx; // DarkShapes: popups and the HUD go over the dark, on a canvas of their own
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0); // screen space
  drawTheDark();
  drawPopups(dt);
  drawHUD();
  drawScreenFlashes();
}

/** The bomb's white-out and the overdrive frame, on the HUD canvas in screen space. */
function drawScreenFlashes() {
  if (bombFlashT > 0 && settings.flash) {
    ctx.fillStyle = `rgba(255,255,255,${bombFlashT * 1.6})`;
    ctx.fillRect(0, 0, W, H);
  }
  if (world.overdrive) {
    const p = settings.flash ? 0.25 + 0.15 * Math.sin(clockNow() / 90) : 0.3;
    ctx.strokeStyle = `rgba(255,228,91,${p})`;
    ctx.lineWidth = 10;
    ctx.strokeRect(5, 5, W - 10, H - 10);
  }
}

// ---------- UltraDark-sb: the same frame on the 3D stage ----------
// The stage moves the chibis, the swarm, the floor and the camera to the world model, and hands
// back a projection; the neon 2D layers -- bullets, the dark, the tags and popups -- are then
// drawn through it on the same three canvases, and the HUD as it always was.
function drawStage(dt) {
  const dark = darknessLevel();
  Stage.update(dt, { W, H, dark, shakeX, shakeY, clockNow, connected: net.connected, nameOf });
  const args = { W, H, dpr, dt, dark, clockNow, settings, nameOf, substitute, particles, popups, cleaves };
  ctx = worldCtx;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  Stage.drawOverlay("world", ctx, args);
  Stage.drawOverlay("dark", lightCtx, args);
  ctx = hudCtx;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  Stage.drawOverlay("tags", ctx, args);
  drawHUD();
  drawScreenFlashes();
  if (stageDebug === null) stageDebug = typeof GameInstance !== "undefined" && GameInstance.launch.stagedebug === "1";
  if (stageDebug) {
    const s = Stage.stats();
    ctx.font = "12px ui-monospace, monospace";
    ctx.textAlign = "left";
    ctx.fillStyle = "#ffe45b";
    ctx.fillText(`stage ${W}x${H} dpr ${dpr.toFixed(2)} frames ${s.frames} elapsed ${s.elapsed}s time ${Time.time.toFixed(1)} dt ${Time.unscaledDeltaTime.toFixed(4)} pitch ${s.camera.pitch.toFixed(1)} dist ${s.camera.dist.toFixed(1)} hangar ${s.hangar} fighters ${s.fighters}`, 14, H - 6);
  }
}
let stageDebug = null;

/** UltraDark-sb: a server event, for the stage's reactions; main.js hands every event over. */
export function stageEvent(ev) { Stage.event(ev); }

// ---------- the dark (SDD §2.4): light lives where your fire is ----------
function darknessLevel() {
  let d = 0;
  if (world.wave >= WAVE.DARK_START) d = Math.min(0.78, (world.wave - WAVE.DARK_START + 1) * 0.13);
  for (const e of world.enemies) {
    if (e.kind === EK.ULTRADARK) d = e.flags & EF.ENRAGED ? 0.96 : 0.92;
  }
  return d * (1 - settings.floor);
}

function drawTheDark() {
  const dark = darknessLevel();
  if (dark <= 0.02) return;
  const LS = 0.5; // lightmap at half resolution
  const lw = Math.ceil(W * LS), lh = Math.ceil(H * LS);
  // DarkShapes: the lightmap is the dark canvas, a half-resolution surface the engine composites
  // over the arena. Its units are the drawing buffer's, so lightmap pixels are scaled onto them.
  const g = lightCtx;
  g.setTransform(dpr / LS, 0, 0, dpr / LS, 0, 0);
  g.composite = "source-over";
  g.clearRect(0, 0, lw, lh);
  g.fillStyle = `rgba(2,1,8,${dark})`;
  g.fillRect(0, 0, lw, lh);
  g.composite = "destination-out";
  const spr = glow("#ffffff");
  const punch = (wx, wy, r) => {
    const sx = (wx * scale + offX + shakeX) * LS, sy = (wy * scale + offY + shakeY) * LS;
    const s = r * scale * LS * 2;
    g.glow(spr, sx, sy, s);
  };
  for (const p of world.players) {
    if (p.state === PS.ALIVE || p.state === PS.DOWNED) {
      punch(p.id === world.myId ? world.me.x : p.x, p.id === world.myId ? world.me.y : p.y, 170);
    }
  }
  let lights = 0;
  for (const b of world.bullets) { punch(b.x, b.y, 90); if (++lights > 220) break; }
  for (const b of world.tracers) { punch(b.x, b.y, 110); if (++lights > 260) break; }
  for (const b of world.eBullets) { punch(b.x, b.y, 55); if (++lights > 380) break; }
  for (const z of world.zones) {
    if (z.kind === ZK.BLAST || z.kind === ZK.FLAME || z.kind === ZK.WELL) punch(z.x, z.y, z.r * 1.7);
  }
  for (const e of world.enemies) punch(e.x, e.y, ENEMIES[e.kind]?.boss ? 60 : 24); // eyes stay visible
}

function drawPickups() {
  const now = clockNow();
  ctx.font = "bold 15px ui-monospace, monospace";
  ctx.textAlign = "center";
  for (const k of world.pickups ?? []) {
    const def = CONSUMABLES[k.kind];
    if (!def) continue;
    // expiring pickups blink (steady-dim when flashes are disabled)
    let a = 1;
    if (k.ttl < 3) a = settings.flash ? (Math.floor(now / 160) % 2 ? 0.25 : 1) : 0.5;
    ctx.globalAlpha = a;
    ctx.composite = "lighter";
    blit(glow(def.color), k.x, k.y, 46);
    ctx.composite = "source-over";
    ctx.strokeStyle = def.color;
    ctx.lineWidth = 2;
    ctx.beginPath(); ctx.arc(k.x, k.y, 14, 0, Math.PI * 2); ctx.stroke();
    ctx.fillStyle = "#fff";
    ctx.fillText(substitute(def.glyph), k.x, k.y + 5);
  }
  ctx.globalAlpha = 1;
}

function drawLasers() {
  const now = clockNow();
  for (const l of world.lasers) {
    if (l.firing) {
      ctx.composite = "lighter";
      ctx.strokeStyle = "rgba(255,61,240,0.9)";
      ctx.lineWidth = 7;
      ctx.beginPath(); ctx.moveTo(l.sx, l.sy); ctx.lineTo(l.tx, l.ty); ctx.stroke();
      ctx.strokeStyle = "#ffffff";
      ctx.lineWidth = 2.5;
      ctx.beginPath(); ctx.moveTo(l.sx, l.sy); ctx.lineTo(l.tx, l.ty); ctx.stroke();
      ctx.composite = "source-over";
    } else {
      const blink = Math.floor(now / 110) % 2 === 0 || !settings.flash;
      ctx.strokeStyle = `rgba(255,61,240,${blink ? 0.55 : 0.25})`;
      ctx.lineWidth = 1.5;
      ctx.setLineDash([14, 10]);
      // telegraph extends through the lock point, arena-length (matches server)
      const dx = l.tx - l.sx, dy = l.ty - l.sy;
      const len = Math.hypot(dx, dy) || 1;
      ctx.beginPath();
      ctx.moveTo(l.sx, l.sy);
      ctx.lineTo(l.sx + (dx / len) * 2600, l.sy + (dy / len) * 2600);
      ctx.stroke();
      ctx.setLineDash([]);
    }
  }
}

function drawGrid() {
  if (settings.themeWorld === "bots") return drawWorldBots();
  if (settings.themeWorld === "zombies") return drawWorldZombies();
  if (settings.themeWorld === "geom") return drawWorldGeom();
  // retro — the original neon grid
  const bright = 0.10 + gridPulse * 0.22;
  ctx.strokeStyle = `rgba(57,240,255,${bright})`;
  ctx.lineWidth = 1.2;
  ctx.beginPath();
  for (let x = 0; x <= ARENA_W; x += 128) { ctx.moveTo(x, 0); ctx.lineTo(x, ARENA_H); }
  for (let y = 0; y <= ARENA_H; y += 128) { ctx.moveTo(0, y); ctx.lineTo(ARENA_W, y); }
  ctx.stroke();
  ctx.strokeStyle = `rgba(57,240,255,${0.5 + gridPulse * 0.5})`;
  ctx.lineWidth = 3;
  ctx.strokeRect(0, 0, ARENA_W, ARENA_H);
}

// GEOM world — Geometry Wars: a fine electric-blue lattice that RIPPLES
// outward from every kill. Pure vectors on black.
function drawWorldGeom() {
  const bright = 0.16 + gridPulse * 0.3;
  ctx.strokeStyle = `rgba(45,70,220,${bright})`;
  ctx.lineWidth = 1;
  ctx.beginPath();
  for (let x = 0; x <= ARENA_W; x += 64) { ctx.moveTo(x, 0); ctx.lineTo(x, ARENA_H); }
  for (let y = 0; y <= ARENA_H; y += 64) { ctx.moveTo(0, y); ctx.lineTo(ARENA_W, y); }
  ctx.stroke();
  // kill ripples racing across the lattice
  ctx.composite = "lighter";
  for (let i = geomRipples.length - 1; i >= 0; i--) {
    const rp = geomRipples[i];
    rp.t += 0.016;
    const life = rp.big ? 0.9 : 0.5;
    if (rp.t >= life) { geomRipples.splice(i, 1); continue; }
    const p = rp.t / life;
    const r = (rp.big ? 420 : 200) * p;
    ctx.strokeStyle = `rgba(90,140,255,${0.5 * (1 - p)})`;
    ctx.lineWidth = 3 * (1 - p) + 0.5;
    ctx.beginPath(); ctx.arc(rp.x, rp.y, r, 0, Math.PI * 2); ctx.stroke();
    ctx.strokeStyle = `rgba(255,255,255,${0.25 * (1 - p)})`;
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.arc(rp.x, rp.y, r * 0.8, 0, Math.PI * 2); ctx.stroke();
  }
  ctx.composite = "source-over";
  ctx.strokeStyle = `rgba(90,140,255,${0.7 + gridPulse * 0.3})`;
  ctx.lineWidth = 3;
  ctx.strokeRect(0, 0, ARENA_W, ARENA_H);
}

// BOTS world — dystopian sci-fi deck plating: panels, rivets, hazard marks
function drawWorldBots() {
  ctx.fillStyle = "#0a0e14";
  ctx.fillRect(0, 0, ARENA_W, ARENA_H);
  ctx.strokeStyle = `rgba(37,56,78,${0.55 + gridPulse * 0.4})`;
  ctx.lineWidth = 2;
  ctx.beginPath();
  for (let x = 0; x <= ARENA_W; x += 256) { ctx.moveTo(x, 0); ctx.lineTo(x, ARENA_H); }
  for (let y = 0; y <= ARENA_H; y += 256) { ctx.moveTo(0, y); ctx.lineTo(ARENA_W, y); }
  ctx.stroke();
  ctx.strokeStyle = "rgba(22,34,47,0.5)";
  ctx.lineWidth = 1;
  ctx.beginPath();
  for (let x = 128; x <= ARENA_W; x += 256) { ctx.moveTo(x, 0); ctx.lineTo(x, ARENA_H); }
  for (let y = 128; y <= ARENA_H; y += 256) { ctx.moveTo(0, y); ctx.lineTo(ARENA_W, y); }
  ctx.stroke();
  ctx.fillStyle = `rgba(63,88,114,${0.7 + gridPulse * 0.3})`;
  for (let x = 0; x <= ARENA_W; x += 256) {
    for (let y = 0; y <= ARENA_H; y += 256) ctx.fillRect(x - 2, y - 2, 4, 4);
  }
  ctx.strokeStyle = "rgba(163,84,29,0.45)"; // hazard corner brackets
  ctx.lineWidth = 3;
  for (let x = 256; x < ARENA_W; x += 512) {
    for (let y = 256; y < ARENA_H; y += 512) {
      ctx.beginPath(); ctx.moveTo(x - 14, y); ctx.lineTo(x, y); ctx.lineTo(x, y - 14); ctx.stroke();
    }
  }
  ctx.strokeStyle = "#5a4632";
  ctx.lineWidth = 4;
  ctx.strokeRect(0, 0, ARENA_W, ARENA_H);
}

// ZOMBIES world — post-apocalyptic wasteland: a seeded, cached ground texture
// DarkShapes: a canvas keeps no picture between frames, so the ground is drawn again every frame from
// the same seed, straight onto the arena at the size the cached texture was stretched to.
function drawWorldZombies() {
  {
    const g = ctx;
    g.save();
    g.scale(ARENA_W / 1024, ARENA_H / 576);
    const rng = mulberry32(0xDEAD);
    g.fillStyle = "#10120a";
    g.fillRect(0, 0, 1024, 576);
    for (let i = 0; i < 150; i++) { // stains and scorch
      g.fillStyle = `rgba(${8 + rng() * 14 | 0},${10 + rng() * 12 | 0},6,${0.25 + rng() * 0.3})`;
      g.beginPath();
      g.ellipse(rng() * 1024, rng() * 576, 12 + rng() * 46, 8 + rng() * 30, rng() * 3.14, 0, 6.3);
      g.fill();
    }
    g.strokeStyle = "#070804"; // cracked earth
    g.lineWidth = 1.5;
    for (let i = 0; i < 60; i++) {
      let cx = rng() * 1024, cy = rng() * 576;
      g.beginPath(); g.moveTo(cx, cy);
      for (let s = 0; s < 4; s++) { cx += (rng() - 0.5) * 60; cy += (rng() - 0.5) * 40; g.lineTo(cx, cy); }
      g.stroke();
    }
    for (let i = 0; i < 90; i++) { // dead scrub + rubble
      g.fillStyle = rng() < 0.5 ? "#1c2412" : "#26301a";
      g.fillRect(rng() * 1024, rng() * 576, 2 + rng() * 3, 2 + rng() * 3);
    }
    for (let i = 0; i < 14; i++) {
      g.fillStyle = "#2e2418";
      g.fillRect(rng() * 1024, rng() * 576, 6 + rng() * 14, 4 + rng() * 8);
    }
    g.restore();
  }
  ctx.strokeStyle = "#4a3a26";
  ctx.lineWidth = 4;
  ctx.strokeRect(0, 0, ARENA_W, ARENA_H);
}

function drawZones(dt) {
  for (const z of world.zones) {
    if (z.kind === ZK.WARP) {
      const p = 1 - Math.min(1, z.ttl / 0.5);
      ctx.strokeStyle = `rgba(255,91,110,${0.9 - p * 0.5})`;
      ctx.lineWidth = 2.5;
      ctx.beginPath(); ctx.arc(z.x, z.y, z.r * (1.6 - p * 0.6), 0, Math.PI * 2); ctx.stroke();
    } else if (z.kind === ZK.MORTAR_TELE) {
      ctx.strokeStyle = "rgba(255,77,77,0.85)";
      ctx.lineWidth = 3;
      ctx.beginPath(); ctx.arc(z.x, z.y, z.r, 0, Math.PI * 2); ctx.stroke();
      ctx.fillStyle = "rgba(255,77,77,0.12)";
      ctx.beginPath(); ctx.arc(z.x, z.y, z.r * (1 - z.ttl / 1.2), 0, Math.PI * 2); ctx.fill();
    } else if (z.kind === ZK.BLAST) {
      ctx.composite = "lighter";
      blit(glow("#ffffff"), z.x, z.y, z.r * 3.2 * (1 - z.ttl / 0.25 * 0.4));
      ctx.composite = "source-over";
    } else if (z.kind === ZK.FLAME) {
      ctx.composite = "lighter";
      for (let i = 0; i < 4; i++) {
        blit(glow("#ff7a3d"), z.x + (Math.random() - 0.5) * z.r, z.y + (Math.random() - 0.5) * z.r, 60);
      }
      ctx.composite = "source-over";
      ctx.strokeStyle = "rgba(255,122,61,0.5)";
      ctx.lineWidth = 2;
      ctx.beginPath(); ctx.arc(z.x, z.y, z.r, 0, Math.PI * 2); ctx.stroke();
    } else if (z.kind === ZK.AEGIS) {
      ctx.strokeStyle = "rgba(184,255,94,0.6)";
      ctx.lineWidth = 2.5;
      ctx.beginPath(); ctx.arc(z.x, z.y, z.r, 0, Math.PI * 2); ctx.stroke();
      ctx.fillStyle = "rgba(184,255,94,0.05)";
      ctx.fill();
    } else if (z.kind === ZK.WELL) {
      ctx.strokeStyle = "rgba(194,107,250,0.7)";
      ctx.lineWidth = 2;
      const t = clockNow() / 300;
      for (let i = 0; i < 3; i++) {
        const rr = z.r * ((t + i / 3) % 1);
        ctx.beginPath(); ctx.arc(z.x, z.y, z.r - rr, 0, Math.PI * 2); ctx.stroke();
      }
    } else if (z.kind === ZK.BEACON) {
      // AMBER's warp anchor — a pulsing diamond
      const pulse = 0.7 + 0.3 * Math.sin(clockNow() / 220);
      ctx.composite = "lighter";
      blit(glow("#b8ff5e"), z.x, z.y, 70 * pulse);
      ctx.composite = "source-over";
      ctx.strokeStyle = "rgba(184,255,94,0.85)";
      ctx.lineWidth = 2.5;
      ctx.beginPath();
      ctx.moveTo(z.x, z.y - 16); ctx.lineTo(z.x + 12, z.y); ctx.lineTo(z.x, z.y + 16); ctx.lineTo(z.x - 12, z.y);
      ctx.closePath(); ctx.stroke();
    } else if (z.kind === ZK.PYLON) {
      // SPARKS' tesla pylon — crackling triangle with range ring
      const jx = (Math.random() - 0.5) * 3, jy = (Math.random() - 0.5) * 3;
      ctx.composite = "lighter";
      blit(glow("#ffe45b"), z.x + jx, z.y + jy, 54);
      ctx.composite = "source-over";
      ctx.strokeStyle = "#ffe45b";
      ctx.lineWidth = 2.5;
      ctx.beginPath();
      ctx.moveTo(z.x, z.y - 16); ctx.lineTo(z.x + 14, z.y + 12); ctx.lineTo(z.x - 14, z.y + 12);
      ctx.closePath(); ctx.stroke();
      ctx.strokeStyle = "rgba(255,228,91,0.14)";
      ctx.lineWidth = 1.5;
      ctx.beginPath(); ctx.arc(z.x, z.y, z.r, 0, Math.PI * 2); ctx.stroke();
    } else if (z.kind === ZK.TURRET) {
      // RIGG's turret — sturdy little emplacement, blinks when expiring
      const a = z.ttl < 2 && settings.flash ? (Math.floor(clockNow() / 180) % 2 ? 0.4 : 1) : 1;
      ctx.globalAlpha = a;
      ctx.composite = "lighter";
      blit(glow("#ff9e2c"), z.x, z.y, 44);
      ctx.composite = "source-over";
      ctx.strokeStyle = "#ff9e2c";
      ctx.fillStyle = "#0a0416";
      ctx.lineWidth = 2.5;
      ctx.strokeRect(z.x - 11, z.y - 11, 22, 22);
      ctx.fillRect(z.x - 11, z.y - 11, 22, 22);
      ctx.fillStyle = "#ff9e2c";
      ctx.beginPath(); ctx.arc(z.x, z.y, 5, 0, Math.PI * 2); ctx.fill();
      ctx.globalAlpha = 1;
    } else if (z.kind === ZK.VENOM) {
      // BROODMOTHER's poison — a bubbling green pool
      const grad = ctx.createRadialGradient(z.x, z.y, z.r * 0.2, z.x, z.y, z.r);
      grad.addColorStop(0, "rgba(70,110,20,0.5)");
      grad.addColorStop(1, "rgba(40,70,10,0)");
      ctx.fillStyle = grad;
      ctx.beginPath(); ctx.arc(z.x, z.y, z.r, 0, Math.PI * 2); ctx.fill();
      ctx.strokeStyle = "rgba(160,224,90,0.6)";
      ctx.lineWidth = 2;
      ctx.setLineDash([5, 7]);
      ctx.beginPath(); ctx.arc(z.x, z.y, z.r, 0, Math.PI * 2); ctx.stroke();
      ctx.setLineDash([]);
      if (Math.random() < 0.3) { // lazy bubbles
        ctx.fillStyle = "rgba(160,224,90,0.5)";
        const a = Math.random() * Math.PI * 2, rr = Math.random() * z.r * 0.8;
        ctx.beginPath(); ctx.arc(z.x + Math.cos(a) * rr, z.y + Math.sin(a) * rr, 2.5, 0, Math.PI * 2); ctx.fill();
      }
    } else if (z.kind === ZK.DARK) {
      // the Shepherd's herding dark — get out before it bites
      const grad = ctx.createRadialGradient(z.x, z.y, z.r * 0.2, z.x, z.y, z.r);
      grad.addColorStop(0, "rgba(3,1,10,0.92)");
      grad.addColorStop(1, "rgba(3,1,10,0)");
      ctx.fillStyle = grad;
      ctx.beginPath(); ctx.arc(z.x, z.y, z.r, 0, Math.PI * 2); ctx.fill();
      ctx.strokeStyle = "rgba(194,107,250,0.5)";
      ctx.lineWidth = 2;
      ctx.setLineDash([4, 10]);
      ctx.beginPath(); ctx.arc(z.x, z.y, z.r, 0, Math.PI * 2); ctx.stroke();
      ctx.setLineDash([]);
    }
  }
}

function drawBullets() {
  ctx.composite = "lighter";
  const pilotOf = new Map();
  for (const p of world.players) pilotOf.set(p.id, p.pilot);
  for (const b of world.bullets) {
    const pilot = pilotOf.get(b.owner) ?? 0;
    // rails are drawn from their spawn events (world.tracers), not the 15Hz
    // snapshot — drawing both would double every shot
    if (PILOTS[pilot]?.weapon.kind === "rail") continue;
    const color = PILOTS[pilot]?.color ?? "#39f0ff";
    blit(glow(color), b.x, b.y, 26);
    ctx.fillStyle = "#fff";
    ctx.fillRect(b.x - 2, b.y - 2, 4, 4);
  }
  for (const b of world.tracers) {
    const l = Math.hypot(b.vx, b.vy) || 1;
    const nx = b.vx / l, ny = b.vy / l;
    blit(glow("#ff5b8e"), b.x, b.y, 30);
    ctx.strokeStyle = "rgba(255,145,180,0.85)";
    ctx.lineWidth = 3;
    ctx.beginPath(); ctx.moveTo(b.x - nx * 34, b.y - ny * 34); ctx.lineTo(b.x, b.y); ctx.stroke();
    ctx.strokeStyle = "#fff";
    ctx.lineWidth = 1.5;
    ctx.beginPath(); ctx.moveTo(b.x - nx * 16, b.y - ny * 16); ctx.lineTo(b.x, b.y); ctx.stroke();
  }
  for (const b of world.eBullets) {
    blit(glow("#ff5b8e"), b.x, b.y, b.r * 5.5);
    ctx.fillStyle = "#ffd7e5";
    ctx.beginPath(); ctx.arc(b.x, b.y, b.r * 0.75, 0, Math.PI * 2); ctx.fill();
  }
  ctx.composite = "source-over";
}

function drawEnemies() {
  let boss = null;
  // warden shield bubbles under everything (the order-of-kill puzzle, visible)
  for (const e of world.enemies) {
    const def = ENEMIES[e.kind];
    if (def?.shieldR) {
      ctx.strokeStyle = "rgba(143,180,255,0.35)";
      ctx.lineWidth = 2;
      ctx.setLineDash([6, 8]);
      ctx.beginPath(); ctx.arc(e.x, e.y, def.shieldR, 0, Math.PI * 2); ctx.stroke();
      ctx.setLineDash([]);
      ctx.fillStyle = "rgba(143,180,255,0.05)";
      ctx.fill();
    }
  }
  for (const e of world.enemies) {
    const def = ENEMIES[e.kind];
    if (!def) continue;
    if (def.boss) boss = { e, def };
    const enraged = e.flags & EF.ENRAGED;
    const phased = e.flags & EF.PHASED;
    const open = e.flags & EF.OPEN;
    const chilled = e.flags & EF.CHILLED;
    const color = open ? "#ffffff" : chilled ? "#bfe9ff" : enraged ? "#ff4d4d" : def.color;
    if (phased) ctx.globalAlpha = 0.3;
    ctx.composite = "lighter";
    blit(glow(color), e.x, e.y, def.radius * (open ? 5.5 : 4));
    ctx.composite = "source-over";
    if (settings.themeEnemies === "bots") {
      drawRobot(e, def, color);
    } else if (settings.themeEnemies === "zombies") {
      drawZombieEnemy(e, def, color);
    } else if (settings.themeEnemies === "geom") {
      // Geometry Wars: pure glowing wireframe, no fill, double-struck
      ctx.strokeStyle = color;
      ctx.lineWidth = 3.5;
      shapePath(def.shape, e.x, e.y, def.radius, clockNow() / 1000 + e.id);
      ctx.stroke();
      ctx.strokeStyle = "rgba(255,255,255,0.75)";
      ctx.lineWidth = 1.2;
      shapePath(def.shape, e.x, e.y, def.radius, clockNow() / 1000 + e.id);
      ctx.stroke();
    } else {
      ctx.strokeStyle = color;
      ctx.fillStyle = "#0a0416";
      ctx.lineWidth = open ? 4 : 2.5;
      shapePath(def.shape, e.x, e.y, def.radius, clockNow() / 1000 + e.id);
      ctx.fill(); ctx.stroke();
      // eyes stay visible (readable chaos)
      ctx.fillStyle = color;
      ctx.beginPath(); ctx.arc(e.x, e.y, Math.max(2.5, def.radius * 0.18), 0, Math.PI * 2); ctx.fill();
    }
    if (phased) ctx.globalAlpha = 1;
    if (def.boss || e.hpPct < 100) {
      ctx.fillStyle = "rgba(255,255,255,0.15)";
      ctx.fillRect(e.x - def.radius, e.y - def.radius - 10, def.radius * 2, 4);
      ctx.fillStyle = color;
      ctx.fillRect(e.x - def.radius, e.y - def.radius - 10, def.radius * 2 * (e.hpPct / 100), 4);
    }
  }
  if (boss) {
    // boss bar top-center (world space top)
    const bw = ARENA_W * 0.4;
    ctx.fillStyle = "rgba(255,255,255,0.12)";
    ctx.fillRect(ARENA_W / 2 - bw / 2, 18, bw, 12);
    ctx.fillStyle = boss.e.flags & EF.ENRAGED ? "#ff4d4d" : boss.def.color;
    ctx.fillRect(ARENA_W / 2 - bw / 2, 18, bw * (boss.e.hpPct / 100), 12);
    ctx.font = "bold 22px ui-monospace, monospace";
    ctx.textAlign = "center";
    ctx.fillStyle = "#fff";
    ctx.fillText(boss.def.name, ARENA_W / 2, 52);
  }
}

// ---------- theme painters ----------
function mixColor(hexA, hexB, t) {
  const a = parseInt(hexA.slice(1), 16), b = parseInt(hexB.slice(1), 16);
  const c = (sh) => Math.round(((a >> sh & 255) * (1 - t)) + ((b >> sh & 255) * t));
  return `rgb(${c(16)},${c(8)},${c(0)})`;
}

// 8-bit pixel sprites, cached per pattern+color. '.'=transparent,
// B=body, D=dark shell, E=eye (bright), T=tread, C=accent, G=gun
const pixCache = new Map();
function pixelSprite(key, rows, palette) {
  let c = pixCache.get(key);
  if (c) return c;
  const px = 4;
  const w = Math.max(...rows.map(r => r.length));
  // DarkShapes: the canvas's pixels() bakes the pattern once a key, as this did; kept here is what a
  // draw hands it, and the size the baked sprite had.
  c = { key, rows, palette, width: w * px, height: rows.length * px };
  pixCache.set(key, c);
  return c;
}

const BOT_PATTERNS = {
  compact: [ // drones, mites, leeches — little scuttlers
    "....DDDD....",
    "...DBBBBD...",
    "..DBBBBBBD..",
    ".DBEBBBBEBD.",
    ".DBBBBBBBBD.",
    ".DBBDBBDBBD.",
    "..DBBBBBBD..",
    "...DDDDDD...",
    "..TT....TT..",
    "..TT....TT..",
  ],
  wing: [ // weavers, snipers, ghosts — fliers
    "D....DD....D",
    "DD..DBBD..DD",
    ".DDDBBBBDDD.",
    "..DBEBBEBD..",
    "..DBBBBBBD..",
    ".DDBBDDBBDD.",
    ".D.DBBBBD.D.",
    "....DBBD....",
    ".....DD.....",
  ],
  tank: [ // brutes, mortars, forges, blocks — treaded armor
    "..DDDDDDDD..",
    ".DBBBBBBBBD.",
    "DBBEBBBBEBBD",
    "DBBBBBBBBBBD",
    "DBBBDDDDBBBD",
    "DBBBBBBBBBBD",
    ".DBBBBBBBBD.",
    "TTTTTTTTTTTT",
    "TDTDTDTDTDTD",
    "TTTTTTTTTTTT",
  ],
  saw: [ // spinners — spiked shredders
    "..D..DD..D..",
    ".DBDDBBDDBD.",
    "DBBBBBBBBBBD",
    ".DBEBBBBEBD.",
    "DBBBBBBBBBBD",
    ".DBBBDDBBBD.",
    "DBBBBBBBBBBD",
    ".DBDDBBDDBD.",
    "..D..DD..D..",
  ],
  magnet: [ // magnets, rings — horseshoe frames
    ".DDDD..DDDD.",
    ".DBBD..DBBD.",
    ".DBBD..DBBD.",
    ".DBBDDDDBBD.",
    ".DBBBBBBBBD.",
    "..DBEBBEBD..",
    "..DBBBBBBD..",
    "...DDDDDD...",
  ],
  shield: [ // wardens, pents — plated guards
    "....DDDD....",
    "..DDBBBBDD..",
    ".DBBBBBBBBD.",
    ".DBEBBBBEBD.",
    ".DBBBBBBBBD.",
    ".DDBBBBBBDD.",
    "..DBBBBBBD..",
    "..DDBBBBDD..",
    "...DDBBDD...",
    "....DDDD....",
  ],
};
const SHAPE_TO_BOT = {
  circle: "compact", dot: "compact", crescent: "compact",
  diamond: "wing", tri: "wing", ghost: "wing",
  hex: "tank", square: "tank", block: "tank",
  gear: "saw", ring: "magnet", hexring: "magnet", pent: "shield",
  pin: "compact", chevron: "wing", pinwheel: "saw", hole: "magnet", cocoon: "tank",
};

function drawRobot(e, def, color) {
  const pat = BOT_PATTERNS[SHAPE_TO_BOT[def.shape] ?? "compact"];
  const key = `bot|${SHAPE_TO_BOT[def.shape]}|${color}`;
  const spr = pixelSprite(key, pat, {
    B: mixColor(color, "#20242c", 0.45),
    D: "#0d1016",
    E: color,
    T: "#3a4148",
  });
  const size = def.radius * 2.6;
  const bob = Math.sin(clockNow() / 250 + e.id) * 1.5;
  ctx.smoothing = false;
  ctx.pixels(spr.key, spr.rows, spr.palette, e.x - size / 2, e.y - size / 2 + bob, size, size * (spr.height / spr.width));
  ctx.smoothing = true;
}

function drawZombieEnemy(e, def, color) {
  const r = def.radius;
  const t = clockNow() / 1000;
  const body = mixColor(color, "#5f7048", 0.6); // sickly flesh tint of the kind color
  // shambling wobble blob — silhouette scale still matches the hitbox
  ctx.fillStyle = "#151a10";
  ctx.strokeStyle = body;
  ctx.lineWidth = 2.5;
  ctx.beginPath();
  const verts = 10;
  for (let i = 0; i <= verts; i++) {
    const a = (i / verts) * Math.PI * 2;
    const rr = r * (0.84 + 0.16 * Math.sin(t * 3.1 + i * 2.7 + e.id));
    const px = e.x + Math.cos(a) * rr, py = e.y + Math.sin(a) * rr;
    i ? ctx.lineTo(px, py) : ctx.moveTo(px, py);
  }
  ctx.closePath();
  ctx.fill(); ctx.stroke();
  if (r < 20) { // small shamblers get grasping arms
    const reach = Math.sin(t * 5 + e.id) * 0.35;
    for (const side of [-0.6 + reach, 0.6 - reach]) {
      ctx.beginPath();
      ctx.moveTo(e.x + Math.cos(side) * r * 0.7, e.y + Math.sin(side) * r * 0.7);
      ctx.lineTo(e.x + Math.cos(side) * (r + 8), e.y + Math.sin(side) * (r + 8));
      ctx.stroke();
    }
  } else { // bigger horrors get a maw
    ctx.strokeStyle = "#6e1d1d";
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.arc(e.x, e.y + r * 0.25, r * 0.35, 0.2, Math.PI - 0.2);
    ctx.stroke();
  }
  // glowing eyes — readable in any theme, and the dark's lightmap expects them
  ctx.fillStyle = color;
  const eo = r * 0.3;
  ctx.beginPath(); ctx.arc(e.x - eo, e.y - r * 0.2, Math.max(2, r * 0.12), 0, Math.PI * 2); ctx.fill();
  ctx.beginPath(); ctx.arc(e.x + eo, e.y - r * 0.2, Math.max(2, r * 0.12), 0, Math.PI * 2); ctx.fill();
}

const MECH_PATTERN = [
  ".....GG.....",
  ".....GG.....",
  "..DDCCCCDD..",
  ".DCCCCCCCCD.",
  "DDCCDEEDCCDD",
  "DCCCDEEDCCCD",
  "DCCCCCCCCCCD",
  ".DCCCDDCCCD.",
  ".DDCC..CCDD.",
  "..DCC..CCD..",
  "..DD....DD..",
  ".DDD....DDD.",
];

function drawMechPlayer(x, y, aim, pilotColor) {
  const spr = pixelSprite(`mech|${pilotColor}`, MECH_PATTERN, {
    C: mixColor(pilotColor, "#2a3140", 0.35),
    D: "#12161f",
    E: "#ffffff",
    G: "#6a7688",
  });
  const size = 42;
  ctx.save();
  ctx.translate(x, y);
  ctx.rotate(aim + Math.PI / 2); // pattern faces up; aim is +x
  ctx.smoothing = false;
  ctx.pixels(spr.key, spr.rows, spr.palette, -size / 2, -size / 2, size, size);
  ctx.smoothing = true;
  ctx.restore();
}

function drawSoldierPlayer(x, y, aim, pilotColor) {
  ctx.save();
  ctx.translate(x, y);
  ctx.rotate(aim);
  ctx.fillStyle = "#222"; // rifle forward
  ctx.fillRect(4, -2, 20, 4);
  ctx.fillStyle = "#39402a"; // fatigues
  ctx.strokeStyle = pilotColor;
  ctx.lineWidth = 2;
  ctx.beginPath(); ctx.ellipse(0, 0, 12, 9, 0, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
  ctx.fillStyle = "#2c331e"; // helmet
  ctx.beginPath(); ctx.arc(-2, 0, 7, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
  ctx.fillStyle = pilotColor; // squad-colored visor strip
  ctx.fillRect(2, -3, 3, 6);
  ctx.restore();
}

function shapePath(shape, x, y, r, t) {
  ctx.beginPath();
  if (shape === "circle" || shape === "dot") {
    ctx.arc(x, y, r, 0, Math.PI * 2);
  } else if (shape === "diamond") {
    ctx.moveTo(x, y - r); ctx.lineTo(x + r, y); ctx.lineTo(x, y + r); ctx.lineTo(x - r, y); ctx.closePath();
  } else if (shape === "hex" || shape === "hexring") {
    for (let i = 0; i < 6; i++) {
      const a = t * 0.4 + (i / 6) * Math.PI * 2;
      const px = x + Math.cos(a) * r, py = y + Math.sin(a) * r;
      i ? ctx.lineTo(px, py) : ctx.moveTo(px, py);
    }
    ctx.closePath();
  } else if (shape === "gear") {
    for (let i = 0; i < 16; i++) {
      const a = t * 2 + (i / 16) * Math.PI * 2;
      const rr = i % 2 ? r : r * 0.62;
      const px = x + Math.cos(a) * rr, py = y + Math.sin(a) * rr;
      i ? ctx.lineTo(px, py) : ctx.moveTo(px, py);
    }
    ctx.closePath();
  } else if (shape === "square" || shape === "block") {
    const s = r * 0.9;
    ctx.rect(x - s, y - s, s * 2, s * 2);
  } else if (shape === "tri") {
    ctx.moveTo(x + r, y);
    ctx.lineTo(x - r * 0.7, y + r * 0.8);
    ctx.lineTo(x - r * 0.7, y - r * 0.8);
    ctx.closePath();
  } else if (shape === "pent") {
    for (let i = 0; i < 5; i++) {
      const a = -Math.PI / 2 + (i / 5) * Math.PI * 2;
      const px = x + Math.cos(a) * r, py = y + Math.sin(a) * r;
      i ? ctx.lineTo(px, py) : ctx.moveTo(px, py);
    }
    ctx.closePath();
  } else if (shape === "ring") {
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.moveTo(x + r * 0.55, y);
    ctx.arc(x, y, r * 0.55, 0, Math.PI * 2, true);
  } else if (shape === "crescent") {
    ctx.arc(x, y, r, Math.PI * 0.25, Math.PI * 1.75);
    ctx.quadraticCurveTo(x + r * 0.2, y, x + Math.cos(Math.PI * 0.25) * r, y + Math.sin(Math.PI * 0.25) * r);
  } else if (shape === "ghost") {
    ctx.arc(x, y, r, Math.PI, 0);
    ctx.lineTo(x + r, y + r * 0.8);
    ctx.lineTo(x + r * 0.5, y + r * 0.5);
    ctx.lineTo(x, y + r * 0.9);
    ctx.lineTo(x - r * 0.5, y + r * 0.5);
    ctx.lineTo(x - r, y + r * 0.8);
    ctx.closePath();
  } else if (shape === "pin") {
    // Wanderer: a slowly twirling 4-spike pin
    for (let i = 0; i < 8; i++) {
      const a = t * 1.2 + (i / 8) * Math.PI * 2;
      const rr = i % 2 ? r : r * 0.35;
      const px = x + Math.cos(a) * rr, py = y + Math.sin(a) * rr;
      i ? ctx.lineTo(px, py) : ctx.moveTo(px, py);
    }
    ctx.closePath();
  } else if (shape === "chevron") {
    // Dodger: the classic nested green Vs
    ctx.moveTo(x - r, y - r * 0.7); ctx.lineTo(x, y + r * 0.7); ctx.lineTo(x + r, y - r * 0.7);
    ctx.lineTo(x + r * 0.55, y - r * 0.7); ctx.lineTo(x, y + r * 0.1); ctx.lineTo(x - r * 0.55, y - r * 0.7);
    ctx.closePath();
  } else if (shape === "pinwheel") {
    // Pinwheel: four curved blades around a hub
    for (let b = 0; b < 4; b++) {
      const a = t * 2.4 + (b / 4) * Math.PI * 2;
      ctx.moveTo(x, y);
      ctx.quadraticCurveTo(
        x + Math.cos(a) * r * 1.2, y + Math.sin(a) * r * 1.2,
        x + Math.cos(a + 0.8) * r * 0.75, y + Math.sin(a + 0.8) * r * 0.75,
      );
    }
  } else if (shape === "hole") {
    // Black Hole: pulsing concentric rings
    const pulse = 1 + 0.15 * Math.sin(t * 5);
    ctx.arc(x, y, r * pulse, 0, Math.PI * 2);
    ctx.moveTo(x + r * 0.55 * pulse, y);
    ctx.arc(x, y, r * 0.55 * pulse, 0, Math.PI * 2, true);
  } else if (shape === "cocoon") {
    ctx.ellipse(x, y, r * 0.75, r * 1.1, 0.3, 0, Math.PI * 2);
  }
}

function drawPlayers() {
  const localPred = new Map();
  for (const seat of world.locals) if (seat.id) localPred.set(seat.id, seat.pred);
  for (const p of world.players) {
    const pilot = PILOTS[p.pilot] ?? PILOTS[0];
    const mine = p.id === world.myId;
    const pred = mine ? world.me : localPred.get(p.id);
    const x = pred ? pred.x : p.x;
    const y = pred ? pred.y : p.y;
    const aim = pred ? pred.aim : p.aim;

    if (p.state === PS.SPECTATING) continue;
    if (p.state === PS.OUT) continue;
    if (p.state === PS.DOWNED) {
      // downed core: pulsing ring + revive arc
      const pulse = 0.6 + 0.4 * Math.sin(clockNow() / 160);
      ctx.composite = "lighter";
      blit(glow(pilot.color), x, y, 60 * pulse);
      ctx.composite = "source-over";
      ctx.strokeStyle = pilot.color;
      ctx.lineWidth = 3;
      ctx.beginPath(); ctx.arc(x, y, 20, 0, Math.PI * 2); ctx.stroke();
      if (p.flags & PF.REVIVING) {
        ctx.strokeStyle = "#b8ff5e";
        ctx.lineWidth = 5;
        ctx.beginPath(); ctx.arc(x, y, 28, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * ((clockNow() / 500) % 1)); ctx.stroke();
      }
      ctx.font = "12px ui-monospace, monospace";
      ctx.textAlign = "center";
      ctx.fillStyle = "#fff";
      ctx.fillText("DOWNED", x, y - 34);
      continue;
    }

    const flicker = (p.flags & PF.IFRAMES) && Math.floor(clockNow() / 70) % 2 === 0;
    if (flicker && !mine) continue;
    ctx.globalAlpha = flicker ? 0.45 : 1;
    ctx.composite = "lighter";
    blit(glow(pilot.color), x, y, 64);
    ctx.composite = "source-over";
    // AMBER's heal aura — the soft ring that follows her
    if (p.pilot === 2) {
      const ar = 140 * (mine ? (world.myStats.auraR ?? 1) : 1);
      ctx.strokeStyle = "rgba(184,255,94,0.28)";
      ctx.lineWidth = 2;
      ctx.setLineDash([3, 9]);
      ctx.beginPath(); ctx.arc(x, y, ar, 0, Math.PI * 2); ctx.stroke();
      ctx.setLineDash([]);
      ctx.fillStyle = "rgba(184,255,94,0.03)";
      ctx.fill();
    }
    // orbital blades — rendered from the same tick formula the server
    // damages with, so what you see is what shreds
    if (p.orbitals > 0) {
      const k = p.orbitals;
      const base = serverTickNow() * TICK_DT * ORBITAL.ROT;
      ctx.composite = "lighter";
      for (let i = 0; i < k; i++) {
        const a = base + (i / k) * Math.PI * 2;
        const ox = x + Math.cos(a) * ORBITAL.R, oy = y + Math.sin(a) * ORBITAL.R;
        blit(glow("#e8fbff"), ox, oy, 34);
        ctx.save();
        ctx.translate(ox, oy);
        ctx.rotate(a * 6);
        ctx.fillStyle = "#e8fbff";
        ctx.beginPath();
        ctx.moveTo(9, 0); ctx.lineTo(0, 4); ctx.lineTo(-9, 0); ctx.lineTo(0, -4);
        ctx.closePath(); ctx.fill();
        ctx.restore();
      }
      ctx.composite = "source-over";
      ctx.strokeStyle = "rgba(232,251,255,0.14)";
      ctx.lineWidth = 1.5;
      ctx.beginPath(); ctx.arc(x, y, ORBITAL.R, 0, Math.PI * 2); ctx.stroke();
    }
    // the ship — themed: retro arrow / 8-bit mech / soldier / GW claw
    if (settings.themePlayers === "bots") {
      drawMechPlayer(x, y, aim, pilot.color);
    } else if (settings.themePlayers === "zombies") {
      drawSoldierPlayer(x, y, aim, pilot.color);
    } else if (settings.themePlayers === "geom") {
      ctx.save();
      ctx.translate(x, y);
      ctx.rotate(aim);
      ctx.strokeStyle = pilot.color;
      ctx.lineWidth = 2.5;
      ctx.beginPath(); // the Geometry Wars claw — wireframe, finned
      ctx.moveTo(18, 0); ctx.lineTo(-8, 9); ctx.lineTo(-3, 0); ctx.lineTo(-8, -9);
      ctx.closePath(); ctx.stroke();
      ctx.beginPath();
      ctx.moveTo(-3, 0); ctx.lineTo(-15, 6);
      ctx.moveTo(-3, 0); ctx.lineTo(-15, -6);
      ctx.stroke();
      ctx.restore();
    } else {
      ctx.save();
      ctx.translate(x, y);
      ctx.rotate(aim);
      ctx.strokeStyle = pilot.color;
      ctx.fillStyle = "#0a0416";
      ctx.lineWidth = 2.5;
      ctx.beginPath();
      ctx.moveTo(16, 0); ctx.lineTo(-11, 10); ctx.lineTo(-6, 0); ctx.lineTo(-11, -10);
      ctx.closePath();
      ctx.fill(); ctx.stroke();
      ctx.restore();
    }
    // THADDIUS polarity marker: ± ring — opposite signs must NOT stand together
    const myCharge = world.charges?.[p.id];
    if (myCharge) {
      const cc = myCharge > 0 ? "#ff5b5b" : "#5b8fff";
      ctx.strokeStyle = cc;
      ctx.lineWidth = 2.5;
      ctx.setLineDash([4, 6]);
      ctx.beginPath(); ctx.arc(x, y, 24, 0, Math.PI * 2); ctx.stroke();
      ctx.setLineDash([]);
      ctx.font = "bold 16px ui-monospace, monospace";
      ctx.textAlign = "center";
      ctx.fillStyle = cc;
      ctx.fillText(myCharge > 0 ? "+" : "−", x, y - 30);
    }
    // BROODMOTHER web wrap: cocooned pilots read as tangled, not gone
    if (p.flags & PF.WRAPPED) {
      ctx.strokeStyle = "rgba(216,216,168,0.85)";
      ctx.lineWidth = 1.5;
      for (let i = 0; i < 4; i++) {
        const a = (i / 4) * Math.PI + clockNow() / 900;
        ctx.beginPath();
        ctx.moveTo(x - Math.cos(a) * 22, y - Math.sin(a) * 22);
        ctx.lineTo(x + Math.cos(a) * 22, y + Math.sin(a) * 22);
        ctx.stroke();
      }
      ctx.beginPath(); ctx.arc(x, y, 20, 0, Math.PI * 2); ctx.stroke();
    }
    // class symbol at the centre — upright regardless of aim or theme
    ctx.font = "bold 11px ui-monospace, monospace";
    ctx.textAlign = "center";
    ctx.fillStyle = settings.themePlayers === "retro" ? pilot.color : "#ffffff";
    ctx.fillText(substitute(pilot.symbol), x - 1, y + 4);
    ctx.globalAlpha = 1;
    if (!mine) {
      ctx.font = "11px ui-monospace, monospace";
      ctx.textAlign = "center";
      ctx.fillStyle = "rgba(255,255,255,0.55)";
      ctx.fillText(substitute(nameOf(p.id)), x, y - 24);
    }
  }
}

const names = new Map();
export function setNames(roster) {
  Stage.setRoster(roster);   // UltraDark-sb: who stands in the hangar
  names.clear();
  for (const r of roster) names.set(r.id, r.name);
}
function nameOf(id) { return names.get(id) ?? "PILOT"; }

// DAVE's melee swing — a fading arc slash
function drawCleaves(dt) {
  ctx.composite = "lighter";
  for (let i = cleaves.length - 1; i >= 0; i--) {
    const c = cleaves[i];
    c.t += dt;
    if (c.t >= c.life) { cleaves.splice(i, 1); continue; }
    const p = c.t / c.life;
    const half = c.full ? Math.PI : 1.05;
    ctx.strokeStyle = `rgba(194,107,250,${0.9 * (1 - p)})`;
    ctx.lineWidth = 14 * (1 - p) + 3;
    ctx.beginPath();
    ctx.arc(c.x, c.y, c.r * (0.6 + 0.4 * p), c.aim - half, c.aim + half);
    ctx.stroke();
    ctx.strokeStyle = `rgba(255,255,255,${0.7 * (1 - p)})`;
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.arc(c.x, c.y, c.r * (0.6 + 0.4 * p), c.aim - half, c.aim + half);
    ctx.stroke();
  }
  ctx.composite = "source-over";
}

function drawParticles(dt) {
  ctx.composite = "lighter";
  for (let i = particles.length - 1; i >= 0; i--) {
    const p = particles[i];
    p.t += dt;
    if (p.t >= p.life) { particles.splice(i, 1); continue; }
    p.x += p.vx * dt; p.y += p.vy * dt;
    p.vx *= 0.94; p.vy *= 0.94;
    const a = 1 - p.t / p.life;
    ctx.globalAlpha = a;
    blit(glow(p.color), p.x, p.y, p.size * 8 * a + 4);
  }
  ctx.globalAlpha = 1;
  ctx.composite = "source-over";
}

function drawPopups(dt) {
  ctx.font = "bold 15px ui-monospace, monospace";
  ctx.textAlign = "center";
  for (let i = popups.length - 1; i >= 0; i--) {
    const p = popups[i];
    p.t += dt;
    if (p.t >= p.life) { popups.splice(i, 1); continue; }
    const sx = p.x * scale + offX, sy = (p.y - 30 - p.t * 50) * scale + offY;
    ctx.globalAlpha = 1 - p.t / p.life;
    ctx.fillStyle = p.color;
    ctx.fillText(substitute(p.text), sx, sy);
  }
  ctx.globalAlpha = 1;
}

function drawHUD() {
  if (world.phase === PHASE.LOBBY) return;
  const pad = 14;
  ctx.textAlign = "left";
  // score
  ctx.font = "bold 20px ui-monospace, monospace";
  ctx.fillStyle = "#ffe45b";
  ctx.fillText(fmt(world.banked), pad, 30);
  ctx.font = "bold 15px ui-monospace, monospace";
  ctx.fillStyle = world.unbanked > 0 ? "#ffffff" : "#66779c";
  ctx.fillText(`+${fmt(world.unbanked)} AT RISK`, pad, 52);
  // multiplier bar
  const mw = 180;
  ctx.fillStyle = "rgba(255,255,255,0.12)";
  ctx.fillRect(pad, 62, mw, 8);
  const mf = (world.mult - 1) / 9;
  ctx.fillStyle = world.overdrive ? "#ffe45b" : "#39f0ff";
  ctx.fillRect(pad, 62, mw * mf, 8);
  ctx.fillStyle = "#fff";
  ctx.font = "bold 13px ui-monospace, monospace";
  ctx.fillText(`×${world.mult.toFixed(1)}${world.overdrive ? " OVERDRIVE" : ""}`, pad + mw + 8, 71);
  // challenge target — the score to beat, live (own line under the bar)
  if (world.challenge) {
    const beat = world.banked + world.unbanked > world.challenge.s;
    ctx.fillStyle = beat ? "#b8ff5e" : "#ff9aa6";
    ctx.fillText(substitute(`⚔ ${world.challenge.n} ${world.challenge.s.toLocaleString("en-US")}${beat ? " ✓ BEATEN" : ""}`), pad, 90);
  }
  // wave (top right). UltraDark-sb: the engine's runtime keeps a button in that corner, so the
  // two lines stand clear of it.
  const cornerPad = pad + 52;
  ctx.textAlign = "right";
  ctx.font = "bold 20px ui-monospace, monospace";
  ctx.fillStyle = "#fff";
  ctx.fillText(`WAVE ${world.wave}`, W - cornerPad, 30);
  ctx.font = "13px ui-monospace, monospace";
  ctx.fillStyle = "#8fa3c8";
  if (world.phase === PHASE.WAVE) ctx.fillText(`${world.enemiesLeft} HOSTILES`, W - cornerPad, 50);
  if (net.rttMs) ctx.fillText(`${net.rttMs}ms`, W - pad, H - 10);
  // hp pips (bottom left) — max grows with Plating/Turtle mods
  const pips = myHpMax();
  for (let i = 0; i < pips; i++) {
    ctx.fillStyle = i < world.myHp ? "#ff5b6e" : "rgba(255,255,255,0.15)";
    ctx.fillRect(pad + i * 26, H - 30, 20, 14);
  }
  // couch co-op seat rows (above P1's hp pips, colour-coded per pilot)
  let couchY = H - 54;
  for (const seat of world.locals) {
    if (!seat.id || !seat.hud) continue;
    const pilot = PILOTS[seat.pilot] ?? PILOTS[0];
    ctx.textAlign = "left";
    ctx.font = "bold 13px ui-monospace, monospace";
    ctx.fillStyle = pilot.color;
    ctx.fillText(substitute(`${pilot.symbol} ${seat.name}`), pad, couchY);
    for (let i = 0; i < 3; i++) {
      ctx.fillStyle = i < seat.hud.hp ? "#ff5b6e" : "rgba(255,255,255,0.15)";
      ctx.fillRect(pad + 110 + i * 16, couchY - 10, 12, 10);
    }
    ctx.fillStyle = "#fff";
    ctx.font = "12px ui-monospace, monospace";
    let extras = `💣${seat.hud.bombs} ⬡${seat.hud.cores ?? 0}`;
    if (seat.hud.cons?.length) extras += "  " + seat.hud.cons.map(k => CONSUMABLES[k]?.glyph ?? "?").join(" ");
    if (seat.hud.state === PS.DOWNED) extras = "⬇ DOWNED";
    if (seat.hud.state === PS.SPECTATING) extras = "…next wave";
    ctx.fillText(substitute(extras), pad + 170, couchY);
    couchY -= 22;
  }
  // consumable slots (bottom centre) — F / gamepad X / touch ✚ to use
  const cons = world.myCons ?? [];
  const slotW = 36, total = 3 * (slotW + 6);
  for (let i = 0; i < 3; i++) {
    const sx = W / 2 - total / 2 + i * (slotW + 6);
    const kind = cons[i];
    const def = kind ? CONSUMABLES[kind] : null;
    ctx.fillStyle = "rgba(255,255,255,0.06)";
    ctx.fillRect(sx, H - 46, slotW, 32);
    ctx.strokeStyle = def ? def.color : "rgba(255,255,255,0.15)";
    ctx.lineWidth = i === 0 && def ? 2.5 : 1;
    ctx.strokeRect(sx, H - 46, slotW, 32);
    if (def) {
      ctx.font = "bold 16px ui-monospace, monospace";
      ctx.textAlign = "center";
      ctx.fillStyle = "#fff";
      ctx.fillText(substitute(def.glyph), sx + slotW / 2, H - 24);
    }
  }
  if (cons.length) {
    ctx.font = "10px ui-monospace, monospace";
    ctx.textAlign = "center";
    ctx.fillStyle = "#66779c";
    ctx.fillText("F · USE", W / 2, H - 52);
  }
  // stasis indicator
  if (world.stasis > 0) {
    ctx.strokeStyle = "rgba(143,180,255,0.5)";
    ctx.lineWidth = 6;
    ctx.strokeRect(3, 3, W - 6, H - 6);
    ctx.font = "bold 15px ui-monospace, monospace";
    ctx.textAlign = "center";
    ctx.fillStyle = "#8fb4ff";
    ctx.fillText(`❄ STASIS ${world.stasis.toFixed(1)}s`, W / 2, 30);
  }
  // bombs + dash + ability (bottom right)
  ctx.textAlign = "right";
  ctx.font = "16px ui-monospace, monospace";
  ctx.fillStyle = "#ffe45b";
  ctx.fillText(substitute(`⬡ ${world.myCores}`), W - pad, H - 110);
  ctx.fillStyle = "#fff";
  ctx.fillText(substitute(`💣 ${world.myBombs}`), W - pad, H - 44);
  ctx.fillStyle = world.myDashCd <= 0.05 ? "#39f0ff" : "rgba(255,255,255,0.25)";
  ctx.fillText("DASH", W - pad, H - 66);
  ctx.fillStyle = world.myAbilCd <= 0 ? "#c26bfa" : "rgba(255,255,255,0.25)";
  ctx.fillText(world.myAbilCd <= 0 ? "✦ READY" : `✦ ${world.myAbilCd}s`, W - pad, H - 88);
}

function fmt(n) { return n.toLocaleString("en-US"); }
