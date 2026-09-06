// GameManager.js — Wave-based enemy spawner and score tracker
// Attach this script to the GameManager actor in the Arena scene.
//
// Responsibilities:
//   1. Spawn waves of enemies with increasing difficulty.
//   2. Track score (kills * wave multiplier).
//   3. Handle transitions between waves (brief cooldown).
//   4. Detect game-over and offer restart via the R key.
//
// Enemy actors are spawned at random positions along the arena edges so they
// do not appear on top of the player. Each enemy gets an EnemyAI.js script
// that handles movement and combat behavior.

// =============================================================================
// Tuning variables
// =============================================================================

// -- Wave management ----------------------------------------------------------

// Current wave number. Incremented at the start of each wave.
var wave = 0;

// Number of enemies currently alive in the arena. When this reaches zero
// the current wave is complete and the between-wave timer starts.
var enemiesAlive = 0;

// Number of enemies to spawn in the current wave. Increases each wave
// using the formula: 3 + wave * 2.
var enemiesPerWave = 5;

// -- Scoring ------------------------------------------------------------------

// Accumulated score across all waves. Increases when enemies are killed.
// The score gain per kill is 100 * currentWave, rewarding survival.
var score = 0;

// -- Timers -------------------------------------------------------------------

// Delay between spawning individual enemies within a wave (in seconds).
// Prevents all enemies from appearing in the same frame.
var spawnDelay = 1.0;

// Countdown between waves. Gives the player a breather to reposition.
var betweenWaveTimer = 3.0;

// Internal timer used for between-wave countdown.
var waveTimer = 0;

// -- Game state ---------------------------------------------------------------

// Current state of the game. Possible values:
//   "playing"       — enemies are alive and the player is fighting
//   "betweenWaves"  — all enemies dead, waiting for next wave to start
//   "gameover"      — player is dead, waiting for R to restart
var state = "playing";

// -- Score logging ------------------------------------------------------------

// Timer used to periodically log the score during gameplay.
var scoreLogTimer = 0;

// =============================================================================
// Multiplayer co-op (commented out)
// =============================================================================
// To add networked co-op multiplayer, uncomment the sections below and ensure
// the Multiplayer template's NetworkManager is configured in your project.
//
// --- In onStart, after the log banner: ---
//
// // Start a co-op server or connect to an existing one
// // Host:
// // Network.startServer(7777);
// // log("Hosting co-op on port 7777...");
// //
// // Client:
// // Network.connect("127.0.0.1", 7777);
// // log("Connecting to co-op host...");
//
// --- Spawn networked players: ---
//
// // function onPlayerJoined(networkId) {
// //     var coopPlayer = Scene.createActor("Player_" + networkId);
// //     coopPlayer.transform.x = 640;
// //     coopPlayer.transform.y = 360;
// //     coopPlayer.tag = "Player";
// //     log("Co-op player joined: " + networkId);
// // }
//
// --- Sync enemy kills across clients: ---
//
// // function onEnemyKilledNetwork(enemyId) {
// //     Network.broadcast("enemyKill", { id: enemyId });
// // }
//
// // function onNetworkMessage(type, data) {
// //     if (type === "enemyKill") {
// //         onEnemyKilled();
// //     }
// // }

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    log("========================================");
    log("      TWIN-STICK SHOOTER!");
    log("========================================");
    log("  WASD        : Move");
    log("  Mouse       : Aim");
    log("  Left Click  : Shoot");
    log("  R           : Restart (after game over)");
    log("========================================");

    // Begin the first wave immediately
    startWave();
}

function onUpdate(dt) {
    // -- Between waves --------------------------------------------------------
    // After clearing a wave, the player gets a few seconds to breathe before
    // the next wave begins. The timer counts down and then starts the wave.
    if (state === "betweenWaves") {
        waveTimer -= dt;

        if (waveTimer <= 0) {
            startWave();
        }
        return;
    }

    // -- Playing --------------------------------------------------------------
    // During active play, check if all enemies have been killed. If so,
    // transition to the between-waves cooldown.
    if (state === "playing") {
        if (enemiesAlive <= 0 && wave > 0) {
            state = "betweenWaves";
            waveTimer = betweenWaveTimer;
            log("Wave " + wave + " cleared! Next wave in " + betweenWaveTimer + " seconds...");
        }

        // Periodically log the score so the player can track progress
        scoreLogTimer += dt;
        if (scoreLogTimer >= 10) {
            log("Score: " + score + "  |  Wave: " + wave + "  |  Enemies: " + enemiesAlive);
            scoreLogTimer -= 10;
        }
        return;
    }

    // -- Game over ------------------------------------------------------------
    // Wait for the player to press R to restart the scene.
    if (state === "gameover") {
        if (Input.isKeyPressed("R")) {
            log("Restarting...");
            Scene.load("Scenes/Arena");
        }
        return;
    }
}

// =============================================================================
// Wave management
// =============================================================================

// Starts the next wave by incrementing the wave counter and spawning enemies
// at random positions along the arena edges.
function startWave() {
    wave++;
    state = "playing";

    // Calculate how many enemies to spawn this wave.
    // Formula: base of 3 plus 2 per wave, so wave 1 = 5, wave 2 = 7, etc.
    enemiesPerWave = 3 + wave * 2;

    log("========================================");
    log("  WAVE " + wave + "  —  " + enemiesPerWave + " enemies incoming!");
    log("========================================");

    // Spawn enemies at random positions along the arena edges. This ensures
    // they do not appear on top of the player in the center.
    for (var i = 0; i < enemiesPerWave; i++) {
        var pos = getRandomEdgePosition();
        spawnEnemy(pos.x, pos.y);
    }
}

// Returns a random position along one of the four arena edges. The position
// is offset slightly inward so the enemy does not spawn inside a wall.
function getRandomEdgePosition() {
    var edge = Math.floor(Math.random() * 4);
    var x, y;

    if (edge === 0) {
        // Top edge
        x = 40 + Math.random() * 1200;
        y = 40;
    } else if (edge === 1) {
        // Bottom edge
        x = 40 + Math.random() * 1200;
        y = 680;
    } else if (edge === 2) {
        // Left edge
        x = 40;
        y = 40 + Math.random() * 640;
    } else {
        // Right edge
        x = 1240;
        y = 40 + Math.random() * 640;
    }

    return { x: x, y: y };
}

// =============================================================================
// Enemy spawning
// =============================================================================

// Creates a single enemy actor at the given position with all required
// components: SpriteRenderer (red), Rigidbody2D (no gravity), BoxCollider2D,
// and the EnemyAI script.
function spawnEnemy(x, y) {
    var enemy = Scene.createActor("Enemy");

    if (enemy) {
        // Position the enemy at the specified location
        enemy.transform.x = x;
        enemy.transform.y = y;
        enemy.tag = "Enemy";

        // A dynamically created actor is empty: a red sprite, a body that ignores
        // gravity so it can chase in every direction, a collider so bullets and the
        // player can hit it, and the script that drives it.
        Scene.addComponent(enemy, "SpriteRenderer", { Tint: "#E04040" });
        Scene.addComponent(enemy, "Rigidbody2D", { GravityScale: 0, FreezeRotation: true });
        Scene.addComponent(enemy, "BoxCollider2D", { Size: [28, 28] });
        Scene.addComponent(enemy, "ScriptComponent", { ScriptPath: "Scripts/EnemyAI.js" });

        enemiesAlive++;
    }
}

// =============================================================================
// Score tracking
// =============================================================================

// Called by EnemyAI.js when an enemy is destroyed. Updates the score using
// a wave-based multiplier and decrements the alive counter.
function onEnemyKilled() {
    enemiesAlive--;

    // Clamp to zero in case of double-death race conditions
    if (enemiesAlive < 0) enemiesAlive = 0;

    // Award points based on the current wave — higher waves give more points
    // per kill, rewarding the player for surviving longer.
    var points = 100 * wave;
    score += points;

    log("Enemy destroyed! +" + points + " points  |  Score: " + score);
}

// =============================================================================
// Game over
// =============================================================================

// Called by PlayerShooter.js when the player dies. Stops all gameplay and
// displays the final score with a restart prompt.
function gameOver() {
    // Prevent multiple calls (e.g., if multiple enemies hit the player
    // on the same frame)
    if (state === "gameover") return;

    state = "gameover";

    log("========================================");
    log("         GAME OVER!");
    log("  Final Score : " + score);
    log("  Waves       : " + wave);
    log("  Press R to restart");
    log("========================================");
}
