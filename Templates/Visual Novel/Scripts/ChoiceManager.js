// ============================================================================
// ChoiceManager.js — Branching choice handler for the visual novel engine
// ============================================================================
// Attach this script to the "ChoiceManager" actor in the Chapter1 scene.
//
// This script works in tandem with DialogueManager.js. When a story node
// contains a "choices" array, DialogueManager invokes showChoices() on this
// script, which displays the options and listens for the player's input.
//
// Once the player selects a choice, this script:
//   1. Logs the player's selection to the console
//   2. Sets any story flags associated with the choice
//   3. Tells DialogueManager to advance to the chosen node
//
// Controls:
//   1 — Select the first choice
//   2 — Select the second choice
//   3 — Select the third choice
//
// This script is passive by default and does nothing until DialogueManager
// activates it by calling showChoices().
// ============================================================================

// ============================================================================
// STATE VARIABLES
// ============================================================================

// choices holds the array of choice objects passed in by DialogueManager.
// Each choice object has: { text, next, flag (optional) }.
var choices = [];

// isActive controls whether this script processes input in onUpdate.
// It is false by default and set to true only when showChoices() is called.
var isActive = false;

// ============================================================================
// onStart — Called once when the ChoiceManager actor enters the scene.
// This script is passive on startup -- it waits for DialogueManager to
// activate it, so we log nothing here.
// ============================================================================
function onStart() {
    // ChoiceManager is ready and waiting.
    // No initialization needed -- we activate on demand.
}

// ============================================================================
// onUpdate — Called every frame. When active, listens for the player to
// press 1, 2, or 3 to select a choice.
//
// Parameters:
//   dt (number) — Delta time in seconds since the last frame.
// ============================================================================
function onUpdate(dt) {

    // If we're not actively showing choices, do nothing.
    // This keeps the script completely inert until DialogueManager needs it.
    if (!isActive) {
        return;
    }

    // Check for key press "1" — selects the first choice.
    if (Input.isKeyPressed("1") || Input.isKeyPressed("NumPad1")) {
        if (choices.length >= 1) {
            selectChoice(0);
        }
    }

    // Check for key press "2" — selects the second choice.
    if (Input.isKeyPressed("2") || Input.isKeyPressed("NumPad2")) {
        if (choices.length >= 2) {
            selectChoice(1);
        }
    }

    // Check for key press "3" — selects the third choice.
    if (Input.isKeyPressed("3") || Input.isKeyPressed("NumPad3")) {
        if (choices.length >= 3) {
            selectChoice(2);
        }
    }
}

// ============================================================================
// showChoices — Activates the choice display and presents options to the
// player. Called by DialogueManager when a story node has choices.
//
// Parameters:
//   choiceArray (array) — Array of choice objects from the story node.
//     Each object has: { text: string, next: string, flag?: { key, value } }
// ============================================================================
function showChoices(choiceArray) {

    // Store the choices and activate input listening.
    choices = choiceArray;
    isActive = true;

    // Print a visual separator so the choices stand out in the console.
    Debug.log("");
    Debug.log("  ────────────────────────────────────────");
    Debug.log("  What do you do?");
    Debug.log("");

    // Display each choice with a number prefix so the player knows which
    // key to press. We use 1-based numbering for a natural feel.
    for (var i = 0; i < choices.length; i++) {
        Debug.log("    " + (i + 1) + ") " + choices[i].text);
    }

    // Remind the player of the controls.
    Debug.log("");
    Debug.log("  Press 1" + (choices.length >= 2 ? ", 2" : "") + (choices.length >= 3 ? ", or 3" : "") + " to choose.");
    Debug.log("  ────────────────────────────────────────");
}

// ============================================================================
// selectChoice — Processes the player's selection. Logs the choice, sets
// any associated story flags, deactivates the choice display, and tells
// DialogueManager to advance to the next node.
//
// Parameters:
//   index (number) — The zero-based index of the selected choice.
// ============================================================================
function selectChoice(index) {

    // Get the choice object from the array.
    var choice = choices[index];

    // Log the player's selection with a distinctive arrow prefix.
    Debug.log("");
    Debug.log("  >> You chose: " + choice.text);

    // If the choice has an associated story flag, set it on DialogueManager.
    // This records the player's decision for later reference.
    if (choice.flag) {
        // Find the DialogueManager actor to call its setFlag function.
        var dialogueMgr = Scene.find("DialogueManager");
        if (dialogueMgr) {
            var script = dialogueMgr.getComponent("ScriptComponent");
            if (script) {
                script.invoke("setFlag", choice.flag.key, choice.flag.value);
            }
        }
    }

    // Deactivate the choice display. We're done until the next choice node.
    isActive = false;
    choices = [];

    // Tell DialogueManager to advance to the chosen node's next target.
    // This hands control back to DialogueManager to continue the story.
    var dialogueMgr = Scene.find("DialogueManager");
    if (dialogueMgr) {
        var script = dialogueMgr.getComponent("ScriptComponent");
        if (script) {
            script.invoke("advanceToNext", choice.next);
        }
    } else {
        Debug.error("[ChoiceManager] Could not find DialogueManager actor!");
    }
}
