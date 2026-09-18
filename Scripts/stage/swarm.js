// -----------------------------------------------------------------------------
// swarm — the enemies as glowing geometry, and the bosses as whatever they are.
//
// The snapshot lists every enemy as an id, a kind, a position, a health share and
// flags. A regular enemy becomes one mesh -- the kind's shape, lit in the kind's
// colour, spinning or facing the way it goes -- pooled by primitive so a wave of
// two hundred Mites allocates nothing after the first. A boss whose recipe is in
// looks.js becomes a BossFighter; HEXAGON PRIME and FOUNDRY become small
// assemblies of parts.
//
// The four ledger rules apply here as everywhere: rows are keyed by id, a row the
// snapshot stops listing is released the same frame, nothing recurses, and the
// positions are kept in arena pixels so a fighter can ask for the nearest threat.
// -----------------------------------------------------------------------------

import { EK } from "../shared/constants.js";
import { ENEMIES } from "../shared/enemies.js";
import { EF } from "../shared/protocol.js";
import { U, rgba, ease } from "./units.js";
import { SHAPE_LOOKS, BOSS_RECIPES } from "./looks.js";
import { BossFighter } from "./fighter.js";

const BODY_ALBEDO = "#0A0416";
const GLOW_FULL = 1.7;
const GLOW_PHASED = 0.25;

function colourFor(def, flags) {
  if (flags & EF.OPEN) return "#ffffff";
  if (flags & EF.CHILLED) return "#bfe9ff";
  if (flags & EF.ENRAGED) return "#ff4d4d";
  return def.color;
}

/** One pooled mesh actor. */
function makeBody(mesh) {
  const a = Scene.createActor("Enemy", 0, 0);
  if (!a) return null;
  a.tag = "Enemy";
  Scene.addComponent(a, "Transform3D", {});
  const renderer = Scene.addComponent(a, "MeshRenderer", {
    meshType: mesh, albedoColor: rgba(BODY_ALBEDO), emissiveColor: "#FFFFFFFF", emissiveIntensity: GLOW_FULL,
    metallic: 0.15, roughness: 0.45, castShadows: false,
  });
  return { actor: a, t3d: a.transform3d, mesh: renderer, colour: "", glow: -1, spin: 0, id: -1, x: 0, y: 0, px: NaN, py: NaN, yaw: 0, ring: null };
}

/** HEXAGON PRIME: a hub, a ring, and six prisms orbiting it. FOUNDRY: a block with four doors. */
class GeoBoss {
  constructor(id, kind, def) {
    this.id = id; this.kind = kind; this.def = def;
    this.parts = []; this.spin = 0; this.open = false;
    this.x = NaN; this.z = NaN;
    this.light = null;
    const r = def.radius * U;
    const glow = def.color;
    if (kind === EK.FOUNDRY) {
      this.add("Cube", 0, r * 0.9, 0, r * 1.7, r * 1.8, r * 1.7, "#1A1008", glow, 0.5);
      this.add("Cube", 0, r * 1.95, 0, r * 0.7, r * 0.3, r * 0.7, "#1A1008", glow, 0.5);   // the stack
      const d = r * 0.86;
      this.doors = [
        this.add("Cube", d, r * 0.9, 0, r * 0.1, r * 1.2, r * 1.0, "#0A0416", glow, 0.3),
        this.add("Cube", -d, r * 0.9, 0, r * 0.1, r * 1.2, r * 1.0, "#0A0416", glow, 0.3),
        this.add("Cube", 0, r * 0.9, d, r * 1.0, r * 1.2, r * 0.1, "#0A0416", glow, 0.3),
        this.add("Cube", 0, r * 0.9, -d, r * 1.0, r * 1.2, r * 0.1, "#0A0416", glow, 0.3),
      ];
    } else {
      this.add("Cylinder", 0, r * 0.5, 0, r * 0.7, r * 1.0, r * 0.7, BODY_ALBEDO, glow, 1.2);        // the hub
      this.add("Cylinder", 0, 0.04, 0, r * 2.2, 0.08, r * 2.2, BODY_ALBEDO, glow, 0.35);              // the footprint
      this.prisms = [];
      for (let i = 0; i < 6; i++) this.prisms.push(this.add("Cube", 0, r * 0.7, 0, r * 0.5, r * 1.4, r * 0.5, BODY_ALBEDO, glow, 2.4));
    }
    this.light = Scene.createActor("BossLight", 0, 0);
    if (this.light) {
      Scene.addComponent(this.light, "Transform3D", {});
      Scene.addComponent(this.light, "Light3D", { type: "Point", color: rgba(glow), intensity: 0.9, range: 14 });
    }
  }

  add(mesh, x, y, z, sx, sy, sz, albedo, glow, intensity) {
    const a = Scene.createActor("BossPart", 0, 0);
    if (!a) return null;
    a.tag = "Boss";
    Scene.addComponent(a, "Transform3D", {});
    a.transform3d.set(x, y, z, sx, sy, sz);
    const renderer = Scene.addComponent(a, "MeshRenderer", {
      meshType: mesh, albedoColor: rgba(albedo), emissiveColor: rgba(glow), emissiveIntensity: intensity,
      metallic: 0.4, roughness: 0.4, castShadows: false,
    });
    const part = { actor: a, ox: x, oy: y, oz: z, mesh: renderer };
    this.parts.push(part);
    return part;
  }

  update(dt, e) {
    this.x = e.x * U; this.z = e.y * U;
    this.spin += dt * (this.kind === EK.FOUNDRY ? 0 : 35);
    const r = this.def.radius * U;
    for (let i = 0; i < this.parts.length; i++) {
      const p = this.parts[i];
      if (!p) continue;
      const t = p.actor.transform3d;
      if (!t) continue;
      t.x = this.x + p.ox; t.z = this.z + p.oz;
      if (this.kind !== EK.FOUNDRY) t.rotY = this.spin;
    }
    if (this.prisms) {
      for (let i = 0; i < this.prisms.length; i++) {
        const p = this.prisms[i];
        if (!p) continue;
        const a = (this.spin * Math.PI / 180) + i * Math.PI / 3;
        const t = p.actor.transform3d;
        t.x = this.x + Math.cos(a) * r * 0.95; t.z = this.z + Math.sin(a) * r * 0.95;
        t.rotY = -a * 180 / Math.PI;
      }
    }
    const open = !!(e.flags & EF.OPEN);
    if (this.doors && open !== this.open) {
      this.open = open;
      for (const d of this.doors) {
        if (!d) continue;
        d.mesh.emissiveIntensity = open ? 5 : 0.3;
        d.mesh.emissiveColor = rgba(open ? "#ffffff" : this.def.color);
      }
    }
    const enraged = !!(e.flags & EF.ENRAGED);
    if (this.light) {
      const lt = this.light.transform3d;
      if (lt) { lt.x = this.x; lt.y = r * 1.5; lt.z = this.z; }
      const l = this.light.getComponent("Light3D");
      if (l && enraged !== this.enraged) l.color = rgba(enraged ? "#ff4d4d" : this.def.color);
    }
    this.enraged = enraged;
  }

  fade(dt) { this.dispose(); return true; }

  dispose() {
    for (const p of this.parts) if (p && p.actor) p.actor.destroy();
    this.parts = [];
    if (this.light) { this.light.destroy(); this.light = null; }
  }
}

export class Swarm {
  constructor() {
    this.live = new Map();     // id -> body
    this.free = new Map();     // mesh -> body[]
    this.bosses = new Map();   // id -> BossFighter | GeoBoss
    this.dying = [];
    this.count = 0;
    this.seen = new Set();
    this.dark = 0;
    this.radar = null;      // the HUD's minimap, which marks each body while it lives
  }

  acquire(mesh) {
    const list = this.free.get(mesh);
    if (list && list.length) {
      const body = list.pop();
      if (body.actor) body.actor.active = true;
      return body;
    }
    return makeBody(mesh);
  }

  release(body) {
    if (!body) return;
    if (this.radar && body.actor) this.radar.unmark(body.actor);
    if (body.ring) { body.ring.actor.active = false; (this.free.get("__ring") || this.free.set("__ring", []).get("__ring")).push(body.ring); body.ring = null; }
    if (body.actor) body.actor.active = false;
    body.id = -1; body.px = NaN; body.py = NaN;
    const mesh = body.meshName;
    if (!this.free.has(mesh)) this.free.set(mesh, []);
    this.free.get(mesh).push(body);
  }

  acquireRing() {
    const list = this.free.get("__ring");
    if (list && list.length) { const r = list.pop(); r.actor.active = true; return r; }
    const a = Scene.createActor("EnemyRing", 0, 0);
    if (!a) return null;
    Scene.addComponent(a, "Transform3D", {});
    const mesh = Scene.addComponent(a, "MeshRenderer", {
      meshType: "Torus", albedoColor: rgba(BODY_ALBEDO), emissiveColor: "#A56BFFFF", emissiveIntensity: 2.4,
      metallic: 0.1, roughness: 0.5, castShadows: false,
    });
    return { actor: a, mesh };
  }

  /** One frame from the snapshot's enemy list. `pilots` is the fighters map, for a boss to look at. */
  sync(enemies, dt, now, nearestPilotOf) {
    const seen = this.seen;
    seen.clear();
    for (let i = 0; i < enemies.length; i++) {
      const e = enemies[i];
      const def = ENEMIES[e.kind];
      if (!def) continue;
      seen.add(e.id);
      if (def.boss) { this.syncBoss(e, def, dt, now, nearestPilotOf); continue; }
      let body = this.live.get(e.id);
      const look = SHAPE_LOOKS[def.shape] || SHAPE_LOOKS.circle;
      if (!body) {
        body = this.acquire(look.mesh);
        if (!body) continue;
        body.meshName = look.mesh;
        body.id = e.id;
        body.spin = Math.random() * 360;
        body.px = NaN; body.py = NaN;
        body.colour = ""; body.glow = -1;
        const d = def.radius * 2 * U;
        body.t3d.set(e.x * U, 0, e.y * U, d * (look.wide || 1), d * look.h, d);
        if (look.tilt) body.t3d.rotX = look.tilt;
        if (look.lie) body.t3d.rotX = 90;
        if (look.ring) { body.ring = this.acquireRing(); if (body.ring) body.ring.actor.transform3d.set(e.x * U, 0.1, e.y * U, d * 1.8, d * 0.25, d * 1.8); }
        if (this.radar && body.actor) this.radar.mark(body.actor, def.color);
        this.live.set(e.id, body);
      }
      this.place(body, e, def, look, dt);
    }
    // Rows the snapshot no longer lists are gone.
    for (const [id, body] of this.live) {
      if (seen.has(id)) continue;
      this.live.delete(id);
      this.release(body);
    }
    for (const [id, boss] of this.bosses) {
      if (seen.has(id)) continue;
      this.bosses.delete(id);
      this.dying.push(boss);
    }
    for (let i = this.dying.length - 1; i >= 0; i--) {
      if (this.dying[i].fade(dt)) this.dying.splice(i, 1);
    }
    this.count = this.live.size;
  }

  place(body, e, def, look, dt) {
    const t = body.t3d;
    if (!t) return;
    const d = def.radius * 2 * U;
    const h = d * look.h;
    const x = e.x * U, z = e.y * U;
    const flags = e.flags | 0;
    // Where it goes; the smaller shapes bob, the standing ones face their travel.
    const bob = look.bob ? Math.sin(body.spin * 0.05 + x) * 0.06 : 0;
    const lift = look.lie ? d * 0.5 : h * 0.5 + (look.lift || 0);
    if (x !== t.x || z !== t.z) { t.x = x; t.z = z; }
    t.y = lift + bob + 0.02;
    if (look.spin) { body.spin += look.spin * dt; if (body.spin > 3600) body.spin -= 3600; }
    let yaw = body.yaw;
    if (look.face && body.px === body.px) {
      const dx = e.x - body.px, dz = e.y - body.py;
      if (dx * dx + dz * dz > 0.25) yaw = ease(yaw, Math.atan2(dx, dz) * 180 / Math.PI, 10, dt);   // +Z lands along the move
    }
    if (look.yaw) yaw += look.yaw;
    const rotY = (look.spin ? body.spin : 0) + yaw;
    if (rotY !== body.rotY) { body.rotY = rotY; t.rotY = rotY; }
    body.yaw = yaw - (look.yaw || 0);
    body.px = e.x; body.py = e.y;
    body.x = e.x; body.y = e.y;

    // Repainted only when the colour or the glow actually changes.
    const colour = colourFor(def, flags);
    const glow = (flags & EF.PHASED) ? GLOW_PHASED : (flags & EF.OPEN) ? 4 : GLOW_FULL;
    if (colour !== body.colour) { body.colour = colour; body.mesh.emissiveColor = rgba(colour); }
    if (glow !== body.glow) { body.glow = glow; body.mesh.emissiveIntensity = glow; }
    if (body.ring) {
      const rt = body.ring.actor.transform3d;
      rt.x = x; rt.z = z; rt.rotY = -body.spin * 2;
      body.ring.actor.transform3d.scaleX = d * (1.6 + 0.2 * Math.sin(body.spin * 0.1));
    }
  }

  syncBoss(e, def, dt, now, nearestPilotOf) {
    let boss = this.bosses.get(e.id);
    if (!boss) {
      boss = BOSS_RECIPES[e.kind] ? new BossFighter(e.id, e.kind, def) : new GeoBoss(e.id, e.kind, def);
      this.bosses.set(e.id, boss);
    }
    if (boss instanceof BossFighter) boss.update(dt, now, e, nearestPilotOf(e.x, e.y));
    else boss.update(dt, e);
    boss.x = e.x * U; boss.z = e.y * U;
    boss.px = e.x; boss.py = e.y;
  }

  /** The nearest regular enemy's actor within `maxPx` of an arena point, or null. */
  nearestActor(px, py, maxPx) {
    let best = null, bd = maxPx * maxPx;
    for (const body of this.live.values()) {
      const dx = body.x - px, dy = body.y - py;
      const d = dx * dx + dy * dy;
      if (d < bd) { bd = d; best = body.actor; }
    }
    if (!best) {
      for (const boss of this.bosses.values()) {
        const dx = boss.px - px, dy = boss.py - py;
        const d = dx * dx + dy * dy;
        if (d < bd * 4 && boss.actor) { bd = d; best = boss.actor; }
      }
    }
    return best;
  }

  /** The bosses' floor points, for the camera to frame. */
  bossPoints(out) {
    for (const boss of this.bosses.values()) if (boss.x === boss.x) out.push({ x: boss.x, z: boss.z });
    return out;
  }

  /** The first live boss, for the marquee and the minimap. */
  get boss() {
    for (const boss of this.bosses.values()) return boss;
    return null;
  }

  setDark(d) {
    this.dark = d;
    for (const boss of this.bosses.values()) if (boss.setDark) boss.setDark(d);
  }

  setActive(on) {
    for (const body of this.live.values()) if (body.actor) body.actor.active = on;
    for (const boss of this.bosses.values()) if (boss.show) boss.show(on);
  }

  dispose() {
    for (const body of this.live.values()) { if (body.ring) body.ring.actor.destroy(); if (body.actor) body.actor.destroy(); }
    for (const list of this.free.values()) for (const body of list) if (body.actor) body.actor.destroy();
    for (const boss of this.bosses.values()) boss.dispose();
    for (const boss of this.dying) boss.dispose();
    this.live.clear(); this.free.clear(); this.bosses.clear(); this.dying = [];
  }
}
