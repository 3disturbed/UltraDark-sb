// -----------------------------------------------------------------------------
// ChibiClips — the motion, in two halves.
//
// Locomotion is procedural: idle, walk and run are functions of phase, so a walk
// cycle can scale with how fast the actor is actually moving and a game needs no
// files to have a character that is alive. Everything expressive is keyed, out of
// chibi-clips.json, because a wave is a performance and a sine wave is not.
//
// Both halves produce the same thing — a Pose — so the animator blends between a
// procedural clip and a keyed one without caring which is which.
//
// Mirrored in SexyBiscuit.Engine/Chibi/ChibiClips.cs.
// -----------------------------------------------------------------------------

import keyed from './chibi-clips.json' with { type: 'json' };

const TAU = Math.PI * 2;

/**
 * A pose: a rotation per joint in euler degrees, plus a displacement of the whole
 * character. Joints left out of a pose stay at rest, which is what lets a wave
 * animate one arm and leave the legs alone.
 */
export class Pose {
    constructor() {
        /** @type {Map<string, [number, number, number]>} */
        this.joints = new Map();
        this.offset = [0, 0, 0];
    }

    set(joint, x, y, z) { this.joints.set(joint, [x, y, z]); return this; }

    clear() { this.joints.clear(); this.offset = [0, 0, 0]; return this; }

    /** Reads a joint's rotation, or the rest pose when the clip did not set one. */
    get(joint) { return this.joints.get(joint) ?? ZERO; }
}

const ZERO = Object.freeze([0, 0, 0]);

/** Every joint either side of a blend, so neither clip's joints snap. */
export function blendInto(out, a, b, amount) {
    out.clear();
    const names = new Set([...a.joints.keys(), ...b.joints.keys()]);
    for (const name of names) {
        const from = a.get(name);
        const to = b.get(name);
        out.set(name,
            from[0] + (to[0] - from[0]) * amount,
            from[1] + (to[1] - from[1]) * amount,
            from[2] + (to[2] - from[2]) * amount);
    }
    for (let axis = 0; axis < 3; axis++) {
        out.offset[axis] = a.offset[axis] + (b.offset[axis] - a.offset[axis]) * amount;
    }
    return out;
}

// -----------------------------------------------------------------------------
// Procedural locomotion
// -----------------------------------------------------------------------------

/** Arms hang a little away from the body; dead-straight arms read as a mannequin. */
const ARM_REST = 7;
/** And the elbows keep a slight bend for the same reason. */
const ELBOW_REST = -12;

function idle(t, pose, intensity) {
    const b = Math.sin(t * TAU) * intensity;

    pose.set('Torso', 1.5 + b * 1.6, 0, 0);
    pose.set('Head', -b * 2, Math.sin(t * TAU * 0.5) * 4, 0);
    pose.set('ArmL', b * 1.5, 0, ARM_REST + b);
    pose.set('ArmR', b * 1.5, 0, -ARM_REST - b);
    pose.set('ForearmL', ELBOW_REST, 0, 0);
    pose.set('ForearmR', ELBOW_REST, 0, 0);
    pose.offset[1] = b * 0.006;
}

function stride(t, pose, swing, knee, lean, bob, elbow) {
    const s = Math.sin(t * TAU);
    const c = Math.cos(t * TAU);

    const thighL = s * swing;
    const thighR = -s * swing;
    // The knee only folds on the back half of the stride; a leg that bends going
    // forward is the single thing that makes a walk cycle look wrong.
    const shinL = -Math.max(0, -s) * knee;
    const shinR = -Math.max(0, s) * knee;

    pose.set('ThighL', thighL, 0, 0);
    pose.set('ThighR', thighR, 0, 0);
    pose.set('ShinL', shinL, 0, 0);
    pose.set('ShinR', shinR, 0, 0);
    // Feet stay roughly flat by undoing half of what the leg above them did.
    pose.set('FootL', -(thighL + shinL) * 0.5, 0, 0);
    pose.set('FootR', -(thighR + shinR) * 0.5, 0, 0);

    pose.set('ArmL', -s * swing * 0.85, 0, ARM_REST);
    pose.set('ArmR', s * swing * 0.85, 0, -ARM_REST);
    pose.set('ForearmL', elbow, 0, 0);
    pose.set('ForearmR', elbow, 0, 0);

    pose.set('Hips', 0, -s * 6, 0);
    pose.set('Torso', lean, s * 5, 0);
    pose.set('Head', -lean * 0.5, -s * 3, 0);

    // Two bobs per stride: one for each foot that lands.
    pose.offset[1] = Math.abs(c) * bob;
}

/**
 * The procedural clips. `intensity` is 1 at the natural speed and scales the
 * whole motion, so a character shuffling at half speed swings its arms half as
 * far instead of playing the same cycle slowly.
 */
export const PROCEDURAL = Object.freeze({
    idle: { duration: 3.2, loop: true, sample: (t, pose, k = 1) => idle(t, pose, k) },
    walk: {
        duration: 0.86, loop: true,
        sample: (t, pose, k = 1) => stride(t, pose, 30 * k, 34 * k, 3 * k, 0.018 * k, ELBOW_REST),
    },
    run: {
        duration: 0.56, loop: true,
        sample: (t, pose, k = 1) => stride(t, pose, 48 * k, 62 * k, 14 * k, 0.032 * k, -58),
    },
});

// -----------------------------------------------------------------------------
// Keyed clips
// -----------------------------------------------------------------------------

/** The raw keyed clip data, as loaded. */
export const KEYED = keyed.clips;

function sampleTrack(keys, t) {
    if (keys.length === 0) return ZERO;
    if (t <= keys[0].t) return keys[0].rot ?? keys[0].pos;

    for (let i = 1; i < keys.length; i++) {
        if (t > keys[i].t) continue;
        const a = keys[i - 1];
        const b = keys[i];
        const span = b.t - a.t;
        const k = span <= 0 ? 0 : (t - a.t) / span;
        const from = a.rot ?? a.pos;
        const to = b.rot ?? b.pos;
        return [
            from[0] + (to[0] - from[0]) * k,
            from[1] + (to[1] - from[1]) * k,
            from[2] + (to[2] - from[2]) * k,
        ];
    }
    const last = keys[keys.length - 1];
    return last.rot ?? last.pos;
}

/** Writes a keyed clip's pose at normalised time `t` into `pose`. */
export function sampleKeyed(clip, t, pose) {
    for (const [joint, keys] of Object.entries(clip.tracks)) {
        const rot = sampleTrack(keys, t);
        pose.set(joint, rot[0], rot[1], rot[2]);
    }
    if (clip.offset) pose.offset = sampleTrack(clip.offset, t).slice();
}

/** Every clip name a game may play, procedural and keyed together. */
export function clipNames() {
    return [...Object.keys(PROCEDURAL), ...Object.keys(KEYED)];
}

/**
 * Looks a clip up by name, in a uniform shape.
 * @returns {?{name, duration, loop, hold, sample(t, pose, intensity)}}
 */
export function findClip(name) {
    const procedural = PROCEDURAL[name];
    if (procedural) {
        return { name, duration: procedural.duration, loop: procedural.loop, hold: false,
                 sample: procedural.sample };
    }

    const clip = KEYED[name];
    if (!clip) return null;
    return {
        name,
        duration: clip.duration,
        loop: clip.loop === true,
        hold: clip.hold === true,
        sample: (t, pose) => sampleKeyed(clip, t, pose),
    };
}
