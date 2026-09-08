// -----------------------------------------------------------------------------
// PlayerPrefs — persistent, cross-session key/value store.
//
// The mirror of SexyBiscuit.Engine/Save/PlayerPrefs.cs, member for member. Native
// writes Saves/prefs.json; this writes one localStorage entry holding the same JSON
// dictionary, so a settings blob written by one engine reads back on the other.
//
// This half did not exist. html5/src/save/ was an empty directory and nothing in the
// browser engine touched localStorage at all, so a web build could not remember a
// single thing between reloads -- which a graphics menu makes immediately obvious.
//
// Every access is wrapped: a private window, cleared site data or a browser set to
// block storage all throw on the accessor itself, and losing a preference is never a
// reason to stop a game running.
// -----------------------------------------------------------------------------

/** The single localStorage entry the whole dictionary lives in. */
const STORAGE_KEY = 'sexybiscuit.prefs';

let values = null;
let dirty = false;

export class PlayerPrefs {
    /** Where the dictionary is stored. Settable, for tests. */
    static storageKey = STORAGE_KEY;

    // -------------------------------------------------------------------------
    // Writing
    // -------------------------------------------------------------------------

    static setFloat(key, value) { write(key, Number(value)); }
    static setInt(key, value) { write(key, Math.trunc(Number(value))); }
    static setString(key, value) { write(key, String(value ?? '')); }
    static setBool(key, value) { write(key, Boolean(value)); }

    // -------------------------------------------------------------------------
    // Reading
    // -------------------------------------------------------------------------

    static getFloat(key, defaultValue = 0) {
        const v = read(key);
        return typeof v === 'number' ? v : Number(v ?? defaultValue) || defaultValue;
    }

    static getInt(key, defaultValue = 0) {
        const v = read(key);
        if (v === undefined || v === null) return defaultValue;
        const n = Math.trunc(Number(v));
        return Number.isFinite(n) ? n : defaultValue;
    }

    static getString(key, defaultValue = '') {
        const v = read(key);
        return v === undefined || v === null ? defaultValue : String(v);
    }

    static getBool(key, defaultValue = false) {
        const v = read(key);
        if (typeof v === 'boolean') return v;
        if (v === undefined || v === null) return defaultValue;
        return v === 'true' || v === 1;
    }

    // -------------------------------------------------------------------------
    // Housekeeping
    // -------------------------------------------------------------------------

    static hasKey(key) { return read(key) !== undefined; }

    static deleteKey(key) {
        ensureLoaded();
        if (key in values) { delete values[key]; dirty = true; }
    }

    static deleteAll() {
        values = {};
        dirty = true;
        PlayerPrefs.save();
    }

    /** Writes the dictionary out. A no-op when nothing has changed. */
    static save() {
        if (!dirty || values === null) return;
        try {
            globalThis.localStorage?.setItem(PlayerPrefs.storageKey, JSON.stringify(values));
            dirty = false;
        } catch {
            // Storage full, or blocked. The values stay live for this session.
        }
    }

    /** Reads the dictionary in. Called automatically on the first access to any key. */
    static load() {
        try {
            const raw = globalThis.localStorage?.getItem(PlayerPrefs.storageKey);
            values = raw ? JSON.parse(raw) : {};
        } catch {
            values = {};   // Blocked, or a corrupt blob. An empty store is a first run.
        }
        dirty = false;
    }

    /** Forgets everything in memory without touching what is stored. For tests. */
    static reset() {
        values = null;
        dirty = false;
    }
}

// -----------------------------------------------------------------------------
// Internals
// -----------------------------------------------------------------------------

function ensureLoaded() {
    if (values === null) PlayerPrefs.load();
}

function read(key) {
    ensureLoaded();
    return values[key];
}

function write(key, value) {
    ensureLoaded();
    values[key] = value;
    dirty = true;
}

// The browser's only reliable "we are closing" signal. `unload` does not fire on
// mobile Safari at all, and `beforeunload` is unreliable for the same reason, so a
// setting chosen and never explicitly saved would be lost every time on a phone.
globalThis.addEventListener?.('pagehide', () => PlayerPrefs.save());
globalThis.addEventListener?.('visibilitychange', () => {
    if (globalThis.document?.visibilityState === 'hidden') PlayerPrefs.save();
});
