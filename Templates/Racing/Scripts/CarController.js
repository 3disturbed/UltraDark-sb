// CarController.js — Top-down vehicle with acceleration, braking, steering, and drift
// Attach this script to a car actor (Player1Car or Player2Car) in the RaceTrack scene.
//
// Controls:
//   Player 1 (Blue):  W = accelerate, S = brake/reverse, A/D = steer left/right
//   Player 2 (Red):   Up = accelerate, Down = brake/reverse, Left/Right = steer
//
// The car uses a simplified top-down physics model:
//   - The car has a forward direction derived from its rotation angle.
//   - Acceleration adds to a scalar speed value along that direction.
//   - Friction naturally slows the car each frame, simulating drag.
//   - Above a speed threshold, drift friction kicks in — a higher multiplier
//     that lets the car slide more, creating a drift-like feel at high speed.
//   - Steering rotates the car only when it is moving (you cannot turn while
//     stationary, which feels natural for a vehicle).

// =============================================================================
// Tuning variables — Vehicle physics
// =============================================================================

// -- Acceleration & top speed -------------------------------------------------

// How quickly the car gains speed when the throttle key is held (px/s^2).
// A higher value makes the car feel more responsive / snappy off the line.
// 400 gives a punchy arcade feel without being instant.
var acceleration = 400;

// Maximum forward speed in pixels per second. This caps how fast the car can
// travel. 350 is fast enough to feel exciting on a 1280x720 track while still
// giving the player time to react to corners.
var maxSpeed = 350;

// -- Braking ------------------------------------------------------------------

// How quickly speed decreases when the brake key is held (px/s^2). Higher
// values give sharper braking. 300 allows quick stops without being instant,
// so the player must plan ahead for turns.
var brakeForce = 300;

// -- Steering -----------------------------------------------------------------

// Rotation speed in radians per second while steering left or right. Only
// applied when speed > 0, so the car cannot spin in place. 3.0 gives tight
// responsive turns that reward careful throttle control — faster cars turn
// at the same angular rate, so high-speed turns sweep wider arcs.
var steerSpeed = 3.0;

// -- Friction / drag ----------------------------------------------------------

// Velocity multiplier applied each frame to simulate rolling resistance and
// air drag. Values close to 1.0 mean less friction (car coasts longer).
// 0.97 gives a noticeable natural deceleration — the car will stop on its
// own after about 2 seconds of coasting from mid-speed.
var friction = 0.97;

// Friction multiplier used when the car exceeds driftThreshold speed. This
// value is intentionally higher (closer to 1.0) than normal friction, which
// means the car loses speed MORE SLOWLY at high speed. The result: once you
// are going fast, the car feels slippery and slides through turns — the
// drift effect. 0.99 creates a subtle but noticeable difference in handling.
var driftFriction = 0.99;

// Speed threshold above which drift friction replaces normal friction. Below
// this speed the car handles tightly; above it the car starts to slide. 200
// is roughly 57% of maxSpeed, so drift only matters when you are really
// pushing it.
var driftThreshold = 200;

// -- Current state ------------------------------------------------------------

// Current velocity components. Updated each frame from speed + direction.
var velocityX = 0;
var velocityY = 0;

// Current scalar speed along the car's forward axis. Positive = forward,
// negative = reversing.
var speed = 0;

// Player index: 0 = Player 1 (WASD), 1 = Player 2 (Arrow keys).
// Determined automatically from the actor name in onStart().
var playerIndex = 0;

// -- Input key bindings (set in onStart based on playerIndex) -----------------
var keyAccel = "W";
var keyBrake = "S";
var keyLeft = "A";
var keyRight = "D";

// Tracks whether this car has crashed before (for first-crash log message).
var hasCrashed = false;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // Determine which player this car belongs to based on the actor name.
    // "Player1Car" → Player 1 (WASD), anything else → Player 2 (Arrows).
    if (actor.name === "Player1Car") {
        playerIndex = 0;
        keyAccel = "W";
        keyBrake = "S";
        keyLeft = "A";
        keyRight = "D";
        log("Player 1 (Blue) — W/A/S/D to drive");
    } else {
        playerIndex = 1;
        keyAccel = "Up";
        keyBrake = "Down";
        keyLeft = "Left";
        keyRight = "Right";
        log("Player 2 (Red) — Arrow keys to drive");
    }
}

function onUpdate(dt) {
    // -- Acceleration / braking -----------------------------------------------
    // Read the throttle and brake inputs. Acceleration increases scalar speed
    // along the car's forward axis; braking decreases it. If braking pushes
    // speed below zero, the car reverses slowly (capped at -100 px/s).

    if (Input.isKeyHeld(keyAccel)) {
        // Accelerate forward. dt scaling makes the acceleration frame-rate
        // independent: at 60 FPS each frame adds ~6.7 px/s, at 30 FPS ~13.3.
        speed += acceleration * dt;
    }

    if (Input.isKeyHeld(keyBrake)) {
        // Brake: reduce speed. If already stopped or moving very slowly,
        // this will push speed negative to allow reversing.
        speed -= brakeForce * dt;
    }

    // -- Speed clamping -------------------------------------------------------
    // Cap forward speed at maxSpeed and reverse speed at a low value so
    // reversing feels deliberately sluggish compared to forward driving.
    if (speed > maxSpeed) {
        speed = maxSpeed;
    }
    if (speed < -100) {
        speed = -100;
    }

    // -- Steering -------------------------------------------------------------
    // Rotate the car left or right. Steering is only active when the car is
    // moving (|speed| > 5) — this prevents the car from spinning in place
    // which would feel unnatural for a vehicle. The rotation rate is constant
    // regardless of speed, which means high-speed turns sweep wider arcs
    // naturally (you cover more ground per radian at higher speed).

    if (Math.abs(speed) > 5) {
        if (Input.isKeyHeld(keyLeft)) {
            actor.transform.rotation -= steerSpeed * dt;
        }
        if (Input.isKeyHeld(keyRight)) {
            actor.transform.rotation += steerSpeed * dt;
        }
    }

    // -- Forward direction from rotation --------------------------------------
    // In the engine's coordinate system, rotation = 0 points up (negative Y).
    // We derive the forward direction using sin/cos:
    //   dirX =  sin(rotation) → positive rotation turns right
    //   dirY = -cos(rotation) → cos gives the "up" component, negated for
    //                           screen coordinates where Y increases downward.
    var dirX = Math.sin(actor.transform.rotation);
    var dirY = -Math.cos(actor.transform.rotation);

    // -- Friction / drift -----------------------------------------------------
    // Apply friction to naturally slow the car when no throttle is held.
    // At high speed (above driftThreshold), we use driftFriction instead —
    // a value closer to 1.0 that reduces deceleration, making the car feel
    // slippery and harder to control. This is the core of the drift mechanic:
    // the car preserves more momentum at high speed, so turns cause it to
    // slide outward rather than track tightly.
    if (Math.abs(speed) > driftThreshold) {
        speed *= driftFriction;
    } else {
        speed *= friction;
    }

    // -- Apply movement -------------------------------------------------------
    // Translate the car along its forward axis by speed * dt. We modify the
    // transform directly rather than using Rigidbody2D velocity so we have
    // full control over the physics feel. The Rigidbody2D is present only
    // for collision detection with walls and checkpoints.
    velocityX = dirX * speed;
    velocityY = dirY * speed;

    actor.transform.x += velocityX * dt;
    actor.transform.y += velocityY * dt;

    // -- Tiny dead-zone snap --------------------------------------------------
    // If speed is very small (coasting to a stop), snap to zero to prevent
    // the car from creeping forward indefinitely due to floating-point drift.
    if (Math.abs(speed) < 1) {
        speed = 0;
    }
}

// =============================================================================
// Collision callbacks
// =============================================================================

// Called by the physics system when this car first touches another collider.
// Wall collisions cause a crash: the car bounces back and loses half its
// speed, punishing reckless driving while keeping the game flowing.
function onCollisionEnter(other) {
    if (other.tag === "Wall") {
        // Reduce speed by 50% and reverse direction slightly to push the car
        // away from the wall. This gives a satisfying "bounce" without fully
        // stopping the car, so the player can recover and keep racing.
        speed *= -0.5;

        // Log the crash on the first collision only, to confirm collision
        // detection is working without flooding the console.
        if (!hasCrashed) {
            var playerLabel = (playerIndex === 0) ? "P1" : "P2";
            log(playerLabel + ": Crash! Watch those walls!");
            hasCrashed = true;
        }
    }
}
