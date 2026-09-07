// -----------------------------------------------------------------------------
// WebSocketTransport — the shared wire.
//
// A browser cannot open a UDP socket or listen on a port, so this is the only
// transport the two engines have in common. It carries exactly the frames
// SexyBiscuit.Engine/Networking/WebSocketNetTransport.cs carries, which is what
// lets a web build and a desktop build sit in the same session.
//
// Every delivery mode collapses to reliable-ordered: TCP has no other setting.
// A state update therefore cannot be dropped in favour of a fresher one the way
// it can over UDP; a stalled connection queues them instead.
// -----------------------------------------------------------------------------

import { NetPeerHandle } from './LoopbackTransport.js';

/** A client-side WebSocket, polled like every other transport. */
export class WebSocketTransport {
    /**
     * @param {string} url A `ws://` or `wss://` endpoint.
     * @param {object} [options]
     * @param {Function} [options.socketFactory] Builds the socket. Tests pass a fake;
     *   nothing else should need it.
     */
    constructor(url, { socketFactory } = {}) {
        this.url = url;
        this._socketFactory = socketFactory ?? ((u) => new WebSocket(u));
        this._socket = null;
        this._peer = new NetPeerHandle(1, url);

        // Everything the socket's callbacks produce is queued and drained in poll(),
        // so the game stays single-threaded in the only sense a browser has one:
        // handlers never run in the middle of a frame's update.
        this._events = [];
        this.isRunning = false;

        this.onPeerConnected = null;
        this.onPeerDisconnected = null;
        this.onFrame = null;
    }

    get name() { return 'websocket'; }

    get peers() { return this._socket && this.isRunning ? [this._peer] : []; }

    start() {
        if (this.isRunning) return;
        this.isRunning = true;

        let socket;
        try {
            socket = this._socketFactory(this.url);
        } catch (err) {
            this._events.push(() => this.onPeerDisconnected?.(this._peer, 'transportError'));
            this.isRunning = false;
            return;
        }

        socket.binaryType = 'arraybuffer';
        this._socket = socket;

        socket.onopen = () => this._events.push(() => this.onPeerConnected?.(this._peer));
        socket.onmessage = (event) => {
            // Text frames are not part of this protocol. Ignoring rather than closing
            // keeps a proxy's keepalive from killing a session.
            if (typeof event.data === 'string') return;
            const frame = new Uint8Array(event.data);
            this._events.push(() => this.onFrame?.(this._peer, frame));
        };
        socket.onclose = (event) => {
            this._reportGone(socket, event?.wasClean === false ? 'transportError' : 'closedByPeer');
        };
        // `onerror` is NOT always followed by `onclose`. A connection that never
        // opened raises error alone -- `close` is only fired once the handshake has
        // succeeded -- so a refused upgrade reported nothing at all and the caller
        // waited for ever. That is the ordinary case, not an exotic one: joining a
        // room that has ended is a 404 on the upgrade, and "nothing happens" is the
        // worst way to say "that room is gone".
        socket.onerror = () => this._reportGone(socket, 'transportError');
    }

    /**
     * Reports a socket as gone, once.
     *
     * An established connection that errors DOES then close, so without the identity
     * check the same disconnect would be raised twice -- and a socket replaced by a
     * reconnect must not be able to report a disconnect for the one that replaced it.
     */
    _reportGone(socket, reason) {
        if (this._socket !== socket) return;
        this._socket = null;
        this._events.push(() => this.onPeerDisconnected?.(this._peer, reason));
    }

    stop() {
        if (!this.isRunning) return;
        this.isRunning = false;

        const socket = this._socket;
        this._socket = null;
        if (!socket) return;

        // Detach first: a late `onclose` from a socket being replaced must not report a
        // disconnect for the socket that replaced it.
        socket.onopen = socket.onmessage = socket.onclose = socket.onerror = null;
        try { socket.close(); } catch { /* already gone */ }
    }

    poll() {
        // Swap the queue out before draining: a handler may send, which can enqueue.
        const events = this._events;
        this._events = [];
        for (const raise of events) raise();
    }

    send(peer, payload) {
        if (this._socket?.readyState !== 1) return;   // 1 = OPEN
        this._socket.send(payload);
    }

    disconnect() { this.stop(); }

    dispose() { this.stop(); }
}
