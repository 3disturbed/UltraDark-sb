// -----------------------------------------------------------------------------
// A WebSocket server, by hand.
//
// Node ships a WebSocket *client* and no server, and this repository has no
// dependencies, so the handshake and the framing live here. It is less code than
// it looks: RFC 6455 needs one SHA-1 over a fixed GUID to accept a connection,
// and a frame header that is at most fourteen bytes.
//
// Only what the engine's wire needs is implemented — binary and text data
// frames, close, ping and pong, and continuation for a fragmented message.
// Extensions (permessage-deflate) are never negotiated, so no frame is ever
// compressed.
// -----------------------------------------------------------------------------

import crypto from 'node:crypto';
import { EventEmitter } from 'node:events';

/** The RFC 6455 magic string. Fixed by the standard; not a secret. */
const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';

const OP_CONTINUATION = 0x0;
const OP_TEXT = 0x1;
const OP_BINARY = 0x2;
const OP_CLOSE = 0x8;
const OP_PING = 0x9;
const OP_PONG = 0xa;

/** One upgraded connection. Emits `message`, `close` and `error`. */
export class WebSocketConnection extends EventEmitter {
    constructor(socket, { maxFrameBytes = 256 * 1024 } = {}) {
        super();
        this.socket = socket;
        this.maxFrameBytes = maxFrameBytes;
        this.closed = false;
        /** Whatever the host hangs off the connection — the room, the player. */
        this.tag = null;

        this._buffer = Buffer.alloc(0);
        this._fragments = [];
        this._fragmentOpcode = 0;

        socket.on('data', (chunk) => this._onData(chunk));
        socket.on('close', () => this._finish());
        socket.on('error', (err) => { this.emit('error', err); this._finish(); });
    }

    /** The address a proxy says the client came from, else the socket's own. */
    static addressOf(request, socket) {
        const forwarded = request.headers['x-forwarded-for'];
        if (typeof forwarded === 'string' && forwarded.length > 0) return forwarded.split(',')[0].trim();
        return socket.remoteAddress ?? 'unknown';
    }

    /** Sends a binary message. */
    send(payload) {
        if (this.closed) return;
        this.socket.write(encodeFrame(OP_BINARY, Buffer.from(payload)));
    }

    /** Sends a text message. Not used by the engine's wire; handy for diagnostics. */
    sendText(text) {
        if (this.closed) return;
        this.socket.write(encodeFrame(OP_TEXT, Buffer.from(String(text), 'utf8')));
    }

    ping() {
        if (this.closed) return;
        this.socket.write(encodeFrame(OP_PING, Buffer.alloc(0)));
    }

    /** Closes politely, with a status code the peer can read. */
    close(code = 1000, reason = '') {
        if (this.closed) return;
        const body = Buffer.alloc(2 + Buffer.byteLength(reason));
        body.writeUInt16BE(code, 0);
        body.write(reason, 2, 'utf8');
        try {
            this.socket.write(encodeFrame(OP_CLOSE, body));
            this.socket.end();
        } catch { /* already gone */ }
        this._finish();
    }

    _finish() {
        if (this.closed) return;
        this.closed = true;
        try { this.socket.destroy(); } catch { /* already gone */ }
        this.emit('close');
    }

    _onData(chunk) {
        this._buffer = this._buffer.length === 0 ? chunk : Buffer.concat([this._buffer, chunk]);

        // A TCP read is not a frame: one read can hold three frames or a third of one,
        // so decode in a loop and keep whatever is left over for the next chunk.
        for (;;) {
            const frame = decodeFrame(this._buffer, this.maxFrameBytes);
            if (frame === null) return;                 // not enough bytes yet
            if (frame === false) { this.close(1009, 'frame too large'); return; }

            this._buffer = this._buffer.subarray(frame.consumed);
            this._handleFrame(frame);
        }
    }

    _handleFrame(frame) {
        switch (frame.opcode) {
            case OP_CLOSE:
                this.close(1000, '');
                return;
            case OP_PING:
                if (!this.closed) this.socket.write(encodeFrame(OP_PONG, frame.payload));
                return;
            case OP_PONG:
                return;
            case OP_CONTINUATION:
                this._fragments.push(frame.payload);
                if (!frame.fin) return;
                this._deliver(this._fragmentOpcode, Buffer.concat(this._fragments));
                this._fragments = [];
                return;
            default:
                if (!frame.fin) {
                    this._fragmentOpcode = frame.opcode;
                    this._fragments = [frame.payload];
                    return;
                }
                this._deliver(frame.opcode, frame.payload);
        }
    }

    _deliver(opcode, payload) {
        // Text frames are not part of the engine's wire. Passing them through as data
        // would hand the manager a frame whose first byte is an ASCII character and let
        // it be read as a message id, so they are dropped here instead.
        if (opcode !== OP_BINARY) return;
        this.emit('message', new Uint8Array(payload));
    }
}

/**
 * Completes the handshake on an HTTP upgrade, returning the connection.
 *
 * Returns null and answers 400 when the request is not a valid upgrade — the
 * only way to say "no" that a browser reports usefully.
 */
export function acceptUpgrade(request, socket, { maxFrameBytes } = {}) {
    const key = request.headers['sec-websocket-key'];
    const version = request.headers['sec-websocket-version'];

    if (request.headers.upgrade?.toLowerCase() !== 'websocket' || !key || version !== '13') {
        socket.end('HTTP/1.1 400 Bad Request\r\n\r\n');
        return null;
    }

    const accept = crypto.createHash('sha1').update(key + GUID).digest('base64');
    socket.write(
        'HTTP/1.1 101 Switching Protocols\r\n'
        + 'Upgrade: websocket\r\n'
        + 'Connection: Upgrade\r\n'
        + `Sec-WebSocket-Accept: ${accept}\r\n\r\n`);

    // Nagle batches small writes, which is exactly wrong for a game: a 9-byte ping
    // would sit in the kernel waiting for company and report a latency that is not real.
    socket.setNoDelay(true);
    return new WebSocketConnection(socket, { maxFrameBytes });
}

/** Builds one frame. A server never masks, per RFC 6455 §5.1. */
export function encodeFrame(opcode, payload) {
    const length = payload.length;
    let header;

    if (length < 126) {
        header = Buffer.alloc(2);
        header[1] = length;
    } else if (length < 65536) {
        header = Buffer.alloc(4);
        header[1] = 126;
        header.writeUInt16BE(length, 2);
    } else {
        header = Buffer.alloc(10);
        header[1] = 127;
        // A length is 64 bits on the wire. Node has no writeUInt64BE, and a payload
        // never exceeds 2^32 here, so the high word is left zero.
        header.writeUInt32BE(0, 2);
        header.writeUInt32BE(length, 6);
    }
    header[0] = 0x80 | opcode;                 // FIN + opcode

    return Buffer.concat([header, payload]);
}

/**
 * Reads one frame from the front of a buffer.
 *
 * @returns {{opcode: number, fin: boolean, payload: Buffer, consumed: number}|null|false}
 *   The frame; null when the buffer does not hold a whole one yet; false when the
 *   declared length exceeds the cap.
 */
export function decodeFrame(buffer, maxFrameBytes = 256 * 1024) {
    if (buffer.length < 2) return null;

    const fin = (buffer[0] & 0x80) !== 0;
    const opcode = buffer[0] & 0x0f;
    const masked = (buffer[1] & 0x80) !== 0;
    let length = buffer[1] & 0x7f;
    let offset = 2;

    if (length === 126) {
        if (buffer.length < offset + 2) return null;
        length = buffer.readUInt16BE(offset);
        offset += 2;
    } else if (length === 127) {
        if (buffer.length < offset + 8) return null;
        // The high word would mean a payload over 4 GB; refuse rather than truncate it
        // into something plausible.
        if (buffer.readUInt32BE(offset) !== 0) return false;
        length = buffer.readUInt32BE(offset + 4);
        offset += 8;
    }

    if (length > maxFrameBytes) return false;

    const maskLength = masked ? 4 : 0;
    if (buffer.length < offset + maskLength + length) return null;

    const mask = masked ? buffer.subarray(offset, offset + 4) : null;
    offset += maskLength;

    const payload = Buffer.from(buffer.subarray(offset, offset + length));
    if (mask) {
        for (let i = 0; i < payload.length; i++) payload[i] ^= mask[i & 3];
    }

    return { opcode, fin, payload, consumed: offset + length };
}
