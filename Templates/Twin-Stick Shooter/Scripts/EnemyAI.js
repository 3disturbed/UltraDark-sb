// EnemyAI.js — Simple enemy behavior for the twin-stick shooter
// Attached to dynamically spawned enemy actors by GameManager.js.
//
// Behavior:
//   - Finds the player and moves toward it each frame.
//   - Rotates to face the player.
//   - If the player is not found or inactive, wanders randomly.
//   - Takes damage from bullets and notifies the GameManager on death.

// =============================================================================
// Tuning variables
// =============================================================================

// Movement speed in pixels per second. Slower than the player so they can
// kite enemies, but fast enough to create pressure.
var speed = 100;

// Current health. Reduced by bullet hits (see takeDamage below).
var health = 50;

// Contact damage dealt to the player on collision. Handled by the player's
// onCollisionEnter, not by this script.
var damage = 15;

// Maximum distance (in pixels) at which the enemy can detect the player.
// Beyond this range the enemy wanders aimlessly.
var detectionRange = 800;

// -- Wander state -------------------------------------------------------------
// Used when the player is not found or is out of detection range. The enemy
// picks a random direction and moves that way for a short duration.
var wanderDirX = 0;
var wanderDirY = 0;
var wanderTimer = 0;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // No log here — many enemies spawn per wave, logging would flood the console.

    // Pick an initial random wander direction so enemies do not all stand still
    // on the first frame if the player is out of range.
    pickWanderDirection();
}

function onUpdate(dt) {
    // -- Find the player ------------------------------------------------------
    var player = Scene.find("Player");

    if (!player || !player.active) {
        // Player not found or inactive — wander randomly
        wander(dt);
        return;
    }

    // -- Distance check -------------------------------------------------------
    var dx = player.transform.x - actor.transform.x;
    var dy = player.transform.y - actor.transform.y;
    var dist = Math.sqrt(dx * dx + dy * dy);

    if (dist > detectionRange) {
        // Player is too far away — wander instead of chasing
        wander(dt);
        return;
    }

    // -- Move toward the player -----------------------------------------------
    // Normalize the direction vector and move at constant speed.
    if (dist > 0) {
        var nx = dx / dist;
        var ny = dy / dist;

        actor.transform.x += nx * speed * dt;
        actor.transform.y += ny * speed * dt;

        // Face the player by setting rotation to the angle toward them
        actor.transform.rotation = Math.atan2(dy, dx);
    }
}

// =============================================================================
// Wander behavior
// =============================================================================

// Picks a new random direction for wandering. Called periodically to keep
// the movement unpredictable.
function pickWanderDirection() {
    var angle = Math.random() * Math.PI * 2;
    wanderDirX = Math.cos(angle);
    wanderDirY = Math.sin(angle);
    wanderTimer = 1.0 + Math.random() * 2.0; // wander for 1-3 seconds
}

// Moves the enemy in the current wander direction. When the timer expires,
// a new random direction is chosen.
function wander(dt) {
    actor.transform.x += wanderDirX * speed * 0.5 * dt;
    actor.transform.y += wanderDirY * speed * 0.5 * dt;

    wanderTimer -= dt;
    if (wanderTimer <= 0) {
        pickWanderDirection();
    }

    // Clamp to arena bounds so wandering enemies do not leave the play area
    if (actor.transform.x < 30) actor.transform.x = 30;
    if (actor.transform.x > 1250) actor.transform.x = 1250;
    if (actor.transform.y < 30) actor.transform.y = 30;
    if (actor.transform.y > 690) actor.transform.y = 690;
}

// =============================================================================
// Damage and death
// =============================================================================

// Called by Bullet.js when a bullet collides with this enemy.
// Reduces health and destroys the enemy if health reaches zero.
function takeDamage(amount) {
    health -= amount;

    if (health <= 0) {
        // Notify the GameManager that an enemy was killed so it can update
        // the score and check if the wave is complete.
        var manager = Scene.find("GameManager");
        if (manager) {
            manager.onEnemyKilled();
        }

        // Remove this enemy from the scene
        actor.destroy();
    }
}

// =============================================================================
// Collision callbacks
// =============================================================================

// Called by the physics system when this enemy first touches another collider.
function onCollisionEnter(other) {
    // Bullet hits are handled here as a fallback. The Bullet.js script also
    // calls takeDamage directly, but this ensures damage is applied even if
    // the bullet's onCollisionEnter fires in a different order.
    if (other.tag === "Bullet") {
        takeDamage(25);
    }
}
