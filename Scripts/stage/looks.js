// -----------------------------------------------------------------------------
// looks — what everything on the stage is made of. Data, so a designer changes a
// boss's face or an enemy's shape without reading the mirror.
// -----------------------------------------------------------------------------

import { EK } from "../shared/constants.js";

/** A pilot's recipe under Assets/Chibi, by PILOTS index. */
export const PILOT_RECIPES = [
  "Assets/Chibi/Pilot0-BINK.chibi",
  "Assets/Chibi/Pilot1-BLAZE.chibi",
  "Assets/Chibi/Pilot2-AMBER.chibi",
  "Assets/Chibi/Pilot3-DAVE.chibi",
  "Assets/Chibi/Pilot4-SPARKS.chibi",
  "Assets/Chibi/Pilot5-RIGG.chibi",
  "Assets/Chibi/Pilot6-KELVIN.chibi",
  "Assets/Chibi/Pilot7-HAWK.chibi",
];

/** The bosses that are people. The other two -- HEXAGON PRIME and FOUNDRY -- are geometry. */
export const BOSS_RECIPES = {
  [EK.BRUTE_PRIME]: "Assets/Chibi/Boss-BrutePrime.chibi",
  [EK.SHEPHERD]: "Assets/Chibi/Boss-NullShepherd.chibi",
  [EK.ULTRADARK]: "Assets/Chibi/Boss-UltraDark.chibi",
  [EK.PATCHWORK]: "Assets/Chibi/Boss-Patchwork.chibi",
  [EK.THADDIUS]: "Assets/Chibi/Boss-Thaddius.chibi",
  [EK.BROODMOTHER]: "Assets/Chibi/Boss-Broodmother.chibi",
};

/** A pilot stands this tall in world units (a chibi is one unit at scale 1). */
export const PILOT_HEIGHT = 1.45;

/** A boss chibi's height, per pixel of its collision radius. */
export const BOSS_HEIGHT_PER_PX = 0.07;

const BLACK = "#0B0D12";
const SLATE = "#2B3242";
const STEEL = "#6E7A8A";

/**
 * The weapon in each pilot's right hand, as primitives in chibi units: a cylinder or a torus runs
 * along its own Y, so `pitch` 90 lays a barrel along the line of fire (+Z of the hand).
 *   [mesh, x, y, z, sx, sy, sz, pitch, albedo, glowIntensity]   -- a part that glows is lit in the pilot's colour
 */
export const GUNS = {
  smg: { muzzle: [0, 0.01, 0.34], parts: [
    ["Cube", 0, -0.04, 0.02, 0.05, 0.09, 0.07, 0, SLATE, 0],
    ["Cylinder", 0, 0.01, 0.17, 0.035, 0.3, 0.035, 90, BLACK, 0],
    ["Cube", 0, 0.04, 0.12, 0.03, 0.03, 0.10, 0, BLACK, 2],
  ] },
  shotgun: { muzzle: [0, 0.02, 0.4], parts: [
    ["Cube", 0, -0.04, 0.02, 0.06, 0.1, 0.08, 0, SLATE, 0],
    ["Cylinder", -0.03, 0.02, 0.2, 0.05, 0.38, 0.05, 90, BLACK, 0],
    ["Cylinder", 0.03, 0.02, 0.2, 0.05, 0.38, 0.05, 90, BLACK, 0],
    ["Cube", 0, 0.02, 0.33, 0.13, 0.05, 0.05, 0, SLATE, 2.5],
  ] },
  blaster: { muzzle: [0, 0.01, 0.38], parts: [
    ["Cube", 0, -0.04, 0.02, 0.05, 0.09, 0.07, 0, SLATE, 0],
    ["Cylinder", 0, 0.01, 0.19, 0.045, 0.34, 0.045, 90, BLACK, 0],
    ["Torus", 0, 0.01, 0.24, 0.05, 0.05, 0.05, 90, BLACK, 3],
    ["Sphere", 0, 0.01, 0.36, 0.045, 0.045, 0.045, 0, BLACK, 3],
  ] },
  cleave: { muzzle: [0, 0.02, 0.5], parts: [
    ["Cylinder", 0, 0.0, 0.08, 0.035, 0.16, 0.035, 90, SLATE, 0],
    ["Cube", 0, 0.02, 0.36, 0.035, 0.16, 0.5, 0, STEEL, 0],
    ["Cube", 0, 0.10, 0.36, 0.02, 0.02, 0.5, 0, BLACK, 3],
  ] },
  arc: { muzzle: [0, 0.01, 0.36], parts: [
    ["Cube", 0, -0.04, 0.02, 0.05, 0.09, 0.07, 0, SLATE, 0],
    ["Cylinder", 0, 0.01, 0.18, 0.04, 0.32, 0.04, 90, BLACK, 0],
    ["Torus", 0, 0.01, 0.14, 0.07, 0.07, 0.07, 90, BLACK, 3],
    ["Torus", 0, 0.01, 0.26, 0.06, 0.06, 0.06, 90, BLACK, 3],
  ] },
  lance: { muzzle: [0, 0.01, 0.6], parts: [
    ["Cube", 0, -0.04, 0.02, 0.05, 0.09, 0.07, 0, SLATE, 0],
    ["Cylinder", 0, 0.01, 0.3, 0.03, 0.56, 0.03, 90, BLACK, 0],
    ["Cylinder", 0, 0.01, 0.56, 0.02, 0.08, 0.02, 90, BLACK, 4],
  ] },
  rail: { muzzle: [0, 0.01, 0.5], parts: [
    ["Cube", 0, -0.05, 0.02, 0.05, 0.1, 0.07, 0, SLATE, 0],
    ["Cylinder", 0, 0.01, 0.22, 0.05, 0.46, 0.05, 90, BLACK, 0],
    ["Torus", 0, 0.01, 0.3, 0.05, 0.05, 0.05, 90, BLACK, 5],
    ["Torus", 0, 0.01, 0.14, 0.045, 0.045, 0.045, 90, BLACK, 3],
    ["Sphere", 0, 0.01, 0.47, 0.045, 0.045, 0.045, 0, BLACK, 4],
  ] },
};

/**
 * How the swarm's shapes become geometry. `mesh` is an engine primitive; `h` the height as a
 * share of the diameter; `spin` degrees a second about Y; `face` turns the shape the way it
 * moves; `wide` stretches it along X; `ring` adds a second, glowing ring round it.
 */
export const SHAPE_LOOKS = {
  circle:   { mesh: "Sphere",   h: 0.55, spin: 0, bob: 1 },
  dot:      { mesh: "Sphere",   h: 0.7, spin: 0, bob: 1 },
  diamond:  { mesh: "Cube",     h: 0.9, spin: 0, yaw: 45, tilt: 35, face: 1 },
  hex:      { mesh: "Cylinder", h: 0.55, spin: 25 },
  gear:     { mesh: "Torus",    h: 0.5, spin: 260 },
  square:   { mesh: "Cube",     h: 0.8, spin: 0 },
  block:    { mesh: "Cube",     h: 1.15, spin: 0 },
  tri:      { mesh: "Wedge",    h: 0.6, spin: 0, face: 1 },
  crescent: { mesh: "Capsule",  h: 0.45, spin: 0, face: 1, lie: 1 },
  pent:     { mesh: "Cylinder", h: 1.0, spin: 18 },
  ghost:    { mesh: "Capsule",  h: 1.5, spin: 0, lift: 0.5 },
  ring:     { mesh: "Torus",    h: 0.4, spin: 50 },
  pin:      { mesh: "Cube",     h: 0.9, spin: 80, yaw: 45 },
  chevron:  { mesh: "Wedge",    h: 0.6, spin: 0, face: 1 },
  pinwheel: { mesh: "Cube",     h: 0.4, spin: 380, wide: 1.8 },
  hole:     { mesh: "Sphere",   h: 1.0, spin: 0, ring: 1 },
  cocoon:   { mesh: "Capsule",  h: 1.3, spin: 0 },
  hexring:  { mesh: "Torus",    h: 0.6, spin: 40 },
};

/** The zones on the floor: a disc, a ring, or a thing standing on it. */
export const ZONE_LOOKS = {
  warp:   { form: "ring", colour: "#ff5b6e", glow: 1.8 },
  mortar: { form: "ring", colour: "#ff4d4d", glow: 2.2, fill: "#ff4d4d" },
  blast:  { form: "disc", colour: "#ffffff", glow: 1.2, flash: 1 },
  flame:  { form: "disc", colour: "#ff7a3d", glow: 2.6, flicker: 1 },
  aegis:  { form: "ring", colour: "#b8ff5e", glow: 1.6 },
  well:   { form: "disc", colour: "#c26bfa", glow: 1.2, ring: "#c26bfa", spin: 90 },
  dark:   { form: "disc", colour: "#03010a", glow: 0, dark: 1 },
  beacon: { form: "gem",  colour: "#b8ff5e", glow: 3 },
  pylon:  { form: "post", colour: "#ffe45b", glow: 3, ring: "#ffe45b" },
  turret: { form: "box",  colour: "#ff9e2c", glow: 2 },
  venom:  { form: "disc", colour: "#7fbf3a", glow: 1.4, ring: "#a0e05a" },
};

/** Which fighters glow in the dark, and how far: a pilot's light in world units. */
export const PILOT_LIGHT_RANGE = 7.5;
export const BOSS_LIGHT_RANGE = 14;
