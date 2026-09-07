// ArenaPlayer.js — Networked player: movement, shooting, health, pickups
// This script is dynamically attached to player actors spawned by ArenaManager.
//
// Controls (local player only):
//   W / A / S / D  = Move up / left / down / right
//   Mouse          = Aim (player rotates to face the cursor)
//   Left Click     = Shoot a bullet in the aim direction
//
// Remote players receive position updates via network replication and do not
// respond to local input.

// =============================================================================
// Movement
// =============================================================================

// Current movement speed in pixels per second. May be temporarily boosted by
// speed pickups.
var moveSpeed = 250;

// Base speed used to restore moveSpeed after a speed boost expires.
var baseMoveSpeed = 250;

// =============================================================================
// Health
// =============================================================================

// Current and maximum health. Player dies when health reaches zero and respawns
// after a short delay.
var health = 100;
var maxHealth = 100;

// =============================================================================
// Shooting
// =============================================================================

// Minimum time between shots in seconds.
var shootCooldown = 0.3;

// Countdown timer for the next allowed shot.
var shootTimer = 0;

// Damage dealt per bullet. May be temporarily boosted by damage pickups.
var damage = 20;

// Base damage used to restore damage after a boost expires.
var baseDamage = 20;

// Whether the player has fired at least once (used to log first shot only).
var hasShot = false;

// =============================================================================
// Networking
// =============================================================================

// Whether this is the locally controlled player. Remote players skip input
// processing entirely.
var isLocal = true;

// How often a move frame goes out. Twenty a second is smooth and a third of the
// bandwidth of one a tick.
var MOVE_INTERVAL = 1 / 20;
var moveTimer = 0;

// Display name used in kill feed messages.
var playerName = "Player";

// =============================================================================
// State
// =============================================================================

// Whether the player is currently alive. Dead players are invisible and
// uncontrollable until the respawn timer expires.
var isAlive = true;

// Countdown until the player respawns after dying (in seconds).
var respawnTimer = 0;

// Active boost timers. When a boost timer is above zero the corresponding
// stat is enhanced. When it reaches zero the stat reverts to its base value.
var boostTimers = { speed: 0, damage: 0 };

// Arena bounds — matches the wall positions in Arena.scene. Used as a safety
// clamp to prevent physics tunneling.
var arenaLeft = 25;
var arenaRight = 1255;
var arenaTop = 25;
var arenaBottom = 695;

// Spawn points for respawning at a random location.
var spawnPoints = [
    { x: 200, y: 200 },
    { x: 1080, y: 200 },
    { x: 200, y: 520 },
    { x: 1080, y: 520 },
    { x: 640, y: 360 }
];

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // Whether this is the local player or a remote replica. ArenaManager names each
    // actor "Player_<clientId>", so the id is in the name -- an actor carries no
    // ownership of its own, and asking the network about the actor would be asking
    // the wrong question.
    var ownerId = Number(String(actor.name).split("_")[1]);
    isLocal = !Number.isFinite(ownerId) || Network.isLocalPlayer(ownerId);

    // Derive the player name from the actor name assigned by ArenaManager.
    playerName = actor.name || "Player";

    if (isLocal) {
        log("You spawned as " + playerName + ". WASD to move, mouse to aim, click to shoot.");
    }
}

function onUpdate(dt) {
    // -- Death / respawn state ------------------------------------------------
    // While dead, count down the respawn timer. No input is processed.
    if (!isAlive) {
        respawnTimer -= dt;
        if (respawnTimer <= 0) {
            respawn();
        }
        return;
    }

    // -- Remote players -------------------------------------------------------
    // Only the local player reads input. Remote positions are updated via
    // network replication.
    if (!isLocal) return;

    // -- Movement -------------------------------------------------------------
    var vx = 0;
    var vy = 0;

    if (Input.isKeyHeld("W")) vy = -moveSpeed;
    if (Input.isKeyHeld("S")) vy = moveSpeed;
    if (Input.isKeyHeld("A")) vx = -moveSpeed;
    if (Input.isKeyHeld("D")) vx = moveSpeed;

    // Normalize diagonal movement so speed is consistent in all directions.
    if (vx !== 0 && vy !== 0) {
        var invLen = 1.0 / Math.sqrt(vx * vx + vy * vy);
        vx *= invLen * moveSpeed;
        vy *= invLen * moveSpeed;
    }

    // Apply velocity through the Rigidbody2D so wall collisions work.
    var rb = actor.getComponent("Rigidbody2D");
    if (rb) {
        rb.velocityX = vx;
        rb.velocityY = vy;
    }

    // -- Aiming ---------------------------------------------------------------
    // Rotate the player to face the mouse cursor.
    var dx = Input.mouseX - actor.transform.x;
    var dy = Input.mouseY - actor.transform.y;
    actor.transform.rotation = Math.atan2(dy, dx);

    // -- Shooting -------------------------------------------------------------
    shootTimer -= dt;
    if (Input.isMouseHeld(0) && shootTimer <= 0) {
        shoot();
    }

    // -- Boost timers ---------------------------------------------------------
    // Decrement active boosts and revert stats when they expire.
    if (boostTimers.speed > 0) {
        boostTimers.speed -= dt;
        if (boostTimers.speed <= 0) {
            moveSpeed = baseMoveSpeed;
            log("Speed boost expired.");
        }
    }
    if (boostTimers.damage > 0) {
        boostTimers.damage -= dt;
        if (boostTimers.damage <= 0) {
            damage = baseDamage;
            log("Damage boost expired.");
        }
    }

    // -- Position clamping ----------------------------------------------------
    // Safety net to keep the player inside the arena bounds.
    if (actor.transform.x < arenaLeft) actor.transform.x = arenaLeft;
    if (actor.transform.x > arenaRight) actor.transform.x = arenaRight;
    if (actor.transform.y < arenaTop) actor.transform.y = arenaTop;
    if (actor.transform.y > arenaBottom) actor.transform.y = arenaBottom;

    // -- Network replication --------------------------------------------------
    // Twenty times a second, not sixty: a move frame every tick is three times the
    // bandwidth for movement no player can see the difference in, and it is the first
    // thing that makes a lobby feel worse than a local game.
    moveTimer += dt;
    if (Network.isConnected && moveTimer >= MOVE_INTERVAL) {
        moveTimer = 0;
        Network.sendToAll("playerMove", {
            name: playerName,
            x: actor.transform.x,
            y: actor.transform.y,
            rotation: actor.transform.rotation
        });
    }
}

// Receives the other players' movement. `sender` is the client id the server
// assigned -- never the one in the payload, which a peer chooses for itself.
function onNetworkMessage(type, data, sender) {
    if (type !== "playerMove" || !data) return;
    if (data.name === playerName) return;            // our own, relayed back

    var other = Scene.find(data.name);
    if (!other) return;

    other.transform.x = data.x;
    other.transform.y = data.y;
    other.transform.rotation = data.rotation;
}

// =============================================================================
// Shooting
// =============================================================================

// Spawns a bullet actor slightly ahead of the player in the aim direction.
// The bullet uses ArenaBullet.js for movement, lifetime, and collision.
function shoot() {
    shootTimer = shootCooldown;

    var angle = actor.transform.rotation;
    var dirX = Math.cos(angle);
    var dirY = Math.sin(angle);

    // Offset the spawn position so the bullet doesn't collide with the player.
    var spawnX = actor.transform.x + dirX * 22;
    var spawnY = actor.transform.y + dirY * 22;

    var bullet = Scene.createActor("Bullet");
    if (bullet) {
        bullet.tag = "Bullet";
        bullet.transform.x = spawnX;
        bullet.transform.y = spawnY;
        bullet.transform.rotation = angle;

        Scene.addComponent(bullet, "ScriptComponent", {
            ScriptPath: "Scripts/ArenaBullet.js",
            Properties: { damage: damage, ownerName: playerName }
        });
    }

    // Log only the first shot to confirm shooting works without flooding.
    if (!hasShot) {
        log("Pew! Shooting works.");
        hasShot = true;
    }
}

// =============================================================================
// Damage and death
// =============================================================================

// Called when this player is hit by a bullet or other damage source.
// attackerName is used for the kill feed.
function takeDamage(amount, attackerName) {
    if (!isAlive) return;

    health -= amount;
    if (health < 0) health = 0;

    Debug.warn(playerName + " health: " + health + "/" + maxHealth);

    if (health <= 0) {
        die(attackerName);
    }
}

// Handles player death. Notifies ArenaManager for scoreboard updates and
// starts the respawn countdown.
function die(killerName) {
    isAlive = false;
    respawnTimer = 3.0;

    log(playerName + " was eliminated! Respawning in 3 seconds...");

    // Notify the ArenaManager to update kill/death scores.
    var manager = Scene.find("ArenaManager");
    if (manager) {
        if (killerName && manager.onPlayerKill) {
            manager.onPlayerKill(killerName, playerName);
        }
        if (manager.onPlayerDeath) {
            // Not `Network.localId || "local"`: the host's id is 0, and `0 || x` is x,
            // so the host's own deaths were being filed under a player that never existed.
            manager.onPlayerDeath(Network.localId);
        }
    }

    // Hide the player while dead. The actor is kept alive for respawning.
    var sr = actor.getComponent("SpriteRenderer");
    if (sr) sr.Visible = false;
}

// Restores the player to full health at a random spawn point.
function respawn() {
    isAlive = true;
    health = maxHealth;
    damage = baseDamage;
    moveSpeed = baseMoveSpeed;
    boostTimers.speed = 0;
    boostTimers.damage = 0;

    // Move to a random spawn point.
    var sp = spawnPoints[Math.floor(Math.random() * spawnPoints.length)];
    actor.transform.x = sp.x;
    actor.transform.y = sp.y;

    // Show the player again.
    var sr = actor.getComponent("SpriteRenderer");
    if (sr) sr.Visible = true;

    log(playerName + " respawned!");
}

// =============================================================================
// Pickup effects
// =============================================================================

// Called by Pickup.js (via onTriggerEnter) when the player walks over a pickup.
// Applies the appropriate effect based on the pickup type.
function applyPickup(type) {
    if (type === "health") {
        health = Math.min(maxHealth, health + 25);
        log(playerName + " picked up health! (" + health + "/" + maxHealth + ")");
    } else if (type === "speed") {
        moveSpeed = baseMoveSpeed * 1.5;
        boostTimers.speed = 5.0;
        log(playerName + " picked up speed boost! (5s)");
    } else if (type === "damage") {
        damage = baseDamage * 2;
        boostTimers.damage = 5.0;
        log(playerName + " picked up damage boost! (5s)");
    }
}

// =============================================================================
// Collision callbacks
// =============================================================================

// Called by the physics system when the player first touches another collider.
function onCollisionEnter(other) {
    // -- Bullet hit -----------------------------------------------------------
    // Only take damage from bullets that were not fired by this player.
    if (other.tag === "Bullet") {
        var bullet = other.getComponent("ScriptComponent");
        var owner = bullet ? bullet.invoke("getOwnerName") : "";
        if (owner && owner !== playerName) {
            var bulletDamage = (bullet && bullet.invoke("getDamage")) || 20;
            takeDamage(bulletDamage, owner);
        }
    }
}
