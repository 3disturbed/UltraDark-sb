// ArenaBullet.js — Projectile fired by arena players
// This script is attached to dynamically spawned Bullet actors by ArenaPlayer.
//
// The bullet travels in a straight line based on the rotation it was given at
// spawn time. It self-destructs after a set lifetime or on collision with a
// player (that is not the owner) or a wall.

// =============================================================================
// Tuning variables
// =============================================================================

// Movement speed in pixels per second. Bullets should be noticeably faster
// than players to feel responsive.
var speed = 500;

// Damage dealt to players on collision. This value is set by ArenaPlayer.js
// at spawn time (may be boosted by a damage pickup).
var damage = 20;

// Maximum time the bullet can exist (in seconds) before self-destructing.
// Prevents bullets from lingering if they miss everyone.
var lifetime = 1.5;

// Elapsed time since the bullet was spawned.
var timer = 0;

// Name of the player who fired this bullet. Used to prevent self-damage and
// to attribute kills in the scoreboard.
var ownerName = "";

// Read by ArenaPlayer.js through getComponent("ScriptComponent").invoke(...):
// a function defined at top level is the only thing another script can reach.
function getOwnerName() { return ownerName; }
function getDamage() { return damage; }

// Direction components calculated from the bullet's rotation on spawn.
// Set once in onStart and never changed.
var dirX = 0;
var dirY = 0;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // Calculate the travel direction from the rotation assigned at spawn time.
    // ArenaPlayer.js sets transform.rotation to the aim angle before this
    // script's first frame.
    dirX = Math.cos(actor.transform.rotation);
    dirY = Math.sin(actor.transform.rotation);
}

function onUpdate(dt) {
    // -- Movement -------------------------------------------------------------
    // Move in a straight line at constant speed. Direct transform manipulation
    // is simpler and more predictable than physics for projectiles.
    actor.transform.x += dirX * speed * dt;
    actor.transform.y += dirY * speed * dt;

    // -- Lifetime check -------------------------------------------------------
    // Destroy the bullet after it has exceeded its maximum lifetime.
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
    // -- Player hit -----------------------------------------------------------
    // Deal damage to any player that is not the bullet's owner. The player's
    // ArenaPlayer.js script handles health reduction and death.
    if (other.tag === "Player") {
        if (other.name !== ownerName) {
            var player = other.getComponent("ScriptComponent");
            if (player) player.invoke("takeDamage", damage, ownerName);
            actor.destroy();
            return;
        }
        // Ignore collision with the owner — bullet passes through.
        return;
    }

    // -- Wall hit -------------------------------------------------------------
    // Bullets that hit arena walls are simply destroyed.
    if (other.tag === "Wall") {
        actor.destroy();
        return;
    }
}
