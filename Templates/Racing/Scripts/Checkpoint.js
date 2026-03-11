// Checkpoint.js — Invisible trigger zone that reports car crossings to the RaceManager
// Attach this script to each Checkpoint actor (Checkpoint0–Checkpoint3) in the scene.
//
// Each checkpoint extracts its index from its actor name (e.g. "Checkpoint2" → 2).
// When a car enters the trigger, it notifies the RaceManager so that lap progress
// can be validated and recorded.

// =============================================================================
// Variables
// =============================================================================

// The index of this checkpoint (0–3). Extracted from the actor name in onStart().
// The RaceManager uses this to enforce sequential checkpoint order.
var checkpointIndex = 0;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // Extract the checkpoint index from the actor name.
    // Actor names follow the pattern "CheckpointN" where N is 0–3.
    // We grab the last character(s) after "Checkpoint" and parse as integer.
    var name = actor.name;
    var prefix = "Checkpoint";

    if (name.indexOf(prefix) === 0) {
        checkpointIndex = parseInt(name.substring(prefix.length), 10);
    }
}

// =============================================================================
// Trigger callbacks
// =============================================================================

// Called by the physics system when another collider enters this trigger zone.
// Only responds to actors tagged "Car" — walls and other checkpoints are ignored.
function onTriggerEnter(other) {
    if (other.tag === "Car") {
        // Notify the RaceManager that a car crossed this checkpoint.
        // The RaceManager validates the order and tracks lap progress.
        var manager = Scene.find("RaceManager");
        if (manager) {
            manager.onCheckpointHit(other.name, checkpointIndex);
        }
    }
}
