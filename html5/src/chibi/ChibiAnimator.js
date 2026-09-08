// -----------------------------------------------------------------------------
// ChibiAnimator — plays a clip over a chibi's joints.
//
// The joints are actors, so animation is nothing more exotic than writing
// `localEulerAngles` on sixteen transforms a frame. Those transforms are looked
// up once and kept: `Actor.transform3D` is a component search on every access,
// which is cheap once and is not cheap sixteen times a frame per character.
//
// This deliberately has nothing to do with SkeletalAnimator. That drives a bone
// palette for a skinning shader, has no browser counterpart, and cannot be saved
// in a scene file — going near it would cost MakeChibi its second engine.
//
// Mirrored in SexyBiscuit.Engine/Chibi/ChibiAnimator.cs.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { Transform3D } from '../core/Transform3D.js';
import { Vector3 } from '../math/index.js';
import { JOINT_NAMES } from './ChibiRig.js';
import { Pose, blendInto, findClip, clipNames } from './ChibiClips.js';
import { ChibiCharacter } from './ChibiCharacter.js';

/** One clip in flight: which clip, how far through, and how fast. */
class Playhead {
    constructor(clip) {
        this.clip = clip;
        this.time = 0;
        this.finished = false;
    }

    advance(dt, speed) {
        this.time += dt * speed;
        const duration = Math.max(1e-4, this.clip.duration);
        if (this.time < duration) return;

        if (this.clip.loop) this.time %= duration;
        else { this.time = duration; this.finished = true; }
    }

    /** Normalised position through the clip, 0 to 1. */
    get phase() { return Math.min(1, this.time / Math.max(1e-4, this.clip.duration)); }
}

/** Plays procedural and keyed chibi clips, cross-fading between them. */
export class ChibiAnimator extends Component {
    static schema = {
        clip: { type: P.String, default: 'idle' },
        speed: { type: P.Number, default: 1 },
        intensity: { type: P.Number, default: 1 },
        playing: { type: P.Bool, default: true },
        blendTime: { type: P.Number, default: 0.18 },
    };

    constructor() {
        super();

        /** The clip to play. Unknown names leave the character at rest. */
        this.clip = 'idle';
        /** Playback rate. */
        this.speed = 1;
        /** Scales a procedural clip's swing; keyed clips ignore it. */
        this.intensity = 1;
        this.playing = true;
        /** Seconds to cross-fade when the clip changes. */
        this.blendTime = 0.18;

        /** Fires with the clip's name when a non-looping clip reaches its end. */
        this.onClipFinished = null;

        /** @type {Map<string, Transform3D>} Resolved once, in start. */
        this._joints = new Map();
        /** @type {?Transform3D} The chibi root, which carries a clip's offset. */
        this._root = null;
        this._rootRest = new Vector3(0, 0, 0);

        this._current = null;
        this._previous = null;
        this._blend = 0;

        this._poseA = new Pose();
        this._poseB = new Pose();
        this._poseOut = new Pose();
        this._resolved = false;
    }

    /** Every clip name this animator will accept. */
    static get clips() { return clipNames(); }

    start() {
        this.resolve();
        if (this.playing) this.play(this.clip, 0);
    }

    /**
     * Finds the joints to drive. Called automatically, and again by anything that
     * rebuilds the body underneath — the joints it cached are destroyed actors.
     */
    resolve() {
        this._joints.clear();
        this._root = null;

        const character = this.getComponent(ChibiCharacter);
        if (character?.chibi) {
            for (const [name, transform] of character.chibi.joints) this._joints.set(name, transform);
            this._root = character.chibi.actor.getComponent(Transform3D);
        } else {
            // No component to ask: find the joints by name in whatever subtree is
            // there, so a baked-out chibi still animates.
            for (const actor of this.actor.descendants()) {
                if (JOINT_NAMES.includes(actor.name) && !this._joints.has(actor.name)) {
                    this._joints.set(actor.name, actor.getComponent(Transform3D));
                }
            }
        }

        if (this._root) this._rootRest = this._root.localPosition.clone();
        this._resolved = this._joints.size > 0;
        return this._resolved;
    }

    /**
     * Starts a clip, cross-fading from whatever was playing.
     *
     * @param {string} name A procedural or keyed clip name.
     * @param {number} [blend] Seconds to fade over; defaults to `blendTime`.
     */
    play(name, blend = null) {
        const clip = findClip(name);
        if (!clip) return false;
        if (this._current?.clip.name === name && this.playing) return true;

        this._previous = this._current;
        this._current = new Playhead(clip);
        this._blend = this._previous ? Math.max(0, blend ?? this.blendTime) : 0;
        this._blendLeft = this._blend;
        this.clip = name;
        this.playing = true;
        return true;
    }

    /** Stops, returning every joint to rest. */
    stop() {
        this.playing = false;
        this._current = null;
        this._previous = null;
        this.applyPose(this._poseOut.clear());
    }

    update(dt) {
        if (!this._resolved && !this.resolve()) return;
        if (!this.playing || !this._current) return;

        this._current.advance(dt, this.speed);

        this._poseB.clear();
        this._current.clip.sample(this._current.phase, this._poseB, this.intensity);

        let pose = this._poseB;

        if (this._previous && this._blend > 0) {
            this._blendLeft = Math.max(0, this._blendLeft - dt);
            const amount = 1 - this._blendLeft / this._blend;

            this._poseA.clear();
            this._previous.clip.sample(this._previous.phase, this._poseA, this.intensity);
            pose = blendInto(this._poseOut, this._poseA, this._poseB, amount);

            if (this._blendLeft <= 0) this._previous = null;
        }

        this.applyPose(pose);

        if (!this._current.finished) return;

        const finished = this._current.clip;
        if (!finished.hold) this._current = null;
        this.playing = finished.hold;
        if (this.onClipFinished) this.onClipFinished(finished.name);
    }

    /**
     * Writes a pose onto the joints. Joints the clip did not mention go back to
     * rest rather than keeping whatever the last clip left there — a wave must
     * not leave the legs mid-stride.
     */
    applyPose(pose) {
        for (const name of JOINT_NAMES) {
            const transform = this._joints.get(name);
            if (!transform) continue;
            const [x, y, z] = pose.get(name);
            transform.localEulerAngles = new Vector3(x, y, z);
        }

        if (!this._root) return;
        this._root.localPosition = new Vector3(
            this._rootRest.x + pose.offset[0],
            this._rootRest.y + pose.offset[1],
            this._rootRest.z + pose.offset[2]);
    }
}
registerComponent(ChibiAnimator, {
    category: 'Chibi',
    summary: 'Plays procedural and keyed clips over a chibi rig.',
});
