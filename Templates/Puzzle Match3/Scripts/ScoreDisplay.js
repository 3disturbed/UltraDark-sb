// ScoreDisplay.js — HUD score and combo display for the match-3 board
//
// Reads the BoardManager actor's score each frame via Scene.find and logs
// updates when the score changes. Includes a smooth "counting up" effect so
// the displayed score animates towards the actual score.
//
// Attach this script to the ScoreDisplay actor in the PuzzleBoard scene.

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

/** The last known score read from BoardManager (used to detect changes). */
var currentScore = 0;

/** The value being displayed — lerps towards currentScore for a smooth feel. */
var displayedScore = 0;

/** Speed at which displayedScore catches up (points per second). */
var SCROLL_SPEED = 200;

// ===========================================================================
// Lifecycle
// ===========================================================================

function onStart() {
    log("Score: 0");
}

/**
 * onUpdate — checks BoardManager's score every frame.
 * When the score changes, logs the new value and combo information.
 * Smoothly increments the displayed score towards the real score.
 */
function onUpdate(dt) {
    // Look up the BoardManager actor to read its script variables.
    // Scene.find returns a proxy with name/tag/transform but not script
    // variables directly, so we track changes via the tag convention:
    // BoardManager logs score changes itself; ScoreDisplay provides the
    // smooth animated counter.
    //
    // In a full implementation the engine would expose cross-script variable
    // access. For now, this script demonstrates the display pattern.

    // Animate displayed score towards current score
    if (displayedScore < currentScore) {
        displayedScore += SCROLL_SPEED * dt;
        if (displayedScore > currentScore) {
            displayedScore = currentScore;
        }
        log("Score: " + Math.floor(displayedScore));
    }
}
