// -----------------------------------------------------------------------------
// SkyGradient — the colour of a day, interpolated from the table both engines read.
//
// The mirror of SexyBiscuit.Engine/Rendering/SkyGradient.cs.
//
// A day/night cycle is almost entirely colour, and colour written twice is two
// different dusks -- the desktop build warm and the web build blue, with nothing able
// to see it. So the keys live in one JSON file and a shared fixture pins what both
// engines make of them.
//
// Time runs 0 to 1 with 0 at midnight and 0.5 at noon, and the table wraps: the value
// at 0.99 blends towards the key at 0, not off the end of the array.
// -----------------------------------------------------------------------------

import table from './sky-gradient.json' with { type: 'json' };

/** How many keys the table holds. */
export const KEY_COUNT = table.keys.length;

/** The name of each key, in order, for an editor's timeline. */
export const KEY_NAMES = table.keys.map((k) => k.name ?? '');

/** Folds any time into 0..1, so a clock that has run for days still works. */
export function wrap01(t) {
    const wrapped = t % 1;
    return wrapped < 0 ? wrapped + 1 : wrapped;
}

/** The sky at a moment. Any time is accepted; it is wrapped into one day first. */
export function sample(time01) {
    const keys = table.keys;
    if (keys.length === 0) return null;

    const t = wrap01(time01);

    // The last key before t, and the one after it. The table wraps, so "after the last
    // key" is the first key again -- which is what makes 23:59 blend into midnight
    // rather than snapping.
    let before = 0;
    for (let i = 0; i < keys.length; i++) {
        if (keys[i].time > t) break;
        before = i;
    }

    const after = Math.min(before + 1, keys.length - 1);
    const a = keys[before];
    const b = keys[after];

    const span = b.time - a.time;
    const f = span <= 0 ? 0 : Math.min(1, Math.max(0, (t - a.time) / span));

    return {
        zenith: mixHex(a.zenith, b.zenith, f),
        horizon: mixHex(a.horizon, b.horizon, f),
        sun: mixHex(a.sun, b.sun, f),
        sunIntensity: lerp(a.sunIntensity, b.sunIntensity, f),
        ambientSky: mixHex(a.ambientSky, b.ambientSky, f),
        ambientGround: mixHex(a.ambientGround, b.ambientGround, f),
        fog: mixHex(a.fog, b.fog, f),
        fogDensity: lerp(a.fogDensity, b.fogDensity, f),
    };
}

// -----------------------------------------------------------------------------
// Colour
// -----------------------------------------------------------------------------

function lerp(a, b, f) { return a + (b - a) * f; }

/**
 * Blends two hex colours per channel.
 *
 * The rounding rule is stated rather than borrowed, and both engines state the same
 * one: floor(x + 0.5), computed in doubles. MonoGame's Color.Lerp truncates a
 * single-precision lerp and JavaScript's Math.round rounds half away from zero while
 * C#'s MathF.Round rounds half to even -- three different answers for the same blend,
 * each of them off by one byte at some fractions and correct at most.
 */
export function mixHex(a, b, f) {
    const ca = parseHex(a);
    const cb = parseHex(b);
    const amount = Math.min(1, Math.max(0, f));

    const mix = (x, y) => Math.min(255, Math.max(0, Math.floor(x + (y - x) * amount + 0.5)));
    return toHex(mix(ca[0], cb[0]), mix(ca[1], cb[1]), mix(ca[2], cb[2]));
}

function parseHex(hex) {
    let body = String(hex).replace(/^#/, '');
    if (body.length === 3) body = body.split('').map((c) => c + c).join('');
    return [0, 2, 4].map((i) => parseInt(body.slice(i, i + 2), 16));
}

function toHex(r, g, b) {
    return `#${[r, g, b].map((c) => c.toString(16).padStart(2, '0')).join('')}`;
}
