// -----------------------------------------------------------------------------
// overlay — the parts of the original's picture that stay 2D over the 3D stage.
//
// Bullets, rail tracers, pattern bullets, sniper beams, DAVE's swing, particles,
// popups, name tags and the dark itself are hundreds of small glowing things a
// frame. As meshes they would be hundreds of transforms written a frame for a
// dot; as the original's neon glows on a Draw canvas, projected through the
// stage's camera, they cost what they always cost and look as they always did.
// The canvas paints over the 3D frame, which for a view this steep is right:
// nothing a bullet passes "behind" is taller than the bullet is bright.
//
// Every draw here is the original render.js's, with one substitution: a world
// point goes through `proj(px, py, height)` -> {x, y, s} where s is screen
// pixels per arena pixel at that depth, so sizes shrink with distance too.
// -----------------------------------------------------------------------------

import { PILOTS, PS, ORBITAL, TICK_DT, EK, ZK } from "../shared/constants.js";
import { ENEMIES } from "../shared/enemies.js";
import { PF } from "../shared/protocol.js";

const SHOT_H = 0.45;     // the height bullets fly at, world units
const HEAD_H = 1.95;     // where a name goes, over a pilot
const CHEST_H = 0.8;

/** The world layer: bullets, tracers, pattern bullets, lasers, swings and particles. */
export function drawWorldLayer(ctx, a) {
  const { world, proj, clockNow, settings } = a;
  const pilotOf = new Map();
  for (const p of world.players) pilotOf.set(p.id, p.pilot);

  // ---- bullets ----
  ctx.composite = "lighter";
  for (const b of world.bullets) {
    const pilot = pilotOf.get(b.owner) ?? 0;
    if (PILOTS[pilot]?.weapon.kind === "rail") continue;     // rails are their tracers
    const q = proj(b.x, b.y, SHOT_H);
    if (!q) continue;
    ctx.glow(PILOTS[pilot]?.color ?? "#39f0ff", q.x, q.y, 26 * q.s);
    ctx.fillStyle = "#fff";
    const d = Math.max(1.5, 4 * q.s);
    ctx.fillRect(q.x - d / 2, q.y - d / 2, d, d);
  }
  for (const b of world.tracers ?? []) {
    const l = Math.hypot(b.vx, b.vy) || 1;
    const nx = b.vx / l, ny = b.vy / l;
    const q = proj(b.x, b.y, SHOT_H);
    const q2 = proj(b.x - nx * 34, b.y - ny * 34, SHOT_H);
    const q3 = proj(b.x - nx * 16, b.y - ny * 16, SHOT_H);
    if (!q || !q2 || !q3) continue;
    ctx.glow("#ff5b8e", q.x, q.y, 30 * q.s);
    ctx.strokeStyle = "rgba(255,145,180,0.85)";
    ctx.lineWidth = Math.max(1, 3 * q.s);
    ctx.beginPath(); ctx.moveTo(q2.x, q2.y); ctx.lineTo(q.x, q.y); ctx.stroke();
    ctx.strokeStyle = "#fff";
    ctx.lineWidth = Math.max(0.8, 1.5 * q.s);
    ctx.beginPath(); ctx.moveTo(q3.x, q3.y); ctx.lineTo(q.x, q.y); ctx.stroke();
  }
  for (const b of world.eBullets) {
    const q = proj(b.x, b.y, SHOT_H);
    if (!q) continue;
    ctx.glow("#ff5b8e", q.x, q.y, b.r * 5.5 * q.s);
    ctx.fillStyle = "#ffd7e5";
    ctx.beginPath(); ctx.arc(q.x, q.y, Math.max(1, b.r * 0.75 * q.s), 0, Math.PI * 2); ctx.fill();
  }
  ctx.composite = "source-over";

  // ---- sniper telegraphs and beams, hateful-charge lines ----
  const now = clockNow();
  for (const l of world.lasers) {
    const s = proj(l.sx, l.sy, 0.5), t = proj(l.tx, l.ty, 0.5);
    if (!s || !t) continue;
    if (l.firing) {
      ctx.composite = "lighter";
      ctx.strokeStyle = "rgba(255,61,240,0.9)";
      ctx.lineWidth = 7 * Math.max(0.5, s.s);
      ctx.beginPath(); ctx.moveTo(s.x, s.y); ctx.lineTo(t.x, t.y); ctx.stroke();
      ctx.strokeStyle = "#ffffff";
      ctx.lineWidth = 2.5 * Math.max(0.5, s.s);
      ctx.beginPath(); ctx.moveTo(s.x, s.y); ctx.lineTo(t.x, t.y); ctx.stroke();
      ctx.composite = "source-over";
    } else {
      const blink = Math.floor(now / 110) % 2 === 0 || !settings.flash;
      ctx.strokeStyle = `rgba(255,61,240,${blink ? 0.55 : 0.25})`;
      ctx.lineWidth = 1.5;
      ctx.setLineDash([14, 10]);
      const dx = l.tx - l.sx, dy = l.ty - l.sy;
      const len = Math.hypot(dx, dy) || 1;
      const far = proj(l.sx + (dx / len) * 2600, l.sy + (dy / len) * 2600, 0.5);
      ctx.beginPath();
      ctx.moveTo(s.x, s.y);
      ctx.lineTo(far ? far.x : t.x, far ? far.y : t.y);
      ctx.stroke();
      ctx.setLineDash([]);
    }
  }

  // ---- DAVE's swing: a fading arc ----
  ctx.composite = "lighter";
  const cleaves = a.cleaves;
  for (let i = cleaves.length - 1; i >= 0; i--) {
    const c = cleaves[i];
    c.t += a.dt;
    if (c.t >= c.life) { cleaves.splice(i, 1); continue; }
    const q = proj(c.x, c.y, CHEST_H);
    if (!q) continue;
    const p = c.t / c.life;
    const half = c.full ? Math.PI : 1.05;
    const r = c.r * (0.6 + 0.4 * p) * q.s;
    ctx.strokeStyle = `rgba(194,107,250,${0.9 * (1 - p)})`;
    ctx.lineWidth = (14 * (1 - p) + 3) * Math.max(0.4, q.s);
    ctx.beginPath(); ctx.ellipse(q.x, q.y, r, r * a.squash, 0, c.aim - half, c.aim + half); ctx.stroke();
    ctx.strokeStyle = `rgba(255,255,255,${0.7 * (1 - p)})`;
    ctx.lineWidth = 3 * Math.max(0.4, q.s);
    ctx.beginPath(); ctx.ellipse(q.x, q.y, r, r * a.squash, 0, c.aim - half, c.aim + half); ctx.stroke();
  }

  // ---- particles ----
  const particles = a.particles;
  for (let i = particles.length - 1; i >= 0; i--) {
    const p = particles[i];
    p.t += a.dt;
    if (p.t >= p.life) { particles.splice(i, 1); continue; }
    p.x += p.vx * a.dt; p.y += p.vy * a.dt;
    p.vx *= 0.94; p.vy *= 0.94;
    const q = proj(p.x, p.y, 0.6);
    if (!q) continue;
    const alpha = 1 - p.t / p.life;
    ctx.globalAlpha = alpha;
    ctx.glow(p.color, q.x, q.y, (p.size * 8 * alpha + 4) * q.s);
  }
  ctx.globalAlpha = 1;
  ctx.composite = "source-over";

  // ---- what rides with a pilot: AMBER's aura, the orbital blades ----
  const localPred = a.localPred;
  for (const p of world.players) {
    if (p.state !== PS.ALIVE) continue;
    const pred = p.id === world.myId ? world.me : localPred(p.id);
    const x = pred ? pred.x : p.x, y = pred ? pred.y : p.y;
    const q = proj(x, y, 0.1);
    if (!q) continue;
    if (p.pilot === 2) {
      const ar = 140 * (p.id === world.myId ? (world.myStats.auraR ?? 1) : 1) * q.s;
      ctx.strokeStyle = "rgba(184,255,94,0.32)";
      ctx.lineWidth = 2;
      ctx.setLineDash([3, 9]);
      ctx.beginPath(); ctx.ellipse(q.x, q.y, ar, ar * a.squash, 0, 0, Math.PI * 2); ctx.stroke();
      ctx.setLineDash([]);
    }
    if (p.orbitals > 0) {
      const k = p.orbitals;
      const base = a.serverTickNow() * TICK_DT * ORBITAL.ROT;
      ctx.composite = "lighter";
      for (let i = 0; i < k; i++) {
        const ang = base + (i / k) * Math.PI * 2;
        const b = proj(x + Math.cos(ang) * ORBITAL.R, y + Math.sin(ang) * ORBITAL.R, CHEST_H);
        if (!b) continue;
        ctx.glow("#e8fbff", b.x, b.y, 34 * b.s);
        ctx.fillStyle = "#e8fbff";
        ctx.save(); ctx.translate(b.x, b.y); ctx.rotate(ang * 6);
        const s = 9 * b.s;
        ctx.beginPath(); ctx.moveTo(s, 0); ctx.lineTo(0, s * 0.45); ctx.lineTo(-s, 0); ctx.lineTo(0, -s * 0.45); ctx.closePath(); ctx.fill();
        ctx.restore();
      }
      ctx.composite = "source-over";
    }
  }
}

/** The dark (SDD 2.4): a shroud with the light punched out of it where the fire is. */
export function drawDark(g, a) {
  const { world, proj, dark, W, H, dpr } = a;
  if (dark <= 0.02) return;
  const LS = 0.5;
  const lw = Math.ceil(W * LS), lh = Math.ceil(H * LS);
  g.setTransform(dpr / LS, 0, 0, dpr / LS, 0, 0);
  g.composite = "source-over";
  g.clearRect(0, 0, lw, lh);
  g.fillStyle = `rgba(2,1,8,${dark})`;
  g.fillRect(0, 0, lw, lh);
  g.composite = "destination-out";
  const punch = (wx, wy, r, h) => {
    const q = proj(wx, wy, h || 0.3);
    if (!q) return;
    g.glow("#ffffff", q.x * LS, q.y * LS, r * q.s * LS * 2);
  };
  const localPred = a.localPred;
  for (const p of world.players) {
    if (p.state === PS.ALIVE || p.state === PS.DOWNED) {
      const pred = p.id === world.myId ? world.me : localPred(p.id);
      punch(pred ? pred.x : p.x, pred ? pred.y : p.y, 170, 0.8);
    }
  }
  let lights = 0;
  for (const b of world.bullets) { punch(b.x, b.y, 90, SHOT_H); if (++lights > 220) break; }
  for (const b of world.tracers ?? []) { punch(b.x, b.y, 110, SHOT_H); if (++lights > 260) break; }
  for (const b of world.eBullets) { punch(b.x, b.y, 55, SHOT_H); if (++lights > 380) break; }
  for (const z of world.zones) {
    if (z.kind === ZK.BLAST || z.kind === ZK.FLAME || z.kind === ZK.WELL) punch(z.x, z.y, z.r * 1.7, 0.2);
  }
  for (const e of world.enemies) punch(e.x, e.y, ENEMIES[e.kind]?.boss ? 60 : 24, 0.5);   // eyes stay visible
}

/** Over the dark: the names, the downed, the raid marks, the popups and the boss marquee. */
export function drawTags(ctx, a) {
  const { world, proj, clockNow, nameOf, W, settings } = a;
  const now = clockNow();
  const localPred = a.localPred;
  for (const p of world.players) {
    if (p.state === PS.SPECTATING || p.state === PS.OUT) continue;
    const mine = p.id === world.myId;
    const pred = mine ? world.me : localPred(p.id);
    const x = pred ? pred.x : p.x, y = pred ? pred.y : p.y;
    const foot = proj(x, y, 0.05);
    if (!foot) continue;
    const pilot = PILOTS[p.pilot] ?? PILOTS[0];
    if (p.state === PS.DOWNED) {
      const pulse = 0.6 + 0.4 * Math.sin(now / 160);
      ctx.composite = "lighter";
      ctx.glow(pilot.color, foot.x, foot.y, 60 * pulse * foot.s);
      ctx.composite = "source-over";
      ctx.strokeStyle = pilot.color;
      ctx.lineWidth = 3;
      ctx.beginPath(); ctx.ellipse(foot.x, foot.y, 20 * foot.s, 20 * foot.s * a.squash, 0, 0, Math.PI * 2); ctx.stroke();
      if (p.flags & PF.REVIVING) {
        ctx.strokeStyle = "#b8ff5e";
        ctx.lineWidth = 5;
        ctx.beginPath(); ctx.ellipse(foot.x, foot.y, 28 * foot.s, 28 * foot.s * a.squash, 0, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * ((now / 500) % 1)); ctx.stroke();
      }
      ctx.font = "12px ui-monospace, monospace";
      ctx.textAlign = "center";
      ctx.fillStyle = "#fff";
      ctx.fillText("DOWNED", foot.x, foot.y - 34 * Math.max(0.5, foot.s));
      continue;
    }
    const head = proj(x, y, HEAD_H);
    if (!head) continue;
    if (!mine) {
      ctx.font = "11px ui-monospace, monospace";
      ctx.textAlign = "center";
      ctx.fillStyle = "rgba(255,255,255,0.7)";
      ctx.fillText(a.substitute(`${pilot.symbol} ${nameOf(p.id)}`), head.x, head.y);
    }
    const charge = world.charges?.[p.id];
    if (charge) {
      const cc = charge > 0 ? "#ff5b5b" : "#5b8fff";
      ctx.strokeStyle = cc;
      ctx.lineWidth = 2.5;
      ctx.setLineDash([4, 6]);
      ctx.beginPath(); ctx.ellipse(foot.x, foot.y, 24 * foot.s, 24 * foot.s * a.squash, 0, 0, Math.PI * 2); ctx.stroke();
      ctx.setLineDash([]);
      ctx.font = "bold 18px ui-monospace, monospace";
      ctx.textAlign = "center";
      ctx.fillStyle = cc;
      ctx.fillText(charge > 0 ? "+" : "−", head.x, head.y - 14);
    }
    if (p.flags & PF.WRAPPED) {
      const c = proj(x, y, CHEST_H);
      if (c) {
        const r = 22 * c.s;
        ctx.strokeStyle = "rgba(216,216,168,0.85)";
        ctx.lineWidth = 1.5;
        for (let i = 0; i < 4; i++) {
          const ang = (i / 4) * Math.PI + now / 900;
          ctx.beginPath();
          ctx.moveTo(c.x - Math.cos(ang) * r, c.y - Math.sin(ang) * r);
          ctx.lineTo(c.x + Math.cos(ang) * r, c.y + Math.sin(ang) * r);
          ctx.stroke();
        }
        ctx.beginPath(); ctx.arc(c.x, c.y, r * 0.9, 0, Math.PI * 2); ctx.stroke();
      }
    }
  }

  // ---- popups: rising from where the thing happened ----
  ctx.font = "bold 15px ui-monospace, monospace";
  ctx.textAlign = "center";
  const popups = a.popups;
  for (let i = popups.length - 1; i >= 0; i--) {
    const p = popups[i];
    p.t += a.dt;
    if (p.t >= p.life) { popups.splice(i, 1); continue; }
    const q = proj(p.x, p.y, 1.4 + p.t * 1.6);
    if (!q) continue;
    ctx.globalAlpha = 1 - p.t / p.life;
    ctx.fillStyle = p.color;
    ctx.fillText(a.substitute(p.text), q.x, q.y);
  }
  ctx.globalAlpha = 1;

  // ---- the boss marquee, top centre, in screen space ----
  let boss = null;
  for (const e of world.enemies) { const def = ENEMIES[e.kind]; if (def?.boss) { boss = { e, def }; break; } }
  if (boss) {
    const bw = Math.min(W * 0.4, 560);
    const enraged = !!(boss.e.flags & 1);
    ctx.fillStyle = "rgba(255,255,255,0.12)";
    ctx.fillRect(W / 2 - bw / 2, 18, bw, 12);
    ctx.fillStyle = enraged ? "#ff4d4d" : boss.def.color;
    ctx.fillRect(W / 2 - bw / 2, 18, bw * (boss.e.hpPct / 100), 12);
    ctx.font = "bold 22px ui-monospace, monospace";
    ctx.textAlign = "center";
    ctx.fillStyle = "#fff";
    ctx.fillText(boss.def.name + (enraged ? " — ENRAGED" : ""), W / 2, 52);
  }
}
