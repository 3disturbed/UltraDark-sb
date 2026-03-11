// Bullet.js — Projectile fired by the player
// This script is attached to dynamically spawned Bullet actors.
//
// The bullet travels in a straight line based on the rotation it was given
// at spawn time. It self-destructs after a set lifetime or on collision
// with an enemy or wall.

// =============================================================================
// Tuning variables
// =============================================================================

// Movement speed in pixels per second. Bullets should be noticeably faster
// than the player to feel responsive.
var speed = 600;

// Maximum time the bullet can exist (in seconds) before self-destructing.
// This prevents bullets from flying forever if they miss everything.
var lifetime = 2.0;

// Elapsed time since the bullet was spawned.
var timer = 0;

// Direction components calculated from the bullet's rotation on spawn.
// These are set once in onStart and never change.
var dirX = 0;
var dirY = 0;

// Damage dealt to enemies on collision.
var damage = 25;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // Calculate the travel direction from the rotation assigned at spawn time.
    // The PlayerShooter sets transform.rotation to the aim angle before this
    // script runs its first frame.
    dirX = Math.cos(actor.transform.rotation);
    dirY = Math.sin(actor.transform.rotation);

    // No log here — bullets are spawned frequently and would flood the console.
}

function onUpdate(dt) {
    // -- Movement -------------------------------------------------------------
    // Move in a straight line at constant speed. No physics needed for bullets;
    // direct transform manipulation is simpler and more predictable.
    actor.transform.x += dirX * speed * dt;
    actor.transform.y += dirY * speed * dt;

    // -- Lifetime check -------------------------------------------------------
    // Destroy the bullet after it has existed for longer than its lifetime.
    // This catches bullets that fly into open space without hitting anything.
    timer += dt;
    if (timer > lifetime) {
        actor.destroy();
    }
}

// =============================================================================
// Collision callbacks
// =============================================================================

// Called by the physics system when the bullet first touches another collider.
function onCollisionEnter(other) {
    // -- Enemy hit ------------------------------------------------------------
    // Deal damage to the enemy and destroy the bullet. The enemy's EnemyAI
    // script exposes a takeDamage function that handles health reduction and
    // death/score notifications.
    if (other.tag === "Enemy") {
        if (other.takeDamage) {
            other.takeDamage(damage);
        }
        actor.destroy();
        return;
    }

    // -- Wall hit -------------------------------------------------------------
    // Bullets that hit arena walls are simply destroyed. No damage or effects.
    if (other.tag === "Wall") {
        actor.destroy();
        return;
    }
}
