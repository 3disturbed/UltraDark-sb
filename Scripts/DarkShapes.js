// -----------------------------------------------------------------------------
// DarkShapes.js — the one script the scene runs.
//
// DarkShapes is an exact 2D port of UltraDark, the neon co-op twin-stick wave shooter
// (github.com/3disturbed/TwinStickTron, pinned at 46baa25), and this project is the engine's
// TwinStick2D template. The game lives in modules beside this file; this script holds only the
// engine's hooks and hands each one to them.
//
//   shared/   the original's shared/ folder, statement for statement: both ends of the wire
//   server/   the original's simulation and wave recipes, its rooms, and its scores table,
//             run by the authority
//   client/   the original's browser client: the flow (main.js), the world model, the
//             renderer on Draw, the screens on UI, input, sound on the synth, and the
//             connection on the engine's sessions
//   engine/   the seams the original stood on: a clock, base64, sockets over Network, and
//             the glyphs the engine's faces draw
//
// Wherever a file differs from the original, the change is marked "DarkShapes" where it is
// made, and html5/tests/fixtures/darkshapes-template records each one with its reason.
//
// Two things run here, as the original's server and its page did:
//   - whichever machine is the session's authority (Network.isHost) serves the rooms: one on a
//     listen host or a solo run, many on a dedicated server;
//   - every machine but a dedicated server plays: the client starts with the script, and is
//     handed each frame. A dedicated server has no screen, no pilot and no input.
// An idle scene -- no session, nobody at the menu -- serves nothing and starts no run, so the
// template's sixty-frame smoke tick on both engines stays a smoke tick.
// -----------------------------------------------------------------------------

import { Wire } from "./engine/wire.js";
import { Authority } from "./server/authority.js";
import { startDarkShapes, updateDarkShapes, stopDarkShapes } from "./client/main.js";

const wire = new Wire(Network);
let authority = null;

function onStart() {
    if (Network.isServer && Network.authority === "dedicated") return;
    // A `server` launch parameter (?server=wss://… in a page, --param server=wss://… natively) puts
    // every room on DarkShapes' official dedicated server; without one, rooms are played or hosted here.
    startDarkShapes({
        ui: UI, input: Input, draw: Draw, audio: Audio, network: Network, dg: DG, prefs: Prefs,
        launch: GameInstance.launch, wire, onSessionEnd: stopServing,
    });
}

function onUpdate(dt) {
    if (Network.isHost && authority === null) {
        authority = new Authority({ network: Network, dedicated: Network.authority === "dedicated", dg: DG, warn });
        wire.serve(authority);
    } else if (!Network.isHost && authority !== null) {
        stopServing();
    }
    wire.pump();
    if (authority !== null) authority.update(Time.unscaledDeltaTime);
    updateDarkShapes(Time.unscaledDeltaTime);
}

/** Stops serving rooms: the session under them ended, or the client ended it to start another. */
function stopServing() {
    if (authority === null) return;
    wire.stopServing();
    authority = null;
}

function onNetworkMessage(type, data, sender) {
    wire.receive(type, data, sender);
}

function onDestroy() {
    stopDarkShapes();
    stopServing();
}
