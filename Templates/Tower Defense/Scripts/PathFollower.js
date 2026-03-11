// PathFollower.js — Enemy path-following behavior for tower defense
// Attached to dynamically spawned enemy actors by TDManager.js.
//
// Behavior:
//   - On start, retrieves the ordered path from TDManager and the current
//     wave's stats (speed, health, reward).
//   - Each frame, moves toward the next path node at constant speed.
//   - When it reaches the final node (the base), it notifies TDManager
//     and destroys itself.
//   - Exposes takeDamage() for Tower.js / TowerBullet.js to call.
//
// Design notes (tower defense pattern):
//   Enemies in a tower defense follow a fixed path and do not chase the
//   player. The path is a sequence of waypoints — the enemy moves toward
//   each one in order. Speed is the primary difficulty lever: faster
//   enemies give towers less time to shoot. Health determines how many
//   hits are needed.

// =============================================================================
// Tuning variables
// =============================================================================

// Movement speed in pixels per second along the path.
// Overridden by the current wave's speed value in onStart.
var speed = 80;

// Current health points. When this reaches zero the enemy dies.
// Overridden by the current wave's health value in onStart.
var health = 100;

// Maximum health (used for health bar calculations if needed).
var maxHealth = 100;

// Ordered array of {x, y} waypoints. Retrieved from TDManager on start.
var pathNodes = [];

// Index of the waypoint we are currently moving toward.
var currentNode = 0;

// Gold awarded to the player when this enemy is killed.
// Overridden by the current wave's reward value in onStart.
var reward = 10;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // -- Retrieve path and wave data from TDManager ---------------------------
    // The TDManager holds the authoritative path and wave definitions.
    var manager = Scene.find("TDManager");

    if (manager) {
        // Get the ordered waypoint positions
        var nodes = manager.getPathNodes();
        if (nodes && nodes.length > 0) {
            pathNodes = nodes;
        }

        // Get current wave stats to configure this enemy
        var waveData = manager.getCurrentWaveData();
        if (waveData) {
            speed = waveData.speed;
            health = waveData.health;
            maxHealth = waveData.health;
            reward = waveData.reward;
        }
    }

    // Start at the first path node
    if (pathNodes.length > 0) {
        actor.transform.x = pathNodes[0].x;
        actor.transform.y = pathNodes[0].y;
        // Begin moving toward the second node (index 1)
        currentNode = 1;
    }
}

function onUpdate(dt) {
    // -- Guard: no path or already past the end -------------------------------
    if (!pathNodes || pathNodes.length === 0) return;
    if (currentNode >= pathNodes.length) return;

    // -- Move toward the current waypoint -------------------------------------
    var targetX = pathNodes[currentNode].x;
    var targetY = pathNodes[currentNode].y;

    var dx = targetX - actor.transform.x;
    var dy = targetY - actor.transform.y;
    var dist = Math.sqrt(dx * dx + dy * dy);

    if (dist > 0) {
        // Normalize direction and move at constant speed
        var nx = dx / dist;
        var ny = dy / dist;

        var moveAmount = speed * dt;

        // If we would overshoot, snap to the waypoint instead
        if (moveAmount >= dist) {
            actor.transform.x = targetX;
            actor.transform.y = targetY;
        } else {
            actor.transform.x += nx * moveAmount;
            actor.transform.y += ny * moveAmount;
        }

        // Face the direction of travel
        actor.transform.rotation = Math.atan2(dy, dx);
    }

    // -- Check if we reached the current waypoint -----------------------------
    // Use a small threshold to account for floating-point imprecision.
    var arrivalDx = targetX - actor.transform.x;
    var arrivalDy = targetY - actor.transform.y;
    var arrivalDist = Math.sqrt(arrivalDx * arrivalDx + arrivalDy * arrivalDy);

    if (arrivalDist < 5) {
        currentNode++;

        // -- Check if we reached the end of the path (the base) ---------------
        if (currentNode >= pathNodes.length) {
            // Notify TDManager that this enemy breached the base
            var manager = Scene.find("TDManager");
            if (manager) {
                manager.onEnemyReachedBase();
            }

            // Remove this enemy from the scene
            actor.destroy();
        }
    }
}

// =============================================================================
// Damage and death
// =============================================================================

// Called by TowerBullet.js (or Tower.js) when this enemy is hit.
// Reduces health and notifies TDManager on death.
function takeDamage(amount) {
    health -= amount;

    if (health <= 0) {
        // Notify TDManager so it can award gold and update counters
        var manager = Scene.find("TDManager");
        if (manager) {
            manager.onEnemyKilled(reward);
        }

        // Remove this enemy from the scene
        actor.destroy();
    }
}
