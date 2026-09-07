// -----------------------------------------------------------------------------
// NetworkManager — sessions, connections, frame dispatch.
//
// The browser half of the engine's networking. It speaks the frames defined in
// protocol.js, which SexyBiscuit.Engine/Networking/NetworkManager.cs also
// speaks, so a script written against `Network.*` behaves the same in a tab and
// in a native build.
//
// Nothing is sent or received until `tick()` is pumped: a game that is not
// networked should pay nothing for the fact that it could be.
// -----------------------------------------------------------------------------

import {
    NetMessage, NET_PROTOCOL_VERSION, NetDelivery,
    NetWriter, NetReader, NetProtocolError, encodeJson, decodeJson,
} from './protocol.js';
import { LoopbackTransport } from './LoopbackTransport.js';
import { WebSocketTransport } from './WebSocketTransport.js';

/** Which wire a session runs on. */
export const NetTransportKind = {
    /** Binary WebSocket. The only one a browser has. */
    WebSocket: 'websocket',
    /** Wired to itself in memory: a single-player run of a multiplayer game, and every test. */
    Loopback: 'loopback',
};

/**
 * The engine's networking façade.
 *
 * A browser can only ever be a client on a real wire — it cannot listen — so
 * `startServer` here means "host in this tab over loopback". A browser player
 * who wants others to join asks a room server for a code and connects to it;
 * see `rooms.js`.
 */
export class NetworkManager {
    /** The running NetworkManager, or null when there is none. */
    static instance = null;

    constructor() {
        if (NetworkManager.instance) {
            throw new Error('A NetworkManager is already running. Dispose the existing one first.');
        }
        NetworkManager.instance = this;
        this._init();
    }

    /** For the second half of a loopback pair, which must not claim the singleton. */
    static _detached() {
        const manager = Object.create(NetworkManager.prototype);
        manager._init();
        return manager;
    }

    _init() {
        this.isServer = false;
        this.isClient = false;
        this.isRunning = false;

        /**
         * The client id the server assigned. 0 on the server itself, -1 on a client
         * until Welcome arrives.
         */
        this.localClientId = -1;

        /** Round-trip time in milliseconds. Populated on clients. */
        this.ping = 0;

        /** The name this peer introduces itself with. Set it before connecting. */
        this.playerName = 'Player';

        /** The room code this session belongs to, when it was started with one. */
        this.room = '';

        this.transportKind = NetTransportKind.Loopback;

        /** @type {Map<number, string>} Every player in the session, by client id. */
        this.players = new Map();

        this._transport = null;
        this._clients = new Map();      // clientId -> peer   (server)
        this._serverPeer = null;        // (client)
        this._nextClientId = 1;
        this._nextNetworkId = 1;
        this._maxClients = 16;
        this._pingTimer = 0;
        this._loopbackPeer = null;
        this._isLoopbackClientHalf = false;
        this._kickReason = null;
        this._handlers = new Map();

        /** Networked actors by network id, for replication and despawn. */
        this.objects = new Map();
    }

    /** True when this process is both ends of the session. */
    get isHost() { return this.isServer && this.isClient; }

    /** True once the server has accepted us, or immediately when we are the server. */
    get isConnected() { return this.isRunning && (this.isServer || this.localClientId >= 0); }

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /**
     * Subscribes to an event. Returns a function that unsubscribes.
     *
     * Events: `clientConnected(id)`, `clientDisconnected(id)`,
     * `connected()`, `disconnected(reason)`, `spawn(netId, name, owner, state)`,
     * `despawn(netId)`, `message(sender, type, payload)`,
     * `playerJoined(id, name)`, `playerLeft(id)`.
     */
    on(event, handler) {
        if (!this._handlers.has(event)) this._handlers.set(event, new Set());
        this._handlers.get(event).add(handler);
        return () => this._handlers.get(event)?.delete(handler);
    }

    _emit(event, ...args) {
        for (const handler of this._handlers.get(event) ?? []) {
            // One misbehaving listener must not stop the rest, and must not take down
            // the frame the socket callback is being drained in.
            try {
                handler(...args);
            } catch (err) {
                console.error(`[NetworkManager] ${event} handler threw:`, err);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Starting a session
    // -------------------------------------------------------------------------

    /**
     * Starts a session with no socket at all: this tab is both server and client.
     *
     * A multiplayer game played alone should not be a different game. Every frame
     * still goes through the same encode, dispatch and replication path a real one
     * does, so solo play exercises the netcode instead of bypassing it — which is
     * what stops "works alone, breaks in a lobby".
     */
    startSolo() {
        this._requireStopped();

        const [serverEnd, clientEnd] = LoopbackTransport.createPair();

        this.transportKind = NetTransportKind.Loopback;
        this._transport = serverEnd;
        this._wireTransport();

        const client = NetworkManager._detached();
        client.transportKind = NetTransportKind.Loopback;
        client.playerName = this.playerName;
        client._transport = clientEnd;
        client._isLoopbackClientHalf = true;
        client._wireTransport();
        client._forwardTo(this);

        this._loopbackPeer = client;

        this.isServer = true;
        this.isClient = true;
        this.isRunning = true;
        this.localClientId = 0;
        this.players.set(0, this.playerName);

        client.isClient = true;
        client.isRunning = true;

        serverEnd.start();
        clientEnd.start();
    }

    /**
     * Hosts in this tab. A browser cannot listen for connections, so this is
     * `startSolo` under the name a game script expects — others join through a room
     * server, not through this tab.
     *
     * @param {number} [port] Ignored in the browser; accepted so the same script runs
     *   natively, where it binds.
     */
    startServer(port) {
        this.startSolo();
    }

    /**
     * Connects to a `ws://` or `wss://` server — a room server, or a native listen
     * server on the LAN.
     *
     * @param {string} url
     * @param {object} [options]
     * @param {string} [options.room] The room code, reported in the handshake.
     * @param {Function} [options.socketFactory] Test seam.
     */
    connectToUrl(url, { room = '', socketFactory } = {}) {
        this._requireStopped();

        this.room = room;
        this.transportKind = NetTransportKind.WebSocket;
        this._transport = new WebSocketTransport(url, { socketFactory });
        this._wireTransport();
        this._transport.start();

        this.isClient = true;
        this.isServer = false;
        this.isRunning = true;
        this.localClientId = -1;
    }

    /**
     * Connects to a host and port. The native engine's `connect(address, port)` maps
     * onto a `ws://` URL here, because that is the only wire a tab has.
     */
    connect(address, port, options = {}) {
        const secure = typeof location !== 'undefined' && location.protocol === 'https:';
        const room = options.room ?? '';
        const query = room ? `?room=${encodeURIComponent(room)}` : '';
        this.connectToUrl(`${secure ? 'wss' : 'ws'}://${address}:${port}/ws${query}`, options);
    }

    _requireStopped() {
        if (this.isRunning) {
            throw new Error('NetworkManager is already running. Call disconnect() first.');
        }
    }

    // -------------------------------------------------------------------------
    // Ending a session
    // -------------------------------------------------------------------------

    /** Leaves the session, whichever end this is. */
    disconnect() {
        if (!this.isRunning) return;

        if (this.isServer) {
            for (const peer of this._clients.values()) this._transport?.disconnect(peer);
        }
        this._teardown();
    }

    /** The server-side spelling of `disconnect`, so a script reads the way it means. */
    stopServer() { this.disconnect(); }

    _teardown() {
        this._loopbackPeer?._teardown();
        this._loopbackPeer = null;

        this._transport?.stop();
        this._transport?.dispose();
        this._transport = null;

        this._clients.clear();
        this.players.clear();
        this.objects.clear();
        this._serverPeer = null;
        this.isRunning = false;
        this.isServer = false;
        this.isClient = false;
        this.localClientId = -1;
        this.ping = 0;
    }

    /** Disconnects one client with a reason it will see. Server only. */
    kickClient(clientId, reason = '') {
        const peer = this._clients.get(clientId);
        if (!this.isServer || !peer) return;

        // The reason rides in a frame of its own: a WebSocket close code carries no text
        // a browser can read back.
        this._send(peer, new NetWriter(NetMessage.Kick).string(reason).toBytes());
        this._transport?.disconnect(peer);
    }

    /** The connected client ids, as of the call. Server only. */
    get connectedClientIds() { return [...this._clients.keys()]; }

    // -------------------------------------------------------------------------
    // The pump
    // -------------------------------------------------------------------------

    /**
     * Polls the transport, dispatches everything that arrived, and drives
     * replication. Call it once per frame; nothing moves without it.
     */
    tick(dt) {
        if (!this.isRunning) return;

        this._transport?.poll();
        this._loopbackPeer?._transport?.poll();

        this._replicate(dt);

        if (this.isClient && !this.isHost) {
            this._pingTimer += dt;
            if (this._pingTimer >= 1) {
                this._pingTimer = 0;
                this.sendToServer(new NetWriter(NetMessage.Ping).double(now()).toBytes(),
                    NetDelivery.Unreliable);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Transport wiring
    // -------------------------------------------------------------------------

    _wireTransport() {
        const transport = this._transport;
        transport.onPeerConnected = (peer) => this._onPeerConnected(peer);
        transport.onPeerDisconnected = (peer, reason) => this._onPeerDisconnected(peer, reason);
        transport.onFrame = (peer, frame) => this._onFrame(peer, frame);
    }

    /**
     * Points the loopback client half's events at the manager the game actually
     * holds, so solo play raises the same events a real client does.
     */
    _forwardTo(host) {
        for (const event of ['message', 'spawn', 'despawn', 'connected', 'disconnected']) {
            this.on(event, (...args) => host._emit(event, ...args));
        }
    }

    _onPeerConnected(peer) {
        // The server waits for Hello before assigning an id: a peer that connects and
        // never introduces itself is a port scan, and should not take a seat.
        if (this.isServer && !this._isLoopbackClientHalf) return;

        this._serverPeer = peer;
        this._send(peer, encodeJson(NetMessage.Hello, {
            proto: NET_PROTOCOL_VERSION,
            name: this.playerName,
            room: this.room,
        }));
    }

    _onPeerDisconnected(peer, reason) {
        if (this.isServer && !this._isLoopbackClientHalf && typeof peer.tag === 'number') {
            const clientId = peer.tag;
            this._clients.delete(clientId);
            this.players.delete(clientId);
            this._broadcast(new NetWriter(NetMessage.PeerLeft).int(clientId).toBytes());
            this._emit('clientDisconnected', clientId);
            this._emit('playerLeft', clientId);
            return;
        }

        if (peer === this._serverPeer || !this._serverPeer) {
            this._serverPeer = null;
            this._emit('disconnected', this._kickReason ?? reason);
            this._kickReason = null;
        }
    }

    _onFrame(peer, frame) {
        try {
            this._dispatch(peer, frame);
        } catch (err) {
            if (!(err instanceof NetProtocolError)) throw err;
            // A bad frame is a peer, not a bug: drop the peer rather than the session.
            console.error(`[NetworkManager] bad frame from ${peer}: ${err.message}`);
            if (this.isServer) this._transport?.disconnect(peer);
        }
    }

    // -------------------------------------------------------------------------
    // Dispatch
    // -------------------------------------------------------------------------

    _dispatch(peer, frame) {
        if (!frame || frame.length === 0) return;

        const reader = new NetReader(frame);
        const id = reader.byte();
        const sender = typeof peer.tag === 'number' ? peer.tag : -1;

        switch (id) {
            case NetMessage.Hello: this._handleHello(peer, reader); break;
            case NetMessage.Welcome: this._handleWelcome(reader); break;
            case NetMessage.Spawn: this._handleSpawn(reader); break;
            case NetMessage.Despawn: this._emit('despawn', reader.uint()); break;
            case NetMessage.State: this._handleState(reader); break;
            case NetMessage.Rpc: this._handleRpc(reader, sender); break;
            case NetMessage.Message: this._handleMessage(reader, sender); break;
            case NetMessage.Ping: this._handlePing(peer, reader); break;
            case NetMessage.Pong: this._handlePong(reader); break;
            case NetMessage.PeerJoined: this._handlePeerJoined(reader); break;
            case NetMessage.PeerLeft: this._handlePeerLeft(reader); break;
            case NetMessage.Kick: this._kickReason = reader.string(); break;
            default:
                console.error(`[NetworkManager] unknown message id 0x${id.toString(16)} from ${peer}`);
                break;
        }
    }

    _handleHello(peer, reader) {
        if (!this.isServer) return;

        const hello = decodeJson(reader);
        const proto = hello?.proto ?? 0;

        if (proto !== NET_PROTOCOL_VERSION) {
            // Refusing beats mis-decoding: a client one version behind would otherwise
            // read every later frame at the wrong offsets and fail somewhere unrelated.
            this._send(peer, new NetWriter(NetMessage.Kick)
                .string(`protocol ${proto} does not match the server's ${NET_PROTOCOL_VERSION}`).toBytes());
            this._transport?.disconnect(peer);
            return;
        }

        if (this._clients.size >= this._maxClients) {
            this._send(peer, new NetWriter(NetMessage.Kick).string('server is full').toBytes());
            this._transport?.disconnect(peer);
            return;
        }

        const clientId = this._nextClientId++;
        const name = String(hello?.name ?? `Player ${clientId}`).slice(0, 32);

        peer.tag = clientId;
        this._clients.set(clientId, peer);
        this.players.set(clientId, name);

        this._send(peer, encodeJson(NetMessage.Welcome, {
            proto: NET_PROTOCOL_VERSION,
            clientId,
            room: this.room,
            players: [...this.players].map(([id, playerName]) => ({ id, name: playerName })),
        }));

        // Everyone already in gets told; the newcomer learnt the roster from Welcome.
        this._broadcast(new NetWriter(NetMessage.PeerJoined).int(clientId).string(name).toBytes(), clientId);

        this._emit('clientConnected', clientId);
        this._emit('playerJoined', clientId, name);
    }

    _handleWelcome(reader) {
        const welcome = decodeJson(reader);
        this.localClientId = welcome?.clientId ?? -1;
        this.room = welcome?.room ?? this.room;

        this.players.clear();
        for (const entry of welcome?.players ?? []) {
            if (typeof entry?.id === 'number') this.players.set(entry.id, entry.name ?? `Player ${entry.id}`);
        }

        this._emit('connected');
    }

    _handleSpawn(reader) {
        const networkId = reader.uint();
        const actorName = reader.string();
        const owner = reader.int();
        const state = reader.bytes();
        this._emit('spawn', networkId, actorName, owner, state);
    }

    _handleState(reader) {
        const networkId = reader.uint();
        const state = reader.bytes();
        this.objects.get(networkId)?.applyRemoteState(state);
    }

    _handleRpc(reader, senderClientId) {
        const networkId = reader.uint();
        const toServer = reader.bool();
        const target = reader.int();
        const method = reader.string();
        const args = reader.bytes();
        this._emit('rpc', { networkId, toServer, target, method, args, sender: senderClientId });
    }

    _handleMessage(reader, senderClientId) {
        const declared = reader.int();
        const type = reader.string();
        const json = reader.string();

        // The server stamps the sender itself. Trusting the client's own number would
        // let any peer post as any other, which is the cheapest possible way to cheat.
        const sender = this.isServer ? senderClientId : declared;

        let payload = null;
        if (json.length > 0) {
            try { payload = JSON.parse(json); } catch { payload = null; }
        }

        this._emit('message', sender, type, payload);

        // A server relays to everyone else, so a script's sendToAll reaches every peer
        // without the game writing a relay of its own.
        if (this.isServer) {
            this._broadcast(
                new NetWriter(NetMessage.Message).int(sender).string(type).string(json).toBytes(),
                sender);
        }
    }

    _handlePing(peer, reader) {
        const clientTime = reader.double();
        if (!this.isServer) return;
        this._send(peer, new NetWriter(NetMessage.Pong).double(clientTime).double(now()).toBytes());
    }

    _handlePong(reader) {
        const sentAt = reader.double();
        reader.double();                       // server clock, for a future clock sync
        this.ping = Math.max(0, Math.round(now() - sentAt));
    }

    _handlePeerJoined(reader) {
        const id = reader.int();
        const name = reader.string();
        this.players.set(id, name);
        this._emit('playerJoined', id, name);
    }

    _handlePeerLeft(reader) {
        const id = reader.int();
        this.players.delete(id);
        this._emit('playerLeft', id);
    }

    // -------------------------------------------------------------------------
    // Sending
    // -------------------------------------------------------------------------

    /** Sends a raw frame to the server. */
    sendToServer(frame, delivery = NetDelivery.ReliableOrdered) {
        if (this._serverPeer) { this._send(this._serverPeer, frame, delivery); return; }

        // The host half of a loopback session has no server peer — it *is* the server —
        // so the frame goes over the loopback client's socket instead.
        const client = this._loopbackPeer;
        if (client?._serverPeer) client._send(client._serverPeer, frame, delivery);
    }

    /** Sends a raw frame to one client. Server only. */
    sendToClient(clientId, frame, delivery = NetDelivery.ReliableOrdered) {
        const peer = this._clients.get(clientId);
        if (peer) this._send(peer, frame, delivery);
    }

    /** Sends a raw frame to every client. Server only. */
    sendToAllClients(frame, delivery = NetDelivery.ReliableOrdered, exceptClientId = -1) {
        this._broadcast(frame, exceptClientId, delivery);
    }

    _broadcast(frame, exceptClientId = -1, delivery = NetDelivery.ReliableOrdered) {
        for (const [clientId, peer] of this._clients) {
            if (clientId === exceptClientId) continue;
            this._send(peer, frame, delivery);
        }
    }

    _send(peer, frame, delivery = NetDelivery.ReliableOrdered) {
        this._transport?.send(peer, frame, delivery);
    }

    // -------------------------------------------------------------------------
    // The script channel
    // -------------------------------------------------------------------------

    /**
     * Sends a named message to every other peer — what a script's
     * `Network.sendToAll(type, data)` reaches.
     *
     * A client sends it to the server, which relays it on. The engine does not
     * interpret the payload; it only guarantees that the sender id the receiver sees
     * is the one the server assigned, not one the sender chose.
     */
    sendMessageToAll(type, payload = null) {
        if (!this.isRunning) return;

        const json = payload == null ? '' : JSON.stringify(payload);
        const frame = new NetWriter(NetMessage.Message)
            .int(this.localClientId).string(type).string(json).toBytes();

        if (this.isServer) this._broadcast(frame);
        else this.sendToServer(frame);
    }

    /** Sends a named message to one peer. A client's goes via the server. */
    sendMessageTo(clientId, type, payload = null) {
        if (!this.isRunning) return;
        const frame = new NetWriter(NetMessage.Message)
            .int(this.localClientId).string(type).string(payload == null ? '' : JSON.stringify(payload))
            .toBytes();

        if (this.isServer) this.sendToClient(clientId, frame);
        else this.sendToServer(frame);
    }

    // -------------------------------------------------------------------------
    // Spawning and replication
    // -------------------------------------------------------------------------

    /** Registers a networked object so state frames find it. */
    registerObject(networkObject) {
        this.objects.set(networkObject.networkId, networkObject);
    }

    unregisterObject(networkObject) {
        this.objects.delete(networkObject.networkId);
    }

    /** Allocates the next network id. Server only. */
    allocateNetworkId() {
        if (!this.isServer) throw new Error('Network ids are allocated by the server.');
        return this._nextNetworkId++;
    }

    /** Tells every client to build a networked actor. Server only. */
    broadcastSpawn(networkId, actorName, ownerClientId, state) {
        this._broadcast(new NetWriter(NetMessage.Spawn)
            .uint(networkId).string(actorName).int(ownerClientId).bytes(state).toBytes());
    }

    /** Tells every client to destroy a networked actor. Server only. */
    broadcastDespawn(networkId) {
        this._broadcast(new NetWriter(NetMessage.Despawn).uint(networkId).toBytes());
    }

    _replicate(dt) {
        this._replicateTimer = (this._replicateTimer ?? 0) + dt;
        if (this._replicateTimer < this.replicationInterval) return;
        this._replicateTimer = 0;

        for (const object of this.objects.values()) {
            // The server sends everything it owns; a client sends only what it owns.
            if (!this.isServer && !object.isOwner) continue;

            const state = object.collectLocalState();
            if (!state || state.length <= 2) continue;   // just the zero count

            const frame = new NetWriter(NetMessage.State).uint(object.networkId).bytes(state).toBytes();
            if (this.isServer) this._broadcast(frame, object.ownerClientId, NetDelivery.Unreliable);
            else this.sendToServer(frame, NetDelivery.Unreliable);
        }
    }

    /** Seconds between state sends. 20 Hz, matching the native engine's default. */
    replicationInterval = 1 / 20;

    dispose() {
        this._teardown();
        if (NetworkManager.instance === this) NetworkManager.instance = null;
    }
}

function now() {
    return typeof performance !== 'undefined' ? performance.now() : Date.now();
}
