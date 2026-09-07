// -----------------------------------------------------------------------------
// LoopbackTransport — two transports joined in memory, with no socket between.
//
// This is what makes networking testable and what makes a single-player run of a
// multiplayer game work: the game hosts and joins itself, every frame goes
// through the same encode and decode path as a real one, and nothing opens a
// connection.
// -----------------------------------------------------------------------------

/** A connected remote. Identity only — no transport types leak through it. */
export class NetPeerHandle {
    constructor(id, address) {
        this.id = id;
        this.address = address;
        /** Whatever the host hangs off this peer; the manager keeps its client id here. */
        this.tag = null;
    }

    toString() { return `peer#${this.id} (${this.address})`; }
}

/**
 * One half of an in-memory pair.
 *
 * Frames are queued and delivered on `poll()` rather than handed over directly.
 * Delivering inline would let a handler run inside the sender's own `send` call,
 * which no real transport ever does and which hides re-entrancy bugs until the
 * first session over a real socket.
 */
export class LoopbackTransport {
    constructor(id, address) {
        this._self = new NetPeerHandle(id, address);
        this._other = null;
        this._inbox = [];
        this._connected = false;
        this._pendingConnect = false;
        this._pendingDisconnect = null;
        this.isRunning = false;

        this.onPeerConnected = null;
        this.onPeerDisconnected = null;
        this.onFrame = null;
    }

    get name() { return 'loopback'; }

    /** Builds a connected pair: the server end first, then the client end. */
    static createPair() {
        const server = new LoopbackTransport(1, 'loopback/server');
        const client = new LoopbackTransport(2, 'loopback/client');
        server._other = client;
        client._other = server;
        return [server, client];
    }

    get peers() { return this._connected && this._other ? [this._other._self] : []; }

    start() {
        if (this.isRunning) return;
        this.isRunning = true;

        // The connection exists once both ends are up. Announcing it from the second
        // start means each end raises connected exactly once, in its own poll.
        if (this._other?.isRunning) {
            this._connected = true;
            this._other._connected = true;
            this._pendingConnect = true;
            this._other._pendingConnect = true;
        }
    }

    stop() {
        if (!this.isRunning) return;
        this.isRunning = false;

        if (this._connected && this._other) {
            this._connected = false;
            this._other._connected = false;
            this._other._pendingDisconnect = 'closedByPeer';
        }
    }

    poll() {
        if (this._pendingConnect) {
            this._pendingConnect = false;
            if (this._other) this.onPeerConnected?.(this._other._self);
        }

        while (this._inbox.length > 0) {
            const frame = this._inbox.shift();
            if (this._other) this.onFrame?.(this._other._self, frame);
        }

        if (this._pendingDisconnect) {
            const reason = this._pendingDisconnect;
            this._pendingDisconnect = null;
            if (this._other) this.onPeerDisconnected?.(this._other._self, reason);
        }
    }

    send(peer, payload) {
        if (!this.isRunning || !this._connected || !this._other) return;
        this._other._inbox.push(payload.slice());
    }

    disconnect() { this.stop(); }

    dispose() { this.stop(); }
}
