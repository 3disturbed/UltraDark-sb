// -----------------------------------------------------------------------------
// clock — the authority's time, advanced by the engine.
//
// UltraDark's server lived on node's clocks: a setInterval woke each room thirty
// times a second, process.hrtime measured how long it had slept, and Date.now and
// setTimeout decided when an empty room closed and a dropped pilot's seat was given
// up. A script has none of those, and would not want them: the engine already
// advances time once a frame, the same way on both engines, and a test can advance
// it as fast as it likes.
//
// So the authority keeps one Clock and advances it from onUpdate with the frame's
// unscaled seconds (a paused game still serves its room). now() is milliseconds on
// that clock, and a timer fires on the frame whose advance passes it. Each room
// keeps its own 30 Hz accumulator over now(), as it kept one over process.hrtime,
// so how often the simulation steps depends on time alone, never on the frame rate.
// -----------------------------------------------------------------------------

export class Clock {
  constructor() {
    this.ms = 0;
    this.timers = [];
    this.nextTimer = 1;
  }

  /** Milliseconds since the clock started: what the room read from Date.now and process.hrtime. */
  now() {
    return this.ms;
  }

  /** Moves the clock on by `seconds` and runs every timer that has come due, earliest first. */
  advance(seconds) {
    if (!(seconds > 0) || seconds === Infinity) return;
    this.ms += seconds * 1000;
    for (;;) {
      let next = -1;
      for (let i = 0; i < this.timers.length; i++) {
        const timer = this.timers[i];
        if (timer.due > this.ms) continue;
        if (next < 0 || timer.due < this.timers[next].due) next = i;
      }
      if (next < 0) return;
      const [timer] = this.timers.splice(next, 1);
      timer.fn();
    }
  }

  /** setTimeout on this clock: runs `fn` once, `ms` milliseconds from now. Returns a handle. */
  setTimeout(fn, ms) {
    const id = this.nextTimer++;
    this.timers.push({ id, due: this.ms + Math.max(0, Number(ms) || 0), fn });
    return id;
  }

  /** clearTimeout on this clock. */
  clearTimeout(id) {
    const i = this.timers.findIndex((timer) => timer.id === id);
    if (i >= 0) this.timers.splice(i, 1);
  }
}

/** The UTC date, yyyy-mm-dd: server/db.js's todayUTC, which a Daily Dark seed and a board are kept by. */
export function todayUTC() {
  return new Date().toISOString().slice(0, 10);
}
