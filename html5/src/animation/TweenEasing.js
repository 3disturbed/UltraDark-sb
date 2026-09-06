// -----------------------------------------------------------------------------
// Easing — the curve library from Animation/TweenEasing.cs. Pure maths.
// -----------------------------------------------------------------------------

/** Named easing curves. Every function maps 0..1 to 0..1. */
export const Easing = {
    linear: (t) => t,

    inQuad: (t) => t * t,
    outQuad: (t) => t * (2 - t),
    inOutQuad: (t) => (t < 0.5 ? 2 * t * t : -1 + (4 - 2 * t) * t),

    inCubic: (t) => t ** 3,
    outCubic: (t) => 1 + (t - 1) ** 3,
    inOutCubic: (t) => (t < 0.5 ? 4 * t ** 3 : 1 + 4 * (t - 1) ** 3),

    inQuart: (t) => t ** 4,
    outQuart: (t) => 1 - (t - 1) ** 4,
    inOutQuart: (t) => (t < 0.5 ? 8 * t ** 4 : 1 - 8 * (t - 1) ** 4),

    inQuint: (t) => t ** 5,
    outQuint: (t) => 1 + (t - 1) ** 5,
    inOutQuint: (t) => (t < 0.5 ? 16 * t ** 5 : 1 + 16 * (t - 1) ** 5),

    inSine: (t) => 1 - Math.cos((t * Math.PI) / 2),
    outSine: (t) => Math.sin((t * Math.PI) / 2),
    inOutSine: (t) => -(Math.cos(Math.PI * t) - 1) / 2,

    inExpo: (t) => (t === 0 ? 0 : 2 ** (10 * (t - 1))),
    outExpo: (t) => (t === 1 ? 1 : 1 - 2 ** (-10 * t)),
    inOutExpo: (t) => {
        if (t === 0 || t === 1) return t;
        return t < 0.5 ? 2 ** (20 * t - 10) / 2 : (2 - 2 ** (-20 * t + 10)) / 2;
    },

    inCirc: (t) => 1 - Math.sqrt(1 - t * t),
    outCirc: (t) => Math.sqrt(1 - (t - 1) ** 2),
    inOutCirc: (t) => (t < 0.5
        ? (1 - Math.sqrt(1 - 4 * t * t)) / 2
        : (Math.sqrt(1 - (-2 * t + 2) ** 2) + 1) / 2),

    inBack: (t) => 2.70158 * t ** 3 - 1.70158 * t * t,
    outBack: (t) => 1 + 2.70158 * (t - 1) ** 3 + 1.70158 * (t - 1) ** 2,
    inOutBack: (t) => {
        const c = 1.70158 * 1.525;
        return t < 0.5
            ? ((2 * t) ** 2 * ((c + 1) * 2 * t - c)) / 2
            : ((2 * t - 2) ** 2 * ((c + 1) * (2 * t - 2) + c) + 2) / 2;
    },

    inElastic: (t) => {
        if (t === 0 || t === 1) return t;
        return -(2 ** (10 * t - 10)) * Math.sin(((t * 10 - 10.75) * 2 * Math.PI) / 3);
    },
    outElastic: (t) => {
        if (t === 0 || t === 1) return t;
        return 2 ** (-10 * t) * Math.sin(((t * 10 - 0.75) * 2 * Math.PI) / 3) + 1;
    },

    outBounce: (t) => {
        const n = 7.5625, d = 2.75;
        if (t < 1 / d) return n * t * t;
        if (t < 2 / d) return n * (t -= 1.5 / d) * t + 0.75;
        if (t < 2.5 / d) return n * (t -= 2.25 / d) * t + 0.9375;
        return n * (t -= 2.625 / d) * t + 0.984375;
    },
    inBounce: (t) => 1 - Easing.outBounce(1 - t),
    inOutBounce: (t) => (t < 0.5
        ? (1 - Easing.outBounce(1 - 2 * t)) / 2
        : (1 + Easing.outBounce(2 * t - 1)) / 2),
};

/** The curve named by `type`, falling back to linear rather than throwing. */
export function getEasing(type) {
    if (typeof type === 'function') return type;
    return Easing[type] ?? Easing.linear;
}

/** Every curve name, for the editor's dropdowns. */
export const EASE_TYPES = Object.keys(Easing);
