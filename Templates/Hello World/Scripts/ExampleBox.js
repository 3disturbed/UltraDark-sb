// ============================================================================
// ExampleBox.js — A simple actor that demonstrates lifecycle and movement
// ============================================================================
// Attach this script to the "ExampleBox" actor (the blue square in the scene).
//
// This script shows you:
//   - How onStart, onUpdate, and onDestroy work as lifecycle functions
//   - How to move an actor back and forth using delta time
//   - How to track time and log periodic status updates
//   - How collision callbacks fire when physics bodies touch
//
// Watch the blue square move left and right in the viewport while reading
// the console output to understand what's happening each step of the way.
// ============================================================================

// --- Variable declarations ---

// moveSpeed controls how many pixels per second the box moves horizontally.
// We multiply this by dt in onUpdate so movement is frame-rate independent.
var moveSpeed = 80;

// direction is either 1 (moving right) or -1 (moving left).
// We flip this value when the box reaches the edges of its movement range.
var direction = 1;

// lifetime accumulates total seconds since onStart was called.
// We use this to show how long the actor has been alive.
var lifetime = 0;

// logInterval is how often (in seconds) we print a status update to the console.
// Every 5 seconds, the box reports its position and how long it has been running.
var logInterval = 5;

// logTimer counts up toward logInterval. When it reaches the interval, we log
// and reset it back to zero. This is a common "cooldown timer" pattern.
var logTimer = 0;

// ============================================================================
// onStart — Called exactly once, on the first frame this actor is active.
// Use onStart for initialization: setting initial values, logging, and
// finding references to other actors or components you'll need later.
// ============================================================================
function onStart() {

    // Log a startup message so you can see exactly when onStart fires.
    // In the console, this appears when the scene first loads.
    Debug.log("[ExampleBox] ExampleBox has started!");

    // Explain the lifecycle timing so the user understands the call order.
    Debug.log("[ExampleBox] onStart fires once when the actor first becomes active.");

    // Let the user know this actor will be moving and logging periodically.
    Debug.log("[ExampleBox] I will move back and forth and log my position every " + logInterval + " seconds.");
}

// ============================================================================
// onUpdate — Called every frame while the actor is active.
// The dt parameter is delta time: the seconds elapsed since the last frame.
// All movement and time tracking should use dt for frame-rate independence.
// ============================================================================
function onUpdate(dt) {

    // --- Horizontal movement ---

    // Move the actor horizontally by adding (speed * direction * dt) to its X position.
    // Multiplying by dt ensures the box moves at the same speed regardless of frame rate.
    // At 60 FPS: 80 * 1 * 0.0167 = ~1.33 pixels per frame.
    // At 30 FPS: 80 * 1 * 0.033  = ~2.67 pixels per frame.
    // Both cover 80 pixels per second — that's why dt matters.
    transform.x = transform.x + (moveSpeed * direction * dt);

    // --- Boundary checking ---

    // If the box moves past the right boundary (X = 600), reverse direction.
    // This creates a simple "ping-pong" movement between two X coordinates.
    if (transform.x > 600) {

        // Set direction to -1 so the box starts moving left.
        direction = -1;
    }

    // If the box moves past the left boundary (X = 200), reverse direction.
    if (transform.x < 200) {

        // Set direction to 1 so the box starts moving right.
        direction = 1;
    }

    // --- Lifetime tracking ---

    // Add this frame's delta time to the total lifetime counter.
    // After 60 seconds of gameplay, lifetime will equal approximately 60.0.
    lifetime = lifetime + dt;

    // --- Periodic logging ---

    // Add this frame's delta time to the log timer.
    logTimer = logTimer + dt;

    // Check if enough time has passed to log another status update.
    if (logTimer >= logInterval) {

        // Log the current position (rounded to 1 decimal place) and total lifetime.
        Debug.log("[ExampleBox] Position: (" + transform.x.toFixed(1) + ", " + transform.y.toFixed(1) + ") | Alive for: " + lifetime.toFixed(1) + "s");

        // Reset the timer back to zero to start counting toward the next interval.
        // We subtract logInterval instead of setting to zero to avoid drift over time.
        logTimer = logTimer - logInterval;
    }
}

// ============================================================================
// onCollisionEnter — Called when this actor's collider first touches another
// actor's collider. Both actors must have collider components, and at least
// one must have a non-static Rigidbody2D for collision detection to work.
// ============================================================================
function onCollisionEnter(other) {

    // Log the name of the actor we collided with.
    // "other" is a reference to the other actor involved in the collision.
    Debug.log("[ExampleBox] Collision detected with: " + other.name);

    // Explain when this callback fires so the user understands the physics system.
    Debug.log("[ExampleBox] onCollisionEnter fires once when two solid colliders first overlap.");
    Debug.log("[ExampleBox] It does NOT fire for trigger colliders — use onTriggerEnter for those.");
}

// ============================================================================
// onDestroy — Called when the actor is removed from the scene.
// This happens when Scene.destroy() is called on this actor, or when the
// scene itself is unloaded. Use it to clean up resources or log final state.
// ============================================================================
function onDestroy() {

    // Log that the actor is being destroyed, along with how long it lived.
    Debug.log("[ExampleBox] ExampleBox was destroyed after " + lifetime.toFixed(1) + " seconds.");

    // Explain typical use cases for onDestroy to the user.
    Debug.log("[ExampleBox] Use onDestroy to clean up timers, remove UI elements, or save state.");
}
