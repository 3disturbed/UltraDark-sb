// -----------------------------------------------------------------------------
// arena — the deck the fight happens on, and the light it happens in.
//
// A 64 x 36 metre slab of dark metal with the original's neon grid inlaid in it
// and a lit rail round its edge, on an apron that runs to the fog so the horizon
// is never the void. Over it a warm sun, a cold fill and a night sky whose rim
// light keeps every silhouette readable against the dark.
//
// The dark is a property of the light. From wave sixteen the sun and the sky go
// down with the simulation's darkness level -- the same number the 2D shroud
// used -- and what is left is what glows: the pilots' own lamps, the bosses,
// the bullets and the grid's last ember. A blast puts a lamp on the floor for a
// third of a second, which is what makes a bomb light the room.
// -----------------------------------------------------------------------------

import { ARENA_UW, ARENA_UH, U, rgba, ease } from "./units.js";

const GRID_STEP = 128 * U;            // the original's 128 px grid
const NEON = "#39F0FF";
const SUN = 1.15;
const FILL = 0.3;
const SKY = 0.8;
const GRID_GLOW = 0.55;
const WALL_GLOW = 1.8;

export class Arena {
  constructor() {
    this.parts = [];
    this.grid = [];
    this.walls = [];
    this.sun = null; this.fill = null; this.sky = null;
    this.blasts = [];
    this.dark = -1;
    this.pulse = 0;
  }

  init() {
    const cx = ARENA_UW / 2, cz = ARENA_UH / 2;
    // The deck and the apron under it.
    this.mesh("Deck", "Cube", cx, -0.25, cz, ARENA_UW, 0.5, ARENA_UH, "#10151E", null, 0, 0.35, 0.7);
    this.mesh("Apron", "Cube", cx, -0.42, cz, 260, 0.3, 260, "#05070C", null, 0, 0.0, 0.95);
    // The grid, inlaid a hair above the deck.
    for (let i = 0; i <= Math.round(ARENA_UW / GRID_STEP); i++) {
      this.grid.push(this.mesh("Grid", "Cube", i * GRID_STEP, 0.012, cz, 0.05, 0.02, ARENA_UH, "#0A2030", NEON, GRID_GLOW, 0, 0.6));
    }
    for (let j = 0; j <= Math.round(ARENA_UH / GRID_STEP); j++) {
      this.grid.push(this.mesh("Grid", "Cube", cx, 0.012, j * GRID_STEP, ARENA_UW, 0.02, 0.05, "#0A2030", NEON, GRID_GLOW, 0, 0.6));
    }
    // The rail round the edge, and a post at each corner.
    const t = 0.35, hgt = 0.5;
    this.walls.push(this.mesh("Wall", "Cube", cx, hgt / 2, -t / 2, ARENA_UW + t * 2, hgt, t, "#12303A", NEON, WALL_GLOW, 0.4, 0.4));
    this.walls.push(this.mesh("Wall", "Cube", cx, hgt / 2, ARENA_UH + t / 2, ARENA_UW + t * 2, hgt, t, "#12303A", NEON, WALL_GLOW, 0.4, 0.4));
    this.walls.push(this.mesh("Wall", "Cube", -t / 2, hgt / 2, cz, t, hgt, ARENA_UH, "#12303A", NEON, WALL_GLOW, 0.4, 0.4));
    this.walls.push(this.mesh("Wall", "Cube", ARENA_UW + t / 2, hgt / 2, cz, t, hgt, ARENA_UH, "#12303A", NEON, WALL_GLOW, 0.4, 0.4));
    for (const [x, z] of [[0, 0], [ARENA_UW, 0], [0, ARENA_UH], [ARENA_UW, ARENA_UH]]) {
      this.walls.push(this.mesh("Post", "Cube", x, 0.9, z, 0.6, 1.8, 0.6, "#1A2430", NEON, 1.2, 0.5, 0.4));
    }

    // The light.
    this.sun = this.light("Sun", "Directional", "#FFF4E6", SUN, 0, -58, -30);
    this.fill = this.light("Fill", "Directional", "#96B4FF", FILL, 0, -22, 150);
    const sky = Scene.createActor("Sky", 0, 0);
    if (sky) {
      Scene.addComponent(sky, "Transform3D", {});
      this.sky = Scene.addComponent(sky, "SkyLight", {
        skyColor: "#1C2438FF", groundColor: "#05070CFF", intensity: SKY, rimColor: "#8FB8FFFF", rimIntensity: 1.3,
      });
      this.parts.push(sky);
    }
    // A night sky: a horizon sliver at a shallow hangar pitch is dark, not the engine's noon.
    const skybox = Scene.createActor("Skybox", 0, 0);
    if (skybox) {
      Scene.addComponent(skybox, "Skybox", { gradientTop: "#03040AFF", gradientBottom: "#0E1626FF" });
      this.parts.push(skybox);
    }
    const post = Scene.createActor("Post Process", 0, 0);
    if (post) {
      Scene.addComponent(post, "PostProcessVolume", { bloomIntensity: 0.8, bloomThreshold: 1.0, bloomRadius: 0.55, exposure: 1.05 });
      this.parts.push(post);
    }
    const fog = Scene.createActor("Fog", 0, 0);
    if (fog) {
      Scene.addComponent(fog, "Fog3D", { color: "#06080FFF", density: 0.006, startDistance: 30, maxOpacity: 0.75 });
      this.parts.push(fog);
    }
    // Four lamps for blasts, parked dark until one goes off.
    for (let i = 0; i < 4; i++) {
      const lamp = this.light("Blast", "Point", "#FFFFFF", 0, 0, 0, 0, 9);
      if (lamp) this.blasts.push({ actor: lamp, light: lamp.getComponent("Light3D"), t: 0, peak: 0 });
    }
  }

  mesh(name, meshType, x, y, z, sx, sy, sz, albedo, glow, intensity, metallic, roughness) {
    const a = Scene.createActor(name, 0, 0);
    if (!a) return null;
    Scene.addComponent(a, "Transform3D", {});
    a.transform3d.set(x, y, z, sx, sy, sz);
    const props = { meshType, albedoColor: rgba(albedo), metallic, roughness, castShadows: false };
    if (glow) { props.emissiveColor = rgba(glow); props.emissiveIntensity = intensity; }
    if (sx > 100 || sz > 100) props.ignoreCulling = true;
    const renderer = Scene.addComponent(a, "MeshRenderer", props);
    this.parts.push(a);
    return { actor: a, mesh: renderer, glow: intensity };
  }

  light(name, type, colour, intensity, y, rotX, rotY, range) {
    const a = Scene.createActor(name, 0, 0);
    if (!a) return null;
    Scene.addComponent(a, "Transform3D", {});
    a.transform3d.set(ARENA_UW / 2, y || 6, ARENA_UH / 2, 1, 1, 1);
    a.transform3d.rotX = rotX;
    a.transform3d.rotY = rotY;
    const props = { type, color: rgba(colour), intensity };
    if (range) props.range = range;
    Scene.addComponent(a, "Light3D", props);
    this.parts.push(a);
    return a;
  }

  /** The darkness level, 0..1: the sun and the sky go down with it, the grid's ember with them. */
  setDark(d) {
    if (d === this.dark) return;
    this.dark = d;
    const lit = Math.pow(1 - d, 1.6);
    const sun = this.sun ? this.sun.getComponent("Light3D") : null;
    if (sun) sun.intensity = SUN * lit;
    const fill = this.fill ? this.fill.getComponent("Light3D") : null;
    if (fill) fill.intensity = FILL * (1 - d);
    if (this.sky) this.sky.intensity = SKY * lit + 0.03;
    const gridGlow = GRID_GLOW * (1 - d) + 0.06;
    for (let i = 0; i < this.grid.length; i++) {
      const g = this.grid[i];
      if (g && g.glow !== gridGlow) { g.glow = gridGlow; g.mesh.emissiveIntensity = gridGlow; }
    }
  }

  /** A lamp on the floor where something exploded, for a third of a second. */
  blast(px, py, size, colour) {
    let lamp = null;
    for (let i = 0; i < this.blasts.length; i++) if (this.blasts[i].t <= 0) { lamp = this.blasts[i]; break; }
    if (!lamp) lamp = this.blasts[0];
    if (!lamp) return;
    const t = lamp.actor.transform3d;
    if (t) { t.x = px * U; t.y = 1.2; t.z = py * U; }
    lamp.t = 0.35;
    lamp.peak = Math.min(8, 2 + size * U);
    if (lamp.light) { lamp.light.intensity = lamp.peak; lamp.light.range = Math.min(20, 5 + size * U * 2); if (colour) lamp.light.color = rgba(colour); }
  }

  /** The kill-multiplier milestone: the grid flares, as the original's did. */
  flare() { this.pulse = 1; }

  update(dt) {
    for (let i = 0; i < this.blasts.length; i++) {
      const b = this.blasts[i];
      if (b.t <= 0) continue;
      b.t -= dt;
      if (b.light) b.light.intensity = b.t <= 0 ? 0 : b.peak * (b.t / 0.35);
    }
    if (this.pulse > 0) {
      this.pulse = Math.max(0, this.pulse - dt * 1.6);
      const glow = GRID_GLOW * (1 - Math.max(0, this.dark)) + 0.06 + this.pulse * 1.4;
      for (let i = 0; i < this.grid.length; i++) { const g = this.grid[i]; if (g) g.mesh.emissiveIntensity = glow; }
      if (this.pulse === 0) this.dark = -1;   // so setDark writes the resting glow back
    }
  }

  setActive(on) {
    for (const a of this.parts) if (a && a.active !== on) a.active = on;
  }

  dispose() {
    for (const a of this.parts) if (a) a.destroy();
    this.parts = []; this.grid = []; this.walls = []; this.blasts = [];
  }
}
