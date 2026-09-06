// WorldManager.js -- Day/night cycle, phase transitions, and night-time enemy waves
// Attach to the WorldManager actor (no visuals needed).
//
// The survival loop depends on time pressure:
//   DAY   --> gather resources, craft defences
//   NIGHT --> enemies spawn in waves; survive until dawn
//
// Each new day increases the number of enemies that spawn at night,
// creating an escalating difficulty curve.

// ---------------------------------------------------------------------------
// Day/night timing
// ---------------------------------------------------------------------------
var dayLength  = 60;        // seconds for one full day cycle
var timeOfDay  = 0;         // 0 .. dayLength
var dayNumber  = 1;

// Phase names and their start thresholds (fraction of dayLength)
var phaseThresholds = {
    morning: 0.0,
    noon:    0.25,
    evening: 0.5,
    night:   0.75
};

var phase   = "morning";
var isNight = false;

// ---------------------------------------------------------------------------
// Enemy wave settings
// ---------------------------------------------------------------------------
var enemiesAlive    = 0;
var maxNightEnemies = 3;        // increases each night
var spawnTimer      = 0;
var spawnInterval   = 3.0;      // seconds between spawns
var totalSpawned    = 0;

// Keep references to spawned enemies so we can clean up at dawn
var spawnedEnemies = [];

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

function onStart() {
    log("Day " + dayNumber + " -- Morning. Gather resources before nightfall!");
}

function onUpdate(dt) {
    // ----- Advance clock -----
    timeOfDay += dt;
    if (timeOfDay >= dayLength) {
        timeOfDay -= dayLength;
    }

    // ----- Determine current phase -----
    var fraction  = timeOfDay / dayLength;
    var newPhase  = getPhase(fraction);

    if (newPhase !== phase) {
        onPhaseChanged(phase, newPhase);
        phase = newPhase;
    }

    // ----- Night: spawn enemy waves -----
    if (isNight) {
        updateNightSpawns(dt);
    }
}

// ---------------------------------------------------------------------------
// Phase helpers
// ---------------------------------------------------------------------------

function getPhase(fraction) {
    // Walk thresholds in reverse order so the latest matching one wins
    if (fraction >= phaseThresholds.night)   { return "night";   }
    if (fraction >= phaseThresholds.evening)  { return "evening"; }
    if (fraction >= phaseThresholds.noon)     { return "noon";    }
    return "morning";
}

function onPhaseChanged(oldPhase, newPhase) {
    if (newPhase === "morning") {
        // A new day dawns
        endNight();
        dayNumber++;
        log("Dawn breaks. Day " + dayNumber + " begins. Gather and craft!");
    } else if (newPhase === "noon") {
        log("High noon. Half the day remains -- keep gathering!");
    } else if (newPhase === "evening") {
        log("Evening approaches. Prepare your defences!");
    } else if (newPhase === "night") {
        startNight();
    }
}

// ---------------------------------------------------------------------------
// Night cycle  (gather --> craft --> SURVIVE THE NIGHT)
// ---------------------------------------------------------------------------

function startNight() {
    isNight         = true;
    maxNightEnemies = 2 + dayNumber;  // escalating difficulty
    totalSpawned    = 0;
    enemiesAlive    = 0;
    spawnTimer      = 0;
    spawnedEnemies  = [];

    log("Night falls! Enemies approach... Survive until dawn!");
    log("Enemies this night: " + maxNightEnemies);
}

function updateNightSpawns(dt) {
    // Spawn enemies on a timer until we've reached the cap
    if (totalSpawned < maxNightEnemies) {
        spawnTimer += dt;
        if (spawnTimer >= spawnInterval) {
            spawnTimer -= spawnInterval;
            spawnNightEnemy();
        }
    }
}

function spawnNightEnemy() {
    // Pick a random position along the edges, away from the player
    var side = Math.floor(Math.random() * 4);
    var sx = 0;
    var sy = 0;

    if (side === 0) {
        // Top edge
        sx = Math.random() * 1280;
        sy = -20;
    } else if (side === 1) {
        // Bottom edge
        sx = Math.random() * 1280;
        sy = 740;
    } else if (side === 2) {
        // Left edge
        sx = -20;
        sy = Math.random() * 720;
    } else {
        // Right edge
        sx = 1300;
        sy = Math.random() * 720;
    }

    var enemy = Scene.createActor("NightEnemy_" + totalSpawned);
    if (enemy) {
        enemy.tag = "Enemy";
        enemy.transform.x = sx;
        enemy.transform.y = sy;

        var sr = enemy.addComponent("SpriteRenderer");
        if (sr) { sr.color = { R: 180, G: 30, B: 30, A: 255 }; }

        var rb = enemy.addComponent("Rigidbody2D");
        if (rb) {
            rb.mass         = 1.0;
            rb.gravityScale = 0;
            rb.freezeRotation = true;
        }

        var col = enemy.addComponent("BoxCollider2D");
        if (col) { col.size = { x: 24, y: 24 }; }

        var sc = enemy.addComponent("ScriptComponent");
        if (sc) { sc.scriptPath = "Scripts/EnemyWander.js"; }

        spawnedEnemies.push(enemy);
    }

    totalSpawned++;
    enemiesAlive++;
    log("An enemy emerges from the darkness! (" + totalSpawned + "/" + maxNightEnemies + ")");
}

function endNight() {
    isNight = false;

    // Destroy any remaining enemies that survived the night
    for (var i = 0; i < spawnedEnemies.length; i++) {
        var enemy = spawnedEnemies[i];
        if (enemy) {
            Scene.destroyActor(enemy);
        }
    }
    spawnedEnemies = [];
    enemiesAlive   = 0;
    totalSpawned   = 0;

    log("The creatures retreat as sunlight returns.");
}

// ---------------------------------------------------------------------------
// Called when an enemy is killed (from EnemyWander.js)
// ---------------------------------------------------------------------------

function onEnemyKilled() {
    enemiesAlive--;
    if (enemiesAlive < 0) { enemiesAlive = 0; }
    log("Enemy defeated! " + enemiesAlive + " remain.");
}
