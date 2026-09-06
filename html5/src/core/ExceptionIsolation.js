// -----------------------------------------------------------------------------
// ExceptionIsolation — one component throwing must not take down the editor.
// -----------------------------------------------------------------------------

let _handler = null;

/**
 * With no handler installed, `tryHandle` returns false and the caller rethrows,
 * so a shipped game keeps its fail-fast behaviour. The editor installs one so a
 * throwing game component logs to the console panel and is skipped instead.
 */
export const ExceptionIsolation = {
    /**
     * @param {?(error: any, owner: object, phase: string) => boolean} handler
     *   Returns true to swallow the error, false to let it propagate.
     */
    setHandler(handler) { _handler = handler; },

    get handler() { return _handler; },

    /** True when the error was handled and the caller should continue. */
    tryHandle(error, owner, phase) {
        if (!_handler) return false;
        try {
            return _handler(error, owner, phase) === true;
        } catch {
            // A throwing handler is worse than no handler; fall back to propagating.
            return false;
        }
    },
};

/**
 * Runs `fn` with the isolation policy applied. Used by every lifecycle
 * call site so the try/catch shape lives in exactly one place.
 */
export function isolate(fn, owner, phase) {
    try {
        return fn();
    } catch (error) {
        if (!ExceptionIsolation.tryHandle(error, owner, phase)) throw error;
        return undefined;
    }
}
