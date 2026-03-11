// ============================================================================
// ClickTarget.js — Demonstrates trigger colliders and interaction
// ============================================================================
// Attach this script to the "ClickTarget" actor (the green square).
//
// This actor has a BoxCollider2D with IsTrigger set to true. Trigger colliders
// do NOT cause physical collisions — objects pass right through them. Instead,
// they fire onTriggerEnter and onTriggerExit callbacks when another collider
// overlaps with them. This is perfect for:
//   - Pickup zones (coins, health packs)
//   - Area-of-effect abilities
//   - Door/portal triggers
//   - Detection zones (aggro radius, checkpoint lines)
// ============================================================================

// --- Variable declarations ---

// interactionCount tracks how many times another actor has entered this trigger.
// We display this in the console so you can see the count increase over time.
var interactionCount = 0;

// pulseTimer accumulates time for the visual pulse effect.
// We feed this into Math.sin to create a smooth oscillation.
var pulseTimer = 0;

// ============================================================================
// onStart — Called once when this actor first becomes active in the scene.
// ============================================================================
function onStart() {

    // Log a message explaining what makes this actor special.
    Debug.log("[ClickTarget] ClickTarget is ready!");

    // Explain trigger colliders vs. solid colliders so the user understands the difference.
    Debug.log("[ClickTarget] This actor has a TRIGGER collider (IsTrigger = true).");
    Debug.log("[ClickTarget] Trigger colliders don't block movement — things pass through them.");
    Debug.log("[ClickTarget] They fire onTriggerEnter/onTriggerExit instead of onCollisionEnter.");
}

// ============================================================================
// onUpdate — Called every frame. We use this to create a gentle pulse effect
// that makes the green square visually "breathe" by scaling up and down.
// ============================================================================
function onUpdate(dt) {

    // Accumulate time for the sine wave calculation.
    // dt ensures this runs at the same visual speed regardless of frame rate.
    pulseTimer = pulseTimer + dt;

    // Calculate a scale value that oscillates between 0.8 and 1.2.
    // Math.sin returns values between -1 and 1 over a full cycle (2*PI seconds).
    // We multiply by 0.2 to get a range of -0.2 to 0.2.
    // Adding 1.0 shifts the range to 0.8 to 1.2.
    // Multiplying pulseTimer by 2 makes the pulse complete a full cycle every ~3.14 seconds.
    var scale = 1.0 + Math.sin(pulseTimer * 2) * 0.2;

    // Apply the calculated scale to both the X and Y axes.
    // Setting both to the same value keeps the square's proportions uniform.
    transform.scaleX = scale;

    // The Y scale matches the X scale for a uniform pulse effect.
    transform.scaleY = scale;
}

// ============================================================================
// onTriggerEnter — Called when another collider first overlaps with this trigger.
// Unlike onCollisionEnter, the other actor is NOT pushed away. It passes through.
// "other" is the actor whose collider entered this trigger zone.
// ============================================================================
function onTriggerEnter(other) {

    // Increment the interaction counter so we can track how many times this fires.
    interactionCount = interactionCount + 1;

    // Log that something entered the trigger zone, along with what it was.
    Debug.log("[ClickTarget] Something entered my trigger zone! Actor: " + other.name);

    // Log the running count so the user can see it increase with each interaction.
    Debug.log("[ClickTarget] Total interactions so far: " + interactionCount);

    // Explain the key difference between triggers and solid collisions.
    Debug.log("[ClickTarget] Note: The other actor passed THROUGH me, not bounced off.");
    Debug.log("[ClickTarget] Triggers detect overlap; colliders create physical response.");
}

// ============================================================================
// onTriggerExit — Called when an actor that was inside this trigger leaves it.
// This fires once, at the moment the other collider stops overlapping.
// ============================================================================
function onTriggerExit(other) {

    // Log that the actor has left the trigger zone.
    Debug.log("[ClickTarget] '" + other.name + "' left my trigger zone.");

    // Explain when this callback is useful in a real game.
    Debug.log("[ClickTarget] onTriggerExit is useful for ending effects when a player leaves an area.");
}
