// -----------------------------------------------------------------------------
// SBEvent — the engine's multicast event, mirroring Core/SBEvent.cs.
//
// `broadcast` walks a cached snapshot of the handler list, so a handler that
// unsubscribes mid-broadcast still receives the current call and one that
// subscribes does not. Each handler is called inside its own try/catch: one
// listener throwing must not stop the rest.
// -----------------------------------------------------------------------------

/** A list of callbacks that can be broadcast to. Payload type is free-form. */
export class SBEvent {
    constructor() {
        this._handlers = [];
        this._snapshot = null;
    }

    /** How many handlers are subscribed. The same handler may be added twice. */
    get count() { return this._handlers.length; }

    /** Subscribes a handler. Returns this, so calls chain. */
    add(handler) {
        if (typeof handler !== 'function') return this;
        this._handlers.push(handler);
        this._snapshot = null;
        return this;
    }

    /** Removes the first occurrence of a handler. Returns true when one went. */
    remove(handler) {
        const i = this._handlers.indexOf(handler);
        if (i < 0) return false;
        this._handlers.splice(i, 1);
        this._snapshot = null;
        return true;
    }

    /** Drops every handler. */
    clear() {
        this._handlers.length = 0;
        this._snapshot = null;
    }

    /**
     * Calls every subscribed handler with `payload`.
     * A handler that throws is reported and skipped; the rest still run.
     */
    broadcast(payload) {
        this._snapshot ??= this._handlers.slice();
        for (const handler of this._snapshot) {
            try {
                handler(payload);
            } catch (err) {
                console.error('[SBEvent] Handler threw:', err);
            }
        }
    }
}
