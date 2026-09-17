// -----------------------------------------------------------------------------
// authority — what TwinStickTron's server/index.js did for a socket, done by
// whichever machine is the session's authority.
//
// The original was one node process holding every room: POST /api/rooms made one,
// a WebSocket to /ws?room=CODE reached it, and index.js walked each socket from
// "which room" through HELLO to the room's own messages. On the engine the rooms
// live in this script, on the machine where Network.isHost is true: one room on a
// listen host, a tab running a solo game or a relay room's host seat, and many on a
// dedicated server (Network.authority === "dedicated"). Each socket comes through
// engine/wire.js and is walked the same way:
//
//   1. Its first frame names the room: ACTION {t: "join", code}. The code is upper-
//      cased as rooms.get did and must read /^[A-Z0-9]{4,8}$/. No such room is
//      EVENT {t: "error", error: "no_such_room"} and a close, as index.js answered a
//      bad URL. A join that also carries a mode asks for the room to be made, which
//      is POST /api/rooms: "daily" or "run", or "challenge" with a seed; ten a minute
//      per machine, and a full registry is "server_full". A listen host's one room is
//      made by the host's own socket; nobody else makes rooms there.
//   2. Its next frame must be HELLO, as index.js required, and joins the room with the
//      account the engine's server verified for that machine (the engine checks a
//      Darks Games token itself, so HELLO's authToken is not read).
//   3. After that every frame is the room's (room.js, onMessage), and a socket that
//      closes -- or a machine that leaves the session -- leaves it (room.leave).
//
// A frame nobody could read closes its socket, as index.js closed a socket whose
// message threw. Nothing here throws out of a frame.
//
// db.js is server/ranked.js: every Daily Dark room takes the date's seed from it,
// and on DarkShapes' official dedicated server (DG.ranked) it is the rooms' db, which
// sends each run's score to the Darks Games boards.
// -----------------------------------------------------------------------------

import { MSG, encodeJson, decodeJson } from "../shared/protocol.js";
import { Rooms } from "./rooms.js";
import { Clock, todayUTC } from "../engine/clock.js";
import { Ranked, dailySeed as seedForDay } from "./ranked.js";

/** The rooms a dedicated server holds at once: index.js's ROOM_CAP default. */
export const ROOM_CAP = 64;

/** What a room code must read, once upper-cased. */
export const ROOM_CODE = /^[A-Z0-9]{4,8}$/;

const CREATES_PER_MINUTE = 10;   // index.js: "Rate limit room creation per IP (SDD §3.9): 10/min"

export class Authority {
  /**
   * @param {object} options
   * @param {object} options.network The engine's Network global, or anything with `players`.
   * @param {boolean} [options.dedicated] Many rooms, made by any machine; otherwise one, made by this one.
   * @param {object} [options.db] db.js's part: the ranked record and the Daily Dark seed. Made from `dg` when it is ranked.
   * @param {object} [options.dg] The engine's DG global: on a dedicated server whose DG.ranked is true, the rooms record to it.
   * @param {(message: string) => void} [options.warn] Where a frame that would not read is reported.
   */
  constructor({ network, dedicated = false, db = null, dg = null, warn = () => {} }) {
    this.network = network;
    this.dedicated = dedicated;
    this.db = db;
    this.dg = dg;
    this.warn = warn;
    this.clock = new Clock();
    this.rooms = new Rooms(dedicated ? ROOM_CAP : 1, this.clock);
    this.createHits = new Map();   // machine -> rooms made this minute
    this.hitsSince = 0;
  }

  /** Once a frame: time moves on, and every room's loop runs. */
  update(seconds) {
    // The hub's secret is checked as the server starts, so a ranked server may say so a frame late.
    if (this.db === null && this.dedicated && this.dg !== null && this.dg.ranked === true) {
      this.db = new Ranked({ dg: this.dg, warn: this.warn });
    }
    this.clock.advance(seconds);
    if (this.clock.now() - this.hitsSince >= 60_000) {
      this.createHits.clear();
      this.hitsSince = this.clock.now();
    }
    this.rooms.tick();
  }

  /** A socket's frame, from engine/wire.js; `bytes` is null for one that was not base64. */
  frame(ws, bytes) {
    try {
      if (bytes === null || bytes.length === 0) { ws.close(); return; }
      if (!ws.room) {
        const room = this.roomFor(ws, bytes);
        if (typeof room === "string") {
          ws.send(encodeJson(MSG.EVENT, { t: "error", error: room }));
          ws.close();
          return;
        }
        ws.room = room;
        ws.joined = false;
      } else if (!ws.joined) {
        if (bytes[0] !== MSG.HELLO) { ws.close(); return; }
        const hello = decodeJson(bytes.subarray(1));
        ws.joined = true;
        ws.room.join(ws, hello, this.accountOf(ws.sender));
      } else {
        ws.room.onMessage(ws, bytes);
      }
    } catch (err) {
      this.warn(`ws message error: ${err.message}`);
      try { ws.close(); } catch { /* gone */ }
    }
  }

  /** A socket closed, from either end or with its machine. */
  closed(ws) {
    if (ws.joined) ws.room.leave(ws);
  }

  /** The room a socket's first frame names, or why there is none. */
  roomFor(ws, bytes) {
    if (bytes[0] !== MSG.ACTION) return "no_such_room";
    const a = decodeJson(bytes.subarray(1));
    const code = String(a?.code ?? "").toUpperCase();
    if (a?.t !== "join" || !ROOM_CODE.test(code)) return "no_such_room";
    const room = this.rooms.get(code);
    if (room) return room;
    if (a.mode === undefined || !(this.dedicated || ws.local)) return "no_such_room";
    return this.create(ws, code, a);
  }

  /** POST /api/rooms, for a socket that asked for `code`: the room, or the error the endpoint gave. */
  create(ws, code, body) {
    const hits = (this.createHits.get(ws.sender) ?? 0) + 1;
    this.createHits.set(ws.sender, hits);
    if (hits > CREATES_PER_MINUTE) return "slow_down";
    let mode = body?.mode === "daily" ? "daily" : "run";
    let dailySeed = mode === "daily" ? (this.db ?? { getDailySeed: seedForDay }).getDailySeed(todayUTC()) : null;
    // challenge rooms: normal scoring, but waves pinned to the challenger's seed
    if (body?.mode === "challenge" && Number.isFinite(Number(body.seed))) {
      mode = "run";
      dailySeed = Number(body.seed) >>> 0;
    }
    const room = this.rooms.create({ code, db: this.db, mode, dailySeed });
    return room ?? "server_full";
  }

  /** The account the engine's server verified for a machine, or null for a guest. */
  accountOf(sender) {
    for (const player of this.network.players) {
      if (player.id === sender) return player.account ?? null;
    }
    return null;
  }
}
