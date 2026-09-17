// Shared constants — imported by BOTH server and client (SDD §3.10).
// Anything both sides must agree on lives here. Pure data, no Node/DOM APIs.

export const ARENA_W = 2048;
export const ARENA_H = 1152;
export const WALL_PAD = 24; // ships/enemies keep this far off the wall

export const TICK_RATE = 30;              // server sim Hz
export const TICK_DT = 1 / TICK_RATE;
export const SNAPSHOT_EVERY = 2;          // snapshots at 15 Hz
export const INPUT_RATE = 30;             // client input send Hz
export const INTERP_DELAY_MS = 100;       // remote entity render delay

export const POS_SCALE = 8;               // quantise positions to 1/8 unit in u16

export const PLAYER = {
  RADIUS: 14,
  MAX_HP: 3,
  SPEED: 300,                 // units/s
  ACCEL: 2600,                // approach accel (reaches speed <80ms per SDD §2.2)
  FRICTION: 3400,
  FIRE_CD: 0.14,              // seconds between shots (base)
  BULLET_SPEED: 720,
  BULLET_LIFE: 1.4,
  BULLET_DMG: 1,
  DASH_SPEED: 900,
  DASH_TIME: 0.16,
  DASH_CD: 2.0,
  DASH_IFRAMES: 0.28,
  HIT_IFRAMES: 1.0,
  ABILITY_CD: 12,
  START_BOMBS: 1,
  MAX_BOMBS: 3,
  REVIVE_RANGE: 56,
  REVIVE_TIME: 1.5,
  DOWNED_TIMEOUT: 15,
  SOLO_LIVES: 3,
};

export const MULT = {
  MAX: 10,
  PER_KILL: 0.12,
  DECAY_PER_S: 0.35,     // after grace
  DECAY_GRACE: 3,        // seconds without a kill before decay
  HIT_FACTOR: 0.5,       // halve on any player hit
};

export const WAVE = {
  // ENDLESS: no victory wave — runs end only on a squad wipe. Budgets,
  // enemy speed and boss HP keep climbing at the same per-wave rate the
  // first 25 waves established; bosses cycle every 5th wave forever.
  INTERMISSION_S: 20,
  // After a boss the Core Shop opens first and the next wave WAITS until every
  // player has shopped and picked an upgrade -- a ready check. A fixed 40s used
  // to start the wave while people were still spending. This is only the
  // fallback, so one idle player cannot hold the room for ever.
  SHOP_WAIT_S: 120,
  READY_COUNTDOWN_S: 3,  // once everyone is ready (every intermission)
  BOSS_EVERY: 5,
  SPAWN_MIN_DIST: 320,   // never spawn within this of a player (SDD §2.9)
  WARP_IN_S: 0.5,
  DARK_START: 16,        // arena lighting starts failing here (SDD §2.4)
};

// Kill cores: the killer keeps the full value and every teammate still in the
// run gets this share of it. Paying only the killer let whoever cleared fastest
// outgrow the rest of the squad in the shop.
export const TEAM_CORE_SHARE = 0.5;

export const REVIVE_COST_PER_WAVE = 100;  // banked-score insurance (SDD §2.3)

// Entity kind ids on the wire (u8)
export const EK = {
  DRONE: 1, MITE: 2, WEAVER: 3, BRUTE: 4, SPINNER: 5, MORTAR: 6,
  SNIPER: 7, LEECH: 8, WARDEN: 9, FORGE: 10, GHOST: 11, MAGNET: 12,
  // Geometry Wars wing
  WANDERER: 13, GRUNT: 14, DODGER: 15, PINWHEEL: 16, BLACKHOLE: 17,
  COCOON: 18, // BROODMOTHER's web-wrap add — shoot it to free your teammate
  BRUTE_PRIME: 20, HEX_PRIME: 21, FOUNDRY: 22, SHEPHERD: 23, ULTRADARK: 24,
  PATCHWORK: 25, THADDIUS: 26, BROODMOTHER: 27,
};

// Regular enemies gain HP with depth (bosses have their own curve)
export const ENEMY_HP_PER_WAVE = 0.04;

// Zone kinds (telegraphs & fields, streamed in snapshots, u8)
export const ZK = {
  WARP: 1,        // enemy spawn telegraph
  MORTAR_TELE: 2, // incoming mortar shell circle
  BLAST: 3,       // brief explosion visual
  FLAME: 4,       // EMBER wall/zone
  AEGIS: 5,       // HALO bubble
  WELL: 6,        // ONYX gravity well
  DARK: 7,        // NULL SHEPHERD's spreading darkness
  BEACON: 8,      // AMBER's placed warp beacon
  PYLON: 9,       // SPARKS' tesla pylon
  TURRET: 10,     // RIGG's auto-turret (visual; logic lives in sim.turrets)
  VENOM: 11,      // BROODMOTHER's poison pools — linger and they bite
};

// Player state (u8)
export const PS = { ALIVE: 0, DOWNED: 1, OUT: 2, SPECTATING: 3 };

// Room phase (u8)
export const PHASE = { LOBBY: 0, WAVE: 1, INTERMISSION: 2, GAMEOVER: 3, VICTORY: 4 };

// symbol: drawn at the centre of the ship arrow so classes read at a glance.
// weapon: the class's DEFAULT weapon — cd owns fire cadence (PLAYER.FIRE_CD
// is just the baseline blaster value); kinds are dispatched in sim.fire().
// abilityCd: per-class Q cooldown (abilityCdr class mods shave it).
export const PILOTS = [
  { id: 0, name: "BINK",   color: "#39f0ff", lean: "fast · skirmisher",  ability: "Blink Volley", symbol: "✦", speed: 1.18, fire: 1.0,  maxHp: 0, abilityCd: 12,
    weapon: { kind: "smg",     label: "SMG",         cd: 0.09, dmg: 0.6, jitter: 0.07 } },
  { id: 1, name: "BLAZE",  color: "#ff7a3d", lean: "mid speed · close range", ability: "Flame Zone", symbol: "▲", speed: 1.0, fire: 1.0, maxHp: 0, abilityCd: 12,
    weapon: { kind: "shotgun", label: "SCATTERGUN",  cd: 0.65, dmg: 0.8, pellets: 7, spread: 0.38, life: 0.4 } },
  { id: 2, name: "AMBER",  color: "#b8ff5e", lean: "support · healer",   ability: "Beacon Warp",  symbol: "◯", speed: 1.0,  fire: 1.0,  maxHp: 0, abilityCd: 8,
    weapon: { kind: "blaster", label: "BLASTER",     cd: 0.14, dmg: 1 } },
  { id: 3, name: "DAVE",   color: "#c26bfa", lean: "slow · tank · melee", ability: "Gravity Well", symbol: "◉", speed: 0.78, fire: 1.0, maxHp: 2, abilityCd: 12,
    weapon: { kind: "cleave",  label: "CLEAVER",     cd: 0.5,  dmg: 3, arcR: 95, arcHalf: 1.05, knockback: 60 } },
  { id: 4, name: "SPARKS", color: "#ffe45b", lean: "chain lightning",    ability: "Tesla Pylon",  symbol: "⌁", speed: 1.0,  fire: 1.0,  maxHp: 0, abilityCd: 14,
    weapon: { kind: "arc",     label: "ARC GUN",     cd: 0.22, dmg: 0.9, chain: 2, chainR: 140, chainDmg: 0.5 } },
  { id: 5, name: "RIGG",   color: "#ff9e2c", lean: "engineer · turrets", ability: "Auto-Turret",  symbol: "⚒", speed: 0.92, fire: 1.0,  maxHp: 0, abilityCd: 16,
    weapon: { kind: "blaster", label: "BLASTER",     cd: 0.16, dmg: 1.1 } },
  { id: 6, name: "KELVIN", color: "#8fd8ff", lean: "control · chill",    ability: "Frost Nova",   symbol: "❄", speed: 1.0,  fire: 1.0,  maxHp: 0, abilityCd: 11,
    weapon: { kind: "lance",   label: "CHILL LANCE", cd: 0.30, dmg: 1.3, chill: 1.2 } },
  { id: 7, name: "HAWK",   color: "#ff5b8e", lean: "sniper · railgun",   ability: "Triple Rail",  symbol: "⌖", speed: 0.95, fire: 1.0,  maxHp: 0, abilityCd: 9,
    weapon: { kind: "rail",    label: "RAILGUN",     cd: 0.9,  dmg: 4, speedMul: 3, pierce: 3 } },
];

export const AURA = { R: 140, ALLY_S: 5, SELF_S: 10 }; // AMBER's healing aura
export const TURRET = { TTL: 8, CD: 0.25, DMG: 0.6, RANGE: 700 };
export const PYLON = { R: 200, TTL: 8, CD: 0.5, DMG: 2 };

export const MAX_PLAYERS = 8; // per lobby

// Orbital blades (the "Orbital"/"Twin Orbital" mods) — shared so the client
// renders blades exactly where the server deals the damage.
export const ORBITAL = { R: 60, BLADE: 14, ROT: 4, DPS: 4 };

export const PICKUP = { RADIUS: 18, TTL: 12, MAX_CARRY: 3, MAX_LIVE: 40 };

export const ROOM_CODE_ALPHABET = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"; // no 0/O/1/I/L
export const ROOM_CODE_LEN = 6;

export function clamp(v, lo, hi) { return v < lo ? lo : v > hi ? hi : v; }
