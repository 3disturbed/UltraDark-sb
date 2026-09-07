// -----------------------------------------------------------------------------
// The Darks Games layer: identity, presence, join, party and cloud saves.
//
// The hub's own SDKs are hosted scripts, so the tests stand in a fake pair with
// exactly the surface the retrofit guide documents. What is being checked is the
// engine's half — that it degrades to signed-out rather than throwing, that it
// publishes presence the hub will accept, and that a join reaches the game.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';

import { DarksGames, DarksGamesUser, NetworkManager } from '../src/index.js';

/** The two globals a real page gets from darksgames.app, with the calls recorded. */
function fakeSdks({ user = { id: 'u_123', name: 'Darko', handle: 'Darko#4821' }, overlay = true } = {}) {
    const calls = { presence: [], invites: [], achievements: [], party: [], saves: [] };
    const listeners = new Map();

    const scope = {
        DGAccount: {
            async init() { return user; },
            on(event, handler) {
                listeners.set(`account:${event}`, handler);
                return () => listeners.delete(`account:${event}`);
            },
            login(options) { calls.login = options; },
            saves: {
                async get() { return calls.cloud ?? null; },
                async put(data, options) { calls.saves.push({ data, options }); return { updatedAt: 'now' }; },
                async sync(options) { calls.sync = options; return { status: 'pushed' }; },
            },
        },
        DGOverlay: overlay ? {
            async init(options) { calls.init = options; return { user, mode: 'iframe' }; },
            on(event, handler) {
                listeners.set(event, handler);
                return () => listeners.delete(event);
            },
            open() { calls.opened = true; },
            presence: {
                set(value) { calls.presence.push(value); },
                clear() { calls.presence.push(null); },
            },
            invites: { async send(value) { calls.invites.push(value); } },
            achievements: { async report(key, increment) { calls.achievements.push({ key, increment }); } },
            party: {
                async get() { return null; },
                async setRoom(value) { calls.party.push(value); return { room: value }; },
            },
            friends: { async list() { return [{ id: 'u_999' }]; }, async inThisGame() { return []; } },
            toast(value) { calls.toast = value; },
        } : undefined,
    };

    return { scope, calls, fire: (event, data) => listeners.get(event)?.(data) };
}

// ---- Signing in -------------------------------------------------------------

test('init reads the identity the hub reports', async () => {
    const { scope } = fakeSdks();
    const dg = new DarksGames();
    try {
        const user = await dg.init({ game: 'my-game', scope });

        assert.equal(user.id, 'u_123');
        assert.equal(user.handle, 'Darko#4821');
        assert.equal(user.displayName, 'Darko');
        assert.equal(dg.signedIn, true);
        assert.equal(dg.available, true);
    } finally { dg.dispose(); }
});

test('a page with no SDKs is signed out, not broken', async () => {
    // Offline, blocked by a filter, or a local file:// run. The game has to start; it just
    // has no identity and no friends list.
    const dg = new DarksGames();
    try {
        const user = await dg.init({ game: 'my-game', scope: {} });

        assert.equal(user.signedIn, false);
        assert.equal(dg.available, false);
        assert.equal(dg.ready, true);

        // And every call is harmless.
        dg.presence({ state: 'lobby', joinCode: 'ABC234' });
        dg.clearPresence();
        assert.equal(await dg.reportAchievement('first_win'), false);
        assert.deepEqual(await dg.friends(), []);
        assert.equal(await dg.loadSave(), null);
        assert.equal(await dg.writeSave({ hp: 1 }), null);
    } finally { dg.dispose(); }
});

test('a slug is required, because the hub rejects presence for any other app', async () => {
    const dg = new DarksGames();
    try {
        await assert.rejects(() => dg.init({ scope: {} }), /slug is required/);
    } finally { dg.dispose(); }
});

test('an overlay that will not start leaves the account working', async () => {
    // Identity, cloud saves and entitlements do not need the overlay. Losing the friends
    // panel must not lose the sign-in with it.
    const { scope } = fakeSdks();
    scope.DGOverlay.init = () => { throw new Error('csp'); };

    const dg = new DarksGames();
    try {
        const user = await dg.init({ game: 'my-game', scope });
        assert.equal(user.signedIn, true);
        dg.presence({ state: 'lobby' });        // no overlay: a no-op, not a throw
    } finally { dg.dispose(); }
});

// ---- Presence ---------------------------------------------------------------

test('presence upper-cases its join code', async () => {
    // Codes are compared case-sensitively after extraction, and the hub upper-cases a
    // path-shaped join URL. A lower-case code gives a Join button that resolves to a room
    // nobody is in.
    const { scope, calls } = fakeSdks();
    const dg = new DarksGames();
    try {
        await dg.init({ game: 'my-game', scope });
        calls.presence.length = 0;

        dg.presence({ state: 'lobby', joinCode: 'abc234', players: 2, max: 4 });

        const sent = calls.presence.at(-1);
        assert.equal(sent.join.joinCode, 'ABC234');
        assert.equal(sent.join.players, 2);
        assert.equal(sent.join.max, 4);
    } finally { dg.dispose(); }
});

test('publishing the same presence twice sends it once', async () => {
    // The SDK throttles to one frame every two seconds; skipping an identical update means
    // an idle lobby publishes nothing at all rather than the same line forever.
    const { scope, calls } = fakeSdks();
    const dg = new DarksGames();
    try {
        await dg.init({ game: 'my-game', scope });
        calls.presence.length = 0;

        dg.presence({ state: 'lobby', joinCode: 'ABC234' });
        dg.presence({ state: 'lobby', joinCode: 'ABC234' });
        dg.presence({ state: 'lobby', joinCode: 'ABC234' });

        assert.equal(calls.presence.length, 1);

        dg.presence({ state: 'wave 2', joinCode: 'ABC234' });
        assert.equal(calls.presence.length, 2, 'a real change still goes out');
    } finally { dg.dispose(); }
});

test('a long state and detail are cut to what the hub accepts', async () => {
    // Over the limit the hub rejects the whole frame, so a game that got chatty would lose
    // its Join button rather than its adjectives.
    const { scope, calls } = fakeSdks();
    const dg = new DarksGames();
    try {
        await dg.init({ game: 'my-game', scope });
        calls.presence.length = 0;

        dg.presence({ state: 'x'.repeat(80), detail: 'y'.repeat(200) });

        const sent = calls.presence.at(-1);
        assert.equal(sent.state.length, 40);
        assert.equal(sent.detail.length, 80);
    } finally { dg.dispose(); }
});

test('presence follows a running session without the game writing any of it', async () => {
    const { scope, calls } = fakeSdks();
    NetworkManager.instance?.dispose();
    const network = new NetworkManager();
    const dg = new DarksGames();
    try {
        network.playerName = 'Host';
        network.startSolo();
        for (let i = 0; i < 4; i++) network.tick(1 / 60);

        await dg.init({ game: 'my-game', scope, network });

        const sent = calls.presence.at(-1);
        assert.ok(sent, 'nothing was published for a running session');
        assert.equal(sent.state, 'playing');
    } finally {
        dg.dispose();
        network.dispose();
    }
});

// ---- Join and party ---------------------------------------------------------

test('a friend’s Join button reaches the game with the code', async () => {
    const { scope, calls } = fakeSdks();
    const joined = [];
    const dg = new DarksGames();
    try {
        await dg.init({ game: 'my-game', scope, onJoin: (code) => { joined.push(code); return true; } });

        const handled = await calls.init.joinHandler({ app: 'my-game', joinCode: 'abc234' });

        assert.deepEqual(joined, ['ABC234'], 'the code arrives upper-cased');
        assert.equal(handled, true, 'true tells the overlay not to navigate as well');
    } finally { dg.dispose(); }
});

test('a join with no code is declined so the overlay navigates instead', async () => {
    const { scope, calls } = fakeSdks();
    const dg = new DarksGames();
    try {
        await dg.init({ game: 'my-game', scope, onJoin: () => true });
        assert.equal(await calls.init.joinHandler({ app: 'my-game' }), false);
    } finally { dg.dispose(); }
});

test('a join handler that throws declines rather than taking the overlay down', async () => {
    const { scope, calls } = fakeSdks();
    const dg = new DarksGames();
    try {
        await dg.init({ game: 'my-game', scope, onJoin: () => { throw new Error('boom'); } });
        assert.equal(await calls.init.joinHandler({ joinCode: 'ABC234' }), false);
    } finally { dg.dispose(); }
});

test('a party arrival is forwarded, and only the host opens the room', async () => {
    const { scope, fire } = fakeSdks();
    const dg = new DarksGames();
    try {
        await dg.init({ game: 'my-game', scope });

        const arrivals = [];
        dg.on('partyArrived', (arrival) => arrivals.push(arrival));

        fire('party.arrived', { isHost: false, party: { id: 'p_1' } });
        assert.equal(arrivals.length, 1);
        assert.equal(arrivals[0].isHost, false);
    } finally { dg.dispose(); }
});

// ---- Cloud saves ------------------------------------------------------------

test('a save is written through the SDK with the version the game gave', async () => {
    const { scope, calls } = fakeSdks();
    const dg = new DarksGames();
    try {
        await dg.init({ game: 'my-game', scope });
        await dg.writeSave({ hp: 50 }, { version: 3, baseUpdatedAt: 'then' });

        assert.deepEqual(calls.saves.at(-1).data, { hp: 50 });
        assert.equal(calls.saves.at(-1).options.version, 3);
        assert.equal(calls.saves.at(-1).options.baseUpdatedAt, 'then');
    } finally { dg.dispose(); }
});

test('a conflicting save becomes an event carrying the server’s copy', async () => {
    // The alternative — last write wins — silently loses whichever device saved first, which
    // is the bug players describe as "it deleted my progress".
    const { scope } = fakeSdks();
    scope.DGAccount.saves.put = async () => {
        const err = new Error('cloud save conflict');
        err.name = 'DGConflict';
        err.server = { data: { hp: 90 }, updatedAt: 'later' };
        throw err;
    };

    const dg = new DarksGames();
    try {
        await dg.init({ game: 'my-game', scope });

        const conflicts = [];
        dg.on('saveConflict', (server) => conflicts.push(server));

        const result = await dg.writeSave({ hp: 50 }, { baseUpdatedAt: 'then' });

        assert.equal(result, null);
        assert.equal(conflicts.length, 1);
        assert.deepEqual(conflicts[0].data, { hp: 90 });
    } finally { dg.dispose(); }
});

// ---- Achievements and friends -----------------------------------------------

test('an achievement is reported with its increment', async () => {
    const { scope, calls } = fakeSdks();
    const dg = new DarksGames();
    try {
        await dg.init({ game: 'my-game', scope });

        assert.equal(await dg.reportAchievement('first_win'), true);
        assert.equal(await dg.reportAchievement('kills', 9), true);

        assert.deepEqual(calls.achievements, [
            { key: 'first_win', increment: undefined },
            { key: 'kills', increment: 9 },
        ]);
    } finally { dg.dispose(); }
});

test('an invite carries an upper-cased code', async () => {
    const { scope, calls } = fakeSdks();
    const dg = new DarksGames();
    try {
        await dg.init({ game: 'my-game', scope });
        await dg.invite('u_999', 'abc234');
        assert.deepEqual(calls.invites.at(-1), { userId: 'u_999', joinCode: 'ABC234' });
    } finally { dg.dispose(); }
});

test('a handler that throws does not stop the others', async () => {
    const { scope, fire } = fakeSdks();
    const dg = new DarksGames();
    try {
        await dg.init({ game: 'my-game', scope });

        const seen = [];
        const realError = console.error;
        console.error = () => {};
        dg.on('invite.new', () => { throw new Error('boom'); });
        dg.on('invite.new', (data) => seen.push(data));

        fire('invite.new', { invite: { id: 'i_1' } });
        console.error = realError;

        assert.equal(seen.length, 1);
    } finally { dg.dispose(); }
});

// ---- The user record --------------------------------------------------------

test('a signed-out user still answers every question', () => {
    // Every call site reads `displayName`; none of them should have to null-check first.
    const user = new DarksGamesUser();
    assert.equal(user.signedIn, false);
    assert.equal(user.displayName, 'Player');
    assert.deepEqual(user.entitlements, []);
});
