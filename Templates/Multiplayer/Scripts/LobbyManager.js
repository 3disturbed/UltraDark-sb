// LobbyManager.js — Handles lobby UI and host/join logic
// Attach to a manager actor in the Lobby scene

var isHost = false;
var serverAddress = "127.0.0.1";
var serverPort = 9050;
var playerName = "Player";
var connectedPlayers = [];

function onStart() {
    log("=== Multiplayer Lobby ===");
    log("Press H to Host a game");
    log("Press J to Join a game");
    log("Press Enter to start (host only)");
}

function onUpdate(dt) {
    // Host a game
    if (Input.isKeyPressed("H")) {
        log("Starting server on port " + serverPort + "...");
        Network.startServer(serverPort);
        isHost = true;
        log("Server started! Waiting for players...");
        log("Others can join at: " + serverAddress + ":" + serverPort);
    }

    // Join a game
    if (Input.isKeyPressed("J")) {
        log("Connecting to " + serverAddress + ":" + serverPort + "...");
        Network.connect(serverAddress, serverPort);
        log("Connected! Waiting for host to start...");
    }

    // Start game (host only)
    if (isHost && Input.isKeyPressed("Enter")) {
        log("Starting game...");
        // Send start signal to all clients
        Network.sendToAll("startGame", {});
        Scene.load("Scenes/Game");
    }
}

function onNetworkMessage(type, data) {
    if (type === "startGame") {
        Scene.load("Scenes/Game");
    }
    if (type === "playerJoined") {
        connectedPlayers.push(data.name);
        log("Player joined: " + data.name + " (" + connectedPlayers.length + " players)");
    }
}
