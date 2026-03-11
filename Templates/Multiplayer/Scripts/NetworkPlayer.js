// NetworkPlayer.js — Networked player with position replication
// This script is spawned for each connected player

var moveSpeed = 200;
var isLocal = false;

function onStart() {
    // Check if this is the local player
    isLocal = Network.isLocalPlayer(actor);

    if (isLocal) {
        log("Local player spawned. Use WASD to move.");
    } else {
        log("Remote player connected: " + actor.name);
    }
}

function onUpdate(dt) {
    if (!isLocal) return; // Only control local player

    var vx = 0;
    var vy = 0;

    if (Input.isKeyHeld("W")) vy = -moveSpeed;
    if (Input.isKeyHeld("S")) vy = moveSpeed;
    if (Input.isKeyHeld("A")) vx = -moveSpeed;
    if (Input.isKeyHeld("D")) vx = moveSpeed;

    actor.transform.x += vx * dt;
    actor.transform.y += vy * dt;

    // Position is automatically replicated via [Replicated] attribute
    // on the NetworkObject component
}

function onDestroy() {
    log("Player disconnected: " + actor.name);
}
