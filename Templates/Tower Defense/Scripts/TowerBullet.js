// TowerBullet.js — Projectile fired by towers at enemies
// Attached to dynamically spawned bullet actors by Tower.js.
//
// Behavior:
//   - On start, reads damage, target position, and splash radius from
//     custom properties set by Tower.js.
//   - Calculates a normalized direction vector toward the target.
//   - Moves in a straight line each frame at constant speed.
//   - On reaching the target area (or colliding with an enemy), applies
//     damage and self-destructs.
//   - If splashRadius > 0, deals damage to all enemies within that radius.
//
// Design notes (tower defense pattern):
//   Bullets in tower defense are typically "fire and forget" — they aim at
//   the enemy's position at the time of firing. Fast bullets feel more
//   responsive; slow bullets can miss if enemies move quickly. Splash
//   bullets reward the player for placing towers where enemies cluster.

// =============================================================================
// Tuning variables
// =============================================================================

// Movement speed in pixels per second. Fast enough to feel instant but
// slow enough to be visible on screen.
var speed = 400;

// Damage dealt on impact. Overridden by Tower.js via actor properties.
var damage = 20;

// Target position (where the enemy was when the tower fired).
var targetX = 0;
var targetY = 0;

// Splash damage radius. 0 = single target, > 0 = area damage.
var splashRadius = 0;

// Normalized direction vector, calculated once in onStart.
var dirX = 0;
var dirY = 0;

// Safety timer to destroy bullets that somehow miss their target.
var lifetime = 3.0;
var timer = 0;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // -- Read properties set by Tower.js --------------------------------------
    if (actor.bulletDamage !== undefined) damage = actor.bulletDamage;
    if (actor.targetX !== undefined) targetX = actor.targetX;
    if (actor.targetY !== undefined) targetY = actor.targetY;
    if (actor.splashRadius !== undefined) splashRadius = actor.splashRadius;

    // -- Calculate direction toward target ------------------------------------
    var dx = targetX - actor.transform.x;
    var dy = targetY - actor.transform.y;
    var dist = Math.sqrt(dx * dx + dy * dy);

    if (dist > 0) {
        dirX = dx / dist;
        dirY = dy / dist;
    }
}

function onUpdate(dt) {
    // -- Movement -------------------------------------------------------------
    actor.transform.x += dirX * speed * dt;
    actor.transform.y += dirY * speed * dt;

    // -- Arrival check --------------------------------------------------------
    // Check if we have reached (or passed) the target position.
    var dx = targetX - actor.transform.x;
    var dy = targetY - actor.transform.y;
    var dist = Math.sqrt(dx * dx + dy * dy);

    if (dist < 10) {
        applyDamage();
        actor.destroy();
        return;
    }

    // -- Lifetime safety ------------------------------------------------------
    timer += dt;
    if (timer > lifetime) {
        actor.destroy();
    }
}

// =============================================================================
// Damage application
// =============================================================================

// Applies damage to enemies at the impact point. For single-target bullets
// (splashRadius === 0), damages only the nearest enemy. For splash bullets,
// damages all enemies within the splash radius.
function applyDamage() {
    var enemies = Scene.findByTag("Enemy");
    if (!enemies || enemies.length === 0) return;

    if (splashRadius > 0) {
        // -- Splash damage: hit all enemies within radius ---------------------
        for (var i = 0; i < enemies.length; i++) {
            var ex = enemies[i].transform.x - actor.transform.x;
            var ey = enemies[i].transform.y - actor.transform.y;
            var eDist = Math.sqrt(ex * ex + ey * ey);

            if (eDist <= splashRadius) {
                if (enemies[i].takeDamage) {
                    enemies[i].takeDamage(damage);
                }
            }
        }
    } else {
        // -- Single target: damage the nearest enemy --------------------------
        var nearest = null;
        var nearestDist = 30; // Only hit enemies very close to impact point

        for (var j = 0; j < enemies.length; j++) {
            var nx = enemies[j].transform.x - actor.transform.x;
            var ny = enemies[j].transform.y - actor.transform.y;
            var nDist = Math.sqrt(nx * nx + ny * ny);

            if (nDist < nearestDist) {
                nearestDist = nDist;
                nearest = enemies[j];
            }
        }

        if (nearest && nearest.takeDamage) {
            nearest.takeDamage(damage);
        }
    }
}

// =============================================================================
// Collision callbacks
// =============================================================================

// Fallback collision handler. If the physics system detects a collision
// with an enemy before the distance check triggers, apply damage here.
function onCollisionEnter(other) {
    if (other.tag === "Enemy") {
        if (other.takeDamage) {
            other.takeDamage(damage);
        }
        actor.destroy();
    }
}
