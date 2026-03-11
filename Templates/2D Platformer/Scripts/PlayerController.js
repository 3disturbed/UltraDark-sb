// PlayerController.js — Basic 2D platformer movement
// Attach this script to the Player actor

var moveSpeed = 300;
var jumpForce = -500;
var isGrounded = false;

function onStart() {
    log("PlayerController started on: " + actor.name);
}

function onUpdate(dt) {
    var vx = 0;

    // Horizontal movement
    if (Input.isHeld("Left") || Input.isKeyHeld("A")) {
        vx = -moveSpeed;
    }
    if (Input.isHeld("Right") || Input.isKeyHeld("D")) {
        vx = moveSpeed;
    }

    // Apply horizontal velocity
    var rb = actor.getComponent("Rigidbody2D");
    if (rb) {
        rb.velocityX = vx;
    }

    // Jump
    if (isGrounded && (Input.isPressed("Up") || Input.isKeyPressed("Space") || Input.isKeyPressed("W"))) {
        if (rb) {
            rb.velocityY = jumpForce;
        }
        isGrounded = false;
    }
}

function onCollisionEnter(other) {
    // Simple ground check — if we hit something below us
    if (other.tag === "Ground") {
        isGrounded = true;
    }
}

function onCollisionExit(other) {
    if (other.tag === "Ground") {
        isGrounded = false;
    }
}
