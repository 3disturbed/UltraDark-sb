// Pickup.js — Collectible arena pickup: health, speed boost, or damage boost
// This script is attached to pickup actors spawned by ArenaManager.
//
// The pickup pulses in size to draw attention. When a player walks over it
// (trigger collision), the pickup applies its effect and destroys itself.

// =============================================================================
// Configuration
// =============================================================================

// The type of pickup: "health", "speed", or "damage".
// Set by ArenaManager.js at spawn time via ScriptComponent Properties.
var pickupType = "health";

// Timer used for the pulsing scale animation.
var pulseTimer = 0;

// Base and amplitude for the pulse effect. The pickup oscillates between
// baseScale - pulseAmplitude and baseScale + pulseAmplitude.
var baseScale = 1.0;
var pulseAmplitude = 0.15;
var pulseSpeed = 3.0;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // The pickupType may be set via Properties at spawn time. If not, it
    // defaults to "health" as declared above.
}

function onUpdate(dt) {
    // -- Pulse animation ------------------------------------------------------
    // Oscillate the scale using a sine wave to make the pickup visually
    // noticeable on the arena floor.
    pulseTimer += dt * pulseSpeed;
    var scale = baseScale + Math.sin(pulseTimer) * pulseAmplitude;
    actor.transform.scaleX = scale;
    actor.transform.scaleY = scale;
}

// =============================================================================
// Trigger callbacks
// =============================================================================

// Called when another collider enters this pickup's trigger zone.
function onTriggerEnter(other) {
    // Only players can collect pickups.
    if (other.tag !== "Player") return;

    // Apply the pickup effect to the player. ArenaPlayer.js exposes an
    // applyPickup(type) function for this purpose.
    if (other.applyPickup) {
        other.applyPickup(pickupType);
    }

    // Notify the ArenaManager that a pickup was collected so it can update
    // the active pickup count.
    var manager = Scene.find("ArenaManager");
    if (manager && manager.onPickupCollected) {
        manager.onPickupCollected();
    }

    // Remove the pickup from the scene.
    actor.destroy();
}
