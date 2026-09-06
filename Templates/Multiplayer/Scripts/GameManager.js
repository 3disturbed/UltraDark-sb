// GameManager.js — Manages the networked game session
// Attach to a manager actor in the Game scene

var players = {};

function onStart() {
    log("Game started!");

    // Spawn local player
    spawnPlayer(Network.localId, "Player_" + Network.localId);
}

function spawnPlayer(networkId, name) {
    var player = Scene.createActor(name);
    player.transform.x = 400 + Math.random() * 200;
    player.transform.y = 300 + Math.random() * 200;
    player.tag = "Player";

    // A created actor is empty: a sprite so it can be seen, and the player script,
    // told which network id it is before its onStart runs.
    Scene.addComponent(player, "SpriteRenderer", { Tint: networkId === Network.localId ? "#66CCFF" : "#FF9966" });
    var script = Scene.addComponent(player, "ScriptComponent", { ScriptPath: "Scripts/NetworkPlayer.js" });
    if (script) script.invoke("configure", networkId);
    log("Spawned player: " + name + " at (" +
        Math.floor(player.transform.x) + ", " +
        Math.floor(player.transform.y) + ")");
    players[networkId] = player;
}

function onNetworkMessage(type, data) {
    if (type === "playerSpawn") {
        spawnPlayer(data.id, data.name);
    }
    if (type === "playerDespawn") {
        if (players[data.id]) {
            players[data.id].destroy();
            delete players[data.id];
        }
    }
}

function onDestroy() {
    log("Game session ended.");
}
