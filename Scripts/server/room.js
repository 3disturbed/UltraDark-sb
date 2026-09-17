// A Room owns one Sim, its roster of sockets, the 30 Hz loop, and all
// broadcast fan-out. The server is the host (SDD §3.2) — there is no
// migration; a room lives here and players are equal clients.
//
// DarkShapes: TwinStickTron's server/room.js at 46baa25, ported onto the engine. The
// game logic is the original's, statement for statement -- resume seats, the input
// flood limit, the event drain, snapshots every second tick with each recipient's
// own ack, the roster and WELCOME. What moved is what it stood on:
//   - time is opts.clock (engine/clock.js): now() where it read Date.now and
//     process.hrtime, setTimeout on that clock, and no setInterval -- the authority
//     calls tickLoop every frame, and the accumulator below decides whether a tick is
//     due, exactly as it did when node woke it;
//   - a socket is engine/wire.js's Connection, with the send, close and readyState the
//     ws had, and a frame arrives as a Uint8Array rather than a Buffer;
//   - there is no db.js (SQLite). On DarkShapes' official server opts.db is
//     server/ranked.js, which sends the run-end record below to the Darks Games boards;
//     it is handed the room's clients, whose accounts the score is for, and answers
//     each of them later, so the event goes out without a rank;
//   - how long a tick took is measured with Date.now, which a script has, rather than
//     process.hrtime, which it has not: its mean over many ticks is still the mean.

import {
  MSG, PROTO, encodeJson, decodeJson, decodeInput, encodeSnapshot, encodePong,
  decodePong, ACK_OFFSET,
} from "../shared/protocol.js";
import { TICK_DT, SNAPSHOT_EVERY, PHASE, PS, MAX_PLAYERS, PILOTS } from "../shared/constants.js";
import { computeStats } from "../shared/mods.js";
import { Sim } from "./sim.js";
import { todayUTC } from "../engine/clock.js";
const EMPTY_GRACE_MS = 10 * 60 * 1000; // codes survive 10 min empty (SDD §3.5)
const INPUT_FLOOD_LIMIT = 90;          // inputs/sec before we drop frames

export class Room {
  constructor(code, onEmpty, opts = {}) {
    this.code = code;
    this.onEmpty = onEmpty;
    this.clock = opts.clock;
    this.db = opts.db ?? null;
    this.mode = opts.mode ?? "run";
    this.sim = new Sim();
    if (opts.dailySeed != null) this.sim.dailySeed = opts.dailySeed;
    this.clients = new Map();   // playerId -> {ws, name, lastSeq, inputCount, resumeKey, account}
    this.resumeKeys = new Map(); // resumeKey -> playerId (survives disconnects)
    this.nextId = 1;
    this.emptySince = this.clock.now();
    this.acc = 0;
    this.last = this.clock.now();
    this.tickBusyMs = 0;
    this.tickCount = 0;
  }

  get size() { return this.clients.size; }

  // ---------- lifecycle ----------
  tickLoop() {
    const now = this.clock.now();
    let dt = (now - this.last) / 1000;
    this.last = now;
    if (dt > 0.25) dt = 0.25; // clamp hitches; never spiral
    this.acc += dt;
    const t0 = Date.now();
    while (this.acc >= TICK_DT) {
      this.acc -= TICK_DT;
      this.sim.step(TICK_DT);
      this.drainEvents();
      if (this.sim.tick % SNAPSHOT_EVERY === 0) this.broadcastSnapshot();
      if (this.sim.tick % 30 === 0) this.everySecond();
    }
    this.tickBusyMs += Date.now() - t0;
    this.tickCount++;
    if (this.size === 0 && now - this.emptySince > EMPTY_GRACE_MS) this.destroy();
  }

  destroy() {
    for (const c of this.clients.values()) { try { c.ws.close(); } catch { /* already gone */ } }
    this.onEmpty(this.code);
  }

  // ---------- joining ----------
  // account: verified DG identity {sub, name, handle} or null for guests.
  // (DarkShapes: the account id the engine's server verified for this client, or null.)
  join(ws, hello, account = null) {
    if (this.size >= MAX_PLAYERS) {
      ws.send(encodeJson(MSG.EVENT, { t: "error", error: "room_full" }));
      ws.close();
      return;
    }
    // reconnect: same resumeKey within grace → same pilot seat (SDD §2.6)
    let id = null;
    if (hello.resumeKey && this.resumeKeys.has(hello.resumeKey)) {
      const pid = this.resumeKeys.get(hello.resumeKey);
      if (this.sim.players.has(pid) && !this.clients.has(pid)) id = pid;
    }
    if (id === null) {
      id = this.nextId++;
      this.sim.addPlayer(id, String(hello.name ?? "PILOT").slice(0, 12), hello.pilot | 0);
    }
    const resumeKey = `${this.code}-${id}-${Math.random().toString(36).slice(2, 10)}`;
    this.resumeKeys.set(resumeKey, id);
    this.clients.set(id, { ws, name: String(hello.name ?? "PILOT").slice(0, 12), lastSeq: 0, inputCount: 0, resumeKey, account });
    ws._roomPlayerId = id;

    ws.send(encodeJson(MSG.WELCOME, {
      id, code: this.code, resumeKey, proto: PROTO,
      phase: this.sim.phase, wave: this.sim.wave,
      roster: this.roster(),
      spectating: this.sim.players.get(id)?.state === PS.SPECTATING,
    }));
    this.broadcast(encodeJson(MSG.EVENT, { t: "roster", roster: this.roster() }), id);
  }

  leave(ws) {
    const id = ws._roomPlayerId;
    if (id == null || !this.clients.has(id)) return;
    this.clients.delete(id);
    // keep the sim player for 30 s so a reconnect resumes the seat
    const p = this.sim.players.get(id);
    if (p) {
      this.clock.setTimeout(() => {
        if (!this.clients.has(id)) {
          this.sim.removePlayer(id);
          this.broadcast(encodeJson(MSG.EVENT, { t: "roster", roster: this.roster() }));
        }
      }, 30_000);
    }
    if (this.size === 0) this.emptySince = this.clock.now();
    this.broadcast(encodeJson(MSG.EVENT, { t: "roster", roster: this.roster() }));
  }

  roster() {
    const out = [];
    for (const [pid, c] of this.clients) {
      const p = this.sim.players.get(pid);
      out.push({ id: pid, name: c.name, pilot: p?.pilot ?? 0, state: p?.state ?? PS.SPECTATING });
    }
    return out;
  }

  // ---------- messages ----------
  onMessage(ws, data) {
    const id = ws._roomPlayerId;
    const c = this.clients.get(id);
    if (!c) return;
    const buf = data instanceof ArrayBuffer ? new Uint8Array(data) : data;
    if (!buf.length) return;
    const type = buf[0];
    const dv = new DataView(buf.buffer, buf.byteOffset, buf.length);
    if (type === MSG.INPUT) {
      if (++c.inputCount > INPUT_FLOOD_LIMIT) return; // reset each second below
      const inp = decodeInput(dv);
      const p = this.sim.players.get(id);
      const fresh = inp.seq > c.lastSeq || (c.lastSeq > 60000 && inp.seq < 5000); // u16 wrap
      if (p && fresh) {
        c.lastSeq = inp.seq;
        p.input = inp;
      }
    } else if (type === MSG.PING) {
      const clientT = dv.getFloat64(1, true);
      ws.send(encodePong(clientT, Date.now(), this.sim.tick));
    } else if (type === MSG.ACTION) {
      let a;
      try { a = decodeJson(new Uint8Array(buf.buffer, buf.byteOffset + 1, buf.length - 1)); }
      catch { ws.close(); return; } // malformed frame → disconnect (SDD §3.9)
      const p = this.sim.players.get(id);
      if (!p) return;
      if (a.t === "pilot" && this.sim.phase === PHASE.LOBBY) {
        p.pilot = Math.max(0, Math.min(PILOTS.length - 1, a.pilot | 0));
        p.weapon = PILOTS[p.pilot].weapon;
        p.stats = computeStats(PILOTS[p.pilot], p.mods);
        p.hp = Math.min(p.hp, this.sim.hpMax(p));
        this.broadcast(encodeJson(MSG.EVENT, { t: "roster", roster: this.roster() }));
      } else {
        this.sim.action(p, a);
      }
    } else if (type === MSG.HELLO) {
      // duplicate HELLO after join — ignore
    } else {
      ws.close(); // unknown frame
    }
  }

  everySecond() {
    for (const c of this.clients.values()) c.inputCount = 0;
  }

  // ---------- broadcast ----------
  drainEvents() {
    if (!this.sim.events.length) return;
    for (const ev of this.sim.events) {
      // run end: record the server-authoritative score, attach the rank
      if ((ev.t === "gameover" || ev.t === "victory") && this.db && ev.score > 0) {
        const names = (ev.roster ?? []).map(r => r.name);
        const r = this.db.submit({
          mode: this.mode, date: todayUTC(), squad: Math.max(1, names.length),
          score: ev.score, wave: ev.wave, names,
          clients: [...this.clients.values()], // DarkShapes: whose accounts it is for
        });
        if (r) { // DarkShapes: ranked.js answers each account later, as EVENT {t: "ranked"}
          ev.rank = r.accepted ? r.rank : null;
          ev.counted = r.accepted;
          ev.mode = this.mode;
        }
      }
      if (ev.to != null) {
        const c = this.clients.get(ev.to);
        if (c && c.ws.readyState === 1) c.ws.send(encodeJson(MSG.EVENT, ev));
      } else {
        this.broadcast(encodeJson(MSG.EVENT, ev));
      }
    }
    this.sim.events.length = 0;
  }

  broadcastSnapshot() {
    const snap = encodeSnapshot(this.sim.buildSnapshot());
    for (const [pid, c] of this.clients) {
      if (c.ws.readyState !== 1) continue;
      const copy = snap.slice(0);
      new DataView(copy).setUint16(ACK_OFFSET, c.lastSeq, true);
      c.ws.send(copy);
    }
  }

  broadcast(bytes, exceptId) {
    for (const [pid, c] of this.clients) {
      if (pid === exceptId) continue;
      if (c.ws.readyState === 1) c.ws.send(bytes);
    }
  }

  // health metrics for /api/health
  stats() {
    const busyMs = this.tickCount ? this.tickBusyMs / this.tickCount : 0;
    return { code: this.code, players: this.size, phase: this.sim.phase, wave: this.sim.wave, avgTickMs: Math.round(busyMs * 100) / 100 };
  }
}
