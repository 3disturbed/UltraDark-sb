// -----------------------------------------------------------------------------
// Networking — the wire, a session, and replication. Mirrors
// SexyBiscuit.Tests/NetworkingTests.cs case for case, and reads the C# source
// directly where the two have to agree byte for byte.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import {
    Scene, Actor, Component, Vector2, registerComponent,
    NetworkManager, NetworkObject, NetTransportKind,
    NetMessage, NET_PROTOCOL_VERSION, NetWriter, NetReader, NetProtocolError,
    isRoomCode, generateRoomCode, roomCodeFromUrl, roomSocketUrl,
} from '../src/index.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const csharp = (...parts) => fs.readFileSync(path.join(here, '../../SexyBiscuit.Engine', ...parts), 'utf8');

/** Starts a session and returns it plus a pump, disposing whatever a test left running. */
function session(configure = () => {}) {
    NetworkManager.instance?.dispose();
    const manager = new NetworkManager();
    configure(manager);
    manager.startSolo();
    const pump = (frames = 4) => { for (let i = 0; i < frames; i++) manager.tick(1 / 60); };
    pump();
    return { manager, pump };
}

// ---- The wire ---------------------------------------------------------------

test('every field survives a write and a read', () => {
    const frame = new NetWriter(NetMessage.Spawn)
        .uint(7).string('Player').int(-1).bytes(new Uint8Array([1, 2, 3]))
        .bool(true).float(1.5).double(2.25).toBytes();

    const reader = new NetReader(frame);
    assert.equal(reader.byte(), NetMessage.Spawn);
    assert.equal(reader.uint(), 7);
    assert.equal(reader.string(), 'Player');
    assert.equal(reader.int(), -1);
    assert.deepEqual([...reader.bytes()], [1, 2, 3]);
    assert.equal(reader.bool(), true);
    assert.equal(reader.float(), 1.5);
    assert.equal(reader.double(), 2.25);
    assert.equal(reader.remaining, 0);
});

test('numbers go out little-endian, not DataView’s big-endian default', () => {
    // DataView defaults to big-endian; the C# side is little-endian on every machine the
    // engine runs on. Every call here passes `true` explicitly, and this is why.
    assert.deepEqual([...new NetWriter().uint(0x01020304).toBytes()], [0x04, 0x03, 0x02, 0x01]);
});

test('a string is prefixed with its byte length, not its character count', () => {
    // A four-byte emoji is one JavaScript string of length two. Prefixing anything but the
    // byte count desynchronises the whole frame after it.
    assert.deepEqual([...new NetWriter().string('é').toBytes()], [2, 0, 0xC3, 0xA9]);
});

test('a truncated frame throws rather than reading rubbish', () => {
    // These bytes come off a socket. A short read has to be a caught error, not an
    // undefined that poisons the game state ten calls later.
    assert.throws(() => new NetReader(new Uint8Array([1, 2])).int(), NetProtocolError);
});

// ---- Sessions ---------------------------------------------------------------

test('a solo session is a full session with no socket', () => {
    // A multiplayer game played alone must not be a different game: solo goes through the
    // same handshake, so "works alone, breaks in a lobby" cannot happen.
    const { manager } = session((m) => { m.playerName = 'Solo'; });
    try {
        assert.equal(manager.isServer, true);
        assert.equal(manager.isClient, true);
        assert.equal(manager.isHost, true);
        assert.equal(manager.isConnected, true);
        assert.equal(manager.transportKind, NetTransportKind.Loopback);
        assert.equal(manager.players.get(0), 'Solo');
    } finally { manager.dispose(); }
});

test('a client is given an id and the roster it joined', () => {
    const { manager } = session((m) => { m.playerName = 'Host'; });
    try {
        assert.ok([...manager.players.values()].includes('Host'));
        assert.equal(manager.connectedClientIds.length, 1);
    } finally { manager.dispose(); }
});

test('a message reaches the other end with the sender the server assigned', () => {
    const { manager, pump } = session();
    try {
        const received = [];
        manager.on('message', (sender, type, payload) => received.push({ sender, type, payload }));

        manager.sendMessageToAll('hit', { hp: 42 });
        pump();

        assert.equal(received.length, 1);
        assert.equal(received[0].type, 'hit');
        assert.equal(received[0].payload.hp, 42);
    } finally { manager.dispose(); }
});

test('a message with no payload still arrives', () => {
    // The templates send bare signals — "startGame", "ready" — with nothing attached.
    const { manager, pump } = session();
    try {
        let seen = null;
        manager.on('message', (_, type, payload) => { seen = type; assert.equal(payload, null); });

        manager.sendMessageToAll('startGame');
        pump();

        assert.equal(seen, 'startGame');
    } finally { manager.dispose(); }
});

test('starting twice is refused rather than leaking the first session', () => {
    const { manager } = session();
    try {
        assert.throws(() => manager.startSolo(), /already running/);
    } finally { manager.dispose(); }
});

test('disconnecting clears everything so the manager can be reused', () => {
    const { manager, pump } = session();
    try {
        manager.disconnect();
        assert.equal(manager.isRunning, false);
        assert.equal(manager.isServer, false);
        assert.equal(manager.players.size, 0);

        // And it starts again cleanly — returning to a menu and hosting a second match is
        // the most ordinary thing a player does.
        manager.startSolo();
        pump();
        assert.equal(manager.isConnected, true);
    } finally { manager.dispose(); }
});

test('a handler that throws does not stop the others or the frame', () => {
    const { manager, pump } = session();
    try {
        const seen = [];
        const errors = [];
        const realError = console.error;
        console.error = (...args) => errors.push(args.join(' '));

        manager.on('message', () => { throw new Error('boom'); });
        manager.on('message', (_, type) => seen.push(type));

        manager.sendMessageToAll('ping');
        pump();
        console.error = realError;

        assert.deepEqual(seen, ['ping']);
        assert.ok(errors.some((e) => e.includes('boom')));
    } finally { manager.dispose(); }
});

test('a refused handshake is reported once, however the socket spells it', () => {
    // The room server answers 404 for a room that has ended, so the socket never opens.
    // Node raises `error` and then no `close` at all — the socket sits in CONNECTING for
    // good — while a browser raises both, in either order. A player who followed a dead
    // link has to be told in all three cases, and told exactly once: a second disconnect
    // arrives after they are already back in the menu, and tears down whatever they
    // started there.
    NetworkManager.instance?.dispose();

    for (const spelling of ['error alone', 'error then close', 'close then error']) {
        const sockets = [];
        const manager = NetworkManager._detached();
        const reasons = [];
        manager.on('disconnected', (reason) => reasons.push(reason));

        manager.connectToUrl('ws://127.0.0.1:1/ws?room=ZZZZZZ', {
            socketFactory: (url) => {
                const socket = { url, readyState: 0, close() { this.readyState = 3; } };
                sockets.push(socket);
                return socket;
            },
        });

        const raise = {
            error: () => sockets[0].onerror?.({ type: 'error' }),
            close: () => sockets[0].onclose?.({ code: 1006, wasClean: false }),
        };
        for (const event of spelling.split(' then ')) raise[event.replace(' alone', '')]();

        manager.tick(1 / 60);
        assert.deepEqual(reasons, ['transportError'], spelling);
        manager.dispose();
    }
});

// ---- Replication ------------------------------------------------------------

class Health extends Component {
    static schema = {
        ...Component.schema,
        hp: { type: 'int', default: 100, replicated: true },
        aim: { type: 'vector2', default: [0, 0], replicated: true },
        ignored: { type: 'int', default: 7 },
    };

    constructor() {
        super();
        this.hp = 100;
        this.aim = new Vector2();
        this.ignored = 7;
    }
}
registerComponent(Health, { category: 'Project', source: 'project' });

test('only changed members are sent after the first snapshot', () => {
    // The point of a dirty check: a full snapshot every tick is the difference between a
    // game that plays over a phone connection and one that does not.
    const scene = new Scene('replicate');
    try {
        const actor = scene.addActor(new Actor('Bot'));
        const health = actor.addComponent(Health);
        const netObject = actor.addComponent(NetworkObject);
        scene.flushPendingActors();

        assert.ok(memberCount(netObject.collectLocalState(true)) >= 2);
        assert.equal(memberCount(netObject.collectLocalState()), 0);

        health.hp = 50;
        assert.equal(memberCount(netObject.collectLocalState()), 1);
    } finally { scene.destroy(); }
});

test('a state blob applies onto another actor', () => {
    const scene = new Scene('apply');
    try {
        const source = scene.addActor(new Actor('Source'));
        const sourceHealth = source.addComponent(Health);
        const sourceNet = source.addComponent(NetworkObject);

        const target = scene.addActor(new Actor('Target'));
        const targetHealth = target.addComponent(Health);
        const targetNet = target.addComponent(NetworkObject);
        scene.flushPendingActors();

        sourceHealth.hp = 33;
        sourceHealth.aim = new Vector2(4, -5);
        targetNet.applyRemoteState(sourceNet.collectLocalState(true));

        assert.equal(targetHealth.hp, 33);
        assert.equal(targetHealth.aim.x, 4);
        assert.equal(targetHealth.aim.y, -5);
        assert.equal(targetHealth.ignored, 7);   // not marked, not sent
    } finally { scene.destroy(); }
});

test('a member the other build does not have is stepped over rather than breaking the blob', () => {
    // What lets an older client stay in a session with a newer server. Without the length in
    // the tag, one unknown member would desynchronise every member after it.
    const scene = new Scene('skip');
    try {
        const target = scene.addActor(new Actor('Target'));
        const health = target.addComponent(Health);
        const netObject = target.addComponent(NetworkObject);
        scene.flushPendingActors();

        const blob = new NetWriter();
        blob.byte(2).byte(0);
        blob.string('stamina').byte(2).int(9);      // tag 2 = Int32
        blob.string('hp').byte(2).int(64);
        netObject.applyRemoteState(blob.toBytes());

        assert.equal(health.hp, 64);
    } finally { scene.destroy(); }
});

function memberCount(blob) { return blob.length < 2 ? 0 : blob[0] | (blob[1] << 8); }

// ---- Rooms ------------------------------------------------------------------

test('room codes avoid every character pair that gets misread', () => {
    // Codes get read aloud and typed from memory. O/0, I/1/l are the whole reason for a
    // custom alphabet rather than base36.
    for (const forbidden of ['O', '0', 'I', '1', 'L']) {
        assert.equal(isRoomCode(forbidden.repeat(6)), false, `${forbidden} should not be in the alphabet`);
    }
    assert.equal(isRoomCode(generateRoomCode()), true);
    assert.equal(isRoomCode('ABC'), false, 'a short code is not a code');
});

test('a join code is read out of every URL shape a Darks Games title uses', () => {
    assert.equal(roomCodeFromUrl('https://game.darksgames.app/j/ABC234'), 'ABC234');
    assert.equal(roomCodeFromUrl('https://game.darksgames.app/?room=abc234'), 'ABC234');
    assert.equal(roomCodeFromUrl('https://game.darksgames.app/#ABC234'), 'ABC234');
    assert.equal(roomCodeFromUrl('https://game.darksgames.app/'), null);
});

test('a room socket url upgrades the scheme rather than assuming one', () => {
    // A page served over https cannot open a ws:// socket — the browser blocks it as mixed
    // content, with an error that says nothing about the room.
    assert.equal(roomSocketUrl('ABC234', { baseUrl: 'https://game.darksgames.app' }),
        'wss://game.darksgames.app/ws?room=ABC234');
    assert.equal(roomSocketUrl('ABC234', { baseUrl: 'http://localhost:8080' }),
        'ws://localhost:8080/ws?room=ABC234');
});

// ---- The script hook --------------------------------------------------------

test('a script’s onNetworkMessage hook receives the message', async () => {
    // The templates were written against a hook that did not exist. It does now, and it
    // has to fire for a script that joins a session after the script started — a lobby
    // calls Network.startServer from onUpdate, which is after every onStart has run.
    const { Scene, Actor, ScriptComponent } = await import('../src/index.js');

    NetworkManager.instance?.dispose();
    const scene = new Scene('hook');
    const manager = new NetworkManager();
    try {
        const actor = scene.addActor(new Actor('Listener'));
        const script = actor.addComponent(ScriptComponent);
        scene.flushPendingActors();

        // `invoke` reaches any top-level function, which is how a test reads a flat
        // script's state — the same door another script uses.
        script.setSource(`
            var seen = "", from = -1;
            function onNetworkMessage(type, data, sender) {
                seen = type + ":" + (data ? data.hp : "-");
                from = sender;
            }
            function readSeen() { return seen; }
            function readFrom() { return from; }
        `);
        scene.update(1 / 60);

        manager.startSolo();
        for (let i = 0; i < 4; i++) manager.tick(1 / 60);

        // One frame so the component notices the session that started after it did.
        scene.update(1 / 60);

        manager.sendMessageToAll('hit', { hp: 7 });
        for (let i = 0; i < 4; i++) manager.tick(1 / 60);

        assert.equal(script.invoke('readSeen'), 'hit:7');
        assert.equal(script.invoke('readFrom'), 0);
    } finally {
        manager.dispose();
        scene.destroy();
    }
});

// ---- Parity with the native engine ------------------------------------------

test('the native engine declares the same message ids and protocol version', () => {
    // The two implementations are the wire. A number that differs by one is a session that
    // connects, handshakes, and then silently mis-decodes every frame.
    const source = csharp('Networking', 'NetProtocol.cs');

    for (const [name, value] of Object.entries(NetMessage)) {
        const match = new RegExp(`\\b${name}\\s*=\\s*0x([0-9A-Fa-f]+)`).exec(source);
        assert.ok(match, `NetProtocol.cs does not declare NetMessage.${name}`);
        assert.equal(parseInt(match[1], 16), value, `${name} differs between the engines`);
    }

    const version = /NetProtocolVersion[\s\S]*?Current\s*=\s*(\d+)/.exec(source);
    assert.ok(version, 'NetProtocol.cs does not declare a protocol version');
    assert.equal(Number(version[1]), NET_PROTOCOL_VERSION);
});

test('the native engine declares the same value tags', () => {
    // The tags inside a replicated state blob. They are private to each engine and have to
    // match anyway, because the blob crosses between them untouched.
    const source = csharp('Networking', 'NetworkObject.cs');
    const ours = fs.readFileSync(path.join(here, '../src/net/NetworkObject.js'), 'utf8');

    const theirTags = [...source.matchAll(/^\s{8}(\w+)\s*=\s*(\d+),/gm)].map(([, name, value]) => [name, Number(value)]);
    assert.ok(theirTags.length >= 10, 'no ValueTag entries found in NetworkObject.cs');

    for (const [name, value] of theirTags) {
        const match = new RegExp(`\\b${name}:\\s*(\\d+)`).exec(ours);
        assert.ok(match, `html5 NetworkObject.js does not declare the ${name} tag`);
        assert.equal(Number(match[1]), value, `${name} differs between the engines`);
    }
});
