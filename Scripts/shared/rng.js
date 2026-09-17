// Seedable PRNG (mulberry32) — the only randomness allowed in anything
// both sides replay (patterns). Same seed → identical stream, both runtimes.

export function mulberry32(seed) {
  let a = seed >>> 0;
  return function () {
    a |= 0; a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export function randInt(rng, lo, hi) { // inclusive
  return lo + Math.floor(rng() * (hi - lo + 1));
}

export function pick(rng, arr) { return arr[Math.floor(rng() * arr.length)]; }
