// -----------------------------------------------------------------------------
// Coroutines — C# IEnumerator routines become JavaScript generators.
//
//     actor.startCoroutine(function* () {
//         yield new WaitForSeconds(1);
//         doThing();
//         yield null;               // resume next frame
//     }());
// -----------------------------------------------------------------------------

/** Base for anything a coroutine can wait on. */
export class YieldInstruction {
    /** @returns {boolean} True while the coroutine should stay suspended. */
    keepWaiting(scaledDt, unscaledDt) { return false; }
}

/** Waits a number of seconds of scaled time — it stretches when time slows. */
export class WaitForSeconds extends YieldInstruction {
    constructor(seconds) { super(); this.remaining = seconds; }
    keepWaiting(scaledDt) { this.remaining -= scaledDt; return this.remaining > 0; }
}

/** Waits a number of seconds of wall-clock time, ignoring time scale. */
export class WaitForSecondsRealtime extends YieldInstruction {
    constructor(seconds) { super(); this.remaining = seconds; }
    keepWaiting(_scaledDt, unscaledDt) { this.remaining -= unscaledDt; return this.remaining > 0; }
}

/** Waits until a predicate returns true. */
export class WaitUntil extends YieldInstruction {
    constructor(predicate) { super(); this.predicate = predicate; }
    keepWaiting() { return !this.predicate(); }
}

/** Waits while a predicate returns true. */
export class WaitWhile extends YieldInstruction {
    constructor(predicate) { super(); this.predicate = predicate; }
    keepWaiting() { return this.predicate(); }
}

/** Waits for the end of the current frame. */
export class WaitForEndOfFrame extends YieldInstruction {
    keepWaiting() { return false; }
}

/** A handle on a running coroutine. Yield one to wait for it to finish. */
export class Coroutine {
    constructor(iterator, owner) {
        this.iterator = iterator;
        this.owner = owner;
        this.isRunning = true;
        this.waiting = null;      // a YieldInstruction
        this.waitingOn = null;    // another Coroutine
    }
}

/** Drives every running coroutine. One instance lives on the engine host. */
export class CoroutineRunner {
    /** The runner the engine host installs. Actors reach coroutines through it. */
    static instance = new CoroutineRunner();

    constructor() {
        this._running = [];
        this._pending = [];
    }

    /** How many coroutines are live, queued ones included. */
    get activeCount() { return this._running.length + this._pending.length; }

    /**
     * Starts a coroutine. Its first step runs on the next tick, not immediately,
     * which matches the C# runner and keeps start-up ordering predictable.
     *
     * @param {Iterator|Generator} routine A generator object.
     * @param {object} [owner] Usually the actor; used by `stopAllFor`.
     */
    start(routine, owner = null) {
        if (!routine || typeof routine.next !== 'function') {
            console.error('[CoroutineRunner] startCoroutine needs a generator object.');
            return null;
        }
        const coroutine = new Coroutine(routine, owner);
        this._pending.push(coroutine);
        return coroutine;
    }

    /** Cancels one coroutine. Safe to call on null or on one already finished. */
    stop(coroutine) {
        if (coroutine) coroutine.isRunning = false;
    }

    /** Cancels every coroutine started by an owner. */
    stopAllFor(owner) {
        for (const c of this._running) if (c.owner === owner) c.isRunning = false;
        for (const c of this._pending) if (c.owner === owner) c.isRunning = false;
    }

    /** Cancels everything. */
    stopAll() {
        for (const c of this._running) c.isRunning = false;
        for (const c of this._pending) c.isRunning = false;
    }

    /** Advances every coroutine by one frame. Called by the engine host. */
    tick(scaledDt, unscaledDt) {
        if (this._pending.length > 0) {
            this._running.push(...this._pending);
            this._pending.length = 0;
        }

        for (const coroutine of this._running) {
            if (!coroutine.isRunning) continue;

            // Waiting on another coroutine takes priority: it was yielded last.
            if (coroutine.waitingOn) {
                if (coroutine.waitingOn.isRunning) continue;
                coroutine.waitingOn = null;
            }

            if (coroutine.waiting) {
                if (coroutine.waiting.keepWaiting(scaledDt, unscaledDt)) continue;
                coroutine.waiting = null;
            }

            let step;
            try {
                step = coroutine.iterator.next();
            } catch (err) {
                console.error('[CoroutineRunner] Coroutine threw and was stopped:', err);
                coroutine.isRunning = false;
                continue;
            }

            if (step.done) { coroutine.isRunning = false; continue; }

            const yielded = step.value;
            if (yielded instanceof YieldInstruction) coroutine.waiting = yielded;
            else if (yielded instanceof Coroutine) coroutine.waitingOn = yielded;
            // Anything else, null included, resumes on the next frame.
        }

        if (this._running.some((c) => !c.isRunning)) {
            this._running = this._running.filter((c) => c.isRunning);
        }
    }
}
