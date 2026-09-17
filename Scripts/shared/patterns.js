// Pattern-seeded enemy projectiles (SDD §3.3): the server broadcasts
// {pid, seed, x, y, angle} (~10 bytes as an event) and BOTH sides call
// spawnPattern with identical arguments to materialise identical bullets.
// Bullets fly straight and never steer, so the copies stay in lockstep.
// Hit verdicts are server-side only; client copies are visuals.

import { mulberry32, randInt } from "./rng.js";

export const PT = { RING: 1, FAN: 2, SPOKES: 3, ORB: 4 };

// Returns [{x, y, vx, vy, r}]
export function spawnPattern(pid, seed, x, y, angle) {
  const rng = mulberry32(seed);
  const out = [];
  if (pid === PT.RING) {
    const n = randInt(rng, 14, 22);
    const speed = 130 + rng() * 60;
    const off = rng() * Math.PI * 2;
    for (let i = 0; i < n; i++) {
      const a = off + (i / n) * Math.PI * 2;
      out.push(bullet(x, y, a, speed, 5));
    }
  } else if (pid === PT.FAN) {
    const n = randInt(rng, 3, 5);
    const spread = 0.5;
    const speed = 260 + rng() * 60;
    for (let i = 0; i < n; i++) {
      const a = angle - spread / 2 + (n === 1 ? 0 : (i / (n - 1)) * spread);
      out.push(bullet(x, y, a, speed, 5));
    }
  } else if (pid === PT.SPOKES) {
    // rotating 6-spoke burst — HEX PRIME's signature; angle carries rotation
    const speed = 170 + rng() * 30;
    for (let i = 0; i < 6; i++) {
      const a = angle + (i / 6) * Math.PI * 2;
      out.push(bullet(x, y, a, speed, 6));
    }
  } else if (pid === PT.ORB) {
    const speed = 90 + rng() * 30;
    out.push(bullet(x, y, angle, speed, 10));
  }
  return out;
}

function bullet(x, y, a, speed, r) {
  return { x, y, vx: Math.cos(a) * speed, vy: Math.sin(a) * speed, r };
}
