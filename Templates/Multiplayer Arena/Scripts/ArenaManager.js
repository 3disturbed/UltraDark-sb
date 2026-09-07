// ArenaManager.js — Manages the networked arena: spawning, pickups, scoring, respawn
// Attach to the ArenaManager actor in the Arena scene.
//
// This script is the central authority for the match. It handles:
//   - Player spawning at random spawn points
//   - Periodic pickup spawning (health, speed, damage boosts)
//   - Kill/death tracking and scoreboard display
//   - Match timer (3 minutes) and end-of-match flow
//
// The ArenaManager communicates with ArenaPlayer.js instances for kill and
// death notifications, and with Pickup.js for pickup consumption events.

// =============================================================================
// Player tracking
// =============================================================================

// Dictionary of active players keyed by clientId. Each entry stores:
//   { name: string, kills: number, deaths: number, actor: Actor }
var players = {};

// =============================================================================
// Spawn points — positions where players and pickups can appear
// =============================================================================

var spawnPoints = [
    { x: 200, y: 200 },
    { x: 1080, y: 200 },
    { x: 200, y: 520 },
    { x: 1080, y: 520 },
    { x: 640, y: 360 }
];

// Pickup spawn positions collected from the scene's PickupSpawn actors.
var pickupSpawns = [];

// =============================================================================
// Pickup configuration
// =============================================================================

// Timer that counts up; when it reaches pickupInterval a new pickup spawns.
var pickupTimer = 0;

// Seconds between pickup spawn attempts.
var pickupInterval = 10.0;

// Current number of active pickups in the arena.
var activePickups = 0;

// Maximum simultaneous pickups allowed. Prevents the arena from becoming
// cluttered with power-ups.
var maxPickups = 3;

// =============================================================================
// Match timing
// =============================================================================

// Periodic scoreboard logging interval and countdown.
var scoreLogTimer = 0;

// Elapsed match time in seconds.
var gameTime = 0;

// Total match duration in seconds (3 minutes).
var maxGameTime = 180;

// Whether the match has ended. When true, gameplay stops and players can
// press R to return to the lobby.
var gameOver = false;

// =============================================================================
// Player color palette — each player gets a distinct color based on their id
// =============================================================================

var playerColors = [
    { R: 50, G: 130, B: 240, A: 255 },   // Blue
    { R: 230, G: 60, B: 60, A: 255 },     // Red
    { R: 50, G: 200, B: 80, A: 255 },     // Green
    { R: 230, G: 180, B: 30, A: 255 },    // Yellow
    { R: 180, G: 60, B: 220, A: 255 }     // Purple
];

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // Collect pickup spawn positions from the scene. These are empty marker
    // actors placed in the Arena.scene file with the "PickupSpawn" tag.
    var spawns = Scene.findAll("PickupSpawn");
    if (spawns && spawns.length > 0) {
        for (var i = 0; i < spawns.length; i++) {
            pickupSpawns.push({
                x: spawns[i].transform.x,
                y: spawns[i].transform.y
            });
        }
    } else {
        // Fallback: use the hardcoded spawn points if scene markers are missing.
        pickupSpawns = spawnPoints.slice();
    }

    // Spawn the local player at a random position.
    // Not `Network.localId || "local"`: the host's id is 0, and `0 || x` is x.
    var localId = Network.localId;
    spawnPlayer(localId);

    log("========================================");
    log("  Arena match started!");
    log("  3 minutes on the clock.");
    log("  Eliminate opponents. Collect pickups.");
    log("========================================");
}

function onUpdate(dt) {
    // -- Game over state ------------------------------------------------------
    // After the match ends, the only accepted input is R to restart.
    if (gameOver) {
        if (Input.isKeyPressed("R")) {
            Scene.load("Scenes/ArenaLobby");
        }
        return;
    }

    // -- Match timer ----------------------------------------------------------
    gameTime += dt;
    if (gameTime >= maxGameTime) {
        endMatch();
        return;
    }

    // -- Pickup spawning ------------------------------------------------------
    // Increment the pickup timer and attempt to spawn a pickup when the
    // interval elapses, as long as we haven't hit the maximum.
    pickupTimer += dt;
    if (pickupTimer >= pickupInterval && activePickups < maxPickups) {
        pickupTimer = 0;
        spawnPickup();
    }

    // -- Periodic scoreboard --------------------------------------------------
    // Log the scoreboard every 15 seconds so players can track standings.
    scoreLogTimer += dt;
    if (scoreLogTimer >= 15.0) {
        scoreLogTimer = 0;
        var remaining = Math.floor(maxGameTime - gameTime);
        log("[" + remaining + "s remaining]");
        logScoreboard();
    }
}

// =============================================================================
// Player spawning
// =============================================================================

// Creates a player actor at a random spawn point with physics, rendering, and
// the ArenaPlayer.js control script attached.
function spawnPlayer(clientId) {
    var sp = spawnPoints[Math.floor(Math.random() * spawnPoints.length)];
    var name = "Player_" + clientId;

    var player = Scene.createActor(name);
    if (!player) return;

    player.tag = "Player";
    player.transform.x = sp.x;
    player.transform.y = sp.y;

    // Pick a color based on the number of existing players.
    var colorIndex = Object.keys(players).length % playerColors.length;

    Scene.addComponent(player, "SpriteRenderer", {
        Color: playerColors[colorIndex]
    });
    Scene.addComponent(player, "Rigidbody2D", { GravityScale: 0 });
    Scene.addComponent(player, "BoxCollider2D", { Width: 28, Height: 28 });
    Scene.addComponent(player, "ScriptComponent", {
        ScriptPath: "Scripts/ArenaPlayer.js"
    });

    // Store the player in the tracking dictionary.
    players[clientId] = {
        name: name,
        kills: 0,
        deaths: 0,
        actor: player
    };

    log("Spawned " + name + " at (" + sp.x + ", " + sp.y + ")");
}

// =============================================================================
// Pickup spawning
// =============================================================================

// Spawns a random pickup (health, speed, or damage) at one of the pickup
// spawn positions. The pickup type determines its color and effect.
function spawnPickup() {
    if (pickupSpawns.length === 0) return;

    // Pick a random spawn position.
    var sp = pickupSpawns[Math.floor(Math.random() * pickupSpawns.length)];

    // Randomly choose a pickup type.
    var types = ["health", "speed", "damage"];
    var type = types[Math.floor(Math.random() * types.length)];

    // Determine color based on pickup type:
    //   health = green, speed = yellow, damage = red
    var color;
    if (type === "health") {
        color = { R: 0, G: 220, B: 80, A: 255 };
    } else if (type === "speed") {
        color = { R: 240, G: 220, B: 40, A: 255 };
    } else {
        color = { R: 220, G: 40, B: 40, A: 255 };
    }

    var pickup = Scene.createActor("Pickup_" + type);
    if (!pickup) return;

    pickup.tag = "Pickup";
    pickup.transform.x = sp.x;
    pickup.transform.y = sp.y;

    Scene.addComponent(pickup, "SpriteRenderer", { Color: color });
    Scene.addComponent(pickup, "BoxCollider2D", {
        Width: 20,
        Height: 20,
        IsTrigger: true
    });
    Scene.addComponent(pickup, "ScriptComponent", {
        ScriptPath: "Scripts/Pickup.js",
        Properties: { pickupType: type }
    });

    activePickups++;
    log("Pickup spawned: " + type + " at (" + sp.x + ", " + sp.y + ")");
}

// =============================================================================
// Kill and death tracking
// =============================================================================

// Called by ArenaPlayer.js when one player eliminates another.
// Updates the scoreboard and logs the event.
function onPlayerKill(killerName, victimName) {
    // Find and update the killer's score.
    for (var id in players) {
        if (players[id].name === killerName) {
            players[id].kills++;
            break;
        }
    }

    log(killerName + " eliminated " + victimName + "!");
    logScoreboard();
}

// Called by ArenaPlayer.js when a player dies. Increments their death count.
// The actual respawn timer is managed inside the player script.
function onPlayerDeath(clientId) {
    if (players[clientId]) {
        players[clientId].deaths++;
    }
}

// Called by Pickup.js when a pickup is collected. Decrements the active count
// so new pickups can spawn.
function onPickupCollected() {
    activePickups--;
    if (activePickups < 0) activePickups = 0;
}

// =============================================================================
// Scoreboard
// =============================================================================

// Logs a formatted table of all players with their kill/death stats.
function logScoreboard() {
    log("--- Scoreboard ---");
    for (var id in players) {
        var p = players[id];
        log("  " + p.name + "  K:" + p.kills + " / D:" + p.deaths);
    }
    log("------------------");
}

// =============================================================================
// Match end
// =============================================================================

// Ends the match: determines the winner, logs the final scoreboard, and
// prompts players to return to the lobby.
function endMatch() {
    gameOver = true;

    log("========================================");
    log("         TIME'S UP! MATCH OVER");
    log("========================================");

    // Determine the winner by highest kill count.
    var winnerName = "";
    var winnerKills = -1;
    for (var id in players) {
        if (players[id].kills > winnerKills) {
            winnerKills = players[id].kills;
            winnerName = players[id].name;
        }
    }

    logScoreboard();

    if (winnerName !== "") {
        log("Winner: " + winnerName + " with " + winnerKills + " kill(s)!");
    } else {
        log("No kills recorded. It's a draw!");
    }

    log("Press R to return to lobby.");
}

// =============================================================================
// Network callbacks
// =============================================================================

// Handles incoming network messages for player spawn/despawn and hit events.
function onNetworkMessage(type, data) {
    // -- Remote player spawn --------------------------------------------------
    if (type === "playerSpawn") {
        spawnPlayer(data.id);
    }

    // -- Remote player disconnect ---------------------------------------------
    if (type === "playerDespawn") {
        if (players[data.id]) {
            if (players[data.id].actor) {
                players[data.id].actor.destroy();
            }
            delete players[data.id];
            log("Player disconnected: Player_" + data.id);
        }
    }

    // -- Hit notification (used for remote damage) ----------------------------
    if (type === "playerHit") {
        var target = players[data.targetId];
        var hitScript = target && target.actor ? target.actor.getComponent("ScriptComponent") : null;
        if (hitScript) hitScript.invoke("takeDamage", data.damage, data.attackerName);
    }
}
