// RunnerController.js — Endless runner player controller
// Attach this script to the Runner actor.
//
// Controls:
//   Space / Up Arrow  = Jump (when grounded)
//   Down Arrow / S    = Duck (when grounded, shrinks collider briefly)
//
// The runner stays at a fixed X position while the world scrolls past.
// Jumping uses the Rigidbody2D velocity; ducking temporarily reduces
// the BoxCollider2D height so the player can pass under low obstacles.

// -- Movement tuning ----------------------------------------------------------

// Upward impulse applied when jumping. Negative because Y-axis points down
// in screen space; a negative velocity moves the runner upward.
var jumpForce = -450;

// -- Ground state -------------------------------------------------------------

// Tracks whether the runner is currently touching the ground. Set to true
// on collision with any "Ground"-tagged actor and false when leaving it.
var isGrounded = false;

// -- Duck state ---------------------------------------------------------------

// Whether the runner is currently in the ducking pose.
var isDucking = false;

// Countdown timer for the duck. While positive the runner stays ducked.
var duckTimer = 0;

// How long (in seconds) the duck pose lasts after pressing the duck key.
// This gives the player a fixed-duration duck rather than requiring them
// to hold the key, which feels snappier in an auto-runner.
var duckDuration = 0.4;

// The normal standing collider height (matches the BoxCollider2D in the scene).
var normalHeight = 48;

// The reduced collider height while ducking. Half the normal height lets
// the runner slip under low obstacles.
var duckHeight = 24;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // Announce controls so the player knows what to do right away.
    log("=== Runner Controls ===");
    log("  Space / Up Arrow  : Jump");
    log("  Down Arrow / S    : Duck");
    log("=======================");
}

function onUpdate(dt) {
    // -- Jumping --------------------------------------------------------------
    // The runner can only jump when grounded. Pressing Space or Up applies an
    // instantaneous upward velocity via the Rigidbody2D. Gravity brings the
    // runner back down, and the collision system re-enables jumping on landing.

    if (isGrounded && (Input.isKeyPressed("Space") || Input.isPressed("Up"))) {
        var rb = actor.getComponent("Rigidbody2D");
        if (rb) {
            rb.velocityY = jumpForce;
        }
        // Immediately mark as not grounded so the player cannot double-jump
        // in the same frame before the physics step separates them.
        isGrounded = false;
    }

    // -- Ducking --------------------------------------------------------------
    // Ducking uses a timer-based approach: pressing the duck key starts a
    // countdown. While the timer is active the BoxCollider2D height is halved,
    // allowing the runner to pass under low obstacles. When the timer expires
    // the collider is restored to full height.
    //
    // Ducking is only available while grounded — mid-air ducks would break
    // the jump arc and feel unintuitive.

    if (isGrounded && !isDucking && (Input.isPressed("Down") || Input.isKeyPressed("S"))) {
        // Begin ducking
        isDucking = true;
        duckTimer = duckDuration;

        // Shrink the collider to the duck height
        var col = actor.getComponent("BoxCollider2D");
        if (col) {
            col.size = { x: col.size.x, y: duckHeight };
        }
    }

    if (isDucking) {
        duckTimer -= dt;

        if (duckTimer <= 0) {
            // Duck time is up — restore the collider to normal height
            isDucking = false;
            duckTimer = 0;

            var col = actor.getComponent("BoxCollider2D");
            if (col) {
                col.size = { x: col.size.x, y: normalHeight };
            }
        }
    }
}

// =============================================================================
// Collision callbacks
// =============================================================================

// Called by the physics system when the runner first touches another collider.
function onCollisionEnter(other) {
    // -- Ground detection -----------------------------------------------------
    // Any actor tagged "Ground" counts as a landing surface. This keeps the
    // system flexible: you could add moving platforms later and they would
    // work as long as they share the "Ground" tag.
    if (other.tag === "Ground") {
        isGrounded = true;
    }

    // -- Obstacle hit ---------------------------------------------------------
    // Obstacles are tagged "Obstacle". Hitting one ends the run. We notify
    // the RunManager by calling its gameOver method through a Scene.find
    // lookup so the scoring and restart logic stays centralized.
    if (other.tag === "Obstacle") {
        log("GAME OVER! You hit an obstacle!");

        // Find the RunManager actor and tell it the game has ended.
        var manager = Scene.find("RunManager");
        if (manager) {
            // The RunManager script exposes a gameOver() function that handles
            // score logging and the restart prompt. We access the script's
            // exported function through the actor proxy.
            manager.gameOver();
        }
    }
}

// Called when the runner stops touching a collider.
function onCollisionExit(other) {
    // When leaving the ground, mark as airborne so the player cannot
    // jump again until they land on another ground surface.
    if (other.tag === "Ground") {
        isGrounded = false;
    }
}
