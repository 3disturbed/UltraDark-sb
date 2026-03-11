// ArenaLobby.js — Lobby screen for the Multiplayer Arena template
// Attach to the LobbyManager actor in the ArenaLobby scene.
//
// Controls:
//   H     = Host a server on the configured port
//   J     = Join an existing server
//   Enter = Start the match (host only)
//
// When the host presses Enter, a "startGame" network message is broadcast to
// all connected clients so everyone transitions to the Arena scene together.

// =============================================================================
// Configuration
// =============================================================================

// Whether this client is acting as the server host.
var isHost = false;

// Address and port used for hosting / joining. Localhost by default so you can
// test with multiple instances on the same machine.
var serverAddress = "127.0.0.1";
var serverPort = 9050;

// List of player names that have joined the lobby. Updated via network messages.
var connectedPlayers = [];

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    log("========================================");
    log("     MULTIPLAYER ARENA - LOBBY");
    log("========================================");
    log("  H     : Host a game");
    log("  J     : Join a game");
    log("  Enter : Start match (host only)");
    log("========================================");
}

function onUpdate(dt) {
    // -- Host a game ----------------------------------------------------------
    // Creates a server that other players can connect to. Only one host is
    // needed per match.
    if (Input.isKeyPressed("H")) {
        log("Starting server on port " + serverPort + "...");
        Network.startServer(serverPort);
        isHost = true;
        log("Server started! Waiting for players...");
        log("Others can join at: " + serverAddress + ":" + serverPort);
    }

    // -- Join a game ----------------------------------------------------------
    // Connects to an existing host as a client.
    if (Input.isKeyPressed("J")) {
        log("Connecting to " + serverAddress + ":" + serverPort + "...");
        Network.connect(serverAddress, serverPort);
        log("Connected! Waiting for host to start the match...");
    }

    // -- Start the match (host only) ------------------------------------------
    // The host broadcasts a "startGame" message so all clients load the Arena
    // scene at the same time, then transitions locally as well.
    if (isHost && Input.isKeyPressed("Enter")) {
        log("Starting arena match with " + (connectedPlayers.length + 1) + " player(s)...");
        Network.sendToAll("startGame", {});
        Scene.load("Scenes/Arena");
    }
}

// =============================================================================
// Network callbacks
// =============================================================================

// Called when a network message arrives from another peer or the server.
function onNetworkMessage(type, data) {
    // -- Start game signal from host ------------------------------------------
    if (type === "startGame") {
        log("Host started the match! Loading arena...");
        Scene.load("Scenes/Arena");
    }

    // -- Player joined notification -------------------------------------------
    if (type === "playerJoined") {
        connectedPlayers.push(data.name);
        log("Player joined: " + data.name + " (" + connectedPlayers.length + " player(s) in lobby)");
    }
}
