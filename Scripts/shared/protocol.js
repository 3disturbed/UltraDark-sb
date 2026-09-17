// Binary wire protocol (SDD §3.4) — one module, imported by server and
// client, so encode/decode can never drift. Little-endian DataView.
// Hot paths (INPUT, SNAPSHOT, PING) are binary; rare messages ride a JSON
// envelope behind a binary id byte.

// Bumped whenever the snapshot layout changes; WELCOME carries it so a
// stale cached client reloads itself instead of mis-decoding forever.
export const PROTO = 2;

export const MSG = {
  HELLO: 0x01,    // C→S JSON {name, pilot}
  WELCOME: 0x02,  // S→C JSON {id, code, solo, roster, phase, wave}
  INPUT: 0x03,    // C→S binary
  SNAPSHOT: 0x04, // S→C binary
  EVENT: 0x05,    // S→C JSON {t, ...}
  ACTION: 0x06,   // C→S JSON {t, ...} (start/pick/bank/again)
  PING: 0x08,     // C→S binary [f64 clientTime]
  PONG: 0x09,     // S→C binary [f64 clientTime][f64 serverTimeMs][u32 serverTick]
};

export const BTN = { FIRE: 1, DASH: 2, BOMB: 4, ABILITY: 8, USE: 16 };

import { POS_SCALE } from "./constants.js";

const te = new TextEncoder();
const td = new TextDecoder();

// ---------- envelopes ----------
export function encodeJson(id, obj) {
  const body = te.encode(JSON.stringify(obj));
  const out = new Uint8Array(1 + body.length);
  out[0] = id;
  out.set(body, 1);
  return out;
}
export function decodeJson(bytes) { // bytes: Uint8Array beyond the id byte
  return JSON.parse(td.decode(bytes));
}

// ---------- INPUT ----------
export function encodeInput(seq, mx, my, ax, ay, buttons) {
  const b = new ArrayBuffer(8);
  const v = new DataView(b);
  v.setUint8(0, MSG.INPUT);
  v.setUint16(1, seq, true);
  v.setInt8(3, q7(mx)); v.setInt8(4, q7(my));
  v.setInt8(5, q7(ax)); v.setInt8(6, q7(ay));
  v.setUint8(7, buttons);
  return b;
}
export function decodeInput(v) { // v: DataView over full message
  return {
    seq: v.getUint16(1, true),
    mx: v.getInt8(3) / 127, my: v.getInt8(4) / 127,
    ax: v.getInt8(5) / 127, ay: v.getInt8(6) / 127,
    buttons: v.getUint8(7),
  };
}
const q7 = (x) => Math.max(-127, Math.min(127, Math.round(x * 127)));

// ---------- PING/PONG ----------
export function encodePing(t) {
  const b = new ArrayBuffer(9); const v = new DataView(b);
  v.setUint8(0, MSG.PING); v.setFloat64(1, t, true); return b;
}
export function encodePong(clientT, serverMs, tick) {
  const b = new ArrayBuffer(21); const v = new DataView(b);
  v.setUint8(0, MSG.PONG); v.setFloat64(1, clientT, true);
  v.setFloat64(9, serverMs, true); v.setUint32(17, tick, true); return b;
}
export function decodePong(v) {
  return { clientT: v.getFloat64(1, true), serverMs: v.getFloat64(9, true), tick: v.getUint32(17, true) };
}

// ---------- SNAPSHOT ----------
// Header: [id u8][tick u32][yourAck u16][phase u8][wave u8][phaseT u16]
//         [mult10 u8][unbanked u32][banked u32][enemiesLeft u16][stasis10 u8]
// players u8 ×19B (…, cores u16), enemies u16 ×9B, bullets u16 ×9B,
// zones u8 ×9B, pickups u8 ×8B
export const ACK_OFFSET = 5; // patched per-recipient before send

export function encodeSnapshot(s) {
  const size = 23 + 1 + s.players.length * 19 + 2 + s.enemies.length * 9 +
               2 + s.bullets.length * 9 + 1 + s.zones.length * 9 +
               1 + s.pickups.length * 8;
  const b = new ArrayBuffer(size);
  const v = new DataView(b);
  let o = 0;
  v.setUint8(o, MSG.SNAPSHOT); o += 1;
  v.setUint32(o, s.tick, true); o += 4;
  v.setUint16(o, 0, true); o += 2; // yourAck placeholder
  v.setUint8(o, s.phase); o += 1;
  v.setUint8(o, Math.min(255, s.wave)); o += 1; // endless runs: clamp the u8
  v.setUint16(o, Math.min(65535, s.phaseT | 0), true); o += 2;
  v.setUint8(o, Math.round(s.mult * 10)); o += 1;
  v.setUint32(o, s.unbanked >>> 0, true); o += 4;
  v.setUint32(o, s.banked >>> 0, true); o += 4;
  v.setUint16(o, s.enemiesLeft, true); o += 2;
  v.setUint8(o, Math.min(255, Math.round((s.stasis ?? 0) * 10))); o += 1;

  v.setUint8(o, s.players.length); o += 1;
  for (const p of s.players) {
    v.setUint8(o, p.id); v.setUint8(o + 1, p.pilot); v.setUint8(o + 2, p.state);
    v.setUint16(o + 3, qp(p.x), true); v.setUint16(o + 5, qp(p.y), true);
    v.setUint8(o + 7, qa(p.aim)); v.setUint8(o + 8, p.hp);
    v.setUint8(o + 9, Math.min(255, Math.round(p.dashCd * 10)));
    v.setUint8(o + 10, Math.min(255, Math.round(p.abilCd)));
    v.setUint8(o + 11, p.bombs);
    v.setUint8(o + 12, p.flags);
    v.setUint8(o + 13, p.orbitals ?? 0);
    v.setUint8(o + 14, p.cons?.[0] ?? 0);
    v.setUint8(o + 15, p.cons?.[1] ?? 0);
    v.setUint8(o + 16, p.cons?.[2] ?? 0);
    v.setUint16(o + 17, Math.min(65535, p.cores ?? 0), true);
    o += 19;
  }
  v.setUint16(o, s.enemies.length, true); o += 2;
  for (const e of s.enemies) {
    v.setUint16(o, e.id, true); v.setUint8(o + 2, e.kind);
    v.setUint16(o + 3, qp(e.x), true); v.setUint16(o + 5, qp(e.y), true);
    v.setUint8(o + 7, e.hpPct); v.setUint8(o + 8, e.flags);
    o += 9;
  }
  v.setUint16(o, s.bullets.length, true); o += 2;
  for (const u of s.bullets) {
    v.setUint16(o, u.id, true);
    v.setUint16(o + 2, qp(u.x), true); v.setUint16(o + 4, qp(u.y), true);
    v.setUint8(o + 6, u.owner); v.setUint16(o + 7, 0, true);
    o += 9;
  }
  v.setUint8(o, s.zones.length); o += 1;
  for (const z of s.zones) {
    v.setUint8(o, z.kind);
    v.setUint16(o + 1, qp(z.x), true); v.setUint16(o + 3, qp(z.y), true);
    v.setUint16(o + 5, Math.min(65535, Math.round(z.r * POS_SCALE)), true);
    v.setUint16(o + 7, Math.min(65535, Math.round(z.ttl * 10)), true);
    o += 9;
  }
  v.setUint8(o, s.pickups.length); o += 1;
  for (const k of s.pickups) {
    v.setUint16(o, k.id, true);
    v.setUint8(o + 2, k.kind);
    v.setUint16(o + 3, qp(k.x), true); v.setUint16(o + 5, qp(k.y), true);
    v.setUint8(o + 7, Math.min(255, Math.round(k.ttl * 10)));
    o += 8;
  }
  return b;
}

export function decodeSnapshot(v) {
  let o = 1;
  const s = {};
  s.tick = v.getUint32(o, true); o += 4;
  s.yourAck = v.getUint16(o, true); o += 2;
  s.phase = v.getUint8(o); o += 1;
  s.wave = v.getUint8(o); o += 1;
  s.phaseT = v.getUint16(o, true); o += 2;
  s.mult = v.getUint8(o) / 10; o += 1;
  s.unbanked = v.getUint32(o, true); o += 4;
  s.banked = v.getUint32(o, true); o += 4;
  s.enemiesLeft = v.getUint16(o, true); o += 2;
  s.stasis = v.getUint8(o) / 10; o += 1;

  const np = v.getUint8(o); o += 1;
  s.players = new Array(np);
  for (let i = 0; i < np; i++) {
    s.players[i] = {
      id: v.getUint8(o), pilot: v.getUint8(o + 1), state: v.getUint8(o + 2),
      x: uq(v.getUint16(o + 3, true)), y: uq(v.getUint16(o + 5, true)),
      aim: ua(v.getUint8(o + 7)), hp: v.getUint8(o + 8),
      dashCd: v.getUint8(o + 9) / 10, abilCd: v.getUint8(o + 10),
      bombs: v.getUint8(o + 11), flags: v.getUint8(o + 12),
      orbitals: v.getUint8(o + 13),
      cons: [v.getUint8(o + 14), v.getUint8(o + 15), v.getUint8(o + 16)],
      cores: v.getUint16(o + 17, true),
    };
    o += 19;
  }
  const ne = v.getUint16(o, true); o += 2;
  s.enemies = new Array(ne);
  for (let i = 0; i < ne; i++) {
    s.enemies[i] = {
      id: v.getUint16(o, true), kind: v.getUint8(o + 2),
      x: uq(v.getUint16(o + 3, true)), y: uq(v.getUint16(o + 5, true)),
      hpPct: v.getUint8(o + 7), flags: v.getUint8(o + 8),
    };
    o += 9;
  }
  const nb = v.getUint16(o, true); o += 2;
  s.bullets = new Array(nb);
  for (let i = 0; i < nb; i++) {
    s.bullets[i] = {
      id: v.getUint16(o, true),
      x: uq(v.getUint16(o + 2, true)), y: uq(v.getUint16(o + 4, true)),
      owner: v.getUint8(o + 6),
    };
    o += 9;
  }
  const nz = v.getUint8(o); o += 1;
  s.zones = new Array(nz);
  for (let i = 0; i < nz; i++) {
    s.zones[i] = {
      kind: v.getUint8(o),
      x: uq(v.getUint16(o + 1, true)), y: uq(v.getUint16(o + 3, true)),
      r: v.getUint16(o + 5, true) / POS_SCALE,
      ttl: v.getUint16(o + 7, true) / 10,
    };
    o += 9;
  }
  const nk = v.getUint8(o); o += 1;
  s.pickups = new Array(nk);
  for (let i = 0; i < nk; i++) {
    s.pickups[i] = {
      id: v.getUint16(o, true), kind: v.getUint8(o + 2),
      x: uq(v.getUint16(o + 3, true)), y: uq(v.getUint16(o + 5, true)),
      ttl: v.getUint8(o + 7) / 10,
    };
    o += 8;
  }
  return s;
}

const qp = (x) => Math.max(0, Math.min(65535, Math.round(x * POS_SCALE)));
const uq = (x) => x / POS_SCALE;
const qa = (a) => Math.round((((a % (Math.PI * 2)) + Math.PI * 2) % (Math.PI * 2)) / (Math.PI * 2) * 255) & 255;
const ua = (b) => (b / 255) * Math.PI * 2;

// Player flag bits
export const PF = { DASHING: 1, IFRAMES: 2, REVIVING: 4, OVERDRIVE: 8, WRAPPED: 16 };
export const EF = { ENRAGED: 1, WARPING: 2, OPEN: 4, PHASED: 8, CHILLED: 16 };
