#!/usr/bin/env node
// -----------------------------------------------------------------------------
// roomserver — link-to-join multiplayer for a web build.
//
// A browser cannot listen for connections, so "hosting" on the web means asking
// a server for a room code and being the first to connect to it. This is that
// server: it hands out codes, relays the engine's frames between everyone in a
// room, and answers the room-info endpoint the Darks Games overlay polls for its
// Join button.
//
//     node html5/tools/roomserver.js [--port 8081] [--root <dir>] [--max 8]
//
// Routes:
//     POST /api/rooms         -> { code, mode, joinUrl }
//     GET  /api/rooms/:code   -> { code, players, max, phase, joinable } | 404
//     GET  /api/health        -> { ok, rooms, players }
//     GET  /j/:code           -> the game, with the code in the URL
//     WS   /ws?room=CODE      -> the session
//
// It owns identity; it does not simulate. It answers the handshake — assigning
// each player the client id everyone else sees — and relays everything else
// untouched. The first player in a room is the game's authority, exactly as it
// would be hosting on a LAN, which is what keeps a game's code identical whether
// it is hosted from a desktop build or through here.
//
// Identity has to live here rather than with the host, because through a relay
// every peer reaches the host down one socket: the host cannot tell two players
// apart by connection, and a sender id a client chose for itself is not one
// anybody should trust.
// -----------------------------------------------------------------------------

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { acceptUpgrade, WebSocketConnection } from './lib/websocket.js';
import { generateRoomCode, isRoomCode } from '../src/net/rooms.js';
import {
    NetMessage, NET_PROTOCOL_VERSION, NetWriter, NetReader, NetProtocolError, encodeJson, decodeJson,
} from '../src/net/protocol.js';

const here = path.dirname(fileURLToPath(import.meta.url));

// ---- Options ----------------------------------------------------------------

const args = process.argv.slice(2);
const option = (name, fallback) => {
    const i = args.indexOf(`--${name}`);
    return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
};

const PORT = Number(option('port', process.env.PORT ?? 8081));
const ROOT = path.resolve(option('root', path.join(here, '..', '..')));
const MAX_PLAYERS = Number(option('max', 8));
const ROOM_CAP = Number(option('rooms', 256));
const PUBLIC_URL = option('url', process.env.PUBLIC_URL ?? '');

/** A room with nobody in it is kept this long, so a reload does not lose it. */
const EMPTY_GRACE_MS = 60_000;

// ---- Rooms ------------------------------------------------------------------

/** @type {Map<string, {code: string, mode: string, members: Set<WebSocketConnection>, emptySince: number, created: number}>} */
const rooms = new Map();

function createRoom(mode = 'default') {
    if (rooms.size >= ROOM_CAP) return null;

    let code;
    do { code = generateRoomCode(); } while (rooms.has(code));

    const room = {
        code, mode,
        members: new Set(),
        // Ids are never reused within a room: a player who rejoins is a new player, and
        // a stale reference to id 3 must not silently resolve to somebody else.
        nextClientId: 1,
        emptySince: Date.now(),
        created: Date.now(),
    };
    rooms.set(code, room);
    return room;
}

function sweep() {
    const now = Date.now();
    for (const [code, room] of rooms) {
        if (room.members.size === 0 && now - room.emptySince > EMPTY_GRACE_MS) rooms.delete(code);
    }
}
setInterval(sweep, 30_000).unref();

// Room creation is rate-limited per address: a code is cheap to make and a room
// holds memory, so an unthrottled endpoint is a one-line denial of service.
const createHits = new Map();
setInterval(() => createHits.clear(), 60_000).unref();

function allowCreate(address) {
    const hits = (createHits.get(address) ?? 0) + 1;
    createHits.set(address, hits);
    return hits <= 10;
}

// ---- HTTP -------------------------------------------------------------------

const TYPES = {
    '.html': 'text/html; charset=utf-8',
    '.js': 'text/javascript; charset=utf-8',
    '.mjs': 'text/javascript; charset=utf-8',
    '.css': 'text/css; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.scene': 'application/json; charset=utf-8',
    '.webmanifest': 'application/manifest+json; charset=utf-8',
    '.png': 'image/png',
    '.jpg': 'image/jpeg',
    '.svg': 'image/svg+xml',
    '.ogg': 'audio/ogg',
    '.wav': 'audio/wav',
    '.glb': 'model/gltf-binary',
    '.ttf': 'font/ttf',
};

const server = http.createServer(async (request, response) => {
    const url = new URL(request.url, `http://${request.headers.host ?? 'localhost'}`);
    const address = clientAddress(request);

    if (request.method === 'POST' && url.pathname === '/api/rooms') {
        if (!allowCreate(address)) return json(response, 429, { error: 'slow_down' });

        const body = await readJson(request);
        const room = createRoom(typeof body?.mode === 'string' ? body.mode.slice(0, 32) : 'default');
        if (!room) return json(response, 503, { error: 'server_full' });

        const origin = PUBLIC_URL || `http://${request.headers.host}`;
        log(`room ${room.code} created (${room.mode}) by ${address}`);
        return json(response, 200, { code: room.code, mode: room.mode, joinUrl: `${origin}/j/${room.code}` });
    }

    const roomInfo = url.pathname.match(/^\/api\/rooms\/([A-Za-z0-9]{4,8})$/);
    if (roomInfo) {
        const room = rooms.get(roomInfo[1].toUpperCase());
        if (!room) return json(response, 404, { error: 'not_found' });
        return json(response, 200, {
            code: room.code,
            players: seated(room).length,
            max: MAX_PLAYERS,
            // The relay does not simulate, so it cannot know a phase. Saying "lobby"
            // would be a guess; the field is present and empty rather than wrong.
            phase: '',
            joinable: seated(room).length < MAX_PLAYERS,
        });
    }

    if (url.pathname === '/api/health') {
        return json(response, 200, {
            ok: true,
            rooms: rooms.size,
            players: [...rooms.values()].reduce((n, r) => n + seated(r).length, 0),
        });
    }

    // /j/CODE is the Darks Games join link. It serves the game itself: the code is in
    // the URL for the client to read, and a deep link a friend pasted has to load
    // something rather than 404.
    const join = url.pathname.match(/^\/j\/([A-Za-z0-9]{4,8})\/?$/);
    if (join) return serveFile(response, path.join(ROOT, 'html5', 'runtime', 'index.html'));

    return serveStatic(response, url.pathname);
});

server.on('upgrade', (request, socket) => {
    const url = new URL(request.url, `http://${request.headers.host ?? 'localhost'}`);
    if (url.pathname !== '/ws') { socket.end('HTTP/1.1 404 Not Found\r\n\r\n'); return; }

    const code = (url.searchParams.get('room') ?? '').toUpperCase();
    const room = rooms.get(code);

    // Refuse before the upgrade: a client that asked for a room that no longer exists
    // should see an HTTP error it can read, not a socket that opens and dies.
    if (!isRoomCode(code) || !room) {
        socket.end('HTTP/1.1 404 Not Found\r\nContent-Type: application/json\r\n\r\n{"error":"no_such_room"}');
        return;
    }
    if (room.members.size >= MAX_PLAYERS) {
        socket.end('HTTP/1.1 503 Service Unavailable\r\nContent-Type: application/json\r\n\r\n{"error":"room_full"}');
        return;
    }

    const connection = acceptUpgrade(request, socket);
    if (!connection) return;

    // A connection has no seat until it says hello. A socket that opens and never
    // introduces itself is a port scan, and should not appear in anyone's roster.
    connection.tag = { room, clientId: -1, name: '', address: WebSocketConnection.addressOf(request, socket) };
    room.members.add(connection);

    connection.on('message', (frame) => {
        try {
            route(room, connection, frame);
        } catch (err) {
            if (!(err instanceof NetProtocolError)) throw err;
            log(`room ${room.code}: bad frame from ${connection.tag.address} — ${err.message}`);
            connection.close(1002, 'protocol error');
        }
    });

    connection.on('close', () => {
        room.members.delete(connection);
        const { clientId, name } = connection.tag;

        if (clientId >= 0) {
            broadcast(room, new NetWriter(NetMessage.PeerLeft).int(clientId).toBytes(), connection);
            log(`room ${room.code}: ${name} left, ${seated(room).length} player(s)`);
        }
        if (room.members.size === 0) room.emptySince = Date.now();
    });
});

// ---- The session ------------------------------------------------------------

/** The connections in a room that have completed the handshake. */
function seated(room) {
    return [...room.members].filter((member) => member.tag.clientId >= 0);
}

function broadcast(room, frame, except = null) {
    for (const member of room.members) {
        if (member !== except && member.tag.clientId >= 0) member.send(frame);
    }
}

/**
 * Handles one frame from one connection.
 *
 * The handshake, the roster and the clock are the relay's; everything else is the
 * game's and goes out untouched. Message is the exception that proves the rule: it
 * is relayed, but with the sender id rewritten to the one this server assigned,
 * because a peer that can name itself can name anybody.
 */
function route(room, connection, frame) {
    if (!frame || frame.length === 0) return;

    const reader = new NetReader(frame);
    const id = reader.byte();

    switch (id) {
        case NetMessage.Hello: {
            const hello = decodeJson(reader);
            if ((hello?.proto ?? 0) !== NET_PROTOCOL_VERSION) {
                connection.send(new NetWriter(NetMessage.Kick)
                    .string(`protocol ${hello?.proto ?? 0} does not match the server's ${NET_PROTOCOL_VERSION}`)
                    .toBytes());
                connection.close(1002, 'protocol mismatch');
                return;
            }
            if (connection.tag.clientId >= 0) return;      // already seated; ignore a repeat

            const clientId = room.nextClientId++;
            const name = String(hello?.name ?? `Player ${clientId}`).slice(0, 32);
            connection.tag.clientId = clientId;
            connection.tag.name = name;

            connection.send(encodeJson(NetMessage.Welcome, {
                proto: NET_PROTOCOL_VERSION,
                clientId,
                room: room.code,
                players: seated(room).map((m) => ({ id: m.tag.clientId, name: m.tag.name })),
            }));

            // Everyone already in gets told; the newcomer learnt the roster from Welcome.
            broadcast(room, new NetWriter(NetMessage.PeerJoined).int(clientId).string(name).toBytes(), connection);
            log(`room ${room.code}: ${name} joined as ${clientId}, ${seated(room).length} player(s)`);
            return;
        }

        case NetMessage.Ping: {
            const clientTime = reader.double();
            connection.send(new NetWriter(NetMessage.Pong).double(clientTime).double(Date.now()).toBytes());
            return;
        }

        case NetMessage.Message: {
            if (connection.tag.clientId < 0) return;       // not seated: not heard
            reader.int();                                  // the sender the client claimed
            const type = reader.string();
            const json = reader.string();
            broadcast(room,
                new NetWriter(NetMessage.Message).int(connection.tag.clientId).string(type).string(json).toBytes(),
                connection);
            return;
        }

        default:
            // Spawn, state, RPC: the game's own traffic. The relay has no opinion.
            if (connection.tag.clientId < 0) return;
            broadcast(room, frame, connection);
    }
}

// A proxy that sees no traffic for five minutes closes the connection. Pinging every
// thirty seconds keeps an idle lobby alive without the game having to send anything.
setInterval(() => {
    for (const room of rooms.values()) {
        for (const member of room.members) member.ping();
    }
}, 30_000).unref();

// ---- Helpers ----------------------------------------------------------------

function clientAddress(request) {
    const forwarded = request.headers['x-forwarded-for'];
    if (typeof forwarded === 'string' && forwarded.length > 0) return forwarded.split(',')[0].trim();
    return request.socket.remoteAddress ?? 'unknown';
}

async function readJson(request) {
    const chunks = [];
    let size = 0;
    for await (const chunk of request) {
        size += chunk.length;
        if (size > 2048) return null;      // nothing legitimate is bigger
        chunks.push(chunk);
    }
    try { return JSON.parse(Buffer.concat(chunks).toString('utf8')); } catch { return null; }
}

function json(response, status, body) {
    const payload = JSON.stringify(body);
    response.writeHead(status, {
        'content-type': 'application/json; charset=utf-8',
        'content-length': Buffer.byteLength(payload),
        // The game may be served from another origin (a CDN, a vanity apex) while the
        // rooms live here, so the room API has to be readable cross-origin.
        'access-control-allow-origin': '*',
    });
    response.end(payload);
}

function serveStatic(response, pathname) {
    const relative = decodeURIComponent(pathname).replace(/^\/+/, '');
    const file = path.join(ROOT, relative || 'html5/runtime/index.html');

    // Never serve outside the root, however many ../ segments the path carries.
    if (!file.startsWith(ROOT)) { response.writeHead(403).end('forbidden'); return; }

    fs.stat(file, (err, stats) => {
        if (err) { response.writeHead(404).end('not found'); return; }
        serveFile(response, stats.isDirectory() ? path.join(file, 'index.html') : file);
    });
}

function serveFile(response, file) {
    fs.readFile(file, (err, body) => {
        if (err) { response.writeHead(404).end('not found'); return; }
        response.writeHead(200, {
            'content-type': TYPES[path.extname(file).toLowerCase()] ?? 'application/octet-stream',
            'content-length': body.length,
        });
        response.end(body);
    });
}

function log(message) {
    console.log(`${new Date().toISOString().slice(11, 19)}  ${message}`);
}

// ---- Go ---------------------------------------------------------------------

server.listen(PORT, () => {
    console.log(`roomserver on http://localhost:${PORT}`);
    console.log(`  root       ${ROOT}`);
    console.log(`  rooms      up to ${ROOM_CAP}, ${MAX_PLAYERS} players each`);
    console.log(`  create     curl -X POST http://localhost:${PORT}/api/rooms`);
});

export { rooms, createRoom, server };
