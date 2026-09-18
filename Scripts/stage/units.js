// -----------------------------------------------------------------------------
// units — the one mapping between UltraDark's arena and the 3D stage.
//
// The simulation measures the arena in pixels (2048 x 1152) and the renderer's
// world in metres, where a chibi is one unit tall and the engine's lights, fog,
// particles and level of detail are all tuned. Thirty-two pixels to the metre puts
// a pilot's 14 px radius at 0.44 m, the arena at 64 x 36 m, and a boss at four
// metres tall -- and keeps every one of the engine's defaults meaningful.
//
//     game x  ->  world X        game y  ->  world Z        height -> world Y
//
// Game +y is "down" the screen; the camera stands on the +Z side looking towards
// -Z, so +Z is towards the bottom of the screen and the mapping needs no flip.
// -----------------------------------------------------------------------------

import { ARENA_W, ARENA_H } from "../shared/constants.js";

/** World units per arena pixel. */
export const U = 1 / 32;

/** The arena in world units. */
export const ARENA_UW = ARENA_W * U;
export const ARENA_UH = ARENA_H * U;

/** A colour as the renderer takes it: six-digit hex made eight, an eight-digit one left alone. */
export function rgba(hex, alpha = 255) {
  const h = String(hex || "#ffffff");
  if (h.length >= 9) return h;
  const a = Math.max(0, Math.min(255, Math.round(alpha))).toString(16).padStart(2, "0");
  return h.slice(0, 7) + a;
}

/** A finite number, or the fallback: what every read across the script boundary goes through. */
export function num(value, fallback = 0) {
  const v = Number(value);
  return v === v && v !== Infinity && v !== -Infinity ? v : fallback;
}

/** Linear interpolation of a scalar towards a target with a per-second rate, frame-rate independent. */
export function ease(current, target, rate, dt) {
  const k = 1 - Math.exp(-rate * dt);
  return current + (target - current) * k;
}

/** The facing (degrees about Y) that turns a chibi's face along a game aim angle. */
export function yawForAim(aim) {
  // A built chibi faces its actor's forward, -Z: the rig is laid out facing +Z and the builder
  // hangs it on the actor under a half-turn (engine 7912b373). Local -Z turned by rotY t lands
  // on (-sin t, 0, -cos t); the aim wants (cos a, 0, sin a). Written for a +Z face, this sent
  // every pilot into the fight backwards, firing out of their own spine.
  return Math.atan2(-Math.cos(aim), -Math.sin(aim)) * 180 / Math.PI;
}
