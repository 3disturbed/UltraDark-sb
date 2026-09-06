// NetworkPlayer.js — Networked player with position replication
// This script is spawned for each connected player

var moveSpeed = 200;
var isLocal = false;
var networkId = -1;

// Called by GameManager.js right after it attaches this script; runs before onStart.
function configure(id) {
    networkId = id;
}

function onStart() {
    // Check if this is the local player
    isLocal = Network.isLocalPlayer(networkId);

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
