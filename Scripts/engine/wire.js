// -----------------------------------------------------------------------------
// wire — UltraDark's WebSockets, carried by the engine's session.
//
// The original server gave every player a WebSocket: the client opened
// /ws?room=CODE, the server read the code off the URL, and binary frames written by
// shared/protocol.js went both ways until one end closed. A script has the engine's
// session instead, where every machine is one client id and a message is a type and
// some JSON. This module puts the sockets back on top of it, so room.js and the
// client keep talking to things shaped like the sockets they had:
//
//   - A socket is a numbered stream from one machine to the session's authority. Its
//     frames cross as script messages of type "ds/<sid>", each one protocol.js frame
//     as base64 text; "ds/<sid>/close" says that end closed it. A machine numbers its
//     sockets from 1 and never reuses a number in a session, so a frame for a socket
//     already closed is recognised and dropped, as a closed WebSocket delivers nothing.
//   - Opening a socket is the URL: its first frame is ACTION {t: "join", code}, and a
//     socket that also asks for a new room says so as the HTTP POST did, with the
//     room's mode (and a challenge's seed). server/authority.js reads it.
//   - Only the authority is listened to: a client drops any "ds/" message that did
//     not come from Network.hostId, since any peer can address any other.
//   - On the machine that is the authority -- a listen host, a solo run -- a socket
//     to itself never touches the network: both ends are in this script, and a frame
//     crosses at the next frame, as it would over loopback. (A message a listen server
//     addresses to itself is heard by nobody.)
//   - Everything a socket does happens later than the call, as it does for a
//     WebSocket: open and close are heard on the next pump, never inside send() or
//     close(), so no handler runs in the middle of the code that caused it.
//
// Couch seats are several sockets from one machine, one per seat, each joining with its
// own HELLO and getting its own pilot -- what the original did with a WebSocket per pad.
// Which pad drives which seat is the client's to decide; the wire carries up to a room's
// worth of sockets from one machine.
// -----------------------------------------------------------------------------

import { MSG, encodeJson } from "../shared/protocol.js";
import { encodeBase64, decodeBase64 } from "./base64.js";

/** WebSocket's readyState values. */
export const READY = { CONNECTING: 0, OPEN: 1, CLOSING: 2, CLOSED: 3 };

/** The most sockets one machine may hold open to an authority: a full room's worth of seats. */
export const MAX_SOCKETS_PER_MACHINE = 8;

const PREFIX = "ds/";
const CLOSE_SUFFIX = "/close";
const SOCKET_NUMBER = /^[1-9][0-9]{0,8}$/;

/** The script message type a socket's frames cross as. */
export function frameType(sid) {
  return `${PREFIX}${sid}`;
}

/** The script message type that says a socket closed. */
export function closeType(sid) {
  return `${PREFIX}${sid}${CLOSE_SUFFIX}`;
}

/** `{ sid, close }` for a message type the wire uses, or null for any other. */
export function parseType(type) {
  if (typeof type !== "string" || !type.startsWith(PREFIX)) return null;
  let rest = type.slice(PREFIX.length);
  const close = rest.endsWith(CLOSE_SUFFIX);
  if (close) rest = rest.slice(0, rest.length - CLOSE_SUFFIX.length);
  return SOCKET_NUMBER.test(rest) ? { sid: Number(rest), close } : null;
}

/** A frame's bytes as a new Uint8Array of their own: what arrives is never what was sent. */
function copyBytes(bytes) {
  return new Uint8Array(bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes));
}

// ---- the authority's end ------------------------------------------------------------------

/**
 * A client's socket, as the authority holds it: `send`, `close` and `readyState`, the part of a
 * WebSocket room.js used. `sender` is the client id the engine's server stamped on its frames.
 */
export class Connection {
  constructor(wire, sender, sid, socket) {
    this.wire = wire;
    this.sender = sender;
    this.sid = sid;
    this.socket = socket;           // the client end, when it is in this script
    this.local = socket !== null;
    this.readyState = READY.OPEN;
  }

  /** Sends one frame to the client. A socket that is not open sends nothing, as ws did. */
  send(bytes) {
    if (this.readyState !== READY.OPEN) return;
    if (this.local) {
      const data = copyBytes(bytes).buffer;
      const socket = this.socket;
      this.wire.queue.push(() => {
        if (socket.readyState === READY.OPEN && socket.onmessage) socket.onmessage({ data });
      });
      return;
    }
    this.wire.network.sendTo(this.sender, frameType(this.sid), encodeBase64(bytes));
  }

  /** Closes the socket from the authority's end; the client and the authority hear it at the next pump. */
  close() {
    if (this.readyState !== READY.OPEN) return;
    this.readyState = READY.CLOSING;
    const wire = this.wire;
    wire.forget(this);
    const server = wire.server;
    wire.queue.push(() => {
      this.readyState = READY.CLOSED;
      if (this.local) wire.closeSocket(this.socket);
      else wire.network.sendTo(this.sender, closeType(this.sid), null);
      if (server !== null && server === wire.server) server.closed(this);
    });
  }
}

// ---- a client's end -----------------------------------------------------------------------

/** A socket to the session's authority, shaped as the WebSocket the original client opened. */
export class Socket {
  constructor(wire, sid) {
    this.wire = wire;
    this.sid = sid;
    this.readyState = READY.CONNECTING;
    this.binaryType = "arraybuffer";
    this.onopen = null;
    this.onmessage = null;
    this.onclose = null;
    this.onerror = null;
    this.hostId = -1;               // the authority it opened to
    this.connection = null;         // the authority's end, when the authority is this script
  }

  /** Sends one frame to the authority. A socket that is not open sends nothing. */
  send(bytes) {
    if (this.readyState !== READY.OPEN) return;
    const wire = this.wire;
    if (this.connection !== null) {
      const connection = this.connection;
      const data = copyBytes(bytes);
      wire.queue.push(() => {
        if (connection.readyState === READY.OPEN && wire.server !== null) wire.server.frame(connection, data);
      });
      return;
    }
    wire.network.sendTo(this.hostId, frameType(this.sid), encodeBase64(bytes));
  }

  /** Closes the socket from this end; `onclose` runs at the next pump. */
  close() {
    const wire = this.wire;
    if (this.readyState === READY.CLOSING || this.readyState === READY.CLOSED) return;
    const wasOpen = this.readyState === READY.OPEN;
    this.readyState = READY.CLOSING;
    wire.sockets.delete(this.sid);
    if (wasOpen && this.connection !== null) {
      const connection = this.connection;
      const server = wire.server;
      if (connection.readyState === READY.OPEN) {
        connection.readyState = READY.CLOSED;
        wire.forget(connection);
        wire.queue.push(() => { if (server !== null && server === wire.server) server.closed(connection); });
      }
    } else if (wasOpen) {
      wire.network.sendTo(this.hostId, closeType(this.sid), null);
    }
    wire.queue.push(() => wire.closeSocket(this));
  }
}

// ---- the wire -----------------------------------------------------------------------------

/** Both ends of every socket this script has, over the engine's `Network`. */
export class Wire {
  /** @param {object} network The engine's Network global, or anything with its members. */
  constructor(network) {
    this.network = network;
    this.server = null;               // the authority, while this machine is it
    this.connections = new Map();     // `${sender}/${sid}` -> Connection
    this.lastSid = new Map();         // sender -> the highest socket number it has used
    this.openCount = new Map();       // sender -> its open connections
    this.sockets = new Map();         // sid -> this machine's own Socket
    this.nextSid = 1;
    this.queue = [];                  // what the next pump runs, in order
  }

  // ---- the authority --------------------------------------------------------------------

  /**
   * Makes this machine the authority: every socket's frames go to `server.frame(connection, bytes)`
   * (bytes null for a frame that was not base64) and every close to `server.closed(connection)`.
   */
  serve(server) {
    this.server = server;
  }

  /** Stops being the authority: every socket to it is gone, and the sockets of this script hear it. */
  stopServing() {
    for (const connection of this.connections.values()) {
      connection.readyState = READY.CLOSED;
      if (connection.local) this.queue.push(() => this.closeSocket(connection.socket));
    }
    this.connections.clear();
    this.lastSid.clear();
    this.openCount.clear();
    this.server = null;
  }

  /** Takes a connection out of the authority's table; its socket number stays used. */
  forget(connection) {
    const key = `${connection.sender}/${connection.sid}`;
    if (this.connections.get(key) !== connection) return;
    this.connections.delete(key);
    this.openCount.set(connection.sender, (this.openCount.get(connection.sender) ?? 1) - 1);
  }

  // ---- a client -------------------------------------------------------------------------

  /**
   * Opens a socket to the room `code` on this session's authority: /ws?room=CODE. With `mode`, the
   * socket asks for the room to be made if there is none ("run", "daily", or "challenge" with a
   * `seed`), as POST /api/rooms did. The socket opens at the next pump, or closes if there is no
   * session to open it on.
   */
  openSocket(code, options = {}) {
    const socket = new Socket(this, this.nextSid++);
    this.sockets.set(socket.sid, socket);
    const join = { t: "join", code: String(code ?? "") };
    if (options.mode !== undefined) join.mode = options.mode;
    if (options.seed !== undefined) join.seed = options.seed;
    const frame = encodeJson(MSG.ACTION, join);
    this.queue.push(() => this.open(socket, frame));
    return socket;
  }

  open(socket, joinFrame) {
    if (socket.readyState !== READY.CONNECTING) return;
    if (this.server !== null) {
      const connection = new Connection(this, this.network.localId, socket.sid, socket);
      this.connections.set(`${connection.sender}/${connection.sid}`, connection);
      this.openCount.set(connection.sender, (this.openCount.get(connection.sender) ?? 0) + 1);
      socket.connection = connection;
      socket.readyState = READY.OPEN;
      this.server.frame(connection, joinFrame);
    } else if (this.network.isConnected && this.network.hostId >= 0) {
      socket.hostId = this.network.hostId;
      socket.readyState = READY.OPEN;
      this.network.sendTo(socket.hostId, frameType(socket.sid), encodeBase64(joinFrame));
    } else {
      this.sockets.delete(socket.sid);
      socket.readyState = READY.CLOSED;
      if (socket.onclose) socket.onclose({ code: 1006, reason: "no session" });
      return;
    }
    if (socket.readyState === READY.OPEN && socket.onopen) socket.onopen({});
  }

  /** Marks a socket closed and tells its handler, once. */
  closeSocket(socket) {
    this.sockets.delete(socket.sid);
    if (socket.readyState === READY.CLOSED) return;
    socket.readyState = READY.CLOSED;
    if (socket.onclose) socket.onclose({ code: 1000, reason: "" });
  }

  // ---- the engine -----------------------------------------------------------------------

  /** onNetworkMessage: true when the message was the wire's. */
  receive(type, data, sender) {
    const parsed = parseType(type);
    if (parsed === null) return false;
    if (this.server !== null) this.receiveAsAuthority(parsed, data, sender);
    else this.receiveAsClient(parsed, data, sender);
    return true;
  }

  receiveAsAuthority({ sid, close }, data, sender) {
    if (sender === this.network.localId) return;   // this machine's own sockets never use the network
    const key = `${sender}/${sid}`;
    let connection = this.connections.get(key);
    if (close) {
      if (connection === undefined) {
        if (sid > (this.lastSid.get(sender) ?? 0)) this.lastSid.set(sender, sid);
        return;
      }
      connection.readyState = READY.CLOSED;
      this.forget(connection);
      this.server.closed(connection);
      return;
    }
    if (connection === undefined) {
      // A number at or below the highest this machine has used is a socket already closed.
      if (sid <= (this.lastSid.get(sender) ?? 0)) return;
      if ((this.openCount.get(sender) ?? 0) >= MAX_SOCKETS_PER_MACHINE) return;
      this.lastSid.set(sender, sid);
      connection = new Connection(this, sender, sid, null);
      this.connections.set(key, connection);
      this.openCount.set(sender, (this.openCount.get(sender) ?? 0) + 1);
    }
    this.server.frame(connection, decodeBase64(data));
  }

  receiveAsClient({ sid, close }, data, sender) {
    if (sender !== this.network.hostId) return;    // only the authority speaks for a socket
    const socket = this.sockets.get(sid);
    if (socket === undefined || socket.connection !== null || socket.hostId !== sender) return;
    if (socket.readyState !== READY.OPEN) return;
    if (close) {
      this.closeSocket(socket);
      return;
    }
    const bytes = decodeBase64(data);
    if (bytes !== null && socket.onmessage) socket.onmessage({ data: bytes.buffer });
  }

  /**
   * onUpdate, before anything else: sockets whose session ended or whose authority changed close,
   * a player who left the session takes their sockets with them, and everything queued since the
   * last pump runs, in order. What that queues waits for the next pump.
   */
  pump() {
    const network = this.network;
    for (const socket of [...this.sockets.values()]) {
      if (socket.readyState !== READY.OPEN || socket.connection !== null) continue;
      if (!network.isConnected || network.hostId !== socket.hostId) this.closeSocket(socket);
    }

    if (this.server !== null && this.connections.size + this.lastSid.size > 0) {
      const present = new Set();
      for (const player of network.players) present.add(player.id);
      for (const connection of [...this.connections.values()]) {
        if (connection.local || present.has(connection.sender)) continue;
        connection.readyState = READY.CLOSED;
        this.forget(connection);
        this.server.closed(connection);
      }
      for (const sender of [...this.lastSid.keys()]) {
        if (!present.has(sender)) {
          this.lastSid.delete(sender);
          this.openCount.delete(sender);
        }
      }
    }

    const work = this.queue;
    this.queue = [];
    for (const job of work) job();
  }
}
