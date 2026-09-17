// Room registry: unambiguous 6-char codes, capacity gate, cleanup.
//
// DarkShapes: TwinStickTron's server/rooms.js at 46baa25, ported onto the engine. The
// registry is the original's; two things are added for the authority that holds it:
//   - create() takes the code a client named (opts.code) as well as making one up, since
//     a room on the engine is asked for by its code rather than handed one by POST
//     /api/rooms, and refuses a code already in use rather than joining anybody to it;
//   - tick() is what each room's setInterval was: the authority calls it every frame
//     with the clock already advanced, and every room's own accumulator steps it.

import { ROOM_CODE_ALPHABET, ROOM_CODE_LEN } from "../shared/constants.js";
import { Room } from "./room.js";

export class Rooms {
  constructor(cap, clock) {
    this.cap = cap;
    this.clock = clock;
    this.map = new Map();
  }

  create(opts = {}) {
    if (this.map.size >= this.cap) return null; // "server full, retry" (SDD §3.5)
    let code = opts.code ?? null;
    if (code !== null && this.map.has(code)) return null;
    if (code === null) {
      do { code = genCode(); } while (this.map.has(code));
    }
    const room = new Room(code, (c) => this.map.delete(c), { ...opts, clock: this.clock });
    this.map.set(code, room);
    return room;
  }

  get(code) { return this.map.get(String(code ?? "").toUpperCase()) ?? null; }

  stats() {
    return { rooms: this.map.size, detail: [...this.map.values()].map(r => r.stats()) };
  }

  // Every room's loop, once a frame. A room that closes as it ticks leaves the map, so the
  // walk is over the rooms there were when it began.
  tick() {
    for (const room of [...this.map.values()]) room.tickLoop();
  }
}

function genCode() {
  let s = "";
  for (let i = 0; i < ROOM_CODE_LEN; i++) {
    s += ROOM_CODE_ALPHABET[Math.floor(Math.random() * ROOM_CODE_ALPHABET.length)];
  }
  return s;
}
