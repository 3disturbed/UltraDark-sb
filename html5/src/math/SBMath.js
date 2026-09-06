// -----------------------------------------------------------------------------
// SBMath — the scalar helpers from SexyBiscuit.Engine.Core.SBMath.
// -----------------------------------------------------------------------------

export const DEG2RAD = Math.PI / 180;
export const RAD2DEG = 180 / Math.PI;
export const EPSILON = 1e-6;

/** Scalar maths helpers shared by the whole engine. */
export const SBMath = {
    DEG2RAD,
    RAD2DEG,
    EPSILON,

    clamp(value, min, max) { return value < min ? min : (value > max ? max : value); },

    /** Clamps to 0..1. */
    clamp01(value) { return value < 0 ? 0 : (value > 1 ? 1 : value); },

    lerp(a, b, t) { return a + (b - a) * t; },

    /** Linear interpolation with `t` clamped to 0..1. */
    lerpClamped(a, b, t) { return a + (b - a) * SBMath.clamp01(t); },

    /** Interpolates between angles in radians, taking the short way round. */
    lerpAngle(a, b, t) {
        let delta = SBMath.repeat(b - a, Math.PI * 2);
        if (delta > Math.PI) delta -= Math.PI * 2;
        return a + delta * SBMath.clamp01(t);
    },

    /** The inverse of lerp: where `value` sits between `a` and `b`, clamped to 0..1. */
    inverseLerp(a, b, value) {
        if (Math.abs(b - a) < EPSILON) return 0;
        return SBMath.clamp01((value - a) / (b - a));
    },

    /** Maps a value from one range to another. */
    remap(value, fromMin, fromMax, toMin, toMax) {
        if (Math.abs(fromMax - fromMin) < EPSILON) return toMin;
        return toMin + (value - fromMin) / (fromMax - fromMin) * (toMax - toMin);
    },

    /** Moves `current` towards `target` by at most `maxDelta`. */
    moveTowards(current, target, maxDelta) {
        const diff = target - current;
        if (Math.abs(diff) <= maxDelta) return target;
        return current + Math.sign(diff) * maxDelta;
    },

    /** Loops `t` so it never exceeds `length` and is never negative. */
    repeat(t, length) {
        return SBMath.clamp(t - Math.floor(t / length) * length, 0, length);
    },

    /** Bounces `t` back and forth between 0 and `length`. */
    pingPong(t, length) {
        const wrapped = SBMath.repeat(t, length * 2);
        return length - Math.abs(wrapped - length);
    },

    /** Hermite interpolation between two edges — the classic smoothstep. */
    smoothStep(edge0, edge1, x) {
        const t = SBMath.clamp01((x - edge0) / (edge1 - edge0 || EPSILON));
        return t * t * (3 - 2 * t);
    },

    /** Wraps an angle in radians to (-PI, PI]. */
    wrapAngle(radians) {
        let a = (radians + Math.PI) % (Math.PI * 2);
        if (a < 0) a += Math.PI * 2;
        return a - Math.PI;
    },

    /** The shortest signed angular distance between two angles in radians. */
    deltaAngle(from, to) { return SBMath.wrapAngle(to - from); },

    toRadians(degrees) { return degrees * DEG2RAD; },
    toDegrees(radians) { return radians * RAD2DEG; },

    /** True when two floats are equal to within `epsilon`. */
    approximately(a, b, epsilon = EPSILON) { return Math.abs(a - b) <= epsilon; },

    sign(value) { return value < 0 ? -1 : 1 },

    /** True when `value` is a power of two. */
    isPowerOfTwo(value) { return value > 0 && (value & (value - 1)) === 0; },

    /** The next power of two at or above `value`. */
    nextPowerOfTwo(value) {
        if (value <= 1) return 1;
        return 2 ** Math.ceil(Math.log2(value));
    },
};
