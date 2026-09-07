// -----------------------------------------------------------------------------
// The wire, shared with the native engine.
//
// Every constant and every encoding here has a counterpart in
// SexyBiscuit.Engine/Networking/NetProtocol.cs, and a test on each side reads the
// other side's source, so the two cannot drift. A browser client and a native
// server play the same game over one socket because of that.
// -----------------------------------------------------------------------------

/**
 * Message ids — the first byte of every frame.
 *
 * These are explicit numbers, never derived from a name at runtime: renumbering
 * one breaks every build already in players' hands.
 */
export const NetMessage = {
    /** C to S, JSON: `{proto, name, room, token}`. The first frame a client sends. */
    Hello: 0x01,
    /** S to C, JSON: `{proto, clientId, room, players:[{id, name}]}`. */
    Welcome: 0x02,
    /** S to C: `[netId u32][name str][owner i32][state bytes]`. */
    Spawn: 0x03,
    /** S to C: `[netId u32]`. */
    Despawn: 0x04,
    /** Either way: `[netId u32][state bytes]`. The replication hot path. */
    State: 0x05,
    /** Either way: `[netId u32][flags u8][target i32][method str][args bytes]`. */
    Rpc: 0x06,
    /**
     * Either way: `[sender i32][type str][json str]`. The channel a game script
     * reaches through `Network.sendToAll(type, data)`; the engine never
     * interprets the payload.
     */
    Message: 0x07,
    /** C to S: `[clientTime f64]`. */
    Ping: 0x08,
    /** S to C: `[clientTime f64][serverTimeMs f64]`. */
    Pong: 0x09,
    /** S to C: `[clientId i32][name str]`. Another player joined. */
    PeerJoined: 0x0A,
    /** S to C: `[clientId i32]`. Another player left. */
    PeerLeft: 0x0B,
    /** S to C: `[reason str]`. Sent immediately before the connection is closed. */
    Kick: 0x0C,
};

/**
 * The protocol version, carried in Hello and Welcome.
 *
 * Bump this whenever a frame's layout changes. A server refuses a client whose
 * number differs rather than mis-decoding its frames forever, which is the
 * failure that is impossible to diagnose from a bug report.
 */
export const NET_PROTOCOL_VERSION = 1;

/**
 * How a frame should be delivered.
 *
 * A transport that cannot honour a mode satisfies it with a stronger one.
 * WebSocket delivers everything reliably ordered, which is a valid — if wasteful
 * — way to meet `Unreliable`.
 */
export const NetDelivery = {
    /**
     * Drop rather than delay. State updates go this way: a lost one is a skipped
     * frame the next update replaces, whereas a re-sent one arrives already stale
     * and holds up the fresher ones behind it.
     */
    Unreliable: 'unreliable',
    /** Must arrive, and in order. Handshakes, spawns, RPCs, script messages. */
    ReliableOrdered: 'reliableOrdered',
    /** Must arrive, order irrelevant. */
    ReliableUnordered: 'reliableUnordered',
};

const encoder = new TextEncoder();
const decoder = new TextDecoder();

/** A frame that could not be decoded. Always caught: it means a peer, not a bug. */
export class NetProtocolError extends Error {}

/**
 * Writes the wire format: little-endian numbers and `[u16 byte length][UTF-8]`
 * strings.
 *
 * The explicit little-endian on every DataView call is not decoration —
 * `DataView` defaults to big-endian, and the C# side is little-endian on every
 * machine the engine runs on.
 */
export class NetWriter {
    /** @param {number} [messageId] When given, starts the frame with its id byte. */
    constructor(messageId) {
        this._bytes = new Uint8Array(256);
        this._view = new DataView(this._bytes.buffer);
        this._length = 0;
        if (messageId !== undefined) this.byte(messageId);
    }

    /** Bytes written so far. */
    get length() { return this._length; }

    byte(value) {
        this._ensure(1);
        this._view.setUint8(this._length, value & 0xff);
        this._length += 1;
        return this;
    }

    bool(value) { return this.byte(value ? 1 : 0); }

    int(value) {
        this._ensure(4);
        this._view.setInt32(this._length, value | 0, true);
        this._length += 4;
        return this;
    }

    uint(value) {
        this._ensure(4);
        this._view.setUint32(this._length, value >>> 0, true);
        this._length += 4;
        return this;
    }

    float(value) {
        this._ensure(4);
        this._view.setFloat32(this._length, Number(value) || 0, true);
        this._length += 4;
        return this;
    }

    double(value) {
        this._ensure(8);
        this._view.setFloat64(this._length, Number(value) || 0, true);
        this._length += 8;
        return this;
    }

    /** A UTF-8 string, length-prefixed with a u16. Longer strings are truncated. */
    string(value) {
        let bytes = encoder.encode(value == null ? '' : String(value));
        if (bytes.length > 0xffff) bytes = bytes.subarray(0, 0xffff);

        this._ensure(2 + bytes.length);
        this._view.setUint16(this._length, bytes.length, true);
        this._length += 2;
        this._bytes.set(bytes, this._length);
        this._length += bytes.length;
        return this;
    }

    /** A byte block, length-prefixed with a u32. */
    bytes(value) {
        const block = toBytes(value);
        this._ensure(4 + block.length);
        this._view.setUint32(this._length, block.length, true);
        this._length += 4;
        this._bytes.set(block, this._length);
        this._length += block.length;
        return this;
    }

    /** Bytes with no length prefix; only valid as the last field of a frame. */
    raw(value) {
        const block = toBytes(value);
        this._ensure(block.length);
        this._bytes.set(block, this._length);
        this._length += block.length;
        return this;
    }

    /** The frame, as a fresh Uint8Array of exactly the bytes written. */
    toBytes() { return this._bytes.slice(0, this._length); }

    _ensure(extra) {
        if (this._length + extra <= this._bytes.length) return;
        const size = Math.max(this._bytes.length * 2, this._length + extra);
        const grown = new Uint8Array(size);
        grown.set(this._bytes.subarray(0, this._length));
        this._bytes = grown;
        this._view = new DataView(grown.buffer);
    }
}

/**
 * Reads what `NetWriter` wrote. Every read is bounds-checked: the bytes come off
 * a socket, so a truncated or hostile frame must be a caught error rather than
 * an undefined that poisons the game state ten calls later.
 */
export class NetReader {
    /** @param {Uint8Array|ArrayBuffer} data */
    constructor(data) {
        this._bytes = toBytes(data);
        this._view = new DataView(this._bytes.buffer, this._bytes.byteOffset, this._bytes.byteLength);
        this._offset = 0;
    }

    /** Bytes not yet read. */
    get remaining() { return this._bytes.length - this._offset; }

    byte() {
        this._need(1);
        return this._view.getUint8(this._offset++);
    }

    bool() { return this.byte() !== 0; }

    int() {
        this._need(4);
        const value = this._view.getInt32(this._offset, true);
        this._offset += 4;
        return value;
    }

    uint() {
        this._need(4);
        const value = this._view.getUint32(this._offset, true);
        this._offset += 4;
        return value;
    }

    float() {
        this._need(4);
        const value = this._view.getFloat32(this._offset, true);
        this._offset += 4;
        return value;
    }

    double() {
        this._need(8);
        const value = this._view.getFloat64(this._offset, true);
        this._offset += 8;
        return value;
    }

    string() {
        this._need(2);
        const length = this._view.getUint16(this._offset, true);
        this._offset += 2;
        this._need(length);
        const value = decoder.decode(this._bytes.subarray(this._offset, this._offset + length));
        this._offset += length;
        return value;
    }

    bytes() {
        this._need(4);
        const length = this._view.getUint32(this._offset, true);
        this._offset += 4;
        this._need(length);
        const value = this._bytes.slice(this._offset, this._offset + length);
        this._offset += length;
        return value;
    }

    /** Everything left, with no length prefix. */
    rest() {
        const value = this._bytes.slice(this._offset);
        this._offset = this._bytes.length;
        return value;
    }

    _need(count) {
        if (this._offset + count > this._bytes.length) {
            throw new NetProtocolError(
                `frame is ${this._bytes.length} bytes; reading ${count} more at offset ${this._offset} runs past the end`);
        }
    }
}

/** A JSON frame: the id byte, then the object as a length-prefixed UTF-8 string. */
export function encodeJson(messageId, value) {
    return new NetWriter(messageId).string(JSON.stringify(value ?? {})).toBytes();
}

/** The object from a JSON frame, given the bytes after the id byte. */
export function decodeJson(reader) {
    try {
        return JSON.parse(reader.string());
    } catch (err) {
        throw new NetProtocolError(`frame does not carry JSON: ${err.message}`);
    }
}

function toBytes(value) {
    if (value instanceof Uint8Array) return value;
    if (value instanceof ArrayBuffer) return new Uint8Array(value);
    if (ArrayBuffer.isView(value)) return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
    if (Array.isArray(value)) return Uint8Array.from(value);
    return new Uint8Array(0);
}
