// -----------------------------------------------------------------------------
// camera — the stage's one camera: where it stands, what it frames, and the maths
// that puts a point of the arena on the screen and a pixel of the screen on the
// arena floor.
//
// The original fits the whole 2048 x 1152 arena on screen. A perspective camera
// that did the same would make a chibi thirty pixels tall, so this one FRAMES:
// every living pilot and the boss, padded, never closer than a solo pilot needs
// to read the fight round them and never further than the whole arena. Solo it
// is a following camera; in a squad that spreads it pulls back; a boss pulls it
// back further. What is off screen is on the minimap.
//
// The projection is done here rather than asked of the engine, because the
// contract has no worldToScreen and the 2D overlay (bullets, the dark, popups)
// needs one for hundreds of points a frame. It is the engine's own convention --
// a vertical field of view, forward -Z at rotY 0, negative rotX looking down --
// and the headless checks hold it to the engine's Camera3D.
// -----------------------------------------------------------------------------

import { ARENA_UW, ARENA_UH, ease } from "./units.js";

const DEG = Math.PI / 180;

export class Rig {
  constructor() {
    this.actor = null;
    this.fov = 40;            // degrees, vertical
    this.pitch = 60;          // degrees below the horizon
    this.near = 0.1;
    this.far = 400;

    // Where it looks (a point on the floor) and how far back it stands, smoothed.
    this.cx = ARENA_UW / 2;
    this.cz = ARENA_UH / 2;
    this.dist = 48;
    this.tx = this.cx;
    this.tz = this.cz;
    this.tdist = this.dist;
    this.tpitch = this.pitch;
    this.h = 0;               // the height of the point it looks at
    this.th = 0;
    this.rate = 5;            // how quickly the centre catches up, per second
    this.distRate = 2.4;      // the pull-back is slower, so a boss entrance reads as a reveal
    this.shakeX = 0;
    this.shakeZ = 0;

    // The frame's basis and focal, recomputed in place() and read by project().
    this.px = 0; this.py = 0; this.pz = 0;   // the camera's position
    this.fx = 0; this.fy = 0; this.fz = -1;  // forward
    this.ux = 0; this.uy = 1; this.uz = 0;   // up
  }

  init() {
    const a = Scene.createActor("StageCamera", 0, 0);
    if (!a) return;
    a.tag = "MainCamera3D";       // the native Camera3D.Main accepts that tag and no other
    Scene.addComponent(a, "Transform3D", {});
    Scene.addComponent(a, "Camera3D", { fieldOfView: this.fov, nearClip: this.near, farClip: this.far });
    this.actor = a;
    this.snap();
    this.place();
  }

  /** Jumps the smoothing to its targets: a scene change, a hangar to arena cut. */
  snap() {
    this.cx = this.tx; this.cz = this.tz; this.dist = this.tdist; this.pitch = this.tpitch; this.h = this.th;
  }

  /**
   * Frames a set of floor points (world units): the centre is their middle, the
   * distance whatever fits their extent plus `pad` on every side, clamped between
   * a view `minW` wide and the whole arena.
   */
  frame(points, { pad = 7, minW = 34, aspect = 16 / 9, pitch = 60 } = {}) {
    if (points.length === 0) return;
    let x0 = Infinity, x1 = -Infinity, z0 = Infinity, z1 = -Infinity;
    for (let i = 0; i < points.length; i++) {
      const p = points[i];
      if (p.x < x0) x0 = p.x; if (p.x > x1) x1 = p.x;
      if (p.z < z0) z0 = p.z; if (p.z > z1) z1 = p.z;
    }
    const w = Math.max(minW, x1 - x0 + pad * 2);
    const h = Math.max(minW / aspect, z1 - z0 + pad * 2);
    this.tx = (x0 + x1) / 2;
    this.tz = (z0 + z1) / 2;
    this.tpitch = pitch;
    this.th = 0;
    this.tdist = Math.min(this.distFor(ARENA_UW + pad * 2, ARENA_UH + pad * 2, aspect, pitch),
                          this.distFor(w, h, aspect, pitch));
  }

  /** Looks at one point from a set distance and pitch: the hangar's hero shot. */
  lookAt(x, z, dist, pitch, height = 0) {
    this.tx = x; this.tz = z; this.tdist = dist; this.tpitch = pitch; this.th = height;
  }

  /** The distance back along the view that fits a floor rectangle w x h (world units). */
  distFor(w, h, aspect, pitch) {
    const tanV = Math.tan(this.fov * DEG / 2);
    const tanH = tanV * aspect;
    const sinP = Math.sin(pitch * DEG);
    // The near edge of the floor is nearer the eye than the centre, so it looms; the
    // margin below is what keeps it inside the frame at the pitches this rig uses.
    return Math.max((w / 2) / tanH, (h / 2) * sinP / tanV * 1.15);
  }

  update(dt, shakeX = 0, shakeZ = 0) {
    this.cx = ease(this.cx, this.tx, this.rate, dt);
    this.cz = ease(this.cz, this.tz, this.rate, dt);
    this.dist = ease(this.dist, this.tdist, this.distRate, dt);
    this.pitch = ease(this.pitch, this.tpitch, this.rate, dt);
    this.h = ease(this.h, this.th, this.rate, dt);
    this.shakeX = shakeX;
    this.shakeZ = shakeZ;
    this.place();
  }

  /** Writes the camera's transform from the rig's state and caches the frame's basis. */
  place() {
    const phi = this.pitch * DEG;
    const sinP = Math.sin(phi), cosP = Math.cos(phi);
    this.fx = 0; this.fy = -sinP; this.fz = -cosP;
    this.ux = 0; this.uy = cosP; this.uz = -sinP;
    this.px = this.cx + this.shakeX;
    this.py = this.h + this.dist * sinP;
    this.pz = this.cz + this.dist * cosP + this.shakeZ;
    if (!this.actor) return;
    const t = this.actor.transform3d;
    if (!t) return;
    t.set(this.px, this.py, this.pz, 1, 1, 1);
    t.rotX = -this.pitch;
    t.rotY = 0;
    t.rotZ = 0;
  }

  /**
   * A world point on the screen: {x, y} in the screen's units (W x H), `depth` along the view,
   * and `s`, the screen size of one world unit at that depth. Null behind the camera.
   */
  project(wx, wy, wz, W, H) {
    const dx = wx - this.px, dy = wy - this.py, dz = wz - this.pz;
    const depth = dx * this.fx + dy * this.fy + dz * this.fz;
    if (depth <= this.near) return null;
    const rx = dx;                                            // right is +X
    const ry = dx * this.ux + dy * this.uy + dz * this.uz;
    const focal = (H / 2) / Math.tan(this.fov * DEG / 2);
    return {
      x: W / 2 + rx / depth * focal,
      y: H / 2 - ry / depth * focal,
      depth,
      s: focal / depth,
    };
  }

  /** The floor point (y = height) under a screen pixel, in world units. */
  ground(sx, sy, W, H, height = 0) {
    const tanV = Math.tan(this.fov * DEG / 2);
    const ndcX = (sx / W) * 2 - 1;
    const ndcY = 1 - (sy / H) * 2;
    const aspect = W / H;
    // direction = forward + right * ndcX * tan * aspect + up * ndcY * tan
    const dx = this.fx + ndcX * tanV * aspect;
    const dy = this.fy + this.uy * ndcY * tanV;
    const dz = this.fz + this.uz * ndcY * tanV;
    if (dy >= -1e-6) {
      // Looking along or above the horizon: hand back a point far along the ray on the floor plane.
      return { x: this.px + dx * 1000, z: this.pz + dz * 1000 };
    }
    const t = (height - this.py) / dy;
    return { x: this.px + dx * t, z: this.pz + dz * t };
  }
}
