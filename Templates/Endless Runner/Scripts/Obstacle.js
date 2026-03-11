// Obstacle.js — Self-scrolling obstacle for the endless runner
// Attach this script to obstacle actors (either placed in the scene or
// instantiated via prefab). Each obstacle moves leftward at a constant
// speed and destroys itself once it scrolls past the left edge of the
// screen, freeing memory and keeping the scene clean.
//
// The speed is read from the RunManager at startup so all obstacles
// share the current scroll speed. If the RunManager cannot be found
// (e.g. during isolated testing), a sensible default is used.

// =============================================================================
// Variables
// =============================================================================

// Horizontal movement speed in pixels per second. Obstacles always move
// left (negative X direction) at this rate.
var speed = 300;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // Try to read the current scroll speed from the RunManager so this
    // obstacle matches the world's pace. Scene.find returns the actor
    // proxy; we access the script variable through it.
    var manager = Scene.find("RunManager");
    if (manager) {
        // The RunManager exposes scrollSpeed as a script-level variable.
        // If the lookup succeeds, use it; otherwise fall back to the default.
        var managerSpeed = manager.scrollSpeed;
        if (managerSpeed && managerSpeed > 0) {
            speed = managerSpeed;
        }
    }
}

function onUpdate(dt) {
    // -- Move left ------------------------------------------------------------
    // Shift the obstacle toward the left side of the screen. The runner
    // stays at a fixed X position, so this creates the illusion of
    // forward movement through the world.
    transform.x -= speed * dt;

    // -- Off-screen cleanup ---------------------------------------------------
    // Once the obstacle is well past the left edge of the viewport (x < -100),
    // destroy it. The -100 threshold gives a small buffer so the obstacle
    // fully disappears before being removed — avoids visual popping.
    if (transform.x < -100) {
        actor.destroy();
    }
}
