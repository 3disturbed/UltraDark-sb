// FightManager.js — Round and match logic for the 1v1 fighting game
// Attach this script to the FightManager actor in the scene.
//
// Responsibilities:
//   1. Track rounds won by each player (first to 2 wins takes the match).
//   2. Display ASCII health bars in the console after each hit.
//   3. Handle round resets — reposition fighters and restore health.
//   4. Handle match-over state and restart via the R key.
//
// The FightManager does not control the fighters directly. Instead, the
// Fighter.js scripts call into this manager via Scene.find("FightManager")
// to report health changes and KOs.

// =============================================================================
// Match settings
// =============================================================================

// Number of round wins required to take the match.
var roundsToWin = 2;

// Rounds won by each player this match.
var p1Wins = 0;
var p2Wins = 0;

// Current round number (starts at 1).
var currentRound = 1;

// =============================================================================
// Round state
// =============================================================================

// Set to true when a fighter is KO'd. While true, gameplay is paused
// and a brief delay runs before the next round starts.
var roundOver = false;

// Countdown timer for the pause between rounds.
var roundTimer = 0;

// How long to wait (in seconds) before resetting for the next round.
var roundResetDelay = 2.0;

// =============================================================================
// Match state
// =============================================================================

// Set to true when one player has won enough rounds to take the match.
// While true, the game is fully paused and only the R key (restart) works.
var matchOver = false;

// =============================================================================
// Fighter spawn positions
// =============================================================================

// Default starting positions for each fighter at the beginning of a round.
var p1StartX = 300;
var p1StartY = 420;
var p2StartX = 980;
var p2StartY = 420;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // Print a fight banner to the console
    log("========================================");
    log("    _____ ___ ____ _   _ _____ _");
    log("   |  ___|_ _/ ___| | | |_   _| |");
    log("   | |_   | | |  _| |_| | | | | |");
    log("   |  _|  | | |_| |  _  | | | |_|");
    log("   |_|   |___\\____|_| |_| |_| (_)");
    log("========================================");
    log("");
    log("  P1 (Blue)                P2 (Red)");
    log("  W     = Jump             Up    = Jump");
    log("  A/D   = Move             Left/Right = Move");
    log("  F     = Attack           K     = Attack");
    log("  G     = Block            L     = Block");
    log("");
    log("  First to " + roundsToWin + " round wins takes the match!");
    log("========================================");
    log("");
    log("=== ROUND " + currentRound + " --- FIGHT! ===");

    // Show initial health bars
    displayHealthBars(100, 100, 100, 100);
}

function onUpdate(dt) {
    // -- Match over state -----------------------------------------------------
    // The match is done. Only accept the R key to restart the entire scene.
    if (matchOver) {
        if (Input.isKeyPressed("R")) {
            Scene.load("Scenes/FightArena");
        }
        return;
    }

    // -- Round over state -----------------------------------------------------
    // A fighter was KO'd. Wait a brief moment, then reset for the next round.
    if (roundOver) {
        roundTimer -= dt;

        if (roundTimer <= 0) {
            resetRound();
        }
        return;
    }
}

// =============================================================================
// Health bar display
// =============================================================================

// Called by Fighter.js whenever a fighter takes damage. Builds an ASCII
// health bar for both players and logs it to the console.
//
// Bar format:  P1: [========  ] 80/100 HP    P2: [=====     ] 50/100 HP
function onHealthChanged(playerTag, health, maxHealth) {
    // We need both players' health to build the full display
    var p1Health = 100;
    var p2Health = 100;
    var p1Max = 100;
    var p2Max = 100;

    var player1 = Scene.find("Player1");
    var player2 = Scene.find("Player2");

    if (player1) {
        p1Health = player1.health;
        p1Max = player1.maxHealth;
    }
    if (player2) {
        p2Health = player2.health;
        p2Max = player2.maxHealth;
    }

    // Override with the freshly reported value (may not be synced yet)
    if (playerTag === "Player1") {
        p1Health = health;
        p1Max = maxHealth;
    } else {
        p2Health = health;
        p2Max = maxHealth;
    }

    displayHealthBars(p1Health, p1Max, p2Health, p2Max);
}

// Builds and logs the ASCII health bars for both players.
function displayHealthBars(p1Health, p1Max, p2Health, p2Max) {
    var barLength = 20;

    // Calculate filled segments for each bar
    var p1Filled = Math.round((p1Health / p1Max) * barLength);
    var p2Filled = Math.round((p2Health / p2Max) * barLength);

    // Clamp to valid range
    if (p1Filled < 0) p1Filled = 0;
    if (p2Filled < 0) p2Filled = 0;

    // Build P1 bar string
    var p1Bar = "";
    for (var i = 0; i < barLength; i++) {
        if (i < p1Filled) {
            p1Bar += "=";
        } else {
            p1Bar += " ";
        }
    }

    // Build P2 bar string
    var p2Bar = "";
    for (var j = 0; j < barLength; j++) {
        if (j < p2Filled) {
            p2Bar += "=";
        } else {
            p2Bar += " ";
        }
    }

    // Pad health numbers for alignment
    var p1Hp = Math.floor(p1Health);
    var p2Hp = Math.floor(p2Health);

    var p1Str = "P1: [" + p1Bar + "] " + p1Hp + "/" + p1Max + " HP";
    var p2Str = "P2: [" + p2Bar + "] " + p2Hp + "/" + p2Max + " HP";

    log(p1Str + "    " + p2Str);
}

// =============================================================================
// Player KO
// =============================================================================

// Called by Fighter.js when a fighter's health reaches zero.
// Awards a round win to the surviving player and checks for match victory.
function onPlayerKO(playerTag) {
    if (roundOver || matchOver) {
        return;
    }

    // Award the round to the other player
    if (playerTag === "Player1") {
        p2Wins++;
        log(">>> Player 2 wins round " + currentRound + "! <<<");
    } else {
        p1Wins++;
        log(">>> Player 1 wins round " + currentRound + "! <<<");
    }

    log("Score:  P1 [" + p1Wins + "]  ---  P2 [" + p2Wins + "]");

    roundOver = true;
    roundTimer = roundResetDelay;

    // Check for match victory
    if (p1Wins >= roundsToWin) {
        matchOver = true;
        log("");
        log("========================================");
        log("   PLAYER 1 WINS THE MATCH!");
        log("   Final Score:  P1 [" + p1Wins + "]  ---  P2 [" + p2Wins + "]");
        log("   Press R to play again");
        log("========================================");
    } else if (p2Wins >= roundsToWin) {
        matchOver = true;
        log("");
        log("========================================");
        log("   PLAYER 2 WINS THE MATCH!");
        log("   Final Score:  P1 [" + p1Wins + "]  ---  P2 [" + p2Wins + "]");
        log("   Press R to play again");
        log("========================================");
    } else {
        log("Next round in " + roundResetDelay + " seconds...");
    }
}

// =============================================================================
// Round reset
// =============================================================================

// Resets both fighters to their starting positions and restores their health.
// Increments the round counter and announces the new round.
function resetRound() {
    currentRound++;

    // Find both fighters
    var player1 = Scene.find("Player1");
    var player2 = Scene.find("Player2");

    // Reset Player 1
    if (player1) {
        player1.transform.x = p1StartX;
        player1.transform.y = p1StartY;
        player1.health = 100;
        player1.maxHealth = 100;
        player1.isAttacking = false;
        player1.isBlocking = false;
        player1.hitstunTimer = 0;
        player1.cooldownTimer = 0;
        player1.attackTimer = 0;

        var rb1 = player1.getComponent("Rigidbody2D");
        if (rb1) {
            rb1.velocityX = 0;
            rb1.velocityY = 0;
        }
    }

    // Reset Player 2
    if (player2) {
        player2.transform.x = p2StartX;
        player2.transform.y = p2StartY;
        player2.health = 100;
        player2.maxHealth = 100;
        player2.isAttacking = false;
        player2.isBlocking = false;
        player2.hitstunTimer = 0;
        player2.cooldownTimer = 0;
        player2.attackTimer = 0;

        var rb2 = player2.getComponent("Rigidbody2D");
        if (rb2) {
            rb2.velocityX = 0;
            rb2.velocityY = 0;
        }
    }

    roundOver = false;

    log("");
    log("=== ROUND " + currentRound + " --- FIGHT! ===");
    displayHealthBars(100, 100, 100, 100);
}
