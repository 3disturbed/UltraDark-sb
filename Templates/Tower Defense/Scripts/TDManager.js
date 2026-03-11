// TDManager.js — Wave management, economy, and game state for tower defense
// Attach this script to the TDManager actor in the Battlefield scene.
//
// Responsibilities:
//   1. Define and advance through 10 waves of increasing difficulty.
//   2. Spawn path-following enemies with per-wave stats (speed, health, reward).
//   3. Track the player's gold economy and base lives.
//   4. Coordinate with PathFollower.js (enemy behavior) and PlacementGrid.js
//      (tower placement) through public functions.
//
// Design notes (tower defense pattern):
//   - Enemies follow a fixed path defined by PathNode actors in the scene.
//   - The player cannot block the path; towers are placed on a grid beside it.
//   - Waves are manually started (press Enter) so the player can prepare.
//   - Economy is the core tension: each tower costs gold, and gold is earned
//     by killing enemies. Overspending early leaves you vulnerable later.

// =============================================================================
// Wave definitions
// =============================================================================
// Each wave object defines the parameters for all enemies in that wave:
//   count  — number of enemies to spawn
//   speed  — movement speed in pixels per second along the path
//   health — hit points each enemy starts with
//   reward — gold earned when the enemy is killed
//   delay  — seconds between each enemy spawn (lower = tighter groups)

var waves = [
    { count: 5,  speed: 80,  health: 100, reward: 10, delay: 1.0 },
    { count: 8,  speed: 90,  health: 150, reward: 12, delay: 0.9 },
    { count: 10, speed: 95,  health: 200, reward: 14, delay: 0.85 },
    { count: 12, speed: 100, health: 250, reward: 16, delay: 0.8 },
    { count: 15, speed: 105, health: 320, reward: 18, delay: 0.75 },
    { count: 15, speed: 110, health: 400, reward: 20, delay: 0.7 },
    { count: 18, speed: 115, health: 500, reward: 22, delay: 0.65 },
    { count: 20, speed: 120, health: 600, reward: 25, delay: 0.6 },
    { count: 22, speed: 130, health: 750, reward: 28, delay: 0.55 },
    { count: 25, speed: 140, health: 1000, reward: 35, delay: 0.5 }
];

// =============================================================================
// Game state variables
// =============================================================================

// Index into the waves array. 0-based; incremented when a new wave starts.
var currentWave = 0;

// Number of enemies currently alive on the battlefield.
var enemiesAlive = 0;

// Number of enemies spawned so far in the current wave.
var enemiesSpawned = 0;

// Countdown timer for spacing out enemy spawns within a wave.
var spawnTimer = 0;

// True while we are actively spawning enemies for the current wave.
var isSpawning = false;

// Player's gold. Used to purchase towers via PlacementGrid.js.
// Starting gold of 150 allows the player to place 2-3 basic towers.
var gold = 150;

// Number of lives (enemies that can reach the base before game over).
var lives = 20;

// Set to true when lives reach zero. Stops all spawning and input
// except the restart key.
var gameOver = false;

// True between waves — the player must press Enter to begin the next wave.
// Starts true so the very first wave requires an explicit start.
var waveComplete = true;

// Ordered array of {x, y} positions representing the enemy path.
// Populated in onStart by collecting PathNode actors from the scene.
var pathNodes = [];

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // -- Collect path nodes from the scene ------------------------------------
    // PathNode actors are named "PathNode0", "PathNode1", etc. We find all
    // actors tagged "PathNode" and sort them alphabetically by name so the
    // path order is correct regardless of scene file ordering.
    var nodeActors = Scene.findByTag("PathNode");

    if (nodeActors && nodeActors.length > 0) {
        // Sort by name to guarantee PathNode0, PathNode1, ... ordering
        nodeActors.sort(function(a, b) {
            if (a.name < b.name) return -1;
            if (a.name > b.name) return 1;
            return 0;
        });

        for (var i = 0; i < nodeActors.length; i++) {
            pathNodes.push({
                x: nodeActors[i].transform.x,
                y: nodeActors[i].transform.y
            });
        }
    }

    // -- Welcome banner -------------------------------------------------------
    log("========================================");
    log("        TOWER DEFENSE");
    log("========================================");
    log("  Gold  : " + gold);
    log("  Lives : " + lives);
    log("  Waves : " + waves.length);
    log("----------------------------------------");
    log("  1/2/3       : Select tower type");
    log("  Left Click  : Place tower");
    log("  Enter       : Start next wave");
    log("  R           : Restart (after game over)");
    log("========================================");
    log("Press ENTER to start Wave 1");
}

function onUpdate(dt) {
    // -- Game over state ------------------------------------------------------
    // After the player has lost, the only valid input is R to restart.
    if (gameOver) {
        if (Input.isKeyPressed("R")) {
            log("Restarting...");
            Scene.load("Scenes/Battlefield");
        }
        return;
    }

    // -- Waiting for player to start next wave --------------------------------
    // waveComplete is true between waves. The player presses Enter to proceed.
    if (waveComplete) {
        if (Input.isKeyPressed("Enter")) {
            startNextWave();
        }
        return;
    }

    // -- Spawning enemies -----------------------------------------------------
    // While isSpawning is true, we count down the spawn timer and create
    // new enemies at the interval defined by the current wave's delay.
    if (isSpawning) {
        spawnTimer -= dt;

        if (spawnTimer <= 0) {
            var wave = waves[currentWave - 1];

            if (enemiesSpawned < wave.count) {
                spawnEnemy();
                spawnTimer = wave.delay;
            } else {
                // All enemies for this wave have been spawned
                isSpawning = false;
            }
        }
    }

    // -- Wave completion check ------------------------------------------------
    // Once all enemies have been spawned and all are dead, the wave is over.
    if (!isSpawning && enemiesAlive <= 0 && enemiesSpawned > 0) {
        waveComplete = true;

        if (currentWave >= waves.length) {
            // Player has beaten all waves — victory!
            log("========================================");
            log("        VICTORY!");
            log("  You survived all " + waves.length + " waves!");
            log("  Gold remaining: " + gold);
            log("  Lives remaining: " + lives);
            log("  Press R to play again");
            log("========================================");
            gameOver = true;
        } else {
            log("Wave " + currentWave + " complete!");
            log("Gold: " + gold + "  |  Lives: " + lives);
            log("Press ENTER to start Wave " + (currentWave + 1));
        }
    }
}

// =============================================================================
// Wave management
// =============================================================================

// Advances to the next wave and begins the spawn sequence. Called when the
// player presses Enter during the waveComplete state.
function startNextWave() {
    currentWave++;

    if (currentWave > waves.length) {
        // Safety check — should not reach here due to onUpdate victory check
        return;
    }

    // Reset per-wave counters
    enemiesSpawned = 0;
    enemiesAlive = 0;
    spawnTimer = 0;
    isSpawning = true;
    waveComplete = false;

    var wave = waves[currentWave - 1];

    log("========================================");
    log("  WAVE " + currentWave + " / " + waves.length);
    log("  Enemies: " + wave.count + "  |  HP: " + wave.health + "  |  Speed: " + wave.speed);
    log("========================================");
}

// =============================================================================
// Enemy spawning
// =============================================================================

// Creates a single enemy actor at the first path node with all required
// components. The enemy's PathFollower.js script handles movement along
// the path, and its per-wave stats are set via the wave definition.
function spawnEnemy() {
    var wave = waves[currentWave - 1];
    var enemy = Scene.createActor("Enemy_" + currentWave + "_" + enemiesSpawned);

    if (enemy) {
        // Position at the start of the path
        if (pathNodes.length > 0) {
            enemy.transform.x = pathNodes[0].x;
            enemy.transform.y = pathNodes[0].y;
        }

        enemy.tag = "Enemy";

        // Visual appearance — enemies are red squares
        var sprite = enemy.addComponent("SpriteRenderer");
        if (sprite) {
            sprite.Color = { R: 220, G: 50, B: 50, A: 255 };
        }

        // Physics body with no gravity (top-down movement)
        var rb = enemy.addComponent("Rigidbody2D");
        if (rb) {
            rb.GravityScale = 0;
        }

        // Collision detection for tower bullets
        var col = enemy.addComponent("BoxCollider2D");
        if (col) {
            col.Width = 24;
            col.Height = 24;
        }

        // Attach the path-following behavior script
        var script = enemy.addComponent("ScriptComponent");
        if (script) {
            script.ScriptPath = "Scripts/PathFollower.js";
        }

        enemiesSpawned++;
        enemiesAlive++;
    }
}

// =============================================================================
// Enemy event handlers
// =============================================================================

// Called by PathFollower.js when an enemy reaches the final path node (the
// player's base). Decrements lives and checks for game over.
function onEnemyReachedBase() {
    lives--;
    enemiesAlive--;

    // Clamp to prevent negative count from race conditions
    if (enemiesAlive < 0) enemiesAlive = 0;

    log("Enemy reached base! Lives: " + lives);

    if (lives <= 0) {
        gameOver = true;
        log("========================================");
        log("         GAME OVER!");
        log("  Your base has been destroyed.");
        log("  Survived " + currentWave + " wave(s).");
        log("  Press R to restart");
        log("========================================");
    }
}

// Called by PathFollower.js when an enemy is killed by tower damage.
// Awards gold to the player's economy.
function onEnemyKilled(reward) {
    gold += reward;
    enemiesAlive--;

    // Clamp to prevent negative count from race conditions
    if (enemiesAlive < 0) enemiesAlive = 0;

    log("Enemy destroyed! +" + reward + " gold  |  Gold: " + gold + "  |  Remaining: " + enemiesAlive);
}

// =============================================================================
// Public API (called by other scripts)
// =============================================================================

// Returns the ordered path node positions for PathFollower.js to follow.
function getPathNodes() {
    return pathNodes;
}

// Returns the current gold amount. Used by PlacementGrid.js to check
// whether the player can afford a tower.
function getGold() {
    return gold;
}

// Deducts gold when a tower is purchased. Called by PlacementGrid.js
// after validation.
function spendGold(amount) {
    gold -= amount;
    log("Spent " + amount + " gold. Remaining: " + gold);
}

// Returns the current wave data so PathFollower.js can read enemy stats.
// Returns null if no wave is active.
function getCurrentWaveData() {
    if (currentWave > 0 && currentWave <= waves.length) {
        return waves[currentWave - 1];
    }
    return null;
}
