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

// Called by RunManager.js right after it attaches this script; runs before onStart.
function configure(newSpeed) {
    if (newSpeed > 0) speed = newSpeed;
}

function onStart() {
    // Ask the RunManager for the current scroll speed so this obstacle matches
    // the world's pace; another script's variables are reached through invoke.
    var manager = Scene.find("RunManager");
    var managerScript = manager ? manager.getComponent("ScriptComponent") : null;
    var managerSpeed = managerScript ? managerScript.invoke("getScrollSpeed") : 0;
    if (managerSpeed > 0) speed = managerSpeed;
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
