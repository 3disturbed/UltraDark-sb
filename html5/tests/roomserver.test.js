// -----------------------------------------------------------------------------
// The room server, end to end: a real HTTP server, real WebSocket frames, and
// two managers that only know about each other through it.
//
// The WebSocket half is written by hand (tools/lib/websocket.js) because Node
// ships a client and no server, so it is worth proving against Node's own
// client rather than against itself.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { NetworkManager, NetWriter, NetReader, NetMessage } from '../src/index.js';
import { encodeFrame, decodeFrame } from '../tools/lib/websocket.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const roomserver = path.join(here, '..', 'tools', 'roomserver.js');

// ---- Framing ----------------------------------------------------------------

test('a frame survives encode and decode at every length class', () => {
    // The header is 2, 4 or 10 bytes depending on the payload; each boundary is its
    // own code path and each one has been got wrong in some implementation.
    for (const length of [0, 1, 125, 126, 127, 65535, 65536]) {
        const payload = Buffer.alloc(length, 0xab);
        const frame = encodeFrame(0x2, payload);
        const decoded = decodeFrame(frame);

        assert.ok(decoded, `length ${length} did not decode`);
        assert.equal(decoded.opcode, 0x2);
        assert.equal(decoded.fin, true);
        assert.equal(decoded.payload.length, length);
        assert.equal(decoded.consumed, frame.length);
    }
});

test('a partial frame decodes to null rather than to rubbish', () => {
    // A TCP read is not a frame. Returning anything but "not yet" here would hand the
    // engine half a message and desynchronise the stream permanently.
    const frame = encodeFrame(0x2, Buffer.alloc(200, 1));
    for (const cut of [1, 2, 3, 100, frame.length - 1]) {
        assert.equal(decodeFrame(frame.subarray(0, cut)), null, `${cut} bytes should be incomplete`);
    }
});

test('an oversized frame is refused rather than allocated', () => {
    // The length is whatever the peer says it is. Believing a declared 4 GB payload is
    // how a listen server becomes a way to exhaust its host's memory.
    const header = Buffer.alloc(10);
    header[0] = 0x82;
    header[1] = 127;
    header.writeUInt32BE(0xffff, 2);        // a high word, i.e. an absurd length
    header.writeUInt32BE(0, 6);
    assert.equal(decodeFrame(header), false);
});

test('a masked client frame is unmasked', () => {
    // Every browser masks. A server that ignored the mask bit would read every byte
    // XORed with a random key and see garbage.
    const payload = Buffer.from([1, 2, 3, 4]);
    const mask = Buffer.from([0x0a, 0x0b, 0x0c, 0x0d]);
    const masked = Buffer.from(payload.map((b, i) => b ^ mask[i & 3]));

    const frame = Buffer.concat([Buffer.from([0x82, 0x80 | payload.length]), mask, masked]);
    const decoded = decodeFrame(frame);

    assert.ok(decoded);
    assert.deepEqual([...decoded.payload], [...payload]);
});

// ---- The server -------------------------------------------------------------

/** Starts the room server on an ephemeral port and resolves once it says so. */
async function startServer() {
    const port = 9000 + Math.floor(Math.random() * 900);
    const child = spawn(process.execPath, [roomserver, '--port', String(port)], { stdio: ['ignore', 'pipe', 'pipe'] });

    await new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('room server did not start')), 5000);
        child.stdout.on('data', (chunk) => {
            if (String(chunk).includes('roomserver on')) { clearTimeout(timer); resolve(); }
        });
        child.on('error', reject);
    });

    return {
        port,
        base: `http://127.0.0.1:${port}`,
        stop: () => new Promise((resolve) => { child.once('close', resolve); child.kill('SIGKILL'); }),
    };
}

test('a room is created, reported and relays between two clients', async (t) => {
    const server = await startServer();
    t.after(() => server.stop());

    // ---- create ----
    const created = await (await fetch(`${server.base}/api/rooms`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ mode: 'coop' }),
    })).json();

    assert.match(created.code, /^[A-Z2-9]{6}$/);
    assert.equal(created.mode, 'coop');
    assert.ok(created.joinUrl.endsWith(`/j/${created.code}`));

    // ---- report ----
    const info = await (await fetch(`${server.base}/api/rooms/${created.code}`)).json();
    assert.equal(info.players, 0);
    assert.equal(info.joinable, true);

    const missing = await fetch(`${server.base}/api/rooms/ZZZZZZ`);
    assert.equal(missing.status, 404);

    // ---- relay ----
    // Two managers whose only connection to each other is the server. The first is the
    // host: it answers Hello and hands out client ids exactly as a listen server would.
    const url = `ws://127.0.0.1:${server.port}/ws?room=${created.code}`;

    const host = new NetworkManager();
    host.playerName = 'Host';
    try {
        // Both peers connect the same way. Nobody declares themselves the host: the relay
        // hands out ids, and the lowest one present is the authority, which is a rule each
        // peer can evaluate for itself.
        host.connectToUrl(url, { room: created.code });
        await settle(host);
        assert.equal(host.localClientId, 1);
        assert.equal(host.isHost, true, 'the first player in is the authority');

        const guest = NetworkManager._detached();
        guest.playerName = 'Guest';
        guest.connectToUrl(url, { room: created.code });
        await settle(guest, host);

        assert.equal(guest.localClientId, 2);
        assert.equal(guest.isHost, false, 'the second player in is not');
        assert.equal(host.players.get(2), 'Guest', 'the host was told who joined');
        assert.equal(guest.players.get(1), 'Host', 'the guest got the roster it joined');

        const seen = [];
        guest.on('message', (sender, type, payload) => seen.push({ sender, type, payload }));
        host.sendMessageToAll('start', { wave: 3 });
        await settle(host, guest);

        assert.equal(seen.length, 1);
        assert.equal(seen[0].type, 'start');
        assert.equal(seen[0].payload.wave, 3);
        assert.equal(seen[0].sender, 1, 'the sender is the id the relay assigned, not one a client chose');

        const after = await (await fetch(`${server.base}/api/rooms/${created.code}`)).json();
        assert.equal(after.players, 2);

        guest.dispose();
    } finally {
        host.dispose();
    }
});

test('the relay stamps the sender, so a peer cannot post as another', async (t) => {
    // The one guarantee a relay can make that the peers cannot: a client that names
    // itself in a frame can name anybody, which is the cheapest possible way to cheat.
    const server = await startServer();
    t.after(() => server.stop());

    const created = await (await fetch(`${server.base}/api/rooms`, { method: 'POST' })).json();
    const url = `ws://127.0.0.1:${server.port}/ws?room=${created.code}`;

    const listener = NetworkManager._detached();
    const liar = NetworkManager._detached();
    try {
        listener.connectToUrl(url);
        await settle(listener);

        liar.connectToUrl(url);
        await settle(liar, listener);

        const seen = [];
        listener.on('message', (sender) => seen.push(sender));

        // Claim to be client 1, the peer already in the room.
        liar.sendToServer(new NetWriter(NetMessage.Message).int(1).string('cheat').string('').toBytes());
        await settle(liar, listener);

        assert.deepEqual(seen, [liar.localClientId]);
    } finally {
        listener.dispose();
        liar.dispose();
    }
});

test('joining a room that does not exist fails before the socket opens', async (t) => {
    // A client should learn "that room has ended" as an HTTP error it can show, not as
    // a socket that opens and dies a second later for no stated reason.
    const server = await startServer();
    t.after(() => server.stop());

    const manager = NetworkManager._detached();
    let reason = null;
    manager.on('disconnected', (r) => { reason = r; });

    manager.connectToUrl(`ws://127.0.0.1:${server.port}/ws?room=ZZZZZZ`);
    await settle(manager);

    assert.ok(reason, 'the manager was never told the connection failed');
    manager.dispose();
});

/** Pumps the given managers until the sockets have had time to move bytes. */
async function settle(...managers) {
    for (let i = 0; i < 40; i++) {
        await new Promise((resolve) => setTimeout(resolve, 5));
        for (const manager of managers) manager.tick(1 / 60);
    }
}
