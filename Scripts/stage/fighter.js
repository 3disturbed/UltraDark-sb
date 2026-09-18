// -----------------------------------------------------------------------------
// fighter — a pilot, or a boss, as a MakeChibi character on the stage.
//
// The simulation knows a pilot as a point, an aim angle, three hit points and a
// few flags. This turns that into a person: a body in the class's kit that walks
// when the point drifts and runs when it sprints, turns its whole body to the
// aim and its head to the nearest threat, holds the class's weapon in its right
// hand with a flash at the muzzle, flinches when a hit point goes, lies down when
// it is downed, and wears the moment on its face -- determined while it fires,
// hurt, sad, surprised, happy -- because a face is the one thing a neon arrow
// never had.
//
// Nothing here decides anything. Every number is read off the world model the
// snapshot built; a fighter that the model stops listing is disposed.
// -----------------------------------------------------------------------------

import { PILOTS, PS, PLAYER } from "../shared/constants.js";
import { PF, EF } from "../shared/protocol.js";
import { U, rgba, num, yawForAim, ease } from "./units.js";
import {
  PILOT_RECIPES, BOSS_RECIPES, PILOT_HEIGHT, BOSS_HEIGHT_PER_PX, GUNS,
  PILOT_LIGHT_RANGE, BOSS_LIGHT_RANGE,
} from "./looks.js";

const RUN_SPEED = 3.2;          // world units a second; PLAYER.SPEED is 9.4
const WALK_SPEED = 0.55;
const BLEND = 0.12;
const HIT_HOLD = 0.42;          // the 'hit' clip's length, near enough
const DASH_HOLD = 0.28;
const CAST_HOLD = 0.6;
const LOOK_EVERY = 0.25;

/** One primitive of a weapon, hung off the hand once the body has arrived. */
function makePart(owner, spec, glow) {
  const part = Scene.createActor("GunPart", 0, 0);
  if (!part) return null;
  Scene.addComponent(part, "Transform3D", {});
  part.transform3d.set(spec[1], spec[2], spec[3], spec[4], spec[5], spec[6]);
  part.transform3d.rotX = spec[7];
  const props = { meshType: spec[0], albedoColor: rgba(spec[8]), castShadows: false, metallic: 0.3, roughness: 0.5 };
  if (spec[9] > 0) { props.emissiveColor = rgba(glow); props.emissiveIntensity = spec[9]; }
  Scene.addComponent(part, "MeshRenderer", props);
  Chibi.attach(owner, "Hand_R", part);
  return part;
}

/** A point light that rides with a fighter. */
function makeLight(colour, range, intensity) {
  const a = Scene.createActor("FighterLight", 0, 0);
  if (!a) return null;
  Scene.addComponent(a, "Transform3D", {});
  Scene.addComponent(a, "Light3D", { type: "Point", color: rgba(colour), intensity, range });
  return a;
}

class Character {
  constructor(name, recipe, scale, colour) {
    this.name = name;
    this.recipe = recipe;
    this.scale = scale;
    this.colour = colour;
    this.actor = null;
    this.armed = false;
    this.clip = "";
    this.face = "";
    this.faceUntil = -1;
    this.hold = 0;          // seconds a one-off clip keeps locomotion away
    this.dead = false;
    this.x = NaN; this.z = NaN;
    this.speed = 0;
    this.yaw = 0;
    this.lookTimer = 0;
    this.lookAtId = -1;
    this.light = null;
    this.lightBase = 0.35;
    this.now = 0;
    this.spawn();
  }

  spawn() {
    const a = Scene.createActor(this.name, 0, 0);
    if (!a) return;
    Scene.addComponent(a, "Transform3D", {});
    a.transform3d.set(0, 0, 0, this.scale, this.scale, this.scale);
    Scene.addComponent(a, "ChibiAnimator", { clip: "idle", blendTime: BLEND });
    Scene.addComponent(a, "ChibiCharacter", { recipePath: this.recipe, buildOnStart: true, expression: "neutral" });
    this.actor = a;
    this.clip = "idle";
    this.face = "neutral";
  }

  /** True once the body exists. In the browser the recipe is read a frame or two late. */
  get ready() {
    return !!this.actor && !!Chibi.socket(this.actor, "Head");
  }

  get active() { return !!this.actor && this.actor.active === true; }

  show(on) {
    if (!this.actor) return;
    if (this.actor.active !== on) this.actor.active = on;
    if (this.light && this.light.active !== on) this.light.active = on;
  }

  /** Moves the body to an arena position (pixels) and reads its speed off the move. */
  moveTo(px, py, dt) {
    if (!this.actor) return;
    const t = this.actor.transform3d;
    if (!t) return;
    const x = px * U, z = py * U;
    if (this.x === this.x) {
      const dx = x - this.x, dz = z - this.z;
      const v = dt > 0 ? Math.sqrt(dx * dx + dz * dz) / dt : 0;
      this.speed = ease(this.speed, Math.min(v, 30), 14, dt);
    }
    this.x = x; this.z = z;
    t.x = x; t.z = z;
    if (this.light) {
      const lt = this.light.transform3d;
      if (lt) { lt.x = x; lt.z = z; lt.y = this.scale * 0.9; }
    }
  }

  faceAim(aim) {
    if (!this.actor) return;
    const yaw = yawForAim(aim);
    if (yaw !== this.yaw) { this.yaw = yaw; this.actor.transform3d.rotY = yaw; }
  }

  /** Walks, runs or stands, unless a one-off clip or death holds the body. */
  locomote(dt) {
    if (!this.actor) return;
    if (this.dead) return;
    if (this.hold > 0) { this.hold -= dt; return; }
    const want = this.speed > RUN_SPEED ? "run" : this.speed > WALK_SPEED ? "walk" : "idle";
    if (want !== this.clip) { this.clip = want; Chibi.play(this.actor, want, BLEND); }
    const anim = this.actor.getComponent("ChibiAnimator");
    if (anim) anim.intensity = want === "run" ? Math.min(1.4, this.speed / RUN_SPEED) : want === "walk" ? Math.max(0.5, this.speed / RUN_SPEED) : 1;
  }

  /** A keyed clip over the locomotion, for `hold` seconds. */
  act(clip, hold, blend = 0.06) {
    if (!this.actor || this.dead) return;
    if (Chibi.play(this.actor, clip, blend)) { this.clip = clip; this.hold = hold; }
  }

  /** A face for a while; `seconds` 0 is the resting face. */
  emote(expression, seconds = 0) {
    if (!this.actor) return;
    if (seconds > 0) { this.faceUntil = this.now + seconds; }
    if (expression !== this.face) { this.face = expression; Chibi.setExpression(this.actor, expression); }
  }

  /** The resting face, once a timed one has passed. */
  settle(resting) {
    if (this.faceUntil >= 0 && this.now < this.faceUntil) return;
    this.faceUntil = -1;
    if (resting !== this.face) { this.face = resting; Chibi.setExpression(this.actor, resting); }
  }

  die() {
    if (this.dead || !this.actor) return;
    this.dead = true;
    this.hold = 0;
    Chibi.play(this.actor, "die", 0.1);
    this.clip = "die";
    this.emote("hurt", 0);
    this.faceUntil = -1;
  }

  revive() {
    if (!this.dead) return;
    this.dead = false;
    this.clip = "idle";
    if (this.actor) Chibi.play(this.actor, "idle", 0.15);
    this.emote("happy", 1.2);
  }

  /** Turns the head, and the eyes ahead of it, to an actor now and then; nothing turns it back. */
  look(target, dt) {
    this.lookTimer -= dt;
    if (this.lookTimer > 0 || !this.actor) return;
    this.lookTimer = LOOK_EVERY;
    const id = target ? target.id : -1;
    if (id === this.lookAtId) return;
    this.lookAtId = id;
    if (target) Chibi.lookAt(this.actor, target); else Chibi.lookAt(this.actor);
  }

  setDark(d) {
    if (!this.light) return;
    const l = this.light.getComponent("Light3D");
    if (l) l.intensity = this.lightBase + d * 2.4;
  }

  dispose() {
    if (this.actor) { this.actor.destroy(); this.actor = null; }
    if (this.light) { this.light.destroy(); this.light = null; }
  }
}

// -----------------------------------------------------------------------------

/** A pilot: a Character in the class's kit, with the class's weapon. */
export class Fighter extends Character {
  constructor(id, pilot, name) {
    const p = PILOTS[pilot] || PILOTS[0];
    super("Pilot " + (name || id), PILOT_RECIPES[pilot] || PILOT_RECIPES[0], PILOT_HEIGHT, p.color);
    this.id = id;
    this.pilot = pilot;
    this.playerName = name || "PILOT";
    this.lastHp = -1;
    this.lastFlags = 0;
    this.firing = false;
    this.fireT = 0;
    this.parts = [];
    this.flash = null;
    this.muzzle = null;
    this.state = PS.ALIVE;
    this.light = makeLight(p.color, PILOT_LIGHT_RANGE, this.lightBase);
    if (this.actor) this.actor.tag = "Pilot";
  }

  /** Another class: the body is rebuilt, since the recipe is the whole description. */
  setPilot(pilot) {
    if (pilot === this.pilot) return;
    const p = PILOTS[pilot] || PILOTS[0];
    this.pilot = pilot;
    this.recipe = PILOT_RECIPES[pilot] || PILOT_RECIPES[0];
    this.colour = p.color;
    const x = this.x, z = this.z, yaw = this.yaw;
    if (this.actor) this.actor.destroy();
    this.actor = null;
    this.armed = false;
    this.parts = [];
    this.flash = null;
    this.dead = false;
    this.spawn();
    if (this.actor) {
      this.actor.tag = "Pilot";
      if (x === x) { this.actor.transform3d.x = x; this.actor.transform3d.z = z; }
      this.yaw = NaN;
      this.faceAim(Math.PI / 2 - yaw * Math.PI / 180);
    }
    if (this.light) {
      const l = this.light.getComponent("Light3D");
      if (l) l.color = rgba(p.color);
    }
  }

  /** Builds the weapon out of primitives and hangs it off the right hand, once the body is there. */
  arm() {
    if (this.armed || !this.ready) return;
    this.armed = true;
    const kind = (PILOTS[this.pilot] || PILOTS[0]).weapon.kind;
    const gun = GUNS[kind] || GUNS.blaster;
    for (let i = 0; i < gun.parts.length; i++) {
      const part = makePart(this.actor, gun.parts[i], this.colour);
      if (part) this.parts.push(part);
    }
    const muzzle = Scene.createActor("Muzzle", 0, 0);
    if (muzzle) {
      Scene.addComponent(muzzle, "Transform3D", {});
      muzzle.transform3d.set(gun.muzzle[0], gun.muzzle[1], gun.muzzle[2], 1, 1, 1);
      this.flash = Scene.addComponent(muzzle, "ParticleSystem3D", {
        maxParticles: 40, emissionRate: 0, shape: "Cone", coneAngle: 22, shapeRadius: 0.02,
        looping: false, playOnAwake: false, seed: 7 + this.id,
        minLifetime: 0.04, maxLifetime: 0.14, minSpeed: 2, maxSpeed: 6,
        minSize: 0.06, maxSize: 0.2, endSizeMultiplier: 0.1,
        startColor: rgba(this.colour), endColor: rgba(this.colour, 0),
        gravity: [0, 0, 0], drag: 6, additiveBlend: true,
      });
      Chibi.attach(this.actor, "Hand_R", muzzle);
      this.muzzle = muzzle;
    }
  }

  /** The flash at the mouth of the barrel. */
  shoot() {
    if (this.flash) this.flash.burst(9);
    this.firing = true;
    this.fireT = 0.35;
  }

  /**
   * One frame, from the snapshot's row for this pilot (and the predicted position for a
   * local seat). `p` is { pilot, state, x, y, aim, hp, flags, orbitals }.
   */
  update(dt, now, p, x, y, aim, nearest) {
    this.now = now;
    if (p.pilot !== this.pilot) this.setPilot(p.pilot);
    this.state = p.state;
    if (p.state === PS.SPECTATING || p.state === PS.OUT) { this.show(false); return; }
    this.show(true);
    this.arm();

    this.moveTo(x, y, dt);
    if (p.state === PS.ALIVE) this.faceAim(aim);

    // Downed is lying down; alive again is standing up.
    if (p.state === PS.DOWNED) { if (!this.dead) { this.die(); this.emote("sad", 0); } }
    else if (this.dead) this.revive();

    // A hit point gone is a flinch and a hurt face; the snapshot is the truth about that.
    if (this.lastHp >= 0 && p.hp < this.lastHp && p.state === PS.ALIVE) {
      this.act("hit", HIT_HOLD, 0.05);
      this.emote("hurt", 1.1);
    }
    this.lastHp = p.hp;

    // A dash is a leap; the flag rises on the frame it starts.
    const flags = p.flags | 0;
    if ((flags & PF.DASHING) && !(this.lastFlags & PF.DASHING)) this.act("jump", DASH_HOLD, 0.05);
    if ((flags & PF.WRAPPED) && !(this.lastFlags & PF.WRAPPED)) this.emote("surprised", 2);
    this.lastFlags = flags;

    if (flags & PF.WRAPPED) { this.speed = 0; }
    this.locomote(dt);

    this.fireT -= dt;
    if (this.fireT <= 0) this.firing = false;
    if (!this.dead) this.settle(this.firing ? "determined" : "neutral");

    this.look(nearest, dt);
  }
}

// -----------------------------------------------------------------------------

/** A boss that is a person: giant, lit in its own colour, angry when enraged. */
export class BossFighter extends Character {
  constructor(id, kind, def) {
    super("Boss " + def.name, BOSS_RECIPES[kind], def.radius * BOSS_HEIGHT_PER_PX, def.color);
    this.id = id;
    this.kind = kind;
    this.def = def;
    this.lastHp = 100;
    this.flinchT = 0;
    this.enraged = false;
    this.dyingT = -1;
    this.lightBase = 0.9;
    this.light = makeLight(def.color, BOSS_LIGHT_RANGE, this.lightBase);
    if (this.actor) { this.actor.tag = "Boss"; this.emote("determined", 0); }
  }

  update(dt, now, e, nearestPilot) {
    this.now = now;
    this.show(true);
    const px = this.x, pz = this.z;
    this.moveTo(e.x, e.y, dt);
    // Faces the way it moves, or its prey when it stands.
    if (px === px) {
      const dx = this.x - px, dz = this.z - pz;
      if (dx * dx + dz * dz > 1e-6 && this.speed > 0.4) this.faceAim(Math.atan2(dz, dx));
      else if (nearestPilot && nearestPilot.transform3d) {
        this.faceAim(Math.atan2(nearestPilot.transform3d.z - this.z, nearestPilot.transform3d.x - this.x));
      }
    }
    const flags = e.flags | 0;
    const enraged = !!(flags & EF.ENRAGED);
    if (enraged !== this.enraged) {
      this.enraged = enraged;
      const l = this.light ? this.light.getComponent("Light3D") : null;
      if (l) l.color = rgba(enraged ? "#ff4d4d" : this.def.color);
    }
    this.flinchT -= dt;
    if (e.hpPct < this.lastHp && this.flinchT <= 0) {
      this.flinchT = 0.7;
      this.act("hit", HIT_HOLD, 0.05);
      this.emote("angry", 0.9);
    }
    this.lastHp = e.hpPct;
    this.locomote(dt);
    this.settle(enraged ? "angry" : e.hpPct < 35 ? "angry" : "determined");
    this.look(nearestPilot, dt);
  }

  /** The end: the body goes down and stays a moment, then goes. Returns true once it has gone. */
  fade(dt) {
    if (this.dyingT < 0) { this.dyingT = 1.6; this.die(); if (this.light) this.light.active = false; }
    this.dyingT -= dt;
    if (this.dyingT > 0) return false;
    this.dispose();
    return true;
  }
}
