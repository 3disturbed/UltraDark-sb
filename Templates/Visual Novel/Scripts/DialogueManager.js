// ============================================================================
// DialogueManager.js — Core visual novel engine with typewriter text
//                      and story progression
// ============================================================================
// Attach this script to the "DialogueManager" actor in the Chapter1 scene.
//
// This script drives the entire visual novel experience:
//   - Loads and traverses a branching story graph
//   - Displays dialogue with a typewriter reveal effect
//   - Manages character portrait highlighting per speaker
//   - Tracks player decisions via a story flag system
//   - Coordinates with ChoiceManager.js for branching choices
//
// Controls:
//   SPACE — Advance to the next dialogue line (or skip typewriter)
//   1/2/3 — Select a choice when options are presented
//   R     — Restart the story after reaching the ending
//
// The story data is embedded directly in this file because each script
// in SexyBiscuit runs in its own isolated Jint runtime. To create your
// own stories, edit the "story" array below. See StoryData.js for a
// complete authoring reference guide.
// ============================================================================

// ============================================================================
// STORY DATA
// ============================================================================
// Each node in the story array represents one dialogue beat.
//
// Node format:
//   id      — Unique string identifier for this node.
//   speaker — Name of the character speaking (displayed in the name tag).
//   text    — The dialogue text to display with the typewriter effect.
//   next    — The id of the next node to advance to (null = end of story).
//   choices — (Optional) Array of choice objects instead of a fixed "next".
//
// Choice format:
//   text — The label shown to the player for this option.
//   next — The node id to jump to if this choice is selected.
//   flag — (Optional) { key, value } pair to store in storyFlags.
// ============================================================================

var story = [
    {
        id: "start",
        speaker: "Narrator",
        text: "You find yourself at the entrance of an ancient library. Towering shelves stretch into darkness above, and the air smells of old parchment and candle wax.",
        next: "look_around"
    },
    {
        id: "look_around",
        speaker: "Narrator",
        text: "Dust motes drift through beams of pale light. The silence is so deep you can hear your own heartbeat echoing off the stone walls.",
        next: "meet_keeper"
    },
    {
        id: "meet_keeper",
        speaker: "???",
        text: "Ah, a visitor! It's been quite some time since anyone found this place.",
        next: "keeper_emerge"
    },
    {
        id: "keeper_emerge",
        speaker: "Narrator",
        text: "A figure steps from behind a towering bookcase. They wear robes covered in tiny, shifting letters -- as though the fabric itself were made of living text.",
        next: "keeper_intro"
    },
    {
        id: "keeper_intro",
        speaker: "Keeper",
        text: "I am the Keeper of this library. Every book here contains a world -- complete with its own skies, its own people, its own stories waiting to be lived.",
        next: "keeper_question"
    },
    {
        id: "keeper_question",
        speaker: "Keeper",
        text: "Tell me, traveler -- what brings you to my domain?",
        choices: [
            { text: "I'm looking for knowledge", next: "path_knowledge", flag: { key: "motivation", value: "knowledge" } },
            { text: "I stumbled in by accident", next: "path_accident", flag: { key: "motivation", value: "accident" } },
            { text: "I was sent here by someone", next: "path_sent", flag: { key: "motivation", value: "sent" } }
        ]
    },
    {
        id: "path_knowledge",
        speaker: "Keeper",
        text: "A seeker of wisdom! How delightful. The library rewards the curious -- it has a way of revealing exactly what you need, even when you don't know what that is.",
        next: "keeper_smile"
    },
    {
        id: "path_accident",
        speaker: "Keeper",
        text: "There are no accidents in this place. The library called to you, traveler. It felt something in your heart and opened a door that only you could find.",
        next: "keeper_smile"
    },
    {
        id: "path_sent",
        speaker: "Keeper",
        text: "Sent by whom, I wonder? No matter -- you are here now, and that is what matters. The library does not concern itself with how its guests arrive, only with what they seek.",
        next: "keeper_smile"
    },
    {
        id: "keeper_smile",
        speaker: "Narrator",
        text: "The Keeper smiles warmly. Light dances across the shifting letters on their robes, forming words you almost recognize before they rearrange themselves again.",
        next: "book_offer"
    },
    {
        id: "book_offer",
        speaker: "Keeper",
        text: "I can offer you one book to take with you when you leave. Each contains great power -- but choose carefully, for the book will shape your journey ahead.",
        choices: [
            { text: "The Book of Fire -- bound in crimson leather", next: "chose_fire", flag: { key: "book", value: "fire" } },
            { text: "The Book of Water -- cool blue with silver clasps", next: "chose_water", flag: { key: "book", value: "water" } }
        ]
    },
    {
        id: "chose_fire",
        speaker: "Keeper",
        text: "Fire -- the element of passion and destruction. A bold choice. This book will teach you to burn away falsehood and forge new paths where none existed before.",
        next: "book_glow"
    },
    {
        id: "chose_water",
        speaker: "Keeper",
        text: "Water -- the element of wisdom and adaptability. A thoughtful choice. This book will teach you to flow around obstacles and find the hidden depths in all things.",
        next: "book_glow"
    },
    {
        id: "book_glow",
        speaker: "Narrator",
        text: "The Keeper reaches into the shelf and produces the book. The moment your fingers touch the cover, warmth floods through your hands and up your arms. The letters on the Keeper's robes swirl excitedly.",
        next: "farewell"
    },
    {
        id: "farewell",
        speaker: "Keeper",
        text: "The book has accepted you. Go now, traveler, and write your own story. Perhaps one day it too will find its way onto these shelves.",
        next: "ending"
    },
    {
        id: "ending",
        speaker: "Narrator",
        text: "You step back through the library's entrance. The heavy doors close behind you with a gentle sigh, and when you look back, there is only an ivy-covered wall. But the book in your hands is warm, and full of possibility.",
        next: "credits"
    },
    {
        id: "credits",
        speaker: "System",
        text: "=== End of Chapter 1: The Meeting === Thank you for playing! Your choices have been logged to the console. Press R to replay the story.",
        next: null
    }
];

// ============================================================================
// STATE VARIABLES
// ============================================================================

// currentNodeIndex stores the array index of the current story node.
// We also look up nodes by id, but keeping the index is useful for debugging.
var currentNodeIndex = 0;

// displayedText holds the portion of the current line that has been revealed
// so far by the typewriter effect. It grows one character at a time.
var displayedText = "";

// fullText holds the complete text of the current dialogue line.
// The typewriter effect reveals characters from this string.
var fullText = "";

// charIndex tracks how many characters of fullText have been revealed.
// When charIndex equals fullText.length, the typewriter effect is complete.
var charIndex = 0;

// typewriterSpeed is the delay in seconds between each character reveal.
// Lower values make text appear faster. 0.03 gives a pleasant reading pace.
var typewriterSpeed = 0.03;

// typewriterTimer accumulates elapsed time between character reveals.
// When it exceeds typewriterSpeed, we reveal the next character and reset it.
var typewriterTimer = 0;

// isTyping is true while the typewriter effect is actively revealing text.
// While true, pressing Space will skip ahead and show the full line instantly.
var isTyping = false;

// isWaitingForInput is true when the full line has been displayed and the
// engine is waiting for the player to press Space to continue.
var isWaitingForInput = false;

// isShowingChoices is true when a choice node is active and we are waiting
// for the player to press 1, 2, or 3. The ChoiceManager handles the input.
var isShowingChoices = false;

// storyFlags is a dictionary that tracks player decisions throughout the story.
// Scripts can set flags with setFlag() and read them with getFlag().
// Example: { motivation: "knowledge", book: "fire" }
var storyFlags = {};

// currentSpeaker holds the name of the character currently speaking.
// This is used to update portrait highlighting and format log output.
var currentSpeaker = "";

// hasStarted tracks whether the story has begun. We use this to prevent
// the opening node from being triggered more than once.
var hasStarted = false;

// isEnded tracks whether the story has reached its conclusion (next: null).
// When true, the engine listens for R to restart instead of Space to advance.
var isEnded = false;

// ============================================================================
// onStart — Called once when the DialogueManager actor enters the scene.
// We print the chapter banner and begin the first story node.
// ============================================================================
function onStart() {

    // Print a clear visual banner so the player can orient themselves.
    Debug.log("================================================================");
    Debug.log("  CHAPTER 1: THE MEETING");
    Debug.log("================================================================");
    Debug.log("");

    // Print control instructions so the player knows how to interact.
    Debug.log("  Controls:");
    Debug.log("    SPACE — Advance dialogue / skip typewriter");
    Debug.log("    1/2/3 — Select a choice when options appear");
    Debug.log("    R     — Restart the story (after ending)");
    Debug.log("");
    Debug.log("================================================================");
    Debug.log("");

    // Mark the story as started and begin the first node.
    hasStarted = true;

    // Start the story at the first node by its id.
    startNode("start");
}

// ============================================================================
// onUpdate — Called every frame. Handles typewriter progression, input
// detection, and state transitions.
// ============================================================================
function onUpdate(dt) {

    // --- Choice State ---
    // When choices are being displayed, the ChoiceManager handles all input.
    // We do nothing here and wait for ChoiceManager to call back into us.
    if (isShowingChoices) {
        return;
    }

    // --- Typewriter State ---
    // While the typewriter is actively revealing text, we advance character
    // by character based on the elapsed time.
    if (isTyping) {

        // Accumulate the frame's delta time into the typewriter timer.
        typewriterTimer = typewriterTimer + dt;

        // Reveal characters as long as enough time has passed.
        // We use a while loop so that at low frame rates we can reveal
        // multiple characters per frame to maintain the correct speed.
        while (typewriterTimer >= typewriterSpeed && charIndex < fullText.length) {

            // Move to the next character in the full text.
            charIndex = charIndex + 1;

            // Update the displayed text to include all revealed characters.
            displayedText = fullText.substring(0, charIndex);

            // Reset the timer, keeping any leftover time for accuracy.
            typewriterTimer = typewriterTimer - typewriterSpeed;
        }

        // Check if the typewriter has finished revealing the entire line.
        if (charIndex >= fullText.length) {
            finishTypewriter();
        }

        // Allow the player to skip the typewriter by pressing Space.
        // This immediately reveals the full line, which is standard in
        // visual novels -- players who read quickly appreciate this.
        if (Input.isKeyPressed("Space")) {
            skipTypewriter();
        }

        return;
    }

    // --- Waiting for Input State ---
    // The full line has been displayed. Wait for the player to press Space
    // to advance to the next node in the story.
    if (isWaitingForInput) {
        if (Input.isKeyPressed("Space")) {

            // Clear the waiting state and advance to the next node.
            isWaitingForInput = false;

            // Find the current node to determine where to go next.
            var node = findNodeById(currentSpeaker);

            // We stored the current node's id when we started it, so
            // search the story array for the matching node.
            var currentNode = null;
            for (var i = 0; i < story.length; i++) {
                if (story[i].id === currentNodeId) {
                    currentNode = story[i];
                    break;
                }
            }

            // Advance to the next node if one exists.
            if (currentNode && currentNode.next) {
                startNode(currentNode.next);
            } else if (currentNode && !currentNode.next) {
                // This is the end of the story.
                isEnded = true;
                Debug.log("");
                Debug.log("[System] Story complete. Press R to replay from the beginning.");
            }
        }
        return;
    }

    // --- Ended State ---
    // The story has concluded. Listen for R to restart.
    if (isEnded) {
        if (Input.isKeyPressed("R")) {
            restartStory();
        }
        return;
    }
}

// ============================================================================
// startNode — Begins displaying a new story node.
//
// Parameters:
//   nodeId (string) — The unique id of the story node to display.
//
// This function finds the node in the story array, updates the speaker,
// highlights the appropriate portrait, and starts the typewriter effect.
// If the node contains choices, it queues them for after the text finishes.
// ============================================================================
function startNode(nodeId) {

    // Search the story array for a node with the matching id.
    var node = null;
    for (var i = 0; i < story.length; i++) {
        if (story[i].id === nodeId) {
            node = story[i];
            currentNodeIndex = i;
            break;
        }
    }

    // If the node wasn't found, log an error. This indicates a broken story
    // link -- the "next" field pointed to an id that doesn't exist.
    if (!node) {
        Debug.error("[DialogueManager] Node not found: '" + nodeId + "'");
        Debug.error("[DialogueManager] Check your story data for broken links.");
        return;
    }

    // Store the current node's id so we can look it up later when advancing.
    currentNodeId = node.id;

    // Update the current speaker name.
    currentSpeaker = node.speaker;

    // Set up the full text for the typewriter to reveal.
    fullText = node.text;
    displayedText = "";
    charIndex = 0;
    typewriterTimer = 0;
    isTyping = true;
    isWaitingForInput = false;
    isShowingChoices = false;

    // Update the portrait colors based on who is speaking.
    // The active speaker's portrait brightens while the other dims.
    updatePortraits(node.speaker);

    // If this node has a flag to set, record it immediately.
    // Flags from non-choice nodes are set when the node starts.
    if (node.flag) {
        setFlag(node.flag.key, node.flag.value);
    }
}

// ============================================================================
// finishTypewriter — Called when the typewriter effect completes naturally.
// Logs the full dialogue line and transitions to the appropriate next state.
// ============================================================================
function finishTypewriter() {

    // The typewriter has finished revealing all characters.
    isTyping = false;

    // Log the complete dialogue line with speaker formatting.
    logDialogue(currentSpeaker, fullText);

    // Look up the current node to check for choices or a next pointer.
    var currentNode = null;
    for (var i = 0; i < story.length; i++) {
        if (story[i].id === currentNodeId) {
            currentNode = story[i];
            break;
        }
    }

    // If the node has choices, hand control to the ChoiceManager.
    if (currentNode && currentNode.choices) {

        // Set the flag so onUpdate stops processing input.
        isShowingChoices = true;

        // Find the ChoiceManager actor and invoke its showChoices function.
        var choiceMgr = Scene.find("ChoiceManager");
        if (choiceMgr) {
            var script = choiceMgr.getComponent("ScriptComponent");
            if (script) {
                script.invoke("showChoices", currentNode.choices);
            }
        } else {
            // Fallback: if ChoiceManager isn't found, log choices manually.
            Debug.warn("[DialogueManager] ChoiceManager actor not found!");
            Debug.log("");
            Debug.log("  Choices:");
            for (var i = 0; i < currentNode.choices.length; i++) {
                Debug.log("    " + (i + 1) + ") " + currentNode.choices[i].text);
            }
        }

    } else {
        // No choices -- wait for the player to press Space to continue.
        isWaitingForInput = true;
    }
}

// ============================================================================
// skipTypewriter — Called when the player presses Space during the typewriter
// effect. Immediately reveals the full line and transitions to the next state.
// ============================================================================
function skipTypewriter() {

    // Set charIndex to the end so the full text is displayed.
    charIndex = fullText.length;
    displayedText = fullText;

    // Finish the typewriter and proceed to the next state.
    finishTypewriter();
}

// ============================================================================
// advanceToNext — Called by ChoiceManager (or internally) to move to the
// next node in the story after a choice is made or Space is pressed.
//
// Parameters:
//   nextId (string) — The id of the next node to display, or null for end.
// ============================================================================
function advanceToNext(nextId) {

    // If nextId is null, the story has ended.
    if (!nextId) {
        isEnded = true;
        isShowingChoices = false;
        Debug.log("");
        Debug.log("[System] Story complete. Press R to replay from the beginning.");
        return;
    }

    // Clear the choice state and start the next node.
    isShowingChoices = false;
    startNode(nextId);
}

// ============================================================================
// setFlag — Records a story flag that tracks a player decision.
//
// Parameters:
//   key   (string) — The flag name (e.g., "motivation", "book").
//   value (string) — The flag value (e.g., "knowledge", "fire").
//
// Flags persist for the duration of the story and can be read with getFlag().
// They are logged to the console so the player can see their choices tracked.
// ============================================================================
function setFlag(key, value) {

    // Store the flag in the storyFlags dictionary.
    storyFlags[key] = value;

    // Log the flag so the player can see their decision was recorded.
    Debug.log("[Story Flag] " + key + " = " + value);
}

// ============================================================================
// getFlag — Retrieves the value of a previously set story flag.
//
// Parameters:
//   key (string) — The flag name to look up.
//
// Returns:
//   The flag's value as a string, or null if the flag has not been set.
// ============================================================================
function getFlag(key) {

    // Check if the flag exists in the dictionary.
    if (storyFlags[key] !== undefined) {
        return storyFlags[key];
    }

    // Return null if the flag was never set.
    return null;
}

// ============================================================================
// logDialogue — Formats and logs a dialogue line to the console.
//
// Parameters:
//   speaker (string) — The name of the character speaking.
//   text    (string) — The dialogue text to display.
//
// The output format is:  [Speaker]: text
// Special formatting is applied for the Narrator and System speakers.
// ============================================================================
function logDialogue(speaker, text) {

    // Add a blank line before each dialogue entry for readability.
    Debug.log("");

    // Format the line with the speaker's name in brackets.
    if (speaker === "Narrator") {
        // Narrator lines are displayed in italics-style formatting.
        Debug.log("  * " + text + " *");
    } else if (speaker === "System") {
        // System messages get special formatting to stand out.
        Debug.log("");
        Debug.log("  " + text);
        Debug.log("");
    } else {
        // Character dialogue shows the speaker name prominently.
        Debug.log("  [" + speaker + "]: " + text);
    }
}

// ============================================================================
// updatePortraits — Adjusts portrait colors to highlight the active speaker.
//
// Parameters:
//   speaker (string) — The name of the current speaker.
//
// The Keeper is represented by the left portrait, and generic/other characters
// by the right portrait. The Narrator dims both portraits.
// ============================================================================
function updatePortraits(speaker) {

    // Find the portrait actors in the scene.
    var leftPortrait = Scene.find("PortraitLeft");
    var rightPortrait = Scene.find("PortraitRight");

    // Safety check: ensure both portraits exist before modifying them.
    if (!leftPortrait || !rightPortrait) {
        return;
    }

    // Get the SpriteRenderer components to modify their colors.
    var leftSR = leftPortrait.getComponent("SpriteRenderer");
    var rightSR = rightPortrait.getComponent("SpriteRenderer");

    if (!leftSR || !rightSR) {
        return;
    }

    // Determine which portrait to highlight based on the speaker.
    if (speaker === "Keeper" || speaker === "???") {
        // Keeper speaks from the left -- brighten left, dim right.
        leftSR.Color = { R: 220, G: 220, B: 240, A: 255 };
        rightSR.Color = { R: 120, G: 110, B: 110, A: 180 };
    } else if (speaker === "Narrator" || speaker === "System") {
        // Narrator and System dim both portraits to draw focus to the text.
        leftSR.Color = { R: 140, G: 140, B: 155, A: 180 };
        rightSR.Color = { R: 155, G: 140, B: 140, A: 180 };
    } else {
        // Any other character speaks from the right -- brighten right, dim left.
        leftSR.Color = { R: 120, G: 120, B: 135, A: 180 };
        rightSR.Color = { R: 240, G: 220, B: 220, A: 255 };
    }
}

// ============================================================================
// restartStory — Resets all state and begins the story from the beginning.
// Called when the player presses R after reaching the ending.
// ============================================================================
function restartStory() {

    // Log a clear restart message.
    Debug.log("");
    Debug.log("================================================================");
    Debug.log("  RESTARTING STORY...");
    Debug.log("================================================================");
    Debug.log("");

    // Reset all state variables to their initial values.
    currentNodeIndex = 0;
    currentNodeId = "";
    displayedText = "";
    fullText = "";
    charIndex = 0;
    typewriterTimer = 0;
    isTyping = false;
    isWaitingForInput = false;
    isShowingChoices = false;
    isEnded = false;
    currentSpeaker = "";

    // Clear all story flags so choices can be made fresh.
    storyFlags = {};

    // Log the previous run's flags for the player's reference.
    Debug.log("[System] All story flags cleared. Starting fresh playthrough.");
    Debug.log("");

    // Begin the story again from the first node.
    startNode("start");
}

// ============================================================================
// Variable to track the current node's id for lookup during state transitions.
// Declared here so it is accessible across all functions.
// ============================================================================
var currentNodeId = "";
