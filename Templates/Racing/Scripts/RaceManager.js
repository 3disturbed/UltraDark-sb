// RaceManager.js — Race state, lap tracking, countdown, and winner declaration
// Attach this script to the RaceManager actor in the RaceTrack scene.
//
// Responsibilities:
//   1. Run a 3-2-1-GO countdown before the race begins.
//   2. Track which checkpoints each player has hit (in sequential order).
//   3. Count laps and record lap times for each player.
//   4. Declare a winner when a player completes all laps.
//   5. Allow restarting the race with the R key after it ends.
//
// Checkpoint validation:
//   Checkpoints must be hit in order (0 → 1 → 2 → 3 → 0...). Hitting a
//   checkpoint out of order is silently ignored. This prevents players from
//   cheating by driving back and forth over the start/finish line.
//
// Communication:
//   Checkpoint.js calls onCheckpointHit(carName, checkpointIndex) on this
//   manager via Scene.find("RaceManager") whenever a car enters a checkpoint
//   trigger zone.

// =============================================================================
// Race settings
// =============================================================================

// Number of laps required to finish the race. 3 laps gives a satisfying race
// length — long enough for comebacks but short enough to avoid tedium.
var totalLaps = 3;

// Total number of checkpoints on the track. Must match the number of
// Checkpoint actors in the scene (Checkpoint0 through Checkpoint3).
var totalCheckpoints = 4;

// =============================================================================
// Per-player tracking
// =============================================================================

// The index of the NEXT checkpoint each player must hit. Starts at 0
// (the start/finish line). After hitting checkpoint 0, the player must
// hit checkpoint 1, then 2, then 3, then back to 0 to complete a lap.
var p1NextCheckpoint = 0;
var p2NextCheckpoint = 0;

// Number of completed laps for each player.
var p1Laps = 0;
var p2Laps = 0;

// Array of lap times (in seconds) for each player. Used to display lap
// splits and determine the fastest lap.
var p1LapTimes = [];
var p2LapTimes = [];

// Timestamp (in race-time seconds) when the current lap started for each
// player. Used to calculate lap duration when a lap is completed.
var p1LapStart = 0;
var p2LapStart = 0;

// =============================================================================
// Race state
// =============================================================================

// Total elapsed race time in seconds (starts counting after countdown).
var raceTimer = 0;

// Whether the race has started (countdown finished).
var raceStarted = false;

// Pre-race countdown value (3, 2, 1, GO!).
var countdown = 3;

// Timer for the countdown sequence.
var countdownTimer = 0;

// Whether the race is over (a player completed all laps).
var raceOver = false;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    log("========================================");
    log("          TOP-DOWN RACING");
    log("========================================");
    log("  P1 (Blue):  W/A/S/D");
    log("  P2 (Red):   Arrow Keys");
    log("  R:          Restart (after race ends)");
    log("========================================");
    log("");
    log("  " + totalLaps + " laps — hit checkpoints in order!");
    log("");
    log("  3...");
}

function onUpdate(dt) {
    // -- Race over state ------------------------------------------------------
    // Once a winner is declared, the only valid input is R to restart.
    if (raceOver) {
        if (Input.isKeyPressed("R")) {
            Scene.load("Scenes/RaceTrack");
        }
        return;
    }

    // -- Countdown phase ------------------------------------------------------
    // Before the race begins, run a 3-2-1-GO countdown. Each second of the
    // countdown is logged to the console. Once the countdown reaches zero,
    // the race starts and the timer begins.
    if (!raceStarted) {
        countdownTimer += dt;

        if (countdownTimer >= 1.0) {
            countdownTimer -= 1.0;
            countdown--;

            if (countdown > 0) {
                log("  " + countdown + "...");
            } else {
                log("  GO! GO! GO!");
                log("");
                raceStarted = true;
                raceTimer = 0;
                p1LapStart = 0;
                p2LapStart = 0;
            }
        }
        return;
    }

    // -- Active race ----------------------------------------------------------
    // Increment the race timer. This is used for lap time calculations and
    // the final race time display.
    raceTimer += dt;
}

// =============================================================================
// Checkpoint hit handler
// =============================================================================

// Called by Checkpoint.js when a car enters a checkpoint trigger zone.
// Validates that the checkpoint is the next expected one for that player,
// then advances the checkpoint counter. When a player completes all
// checkpoints and returns to checkpoint 0, a lap is recorded.
//
// Parameters:
//   carName        — name of the car actor ("Player1Car" or "Player2Car")
//   checkpointIndex — index of the checkpoint (0-3)
function onCheckpointHit(carName, checkpointIndex) {
    // Ignore checkpoint hits before the race starts or after it ends
    if (!raceStarted || raceOver) return;

    // Determine which player hit the checkpoint
    if (carName === "Player1Car") {
        handleCheckpoint(1, checkpointIndex);
    } else if (carName === "Player2Car") {
        handleCheckpoint(2, checkpointIndex);
    }
}

// Internal helper that processes a checkpoint hit for a given player.
function handleCheckpoint(playerNum, checkpointIndex) {
    // Read the player-specific state
    var nextCP, laps, lapStart, lapTimes;

    if (playerNum === 1) {
        nextCP = p1NextCheckpoint;
        laps = p1Laps;
        lapStart = p1LapStart;
        lapTimes = p1LapTimes;
    } else {
        nextCP = p2NextCheckpoint;
        laps = p2Laps;
        lapStart = p2LapStart;
        lapTimes = p2LapTimes;
    }

    // Validate sequential order: the checkpoint index must match the next
    // expected checkpoint. Out-of-order hits are silently ignored.
    if (checkpointIndex !== nextCP) return;

    // Advance to the next checkpoint (wraps around using modulo)
    nextCP = (nextCP + 1) % totalCheckpoints;

    // Check if this completes a lap (player just hit the last checkpoint
    // and the next expected checkpoint wrapped back to 0)
    if (nextCP === 0) {
        // Lap complete!
        laps++;
        var lapTime = raceTimer - lapStart;
        lapTimes.push(lapTime);

        log("P" + playerNum + " completed lap " + laps + "/" + totalLaps +
            "  [" + formatTime(lapTime) + "]");

        // Reset lap start time for the next lap
        lapStart = raceTimer;

        // Check for race completion
        if (laps >= totalLaps) {
            finishRace(playerNum, lapTimes);
        }
    }

    // Write the updated state back to the player-specific variables
    if (playerNum === 1) {
        p1NextCheckpoint = nextCP;
        p1Laps = laps;
        p1LapStart = lapStart;
    } else {
        p2NextCheckpoint = nextCP;
        p2Laps = laps;
        p2LapStart = lapStart;
    }
}

// =============================================================================
// Race finish
// =============================================================================

// Called when a player completes all required laps. Declares them the winner
// and displays a summary of all lap times.
function finishRace(winnerNum, lapTimes) {
    raceOver = true;

    log("");
    log("========================================");
    log("   PLAYER " + winnerNum + " WINS THE RACE!");
    log("========================================");
    log("   Total time: " + formatTime(raceTimer));
    log("");

    // Display individual lap times
    for (var i = 0; i < lapTimes.length; i++) {
        log("   Lap " + (i + 1) + ": " + formatTime(lapTimes[i]));
    }

    // Find and display the fastest lap
    if (lapTimes.length > 0) {
        var fastest = lapTimes[0];
        for (var j = 1; j < lapTimes.length; j++) {
            if (lapTimes[j] < fastest) {
                fastest = lapTimes[j];
            }
        }
        log("   Fastest lap: " + formatTime(fastest));
    }

    log("");
    log("   Press R to race again!");
    log("========================================");
}

// =============================================================================
// Utility
// =============================================================================

// Formats a time value in seconds into a human-readable string (e.g. "1:23.45").
function formatTime(seconds) {
    var mins = Math.floor(seconds / 60);
    var secs = seconds - (mins * 60);

    // Round to 2 decimal places
    secs = Math.floor(secs * 100) / 100;

    // Pad seconds with leading zero if < 10
    var secStr = "";
    if (secs < 10) {
        secStr = "0" + secs.toFixed(2);
    } else {
        secStr = secs.toFixed(2);
    }

    return mins + ":" + secStr;
}
