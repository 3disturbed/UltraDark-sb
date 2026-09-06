// PlayerShooter.js — Twin-stick shooter player controller
// Attach this script to the Player actor in the Arena scene.
//
// Controls:
//   W / A / S / D  = Move up / left / down / right
//   Mouse          = Aim (player rotates to face the cursor)
//   Left Click     = Shoot a bullet in the aim direction
//
// The player uses a Rigidbody2D with zero gravity for top-down movement.
// Bullets are spawned as new actors with the Bullet.js script attached.

// =============================================================================
// Tuning variables
// =============================================================================

// -- Movement -----------------------------------------------------------------

// Movement speed in pixels per second. Applied directly to Rigidbody2D velocity
// so the physics system handles wall collisions automatically.
var moveSpeed = 250;

// -- Shooting -----------------------------------------------------------------

// Minimum time between shots in seconds. Prevents the player from firing a
// continuous stream by holding down the mouse button too quickly.
var shootCooldown = 0.15;

// Countdown timer for the next allowed shot. Decremented each frame.
var shootTimer = 0;

// Tracks whether the player has fired at least once, used to log "Pew!" only
// on the first shot to avoid spamming the console.
var hasShot = false;

// -- Health -------------------------------------------------------------------

// Current health. Reduced by enemy collisions. At zero the player dies.
var health = 100;

// Maximum health. Used for display and clamping.
var maxHealth = 100;

// Whether the player is still alive. When false, all input is ignored.
var isAlive = true;

// -- Arena bounds -------------------------------------------------------------
// These match the wall positions in Arena.scene. The player is clamped inside
// these bounds as a safety net in case physics tunneling occurs.
var arenaLeft = 25;
var arenaRight = 1255;
var arenaTop = 25;
var arenaBottom = 695;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    log("=== Twin-Stick Shooter ===");
    log("  WASD        : Move");
    log("  Mouse       : Aim");
    log("  Left Click  : Shoot");
    log("==========================");
}

function onUpdate(dt) {
    // Skip all logic if the player has been destroyed
    if (!isAlive) return;

    // -- Movement -------------------------------------------------------------
    // Read WASD input and build a velocity vector. The Rigidbody2D handles
    // collision response with the arena walls, so we just set velocity each
    // frame rather than modifying the transform directly.
    var vx = 0;
    var vy = 0;

    if (Input.isKeyHeld("W")) {
        vy = -moveSpeed;
    }
    if (Input.isKeyHeld("S")) {
        vy = moveSpeed;
    }
    if (Input.isKeyHeld("A")) {
        vx = -moveSpeed;
    }
    if (Input.isKeyHeld("D")) {
        vx = moveSpeed;
    }

    // Normalize diagonal movement so the player does not move faster at 45
    // degrees. Without this, holding W+D would give sqrt(2) * moveSpeed.
    if (vx !== 0 && vy !== 0) {
        var invLen = 1.0 / Math.sqrt(vx * vx + vy * vy);
        vx *= invLen * moveSpeed;
        vy *= invLen * moveSpeed;
    }

    // Apply velocity to the Rigidbody2D component
    var rb = actor.getComponent("Rigidbody2D");
    if (rb) {
        rb.velocityX = vx;
        rb.velocityY = vy;
    }

    // -- Aiming ---------------------------------------------------------------
    // Calculate the angle from the player to the mouse cursor. Math.atan2
    // gives us the angle in radians, which we assign directly to the
    // transform rotation. This makes the player sprite point at the mouse.
    var dx = Input.mouseX - actor.transform.x;
    var dy = Input.mouseY - actor.transform.y;
    actor.transform.rotation = Math.atan2(dy, dx);

    // -- Shooting -------------------------------------------------------------
    // Decrement the cooldown timer each frame. When it reaches zero (or below)
    // and the player is holding left mouse, fire a bullet.
    shootTimer -= dt;

    if (Input.isMouseHeld(0) && shootTimer <= 0) {
        shoot();
    }

    // -- Position clamping ----------------------------------------------------
    // Safety net: keep the player inside the arena even if physics tunneling
    // allows them to slip through a wall collider.
    if (actor.transform.x < arenaLeft) actor.transform.x = arenaLeft;
    if (actor.transform.x > arenaRight) actor.transform.x = arenaRight;
    if (actor.transform.y < arenaTop) actor.transform.y = arenaTop;
    if (actor.transform.y > arenaBottom) actor.transform.y = arenaBottom;
}

// =============================================================================
// Shooting
// =============================================================================

// Spawns a bullet actor slightly ahead of the player in the aim direction.
// The bullet receives its own Bullet.js script to handle movement, lifetime,
// and collision with enemies/walls.
function shoot() {
    // Reset the cooldown so we cannot fire again immediately
    shootTimer = shootCooldown;

    // Calculate the aim direction from the current rotation
    var angle = actor.transform.rotation;
    var dirX = Math.cos(angle);
    var dirY = Math.sin(angle);

    // Spawn the bullet slightly ahead of the player (20px offset) so it does
    // not collide with the player's own collider on the first frame.
    var spawnX = actor.transform.x + dirX * 20;
    var spawnY = actor.transform.y + dirY * 20;

    // Create the bullet actor in the scene
    var bullet = Scene.createActor("Bullet");
    if (bullet) {
        bullet.transform.x = spawnX;
        bullet.transform.y = spawnY;
        bullet.transform.rotation = angle;
        bullet.tag = "Bullet";

        // A dynamically created actor is empty; give it a look, a solid body so it
        // collides with enemies and walls, and the script that flies it.
        Scene.addComponent(bullet, "SpriteRenderer", { Tint: "#FFE066" });
        Scene.addComponent(bullet, "Rigidbody2D", { GravityScale: 0, FreezeRotation: true });
        Scene.addComponent(bullet, "BoxCollider2D", { Size: [8, 8] });
        Scene.addComponent(bullet, "ScriptComponent", { ScriptPath: "Scripts/Bullet.js" });
    }

    // Log "Pew!" only on the first shot to confirm shooting works without
    // flooding the console during sustained fire.
    if (!hasShot) {
        log("Pew! Shooting works.");
        hasShot = true;
    }
}

// =============================================================================
// Damage and death
// =============================================================================

// Called when the player takes damage from an enemy collision or projectile.
// Reduces health and triggers death if health reaches zero.
function takeDamage(amount) {
    if (!isAlive) return;

    health -= amount;

    // Clamp health to zero — negative health looks odd in log messages
    if (health < 0) health = 0;

    Debug.warn("Player health: " + health + "/" + maxHealth);

    if (health <= 0) {
        die();
    }
}

// Handles player death. Marks the player as inactive and notifies the
// GameManager so it can transition to the game-over state.
function die() {
    isAlive = false;
    Debug.error("Player destroyed! Game Over!");

    // Notify the GameManager to trigger game-over logic (score display,
    // restart prompt, etc.)
    var manager = Scene.find("GameManager");
    if (manager) {
        manager.gameOver();
    }
}

// =============================================================================
// Collision callbacks
// =============================================================================

// Called by the physics system when the player first touches another collider.
// Enemy collisions deal damage to the player.
function onCollisionEnter(other) {
    if (other.tag === "Enemy") {
        takeDamage(20);
    }
}
