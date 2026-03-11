// Tower.js — Tower combat behavior for tower defense
// Attached to tower actors created by PlacementGrid.js.
//
// Behavior:
//   - On start, reads tower stats (range, damage, cooldown) from the actor's
//     custom properties set by PlacementGrid.js during placement.
//   - Each frame, counts down the attack cooldown.
//   - When ready to fire, scans for the nearest enemy within range.
//   - Fires a bullet (TowerBullet.js) at the target and resets the cooldown.
//   - Rotates to face the current target for visual feedback.
//
// Design notes (tower defense pattern):
//   Towers are stationary units that automatically attack enemies in range.
//   The "nearest enemy" targeting strategy is simple but effective — it
//   prioritizes threats closest to the tower, which often means enemies
//   that are furthest along the path (and thus most dangerous).
//   The Splash tower type uses a splash radius on its bullet to hit groups.

// =============================================================================
// Combat variables
// =============================================================================

// Maximum distance (in pixels) at which this tower can target enemies.
var range = 120;

// Damage dealt per bullet. Applied by TowerBullet.js on impact.
var damage = 20;

// Minimum time (in seconds) between shots. Lower = faster fire rate.
var cooldown = 1.0;

// Countdown timer. The tower can fire when this reaches zero.
var timer = 0;

// Splash damage radius. 0 means single-target (Basic and Fast towers).
// Values > 0 cause the bullet to deal area damage (Splash tower).
var splashRadius = 0;

// Tower type name (for logging and identification).
var towerType = "Basic";

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // -- Read tower stats from actor properties -------------------------------
    // PlacementGrid.js sets these custom properties when creating the tower.
    // This decouples tower definitions from this script — adding new tower
    // types only requires changes to PlacementGrid.js.
    if (actor.towerRange !== undefined) range = actor.towerRange;
    if (actor.towerDamage !== undefined) damage = actor.towerDamage;
    if (actor.towerCooldown !== undefined) cooldown = actor.towerCooldown;
    if (actor.towerType !== undefined) towerType = actor.towerType;

    // Splash towers get a splash radius so their bullets deal area damage
    if (towerType === "Splash") {
        splashRadius = 60;
    }
}

function onUpdate(dt) {
    // -- Cooldown countdown ---------------------------------------------------
    timer -= dt;

    if (timer > 0) return;

    // -- Find the nearest enemy in range --------------------------------------
    // Scan all actors tagged "Enemy" and pick the closest one within range.
    // This is an O(n) scan each time the tower is ready to fire. For large
    // numbers of enemies, a spatial partition would be more efficient, but
    // for typical tower defense enemy counts this is fine.
    var enemies = Scene.findByTag("Enemy");
    if (!enemies || enemies.length === 0) return;

    var nearestEnemy = null;
    var nearestDist = range + 1; // Initialize beyond range so only in-range enemies qualify

    for (var i = 0; i < enemies.length; i++) {
        var enemy = enemies[i];
        var dx = enemy.transform.x - actor.transform.x;
        var dy = enemy.transform.y - actor.transform.y;
        var dist = Math.sqrt(dx * dx + dy * dy);

        if (dist < nearestDist) {
            nearestDist = dist;
            nearestEnemy = enemy;
        }
    }

    // -- Fire at the nearest enemy --------------------------------------------
    if (nearestEnemy && nearestDist <= range) {
        // Rotate tower to face the target (visual feedback)
        var aimDx = nearestEnemy.transform.x - actor.transform.x;
        var aimDy = nearestEnemy.transform.y - actor.transform.y;
        actor.transform.rotation = Math.atan2(aimDy, aimDx);

        // Create and launch a bullet
        shootAt(nearestEnemy);

        // Reset cooldown
        timer = cooldown;
    }
}

// =============================================================================
// Shooting
// =============================================================================

// Creates a bullet actor aimed at the target enemy. The bullet handles
// its own movement and damage application via TowerBullet.js.
function shootAt(target) {
    var bulletName = "Bullet_" + actor.name;
    var bullet = Scene.createActor(bulletName);

    if (bullet) {
        // Start at the tower's position
        bullet.transform.x = actor.transform.x;
        bullet.transform.y = actor.transform.y;
        bullet.tag = "Bullet";

        // Visual appearance — small white projectile
        var sprite = bullet.addComponent("SpriteRenderer");
        if (sprite) {
            sprite.Color = { R: 255, G: 255, B: 255, A: 255 };
        }
        bullet.transform.scaleX = 0.3;
        bullet.transform.scaleY = 0.3;

        // Collision detection
        var col = bullet.addComponent("BoxCollider2D");
        if (col) {
            col.Width = 8;
            col.Height = 8;
        }

        // Attach bullet behavior script
        var script = bullet.addComponent("ScriptComponent");
        if (script) {
            script.ScriptPath = "Scripts/TowerBullet.js";
        }

        // Pass target position and damage to the bullet. The bullet reads
        // these in its onStart to determine direction and impact behavior.
        bullet.bulletDamage = damage;
        bullet.targetX = target.transform.x;
        bullet.targetY = target.transform.y;
        bullet.splashRadius = splashRadius;

        // Aim the bullet toward the target
        var dx = target.transform.x - actor.transform.x;
        var dy = target.transform.y - actor.transform.y;
        bullet.transform.rotation = Math.atan2(dy, dx);
    }
}
