// -----------------------------------------------------------------------------
// TimerManager — one-shot and repeating callbacks on engine time.
//
// Timers run on the engine clock, so a paused game pauses them, unlike
// setTimeout. Ask for unscaled time explicitly when a timer must keep running
// through a pause menu.
// -----------------------------------------------------------------------------

let _nextId = 1;

/** An opaque reference to a scheduled timer. */
export class TimerHandle {
    constructor(id = 0) { this.id = id; }
    get isValid() { return this.id !== 0; }
    equals(other) { return other instanceof TimerHandle && other.id === this.id; }
}

/** Schedules callbacks against engine time. One instance lives on the host. */
export class TimerManager {
    /** The manager the engine host installs. */
    static instance = new TimerManager();

    constructor() { this._timers = new Map(); }

    /** How many timers are scheduled, paused ones included. */
    get activeCount() { return this._timers.size; }

    /**
     * Schedules a callback.
     *
     * @param {number} intervalSeconds Seconds between fires. Clamped at zero.
     * @param {Function} callback What to run.
     * @param {object} [options]
     * @param {boolean} [options.looping=false] Repeat rather than fire once.
     * @param {number} [options.firstDelay] Delay before the first fire. Defaults to the interval.
     * @param {boolean} [options.useUnscaledTime=false] Ignore `Time.timeScale`.
     * @returns {TimerHandle}
     */
    setTimer(intervalSeconds, callback, options = {}) {
        const { looping = false, firstDelay = -1, useUnscaledTime = false } = options;
        const interval = Math.max(0, intervalSeconds);
        const handle = new TimerHandle(_nextId++);

        this._timers.set(handle.id, {
            interval,
            remaining: firstDelay >= 0 ? firstDelay : interval,
            callback,
            looping,
            useUnscaledTime,
            paused: false,
        });

        return handle;
    }

    /** Runs a callback at the start of the next tick. */
    setTimerForNextTick(callback) { return this.setTimer(0, callback); }

    /** Cancels a timer. @returns {boolean} False when the handle was already spent. */
    clear(handle) { return this._timers.delete(handle?.id); }

    /** Cancels every timer. */
    clearAll() { this._timers.clear(); }

    pause(handle) {
        const t = this._timers.get(handle?.id);
        if (!t) return false;
        t.paused = true;
        return true;
    }

    resume(handle) {
        const t = this._timers.get(handle?.id);
        if (!t) return false;
        t.paused = false;
        return true;
    }

    isActive(handle) { return this._timers.has(handle?.id); }

    /** Seconds until the next fire, or -1 when the handle is unknown. */
    getRemaining(handle) { return this._timers.get(handle?.id)?.remaining ?? -1; }

    /** Advances every timer. Called by the engine host. */
    tick(scaledDt, unscaledDt) {
        if (this._timers.size === 0) return;

        // Snapshot: a callback routinely schedules or cancels timers.
        for (const [id, timer] of [...this._timers]) {
            if (timer.paused) continue;

            timer.remaining -= timer.useUnscaledTime ? unscaledDt : scaledDt;
            if (timer.remaining > 0) continue;

            if (timer.looping) {
                // Carry the overshoot forward so a repeating timer does not drift.
                timer.remaining += timer.interval > 0 ? timer.interval : Number.EPSILON;
            } else {
                this._timers.delete(id);
            }

            try {
                timer.callback();
            } catch (err) {
                console.error('[TimerManager] Timer callback threw:', err);
            }
        }
    }
}
