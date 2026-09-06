// RunManager.js — Core game loop for the endless runner
// Attach this script to the RunManager actor in the scene.
//
// Responsibilities:
//   1. Scroll the world by spawning obstacles that move left.
//   2. Gradually increase difficulty (speed + spawn rate).
//   3. Track distance-based score and high score.
//   4. Handle game-over state and restart via the R key.
//
// Obstacle types:
//   - "tall" obstacles sit on the ground and must be jumped over.
//   - "low"  obstacles float at head height and must be ducked under.
//
// The RunManager does NOT move the runner — it creates obstacles off-screen
// to the right and lets them scroll left. Each obstacle is given its own
// Obstacle.js script to handle movement and self-cleanup.

// =============================================================================
// Tuning variables
// =============================================================================

// -- Scrolling ----------------------------------------------------------------

// Base horizontal speed in pixels per second. Obstacles move left at this rate.
var scrollSpeed = 300;

// Read by Obstacle.js through getComponent("ScriptComponent").invoke("getScrollSpeed"),
// so an obstacle spawned late matches the world's current pace.
function getScrollSpeed() { return scrollSpeed; }

// Additional speed added every 10 seconds of play. This makes the game
// progressively harder the longer the player survives.
var speedIncrease = 15;

// Timer that tracks elapsed time for the speed-increase intervals.
var speedTimer = 0;

// -- Obstacle spawning --------------------------------------------------------

// Countdown until the next obstacle spawns.
var spawnTimer = 0;

// Seconds between obstacle spawns. Decreases over time to increase pressure.
var spawnInterval = 1.5;

// The spawn interval will never drop below this value, ensuring there is
// always a minimum reaction window for the player.
var minSpawnInterval = 0.5;

// -- Scoring ------------------------------------------------------------------

// Distance score accumulates based on scroll speed. Higher speed means the
// score climbs faster, rewarding survival.
var distanceScore = 0;

// Best score across runs in this session. Persists until the game is closed.
var highScore = 0;

// -- Game state ---------------------------------------------------------------

// Flipped to true when the runner hits an obstacle. Stops spawning and
// scoring until the player presses R to restart.
var isGameOver = false;

// -- Obstacle catalog ---------------------------------------------------------

// Each entry describes an obstacle variant. The spawner picks one at random.
//   name   : display name (logged for debugging)
//   height : collider height in pixels
//   width  : collider width in pixels
//   y      : vertical spawn position (world coordinates)
//   colorR/G/B : tint for the SpriteRenderer so the player can distinguish types
var obstacleTypes = [
    // Tall obstacle — sits on the ground, player must jump over it
    { name: "tall", width: 32, height: 64, y: 486, colorR: 200, colorG: 50, colorB: 50 },
    // Low obstacle — floats at head height, player must duck under it
    { name: "low",  width: 48, height: 28, y: 420, colorR: 200, colorG: 80, colorB: 50 }
];

// -- Score logging ------------------------------------------------------------

// Timer used to periodically log the current score so the player can track
// progress without UI (useful during development).
var scoreLogTimer = 0;

// -- Internal bookkeeping -----------------------------------------------------

// Array to track spawned obstacle actors so we can pass speed updates if needed.
var obstacles = [];

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    log("========================================");
    log("       ENDLESS RUNNER");
    log("========================================");
    log("  Space / Up   = Jump over tall obstacles");
    log("  Down  / S    = Duck under low obstacles");
    log("  R            = Restart after game over");
    log("========================================");
    log("Run!");
}

function onUpdate(dt) {
    // -- Game-over state ------------------------------------------------------
    // While the game is over, ignore all gameplay logic. Only listen for the
    // R key to restart. Restarting reloads the entire scene, which resets
    // every actor and script variable back to their initial state.
    if (isGameOver) {
        if (Input.isKeyPressed("R")) {
            Scene.load("Scenes/Run");
        }
        return;
    }

    // -- Score accumulation ---------------------------------------------------
    // Distance score increases proportionally to scroll speed. Dividing by 10
    // keeps the numbers in a comfortable range (roughly 30 points/sec at the
    // base speed of 300 px/s).
    distanceScore += scrollSpeed * dt / 10;

    // -- Speed ramp-up --------------------------------------------------------
    // Every 10 seconds of play, add a fixed amount to the scroll speed. This
    // creates a smooth difficulty curve that the player can feel but does not
    // cause sudden spikes.
    speedTimer += dt;
    if (speedTimer >= 10) {
        scrollSpeed += speedIncrease;
        speedTimer -= 10;
        log("Speed increased to " + Math.floor(scrollSpeed) + " px/s");
    }

    // -- Spawn interval ramp-up -----------------------------------------------
    // Slowly reduce the time between spawns. The interval shrinks by a tiny
    // amount each frame, converging toward minSpawnInterval. This ensures
    // obstacles appear more frequently as the game progresses.
    if (spawnInterval > minSpawnInterval) {
        spawnInterval -= dt * 0.02;
        if (spawnInterval < minSpawnInterval) {
            spawnInterval = minSpawnInterval;
        }
    }

    // -- Obstacle spawn timer -------------------------------------------------
    // Count down. When the timer expires, spawn a new obstacle and reset.
    spawnTimer -= dt;
    if (spawnTimer <= 0) {
        spawnObstacle();
        spawnTimer = spawnInterval;
    }

    // -- Periodic score logging -----------------------------------------------
    // Log the score every 5 seconds so the player has a rough idea of their
    // progress. In a full game this would update a UI label instead.
    scoreLogTimer += dt;
    if (scoreLogTimer >= 5) {
        log("Score: " + Math.floor(distanceScore) + "  |  Speed: " + Math.floor(scrollSpeed) + " px/s");
        scoreLogTimer -= 5;
    }
}

// =============================================================================
// Obstacle spawning
// =============================================================================

// Picks a random obstacle type and creates a new actor at the right edge of
// the screen. The actor is given an Obstacle.js script that handles its
// leftward movement and self-destruction when it scrolls off-screen.
function spawnObstacle() {
    // Pick a random type from the catalog
    var typeIndex = Math.floor(Math.random() * obstacleTypes.length);
    var obsType = obstacleTypes[typeIndex];

    // Create the obstacle actor just off the right edge of the viewport.
    // x=1400 is ~120px past the right side of a 1280-wide window, so the
    // obstacle is fully off-screen before it starts scrolling into view.
    var obstacle = Scene.createActor("Obstacle_" + obsType.name);

    if (obstacle) {
        // Position the obstacle at the correct height for its type.
        // Tall obstacles sit on the ground; low obstacles float higher.
        obstacle.transform.x = 1400;
        obstacle.transform.y = obsType.y;

        // Tag it so the runner's collision callback can identify it.
        obstacle.tag = "Obstacle";

        // Track the obstacle for bookkeeping (optional cleanup, speed sync).
        obstacles.push(obstacle);

        // A created actor is empty: a coloured block to see, and the script that
        // scrolls it, told the current speed before its onStart runs. Collision
        // stays distance-based in this manager (see below).
        Scene.addComponent(obstacle, "SpriteRenderer", {
            Tint: { R: obsType.colorR, G: obsType.colorG, B: obsType.colorB, A: 255 },
        });
        var script = Scene.addComponent(obstacle, "ScriptComponent", { ScriptPath: "Scripts/Obstacle.js" });
        if (script) script.invoke("configure", scrollSpeed);
    }

    // -- Distance-based collision check (fallback) ----------------------------
    // Since dynamically created actors cannot have physics colliders added
    // from script, we also do a simple proximity check each frame in
    // updateObstacles(). This is called at the end of onUpdate implicitly
    // via the obstacle list tracking.
}

// =============================================================================
// Game Over
// =============================================================================

// Called by RunnerController.js when the runner hits an obstacle, or by the
// internal proximity check. Stops the game and prompts for restart.
function gameOver() {
    if (isGameOver) return; // Prevent multiple triggers

    isGameOver = true;

    // Update the session high score if the current run beat it
    if (distanceScore > highScore) {
        highScore = distanceScore;
    }

    log("========================================");
    log("         GAME OVER!");
    log("  Score     : " + Math.floor(distanceScore));
    log("  High Score: " + Math.floor(highScore));
    log("  Press R to restart");
    log("========================================");
}
