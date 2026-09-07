// -----------------------------------------------------------------------------
// DarksGames — account, presence, join, party, cloud saves and achievements.
//
// One façade over the hub's two SDKs, wired to the engine's NetworkManager so a
// game gets the social layer without writing any of the glue the retrofit guide
// describes: presence follows the session, a friend's Join button joins the
// room, and a party launch opens one.
//
// Everything degrades to a signed-out no-op. A player who is offline, blocked
// from the hub, or simply not signed in has to be able to play, so nothing here
// ever throws into a game's boot path — `signedIn` is false and every call
// returns a benign value.
// -----------------------------------------------------------------------------

import { loadDarksGamesSdks, DG_ORIGIN } from './sdk.js';
import { NetworkManager } from '../net/NetworkManager.js';
import { createRoom, readRoom, roomSocketUrl, roomCodeFromUrl } from '../net/rooms.js';

/** The account as the engine reports it. Null fields mean "not signed in". */
export class DarksGamesUser {
    constructor(raw = null) {
        /** The hub's stable user id, `u_…`. This is the id every s2s call takes. */
        this.id = raw?.id ?? null;
        /** The display name the player chose. */
        this.name = raw?.name ?? raw?.displayName ?? null;
        /** `Darko#4821` — unique, and what a friend request is addressed to. */
        this.handle = raw?.handle ?? null;
        /** Entitlement keys carried on the token, for a fast `has()` with no round trip. */
        this.entitlements = raw?.ents ?? [];
    }

    get signedIn() { return this.id != null; }

    /** What to show above a player's head: their handle if they have one, else a name. */
    get displayName() { return this.name ?? this.handle ?? 'Player'; }
}

/**
 * The engine's Darks Games client.
 *
 * @example
 * const dg = new DarksGames();
 * await dg.init({ game: 'my-game', onJoin: (code) => joinRoom(code) });
 * dg.presence({ state: 'lobby', joinCode: room.code, players: 2, max: 4 });
 */
export class DarksGames {
    /** The instance a game's scripts reach through the `DG` global. */
    static instance = null;

    constructor() {
        /** The catalogue slug. Also the token audience, which the hub enforces. */
        this.game = '';
        /** @type {DarksGamesUser} */
        this.user = new DarksGamesUser();
        /** True once init has run, whether or not anybody signed in. */
        this.ready = false;
        /** True when the hub's SDKs are on the page and answering. */
        this.available = false;

        this._account = null;
        this._overlay = null;
        this._handlers = new Map();
        this._lastPresence = null;
        this._presenceTimer = null;
        this._onJoin = null;
        this._network = null;
        this._unwire = [];
    }

    get signedIn() { return this.user.signedIn; }

    // -------------------------------------------------------------------------
    // Startup
    // -------------------------------------------------------------------------

    /**
     * Loads the SDKs, signs in if the player already has a session, and wires the
     * social layer to the engine.
     *
     * @param {object} options
     * @param {string} options.game The catalogue slug. Required: the hub rejects
     *   presence for any app other than the token's audience.
     * @param {Function} [options.onJoin] `(code, join) => boolean|Promise<boolean>` —
     *   called when a friend's Join button, an accepted invite or a party room points
     *   at this game. Return true when you joined in place; false lets the overlay
     *   navigate, which re-runs the game's own deep-link path.
     * @param {NetworkManager} [options.network] The session to publish presence from.
     *   Defaults to whichever manager is running.
     * @param {string} [options.accent] Overlay accent colour.
     * @param {string|null} [options.hotkey] Overlay hotkey; `Shift+Tab` by default.
     *   Pass null when the game already binds it.
     * @param {object} [options.scope] Test seam: the object the SDKs attach to.
     */
    async init({
        game,
        onJoin = null,
        network = null,
        accent = '#7df9c6',
        hotkey = undefined,
        origin = DG_ORIGIN,
        scope = globalThis,
    } = {}) {
        if (!game) throw new Error('DarksGames.init: a catalogue slug is required.');

        this.game = game;
        this._onJoin = onJoin;
        this._network = network;
        DarksGames.instance = this;

        const { account, overlay } = await loadDarksGamesSdks({ origin, scope });
        this._account = account;
        this._overlay = overlay;
        this.available = Boolean(account);

        if (!account) {
            // Offline, blocked, or a local `file://` run. The game still starts; it just
            // has no identity and no friends list.
            this.ready = true;
            this._emit('ready', { signedIn: false, available: false });
            return this.user;
        }

        try {
            const raw = await account.init({ game });
            this.user = new DarksGamesUser(raw);
            account.on?.('user', (next) => {
                this.user = new DarksGamesUser(next);
                this._emit('user', this.user);
            });
        } catch (err) {
            console.warn(`[DarksGames] sign-in failed: ${err.message}`);
        }

        if (overlay) {
            try {
                await overlay.init({
                    game,
                    accent,
                    ...(hotkey === undefined ? {} : { hotkey }),
                    joinHandler: (join) => this._handleJoin(join),
                });
                this._wireOverlay(overlay);
            } catch (err) {
                console.warn(`[DarksGames] the overlay did not start: ${err.message}`);
                this._overlay = null;
            }
        }

        this._wireNetwork();
        this.ready = true;
        this._emit('ready', { signedIn: this.signedIn, available: this.available });
        return this.user;
    }

    /** Sends the player to the hub's sign-in page and back again. */
    signIn() { this._account?.login?.({ returnTo: globalThis.location?.href }); }

    signOut() { return this._account?.logout?.() ?? Promise.resolve(); }

    /** Opens the friends overlay, as the hotkey does. */
    openOverlay() { this._overlay?.open?.(); }

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /**
     * Subscribes to a DG event. Returns a function that unsubscribes.
     *
     * `ready`, `user`, `join`, `partyArrived`, plus every social frame the overlay
     * forwards: `presence.update`, `invite.new`, `party.update`, `party.room`,
     * `chat.message`, `activity.new`, `achievement.unlocked`.
     */
    on(event, handler) {
        if (!this._handlers.has(event)) this._handlers.set(event, new Set());
        this._handlers.get(event).add(handler);
        return () => this._handlers.get(event)?.delete(handler);
    }

    _emit(event, ...args) {
        for (const handler of this._handlers.get(event) ?? []) {
            try {
                handler(...args);
            } catch (err) {
                console.error(`[DarksGames] ${event} handler threw:`, err);
            }
        }
    }

    _wireOverlay(overlay) {
        const forwarded = [
            'presence.update', 'friend.request', 'friend.accepted', 'invite.new',
            'party.update', 'party.room', 'party.disband', 'chat.message',
            'activity.new', 'achievement.unlocked', 'notice', 'error',
        ];
        for (const name of forwarded) {
            const off = overlay.on?.(name, (data) => this._emit(name, data));
            if (off) this._unwire.push(off);
        }

        // A party launch: the host opens a room and reports it, and every member's
        // joinHandler then fires with the code. Doing it here means a game gets Party
        // Launch by having a room API, not by implementing the state machine.
        const offArrived = overlay.on?.('party.arrived', (arrival) => {
            this._emit('partyArrived', arrival);
            if (arrival?.isHost && !arrival?.room) this._hostPartyRoom();
        });
        if (offArrived) this._unwire.push(offArrived);
    }

    async _hostPartyRoom() {
        try {
            const room = await createRoom({ mode: 'party' });
            await this._overlay?.party?.setRoom?.({ joinCode: room.code });
            this._emit('partyRoom', room);
        } catch (err) {
            this._overlay?.toast?.({ title: 'Party room failed', body: String(err.message ?? err) });
        }
    }

    async _handleJoin(join) {
        // The overlay only offers an in-place join for a URL on this origin, but be
        // defensive: a handler is the one place a wrong code becomes a wrong game.
        const code = String(join?.joinCode ?? roomCodeFromUrl(join?.joinUrl ?? '') ?? '').toUpperCase();
        if (!code) return false;

        this._emit('join', code, join);
        if (!this._onJoin) return false;

        try {
            return (await this._onJoin(code, join)) === true;
        } catch (err) {
            console.warn(`[DarksGames] the join handler threw: ${err.message}`);
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Presence
    // -------------------------------------------------------------------------

    /**
     * Publishes what the player is doing, so friends see it and can join.
     *
     * @param {object} fields
     * @param {string} [fields.state] The coarse phase — `menu`, `lobby`, `wave 7`.
     * @param {string} [fields.detail] Free text, up to 80 characters.
     * @param {string} [fields.joinCode] The room code. Its presence is what puts a
     *   Join button on the player's row in every friend's overlay.
     * @param {boolean} [fields.joinable] False shows "in a room" with no Join button.
     * @param {number} [fields.players] / {number} [fields.max] Seats, for the row's count.
     */
    presence({ state = 'playing', detail = '', joinCode = null, joinable = true, players, max } = {}) {
        if (!this._overlay?.presence) return;

        const next = { state: String(state).slice(0, 40), detail: String(detail).slice(0, 80) };
        if (joinCode) {
            // Codes are compared case-sensitively after extraction, and a path-shaped
            // join URL is upper-cased by the hub. Publishing lower case gives a Join
            // button that resolves to a room nobody is in.
            next.join = { joinCode: String(joinCode).toUpperCase(), joinable };
            if (Number.isFinite(players)) next.join.players = players;
            if (Number.isFinite(max)) next.join.max = max;
        } else {
            next.join = null;
        }

        // The SDK throttles to one frame every two seconds; skipping an identical
        // update means an idle lobby publishes nothing at all rather than the same
        // line forever.
        const encoded = JSON.stringify(next);
        if (encoded === this._lastPresence) return;
        this._lastPresence = encoded;

        try { this._overlay.presence.set(next); } catch { /* the overlay is gone */ }
    }

    /** Clears presence — leaving a room, or returning to a menu. */
    clearPresence() {
        this._lastPresence = null;
        try { this._overlay?.presence?.clear?.(); } catch { /* gone */ }
    }

    /**
     * Publishes presence from the running session, and keeps doing it as it changes.
     *
     * This is the `publishPresence()` pattern the retrofit guide describes, done once
     * in the engine: a game that starts a session gets a joinable friends-list row
     * without writing any of it, and a game that wants different words calls
     * `presence()` itself instead.
     */
    _wireNetwork() {
        const network = () => this._network ?? NetworkManager.instance;

        const publish = () => {
            const manager = network();
            if (!manager?.isRunning) { this.clearPresence(); return; }

            this.presence({
                state: manager.room ? 'in a room' : 'playing',
                detail: manager.room ? `Room ${manager.room}` : '',
                joinCode: manager.room || null,
                players: manager.players.size,
            });
        };

        for (const event of ['connected', 'disconnected', 'playerJoined', 'playerLeft']) {
            const manager = network();
            const off = manager?.on?.(event, publish);
            if (off) this._unwire.push(off);
        }
        publish();
    }

    /** Re-attaches presence to a session started after `init`. */
    watch(manager) {
        this._network = manager;
        this._wireNetwork();
    }

    // -------------------------------------------------------------------------
    // Cloud saves
    // -------------------------------------------------------------------------

    /**
     * Asks for the cloud save; it arrives on the `save` event.
     *
     * The event rather than the returned promise is what a game script uses, because
     * the Jint bridge cannot await and one contract has to describe both engines.
     */
    requestSave() {
        this.loadSave().then((save) => this._emit('save', save?.data ?? null));
    }

    /**
     * The player's cloud save, or null when there is none — and null, not an error,
     * when they are signed out.
     */
    async loadSave() {
        if (!this._account?.saves) return null;
        try { return await this._account.saves.get(); } catch { return null; }
    }

    /**
     * Writes the cloud save.
     *
     * @param {*} data Anything JSON can express.
     * @param {object} [options]
     * @param {number} [options.version] The game's own save version.
     * @param {string} [options.baseUpdatedAt] The timestamp this edit was based on.
     *   Passing it turns a lost update into a rejected write the game can resolve,
     *   rather than one device silently overwriting the other.
     * @returns {Promise<object|null>} Null when signed out, and on a conflict, which
     *   arrives as a `saveConflict` event carrying the server's copy.
     */
    async writeSave(data, { version = 1, baseUpdatedAt } = {}) {
        if (!this._account?.saves) return null;
        try {
            return await this._account.saves.put(data, { version, baseUpdatedAt });
        } catch (err) {
            if (err?.name === 'DGConflict') { this._emit('saveConflict', err.server); return null; }
            console.warn(`[DarksGames] the save was not written: ${err.message}`);
            return null;
        }
    }

    /**
     * Local-first sync: pushes when only the local copy moved, pulls when only the
     * cloud did, and asks `onConflict` when both did.
     */
    async syncSave({ read, write, onConflict, version = 1 } = {}) {
        if (!this._account?.saves?.sync) return { status: 'unavailable' };
        try {
            return await this._account.saves.sync({ read, write, onConflict, version });
        } catch (err) {
            return { status: 'failed', error: err.message };
        }
    }

    // -------------------------------------------------------------------------
    // Achievements, friends, invites, party
    // -------------------------------------------------------------------------

    /**
     * Reports an achievement from the client.
     *
     * Only accepted for games the catalogue marks `clientAchievements: true` — a game
     * with a server unlocks over s2s instead, because a self-reported unlock is a
     * claim, not a fact. Never hang an entitlement or a ranking off one.
     */
    async reportAchievement(key, increment) {
        if (!this._overlay?.achievements) return false;
        try {
            await this._overlay.achievements.report(String(key), increment);
            return true;
        } catch (err) {
            console.warn(`[DarksGames] achievement '${key}' was not reported: ${err.message}`);
            return false;
        }
    }

    /** The player's friends, or an empty list when signed out. */
    async friends() {
        try { return (await this._overlay?.friends?.list?.()) ?? []; } catch { return []; }
    }

    /** The friends who are in this game right now. */
    async friendsInGame() {
        try { return (await this._overlay?.friends?.inThisGame?.()) ?? []; } catch { return []; }
    }

    /** Invites one friend into the current room. */
    async invite(userId, joinCode) {
        if (!this._overlay?.invites) return false;
        try {
            await this._overlay.invites.send({ userId, joinCode: String(joinCode).toUpperCase() });
            return true;
        } catch (err) {
            console.warn(`[DarksGames] the invite was not sent: ${err.message}`);
            return false;
        }
    }

    /** The player's party, or null. */
    async party() {
        try { return (await this._overlay?.party?.get?.()) ?? null; } catch { return null; }
    }

    /** Tells the party which room the host opened, so every member joins it. */
    async setPartyRoom(joinCode) {
        try {
            await this._overlay?.party?.setRoom?.({ joinCode: String(joinCode).toUpperCase() });
            return true;
        } catch { return false; }
    }

    // -------------------------------------------------------------------------
    // Rooms
    // -------------------------------------------------------------------------

    /** Opens a room and publishes it as joinable presence in one step. */
    async host({ mode = 'default', baseUrl = '' } = {}) {
        const room = await createRoom({ mode, baseUrl });
        this.presence({ state: 'lobby', joinCode: room.code, players: 1 });
        return room;
    }

    /** What a room looks like right now, or null when it has ended. */
    room(code, options) { return readRoom(code, options); }

    /** The socket URL for a room, so a game can connect without composing one. */
    socketUrl(code, options) { return roomSocketUrl(code, options); }

    /** The room code in the page's URL, when the player arrived through a join link. */
    joinCodeFromUrl(url) { return roomCodeFromUrl(url); }

    // -------------------------------------------------------------------------

    dispose() {
        for (const off of this._unwire) {
            try { off(); } catch { /* already gone */ }
        }
        this._unwire = [];
        this._handlers.clear();
        if (DarksGames.instance === this) DarksGames.instance = null;
    }
}
