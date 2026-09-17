// WebSocket client — connects, speaks the shared binary protocol, and
// dispatches to callbacks wired up by main.js. No game logic here.
//
// DarkShapes: TwinStickTron's client/js/net.js at 46baa25, ported onto the engine. What
// the client says and hears is the original's, frame for frame, and so are the callbacks
// main.js wires; what moved is where the frames go:
//   - the WebSocket to /ws?room=CODE is a socket on engine/wire.js, which rides the
//     engine's session to whichever machine is its authority;
//   - POST /api/rooms is a session. A run played here is Network.startSolo(); a lobby
//     other pilots join is Network.hostRoom(), a room on the room server this machine
//     serves; and where a `server` launch parameter names DarkShapes' official dedicated
//     server, every room is on it, as every room was on the original's one server. The
//     room itself is made by the socket that joins it, carrying the mode the POST carried;
//   - a code typed, followed or sent by a friend is Network.joinRoom(code), or that server;
//   - nothing can be awaited alike on both engines, so createRoom answers through
//     callbacks, and a socket whose session is still starting waits in pumpNet();
//   - performance.now() and setInterval are the client's clock (engine/clock.js).
// Each change is written down, with why, in html5/tests/fixtures/darkshapes-template/ports.json.

import {
  MSG, encodeJson, decodeJson, decodeSnapshot, decodePong, encodeInput, encodePing,
} from "../shared/protocol.js";
import { MAX_PLAYERS, ROOM_CODE_ALPHABET, ROOM_CODE_LEN } from "../shared/constants.js";

export const net = {
  ws: null,
  connected: false,
  rttMs: 0,
  onWelcome: null, onSnapshot: null, onEvent: null, onClose: null,
  pingTimer: null,
};

// ---------- DarkShapes: the session under the sockets ----------

/** How long a socket waits for its session to come up before it counts as a room that is not there. */
export const SESSION_WAIT_MS = 10000;

const session = {
  wire: null,          // engine/wire.js's Wire: the sockets
  network: null,       // the engine's Network
  clock: null,         // the client clock
  server: "",          // the official dedicated server's address, or "" to play here
  onEnd: () => {},     // told when this module ends a session, so the entry drops its authority
  kind: "",            // the session this module started: "solo", "hosted", "joined", "server", or ""
  code: "",            // the room a "joined" session joined
  up: false,           // whether that session has been up since it started
  opening: null,       // { mode, done, failed } while the room server opens a lobby
  created: null,       // { code, mode, seed }: what the next connect() to `code` asks to have made
  waiting: null,       // { ws, code, hello, create, since }: the primary socket, before its session is up
  roomEvents: false,   // whether the room server's events are listened to yet
};

/**
 * DarkShapes: hands this module what it stands on, once, at start: the wire, the engine's Network,
 * the client clock, the official dedicated server's address when there is one, and `onEnd`, called
 * each time this module ends a session so the entry script stops serving the rooms it held.
 */
export function initNet({ wire, network, clock, server = "", onEnd = () => {} }) {
  session.wire = wire;
  session.network = network;
  session.clock = clock;
  session.server = String(server ?? "");
  session.onEnd = onEnd;
}

/**
 * DarkShapes: listens for the room server's answer, once, when a lobby is first asked for. Subscribing
 * to a room event starts a session object to deliver it on, since a room is opened before there is a
 * session; a game that never opens one -- a solo run, a dedicated server, a scene nobody has played
 * yet -- should not start anything at all by loading.
 */
function wireRoomEvents() {
  const network = session.network;
  if (session.roomEvents) return;
  session.roomEvents = true;
  network.on("roomOpened", (room) => {
    const opening = session.opening;
    session.opening = null;
    if (opening === null || session.kind !== "hosted") return;
    const code = String(room?.code ?? "").toUpperCase();
    session.created = { code, mode: opening.mode, seed: undefined };
    opening.done({ code, mode: opening.mode, joinUrl: String(room?.joinUrl ?? "") || linkFor(code) });
  });
  network.on("roomFailed", (reason) => {
    const opening = session.opening;
    session.opening = null;
    if (opening === null) return;
    endSession();
    opening.failed(new Error(String(reason || "create_failed")));
  });
}

/** DarkShapes: the link to share for a room: the room server's, or the official server's page. */
export function linkFor(code) {
  const network = session.network;
  if (network === null) return "";
  if (session.kind === "server") {
    const origin = String(network.roomServer ?? "").replace(/\/+$/, "");
    return origin ? `${origin}/?room=${code}&server=${encodeURIComponent(session.server)}` : "";
  }
  if (session.kind === "hosted" || session.kind === "joined") return String(network.joinLink ?? "");
  return "";
}

/** DarkShapes: whether this room's runs are ranked: on the official dedicated server, and nowhere else. */
export function onOfficialServer() {
  return session.kind === "server";
}

function genCode() {
  let s = "";
  for (let i = 0; i < ROOM_CODE_LEN; i++) {
    s += ROOM_CODE_ALPHABET[Math.floor(Math.random() * ROOM_CODE_ALPHABET.length)];
  }
  return s;
}

/**
 * Forgets the session this module started, if any, and tells the entry. Ending it on purpose also
 * closes it, with the primary socket unheard, as a deliberate leave detaches its handlers; a session
 * that went down by itself has already taken its sockets, whose closes main.js hears and rejoins on.
 */
function endSession(onPurpose = true) {
  const { network } = session;
  const had = session.kind !== "";
  session.kind = "";
  session.code = "";
  session.up = false;
  session.opening = null;
  session.created = null;
  if (onPurpose) {
    const ws = net.ws;
    if (ws !== null && session.waiting === null) {
      ws.onopen = ws.onmessage = ws.onclose = ws.onerror = null;
      try { ws.close(); } catch { /* already gone */ }
    }
    if (network.isConnected) network.disconnect();
  }
  if (had) session.onEnd();
}

/** Starts the session `kind` names, ending any other first; false when it could not be asked for. */
function startSession(kind, code = "") {
  const { network, server } = session;
  endSession();
  session.kind = kind;
  session.code = code;
  if (kind === "solo") network.startSolo();
  else if (kind === "server") network.connect(server);
  else if (kind === "joined" && network.joinRoom(code) === false) {
    session.kind = "";
    session.code = "";
    return false;
  }
  return true;
}

/** Whether a socket to the room `code` belongs on the session running, or on its way up. */
function sessionFits(code, create) {
  if (session.server) return session.kind === "server";
  if (create !== null) return session.kind === "solo" || session.kind === "hosted";
  if (session.kind === "joined") return session.code === code;
  return (session.kind === "solo" || session.kind === "hosted") && session.up;
}

/** Whether the session is up far enough for a socket to open on it. */
function sessionReady() {
  const { network, wire } = session;
  if (session.kind === "solo" || session.kind === "hosted") return wire.server !== null;
  return wire.server === null && network.isConnected && network.hostId >= 0;
}

/**
 * DarkShapes: once a frame, before the flow reads the network. A session this module started is
 * watched: once up, a session that goes down is over. A primary socket waiting on its session opens
 * once the session is up; one whose session never comes, or goes, hears what the original's server
 * said about a room it did not have, and closes.
 */
export function pumpNet() {
  if (session.kind !== "" && session.opening === null) {
    if (sessionReady()) session.up = true;
    else if (session.up) endSession(false);
  }
  const w = session.waiting;
  if (w === null) return;
  if (net.ws !== w.ws) { session.waiting = null; return; }
  if (session.kind !== "" && session.up) {
    session.waiting = null;
    open(w.code, w.hello, w.create);
    return;
  }
  if (session.kind !== "" && session.clock.now() - w.since < SESSION_WAIT_MS) return;
  session.waiting = null;
  net.ws = null;
  net.connected = false;
  endSession();
  net.onEvent?.({ t: "error", error: "no_such_room" });
  net.onClose?.();
}

/**
 * POST /api/rooms. DarkShapes: a request whose answer comes later, as done({code, mode, joinUrl}) or
 * failed(error), never inside this call. `extra.seed` pins a challenge's waves; `extra.hosted` asks
 * for a lobby other pilots can join. With an official server, every room is made there.
 */
export function createRoom(mode = "run", extra = {}, done = () => {}, failed = () => {}) {
  const { clock } = session;
  if (session.server) {
    if (session.kind !== "server") startSession("server");
  } else if (extra.hosted) {
    wireRoomEvents();
    startSession("hosted");
    const opening = { mode, done, failed };
    session.opening = opening;
    if (session.network.hostRoom({ mode, maxPlayers: MAX_PLAYERS }) === false) {
      clock.setTimeout(() => {
        if (session.opening !== opening) return;
        endSession();
        failed(new Error("create_failed"));
      }, 0);
    }
    return;
  } else {
    startSession("solo");
  }
  const code = genCode();
  session.created = { code, mode, seed: mode === "challenge" ? extra.seed : undefined };
  clock.setTimeout(() => done({ code, mode, joinUrl: linkFor(code) }), 0);
}

export function connect(code, hello) {
  code = String(code ?? "").toUpperCase();
  const create = session.created !== null && session.created.code === code ? session.created : null;
  session.created = null;
  if (!sessionFits(code, create)) startSession(session.server ? "server" : "joined", code);
  const ws = { readyState: 0, onopen: null, onmessage: null, onclose: null, onerror: null, send() {}, close() {} };
  net.ws = ws; // DarkShapes: not open yet; pumpNet opens it once its session is up
  session.waiting = { ws, code, hello, create, since: session.clock.now() };
}

function open(code, hello, create) {
  const { wire, clock } = session;
  const options = create === null ? {} : create.mode === "challenge" ? { mode: create.mode, seed: create.seed } : { mode: create.mode };
  const ws = wire.openSocket(code, options);
  ws.binaryType = "arraybuffer";
  net.ws = ws;

  ws.onopen = () => {
    net.connected = true;
    ws.send(encodeJson(MSG.HELLO, hello));
    const ping = () => {
      net.pingTimer = clock.setTimeout(ping, 2000);
      if (ws.readyState === 1) ws.send(encodePing(clock.now()));
    };
    net.pingTimer = clock.setTimeout(ping, 2000);
  };
  ws.onmessage = (msg) => {
    const buf = msg.data;
    if (typeof buf === "string") return;
    const bytes = new Uint8Array(buf);
    const type = bytes[0];
    if (type === MSG.SNAPSHOT) {
      net.onSnapshot?.(decodeSnapshot(new DataView(buf)));
    } else if (type === MSG.EVENT) {
      net.onEvent?.(decodeJson(bytes.subarray(1)));
    } else if (type === MSG.WELCOME) {
      net.onWelcome?.(decodeJson(bytes.subarray(1)));
    } else if (type === MSG.PONG) {
      const p = decodePong(new DataView(buf));
      net.rttMs = Math.round(clock.now() - p.clientT);
    }
  };
  ws.onclose = () => {
    net.connected = false;
    clock.clearTimeout(net.pingTimer);
    net.onClose?.();
  };
  ws.onerror = () => { /* onclose follows */ };
}

// Leave the current room on purpose (social join into another room). The
// old socket's handlers are detached first so its late `onclose` can never
// flip `net.connected` or clear the ping timer of a socket opened after it,
// and main.js's auto-rejoin in onClose never fires for a deliberate leave.
export function disconnect() {
  const ws = net.ws;
  session.clock.clearTimeout(net.pingTimer);
  net.pingTimer = null;
  net.connected = false;
  net.ws = null;
  session.waiting = null;
  endSession();
  if (!ws) return;
  ws.onopen = ws.onmessage = ws.onclose = ws.onerror = null;
  try { ws.close(); } catch { /* already gone */ }
}

export function sendInput(seq, st) {
  if (net.ws?.readyState === 1) net.ws.send(encodeInput(seq, st.mx, st.my, st.ax, st.ay, st.buttons));
}

export function sendAction(a) {
  if (net.ws?.readyState === 1) net.ws.send(encodeJson(MSG.ACTION, a));
}

// Extra seat for couch co-op: its own socket = its own server seat. The
// primary connection stays the world-state driver; extra seats skip
// snapshot decoding entirely (all players ride the primary's snapshots)
// and only care about WELCOME + their personal events (draft_offer,
// class_grant).
export function connectExtra(code, hello, handlers) {
  const ws = session.wire.openSocket(String(code ?? "").toUpperCase());
  ws.binaryType = "arraybuffer";
  const conn = {
    ws,
    sendInput(seq, st) {
      if (ws.readyState === 1) ws.send(encodeInput(seq, st.mx, st.my, st.ax, st.ay, st.buttons));
    },
    sendAction(a) {
      if (ws.readyState === 1) ws.send(encodeJson(MSG.ACTION, a));
    },
    close() { try { ws.close(); } catch { /* gone */ } },
  };
  ws.onopen = () => ws.send(encodeJson(MSG.HELLO, hello));
  ws.onmessage = (msg) => {
    if (typeof msg.data === "string") return;
    const bytes = new Uint8Array(msg.data);
    const type = bytes[0];
    if (type === MSG.SNAPSHOT || type === MSG.PONG) return; // primary's job
    if (type === MSG.WELCOME) handlers.onWelcome?.(decodeJson(bytes.subarray(1)));
    else if (type === MSG.EVENT) handlers.onEvent?.(decodeJson(bytes.subarray(1)));
  };
  ws.onclose = () => handlers.onClose?.();
  ws.onerror = () => { /* onclose follows */ };
  return conn;
}
