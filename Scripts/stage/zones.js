// -----------------------------------------------------------------------------
// zones — what lies on the floor: the telegraphs, the fields, the deployables
// and the consumables, as small glowing geometry pooled by form.
//
// A zone in the snapshot has no id, so these are positional pools: the first
// disc goes to the first disc-shaped zone, and whatever is left over at the end
// of the frame is switched off. A pickup has an id but the same treatment does.
// -----------------------------------------------------------------------------

import { ZK } from "../shared/constants.js";
import { CONSUMABLES } from "../shared/consumables.js";
import { U, rgba } from "./units.js";
import { ZONE_LOOKS } from "./looks.js";

const KIND_LOOK = {
  [ZK.WARP]: "warp", [ZK.MORTAR_TELE]: "mortar", [ZK.BLAST]: "blast", [ZK.FLAME]: "flame",
  [ZK.AEGIS]: "aegis", [ZK.WELL]: "well", [ZK.DARK]: "dark", [ZK.BEACON]: "beacon",
  [ZK.PYLON]: "pylon", [ZK.TURRET]: "turret", [ZK.VENOM]: "venom",
};
const FORM_MESH = { disc: "Cylinder", ring: "Torus", ball: "Sphere", gem: "Cube", post: "Cylinder", box: "Cube" };

class Pool {
  constructor(form) { this.form = form; this.items = []; this.used = 0; }

  next() {
    if (this.used < this.items.length) {
      const item = this.items[this.used++];
      if (item.actor.active !== true) item.actor.active = true;
      return item;
    }
    const a = Scene.createActor("Zone " + this.form, 0, 0);
    if (!a) return null;
    Scene.addComponent(a, "Transform3D", {});
    const mesh = Scene.addComponent(a, "MeshRenderer", {
      meshType: FORM_MESH[this.form], albedoColor: "#0A0416FF", emissiveColor: "#FFFFFFFF", emissiveIntensity: 1,
      metallic: 0.1, roughness: 0.5, castShadows: false,
    });
    const item = { actor: a, t3d: a.transform3d, mesh, colour: "", glow: -1 };
    this.items.push(item);
    this.used++;
    return item;
  }

  /** Switches off whatever this frame did not use. */
  settle() {
    for (let i = this.used; i < this.items.length; i++) {
      if (this.items[i].actor.active) this.items[i].actor.active = false;
    }
    this.used = 0;
  }

  setActive(on) { for (const it of this.items) it.actor.active = on; }
  dispose() { for (const it of this.items) it.actor.destroy(); this.items = []; this.used = 0; }
}

function paint(item, colour, glow, albedo) {
  if (colour !== item.colour) { item.colour = colour; item.mesh.emissiveColor = rgba(colour); if (albedo) item.mesh.albedoColor = rgba(albedo); }
  if (glow !== item.glow) { item.glow = glow; item.mesh.emissiveIntensity = glow; }
}

export class Zones {
  constructor() {
    this.pools = {};
    for (const form in FORM_MESH) this.pools[form] = new Pool(form);
    this.gems = new Pool("gem");
    this.spin = 0;
  }

  sync(zones, pickups, dt, now, arena) {
    this.spin += dt * 120;
    for (let i = 0; i < zones.length; i++) {
      const z = zones[i];
      const key = KIND_LOOK[z.kind];
      const look = key ? ZONE_LOOKS[key] : null;
      if (!look) continue;
      const item = this.pools[look.form].next();
      if (!item) continue;
      const x = z.x * U, zz = z.y * U, r = z.r * U;
      const t = item.t3d;
      if (look.form === "ring") {
        let rr = r;
        if (key === "warp") rr = r * (1.6 - (1 - Math.min(1, z.ttl / 0.5)) * 0.6);
        t.set(x, 0.06, zz, rr * 2, 0.14, rr * 2);
        paint(item, look.colour, look.glow);
        if (key === "mortar") {
          // The landing circle fills as the shell comes down.
          const fill = this.pools.disc.next();
          if (fill) {
            const fr = r * (1 - Math.min(1, z.ttl / 1.2));
            fill.t3d.set(x, 0.03, zz, Math.max(0.05, fr * 2), 0.04, Math.max(0.05, fr * 2));
            paint(fill, look.colour, 0.9);
          }
        }
      } else if (look.form === "disc") {
        t.set(x, 0.035, zz, r * 2, 0.05, r * 2);
        const glow = look.flicker ? look.glow * (0.8 + 0.4 * Math.random()) : look.glow;
        paint(item, look.colour, glow, look.dark ? "#000000" : null);
        if (look.ring) {
          const ring = this.pools.ring.next();
          if (ring) {
            ring.t3d.set(x, 0.08, zz, r * 2, 0.1, r * 2);
            ring.t3d.rotY = look.spin ? this.spin * (look.spin / 120) : 0;
            paint(ring, look.ring, 1.6);
          }
        }
      } else if (look.form === "ball") {
        // A blast: white, growing for a quarter of a second, and a lamp on the floor as it starts.
        const s = r * 1.4 * (1 - z.ttl / 0.25 * 0.4);
        t.set(x, 0.6, zz, s, s, s);
        paint(item, look.colour, look.glow);
        if (z.ttl >= 0.22 && arena) arena.blast(z.x, z.y, z.r, "#FFFFFF");
      } else if (look.form === "gem") {
        t.set(x, 0.55 + Math.sin(now / 220 + x) * 0.1, zz, 0.5, 0.5, 0.5);
        t.rotX = 45; t.rotZ = 45; t.rotY = this.spin * 0.8;
        paint(item, look.colour, look.glow);
      } else if (look.form === "post") {
        t.set(x, 0.8, zz, 0.35, 1.6, 0.35);
        paint(item, look.colour, look.glow + Math.random() * 1.2);
        if (look.ring) {
          const ring = this.pools.ring.next();
          if (ring) { ring.t3d.set(x, 0.05, zz, r * 2, 0.06, r * 2); paint(ring, look.ring, 0.5); }
        }
      } else if (look.form === "box") {
        const blink = z.ttl < 2 && Math.floor(now / 180) % 2;
        t.set(x, 0.32, zz, 0.7, 0.64, 0.7);
        t.rotY = 0;
        paint(item, look.colour, blink ? 0.4 : look.glow);
      }
    }
    // The consumables: a gem in the item's colour, bobbing, blinking as it expires.
    for (let i = 0; i < pickups.length; i++) {
      const k = pickups[i];
      const def = CONSUMABLES[k.kind];
      if (!def) continue;
      if (k.ttl < 3 && Math.floor(now / 160) % 2) continue;
      const item = this.gems.next();
      if (!item) continue;
      const x = k.x * U, zz = k.y * U;
      item.t3d.set(x, 0.55 + Math.sin(now / 260 + k.id) * 0.12, zz, 0.55, 0.55, 0.55);
      item.t3d.rotX = 45; item.t3d.rotZ = 45; item.t3d.rotY = this.spin + k.id * 40;
      paint(item, def.color, 2.4);
    }
    for (const form in this.pools) this.pools[form].settle();
    this.gems.settle();
  }

  setActive(on) { for (const form in this.pools) this.pools[form].setActive(on); this.gems.setActive(on); }
  dispose() { for (const form in this.pools) this.pools[form].dispose(); this.gems.dispose(); }
}
