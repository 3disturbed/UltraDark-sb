// HeroMovement.js — 4-directional top-down movement
// Attach this script to the Hero actor

var moveSpeed = 150;
var direction = "down"; // current facing direction

function onStart() {
    log("Hero ready! Use WASD or arrow keys to move.");
    log("Walk up to the NPC and press E to talk.");
}

function onUpdate(dt) {
    var vx = 0;
    var vy = 0;

    // 4-directional movement
    if (Input.isHeld("Left") || Input.isKeyHeld("A")) {
        vx = -moveSpeed;
        direction = "left";
    }
    if (Input.isHeld("Right") || Input.isKeyHeld("D")) {
        vx = moveSpeed;
        direction = "right";
    }
    if (Input.isHeld("Up") || Input.isKeyHeld("W")) {
        vy = -moveSpeed;
        direction = "up";
    }
    if (Input.isHeld("Down") || Input.isKeyHeld("S")) {
        vy = moveSpeed;
        direction = "down";
    }

    // Normalize diagonal movement
    if (vx !== 0 && vy !== 0) {
        var factor = 0.7071; // 1/sqrt(2)
        vx *= factor;
        vy *= factor;
    }

    // Apply velocity via Rigidbody2D
    var rb = actor.getComponent("Rigidbody2D");
    if (rb) {
        rb.velocityX = vx;
        rb.velocityY = vy;
    }
}
