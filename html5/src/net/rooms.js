// -----------------------------------------------------------------------------
// Rooms — the link-to-join layer.
//
// A browser cannot listen for connections, so "hosting" on the web means asking
// a room server for a code and being the first to connect to it. The shape is
// the one every Darks Games multiplayer title already uses, so a game built here
// drops onto the same infrastructure and the DG overlay's Join button works
// without any translation:
//
//   POST /api/rooms        -> { code, joinUrl, mode }
//   GET  /api/rooms/:code  -> { code, players, max, phase, joinable } | 404
//
// tools/roomserver.js implements exactly that and is what `npm run serve` runs.
// -----------------------------------------------------------------------------

/**
 * Codes get read aloud, typed from memory and pasted into chat, so the alphabet
 * omits every character pair that gets confused: no 0 or O, no 1, I or L.
 */
export const ROOM_CODE_ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';

/** How long a room code is. Six is 887 million codes: enough that guessing one is not a route in. */
export const ROOM_CODE_LENGTH = 6;

/** True when a string is shaped like a room code. Case-insensitive; codes are upper-case. */
export function isRoomCode(value) {
    const code = String(value ?? '').toUpperCase();
    if (code.length !== ROOM_CODE_LENGTH) return false;
    for (const character of code) if (!ROOM_CODE_ALPHABET.includes(character)) return false;
    return true;
}

/** Generates a room code. Used by the room server; a client only ever reads one. */
export function generateRoomCode(random = Math.random) {
    let code = '';
    for (let i = 0; i < ROOM_CODE_LENGTH; i++) {
        code += ROOM_CODE_ALPHABET[Math.floor(random() * ROOM_CODE_ALPHABET.length)];
    }
    return code;
}

/**
 * The room code in a URL, whichever shape the game uses.
 *
 * `/j/CODE` is the Darks Games convention and what the social catalogue's
 * `joinUrlPattern` produces; `?room=` and `#CODE` are accepted because a game
 * that already used one should not have to change its links to get a Join button.
 */
export function roomCodeFromUrl(url = typeof location !== 'undefined' ? location.href : '') {
    let parsed;
    try {
        parsed = new URL(url, typeof location !== 'undefined' ? location.href : 'https://localhost/');
    } catch {
        return null;
    }

    const path = parsed.pathname.match(/^\/j\/([A-Za-z0-9]{4,8})\/?$/);
    if (path) return path[1].toUpperCase();

    const query = parsed.searchParams.get('room') ?? parsed.searchParams.get('join');
    if (query) return query.toUpperCase();

    const hash = parsed.hash.replace(/^#/, '');
    if (hash && /^[A-Za-z0-9]{4,8}$/.test(hash)) return hash.toUpperCase();

    return null;
}

/**
 * Asks the room server to open a room.
 *
 * @param {object} [options]
 * @param {string} [options.baseUrl] Where the room API lives. Same origin by default.
 * @param {string} [options.mode] Passed through to the server; games use it for game modes.
 * @param {Function} [options.fetchImpl] Test seam.
 * @returns {Promise<{code: string, joinUrl: string, mode: string}>}
 */
export async function createRoom({ baseUrl = '', mode = 'default', fetchImpl } = {}) {
    const request = fetchImpl ?? fetch;
    const response = await request(`${baseUrl}/api/rooms`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ mode }),
    });

    if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error(body.error ?? `create_failed_${response.status}`);
    }
    return response.json();
}

/**
 * What a room looks like right now, or null when it is gone.
 *
 * Worth calling before opening a socket: "that room has ended" is a far better
 * thing to show than a connection that opens and dies a second later.
 */
export async function readRoom(code, { baseUrl = '', fetchImpl } = {}) {
    const request = fetchImpl ?? fetch;
    const response = await request(`${baseUrl}/api/rooms/${encodeURIComponent(code)}`);
    if (!response.ok) return null;
    return response.json();
}

/** The socket URL for a room on a given origin. */
export function roomSocketUrl(code, { baseUrl = '' } = {}) {
    const origin = baseUrl || (typeof location !== 'undefined' ? location.origin : '');
    const url = new URL('/ws', origin || 'http://localhost');
    url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
    url.searchParams.set('room', code);
    return url.toString();
}
