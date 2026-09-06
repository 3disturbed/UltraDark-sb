// -----------------------------------------------------------------------------
// Tween — the fluent animation API from Animation/Tween.cs.
//
//     Tween.to(actor.transform, { x: 400 }, 1).ease('outCubic').play();
//
// Tweens run on engine time, so they stop when the game pauses.
// -----------------------------------------------------------------------------

import { getEasing } from './TweenEasing.js';
import { Vector2, Vector3, Color } from '../math/index.js';

const _active = [];

/** One interpolation, from a start value to a target over a duration. */
export class Tween {
    constructor() {
        this.duration = 1;
        this.delay = 0;
        this.elapsed = 0;
        this.easing = getEasing('linear');
        this.loops = 0;              // 0 once, -1 forever
        this.pingPong = false;
        this.useUnscaledTime = false;

        this.isPlaying = false;
        this.isComplete = false;

        /** The actor whose destruction cancels this tween. */
        this.owner = null;

        this._tracks = [];
        this._onComplete = null;
        this._onUpdate = null;
        this._loopsDone = 0;
        this._reversed = false;
        this._captured = false;
    }

    // ---- Building ------------------------------------------------------------

    /** Sets the easing curve, by name or as a function. */
    ease(type) { this.easing = getEasing(type); return this; }

    /** Waits this many seconds before starting. */
    setDelay(seconds) { this.delay = seconds; return this; }

    /** Repeats. `-1` loops forever. */
    loop(times = -1, pingPong = false) {
        this.loops = times;
        this.pingPong = pingPong;
        return this;
    }

    /** Runs on wall-clock time, so a paused game still animates it. */
    unscaled() { this.useUnscaledTime = true; return this; }

    /** Ties the tween's life to an actor. */
    setOwner(actor) { this.owner = actor; return this; }

    onComplete(callback) { this._onComplete = callback; return this; }
    onUpdate(callback) { this._onUpdate = callback; return this; }

    /** Adds this tween to the running list. */
    play() {
        if (this.isPlaying) return this;
        this.isPlaying = true;
        this.isComplete = false;
        _active.push(this);
        return this;
    }

    pause() { this.isPlaying = false; return this; }
    resume() { this.isPlaying = true; return this; }

    /** Stops and removes the tween. Does not run `onComplete`. */
    kill() {
        this.isPlaying = false;
        this.isComplete = true;
        const i = _active.indexOf(this);
        if (i >= 0) _active.splice(i, 1);
        return this;
    }

    /** Jumps to the end, running `onComplete`. */
    complete() {
        this._apply(1);
        this._onComplete?.();
        this.kill();
        return this;
    }

    // ---- Ticking -------------------------------------------------------------

    tick(scaledDt, unscaledDt) {
        if (!this.isPlaying) return;

        if (this.owner?.isDestroyed) { this.kill(); return; }

        const dt = this.useUnscaledTime ? unscaledDt : scaledDt;

        if (this.delay > 0) {
            this.delay -= dt;
            if (this.delay > 0) return;
            // Spend the leftover on the tween itself, so a delayed tween is not
            // short by up to one frame.
            this.elapsed = -this.delay;
        } else {
            this.elapsed += dt;
        }

        // Start values are captured on the first tick, not at construction: a
        // queued tween should animate from wherever the object is when its turn
        // comes, not from where it was when the sequence was built.
        if (!this._captured) {
            for (const track of this._tracks) track.from = track.read();
            this._captured = true;
        }

        const raw = this.duration > 0 ? Math.min(this.elapsed / this.duration, 1) : 1;
        this._apply(this._reversed ? 1 - raw : raw);
        this._onUpdate?.(raw);

        if (raw < 1) return;

        if (this.loops === 0 || (this.loops > 0 && this._loopsDone >= this.loops - 1)) {
            this._onComplete?.();
            this.kill();
            return;
        }

        this._loopsDone++;
        this.elapsed = 0;
        if (this.pingPong) this._reversed = !this._reversed;
        else this._captured = this.pingPong;   // re-capture only when looping forwards
    }

    _apply(t) {
        const eased = this.easing(t);
        for (const track of this._tracks) track.write(interpolate(track.from, track.to, eased));
    }

    // ---- Factories -----------------------------------------------------------

    /**
     * Animates properties of an object towards target values.
     *
     * @param {object} target
     * @param {object} properties Property name to target value.
     * @param {number} duration Seconds.
     */
    static to(target, properties, duration = 1) {
        const tween = new Tween();
        tween.duration = duration;

        for (const [key, to] of Object.entries(properties)) {
            tween._tracks.push({
                read: () => cloneValue(target[key]),
                write: (value) => { target[key] = value; },
                from: cloneValue(target[key]),
                to,
            });
        }

        return tween;
    }

    /** Animates through an explicit getter and setter. */
    static value(getter, setter, to, duration = 1) {
        const tween = new Tween();
        tween.duration = duration;
        tween._tracks.push({ read: getter, write: setter, from: getter(), to });
        return tween;
    }

    /** Moves a transform to a world position. */
    static move(transform, to, duration = 1) {
        return Tween.value(
            () => transform.position.clone(),
            (value) => { transform.position = value; },
            to, duration);
    }

    /** Rotates a 2D transform to an angle in radians. */
    static rotate(transform, radians, duration = 1) {
        return Tween.value(
            () => transform.rotation,
            (value) => { transform.rotation = value; },
            radians, duration);
    }

    /** Scales a transform. */
    static scale(transform, to, duration = 1) {
        return Tween.value(
            () => transform.scale.clone(),
            (value) => { transform.scale = value; },
            to, duration);
    }

    /** Fades or shifts a renderer's tint. */
    static color(renderer, to, duration = 1) {
        return Tween.value(
            () => renderer.tint.clone(),
            (value) => { renderer.tint = value; },
            Color.from(to), duration);
    }

    /** A tween that does nothing but wait — useful inside a sequence. */
    static delay(seconds) {
        const tween = new Tween();
        tween.duration = seconds;
        return tween;
    }

    // ---- Global control ------------------------------------------------------

    /** Advances every running tween. Called by the engine host. */
    static updateAll(scaledDt, unscaledDt = scaledDt) {
        // A snapshot: a completion callback routinely starts or kills tweens.
        for (const tween of _active.slice()) tween.tick(scaledDt, unscaledDt);
    }

    /** Stops every tween. */
    static killAll() {
        for (const tween of _active.slice()) tween.kill();
    }

    /** Stops every tween owned by an actor. */
    static killAllFor(owner) {
        for (const tween of _active.slice()) if (tween.owner === owner) tween.kill();
    }

    /** How many tweens are running. */
    static get activeCount() { return _active.length; }

    /** Starts a sequence builder. */
    static sequence() { return new TweenSequence(); }
}

/** Runs tweens one after another, or together. */
export class TweenSequence {
    constructor() {
        this._steps = [];
        this._index = 0;
        this.isPlaying = false;
    }

    /** Adds a tween that starts after everything before it finishes. */
    append(tween) { this._steps.push({ mode: 'append', tween }); return this; }

    /** Adds a tween that starts alongside the previous one. */
    join(tween) { this._steps.push({ mode: 'join', tween }); return this; }

    /** Waits before the next step. */
    appendInterval(seconds) { return this.append(Tween.delay(seconds)); }

    /** Runs a callback at this point in the sequence. */
    appendCallback(callback) {
        return this.append(Tween.delay(0).onComplete(callback));
    }

    play() {
        this.isPlaying = true;
        this._runFrom(0);
        return this;
    }

    kill() {
        this.isPlaying = false;
        for (const step of this._steps) step.tween.kill();
        return this;
    }

    _runFrom(index) {
        if (index >= this._steps.length) { this.isPlaying = false; return; }

        // Take this step plus every `join` that follows, and start them together.
        const group = [this._steps[index]];
        let next = index + 1;
        while (next < this._steps.length && this._steps[next].mode === 'join') {
            group.push(this._steps[next]);
            next++;
        }

        let remaining = group.length;
        for (const step of group) {
            const previous = step.tween._onComplete;
            step.tween._onComplete = () => {
                previous?.();
                remaining--;
                if (remaining === 0 && this.isPlaying) this._runFrom(next);
            };
            step.tween.play();
        }
    }
}

function cloneValue(value) {
    if (value == null) return value;
    return typeof value.clone === 'function' ? value.clone() : value;
}

/** Interpolates numbers, vectors and colours; anything else snaps at the end. */
function interpolate(from, to, t) {
    if (typeof from === 'number') return from + (to - from) * t;
    if (from instanceof Color) return Color.lerp(from, Color.from(to), t);
    if (from instanceof Vector2) return Vector2.lerp(from, Vector2.from(to), t);
    if (from instanceof Vector3) return Vector3.lerp(from, Vector3.from(to), t);
    return t >= 1 ? to : from;
}
