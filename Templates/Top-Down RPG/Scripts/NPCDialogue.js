// NPCDialogue.js — Simple trigger-based dialogue
// Attach this to an NPC actor with a trigger collider

var dialogueLines = [
    "Welcome, traveler! This is the village of Biscuit Vale.",
    "Our village has been peaceful... until the wild biscuits appeared!",
    "Perhaps you could help us? Speak to me again when you're ready."
];
var currentLine = 0;
var playerNearby = false;

function onStart() {
    // NPC is ready
}

function onUpdate(dt) {
    if (playerNearby && (Input.isKeyPressed("E") || Input.isKeyPressed("Enter"))) {
        // Show next dialogue line
        log("[" + actor.name + "]: " + dialogueLines[currentLine]);
        currentLine = (currentLine + 1) % dialogueLines.length;
    }
}

function onTriggerEnter(other) {
    if (other.tag === "Player") {
        playerNearby = true;
        log("Press E to talk to " + actor.name);
    }
}

function onTriggerExit(other) {
    if (other.tag === "Player") {
        playerNearby = false;
    }
}
