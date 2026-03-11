// Fighter.js — Controls one fighter. Keybinds determined by actor.tag (Player1 or Player2).
// Attach this script to both Player1 and Player2 actors in the scene.
//
// Player 1 controls: W/A/S/D movement, F to attack, G to block
// Player 2 controls: Arrow keys movement, K to attack, L to block
//
// Each fighter has health, can jump, attack, and block. Attacks deal damage
// to the opponent when within striking range. Blocking reduces incoming
// damage significantly. A brief hitstun period prevents action after being
// hit, creating a natural combat rhythm.

// =============================================================================
// Movement tuning
// =============================================================================

// Horizontal movement speed in pixels per second.
var moveSpeed = 250;

// Vertical impulse applied on jump. Negative because Y-axis points down.
var jumpForce = -400;

// =============================================================================
// Health
// =============================================================================

// Current and maximum hit points. When health reaches 0 the fighter is KO'd.
var health = 100;
var maxHealth = 100;

// =============================================================================
// Combat tuning
// =============================================================================

// Base damage dealt by a single attack.
var attackDamage = 12;

// Fraction of damage absorbed when blocking. 0.75 means only 25% gets through.
var blockDamageReduction = 0.75;

// =============================================================================
// State flags
// =============================================================================

// Whether the fighter is touching the ground (set by collision callbacks).
var isGrounded = false;

// Whether the fighter is currently in the attack animation window.
var isAttacking = false;

// Whether the fighter is holding the block stance.
var isBlocking = false;

// =============================================================================
// Attack timing
// =============================================================================

// Countdown for the active attack window. During this time the fighter checks
// for hits against the opponent.
var attackTimer = 0;

// How long the attack animation lasts (seconds). The hit check fires at the
// end of this window.
var attackDuration = 0.3;

// Minimum time between attacks. Prevents button mashing from being too strong.
var attackCooldown = 0.5;

// Countdown that must reach 0 before the fighter can attack again.
var cooldownTimer = 0;

// =============================================================================
// Hitstun
// =============================================================================

// Timer for the hitstun state. While positive the fighter cannot act.
var hitstunTimer = 0;

// Duration of the hitstun freeze after taking a hit.
var hitstunDuration = 0.3;

// =============================================================================
// Key bindings (set in onStart based on actor.tag)
// =============================================================================

// Will be populated with key names for: left, right, up, down, attack, block
var keys = {};

// =============================================================================
// Facing direction
// =============================================================================

// True if the fighter is facing right, false if facing left.
// Player1 starts facing right; Player2 starts facing left.
var facingRight = true;

// =============================================================================
// Identity
// =============================================================================

// Short label used in log messages ("P1" or "P2").
var label = "";

// =============================================================================
// Arena bounds
// =============================================================================

// Fighters are clamped to this horizontal range so they cannot walk off-screen.
var arenaLeft = 100;
var arenaRight = 1180;

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    // Assign key bindings based on which player this fighter is.
    // The actor.tag is set in the scene file ("Player1" or "Player2").

    if (actor.tag === "Player1") {
        keys = {
            left:   "A",
            right:  "D",
            up:     "W",
            down:   "S",
            attack: "F",
            block:  "G"
        };
        facingRight = true;
        label = "P1";
        log("[P1] Ready!  Controls: A/D = move, W = jump, F = attack, G = block");
    }

    if (actor.tag === "Player2") {
        keys = {
            left:   "Left",
            right:  "Right",
            up:     "Up",
            down:   "Down",
            attack: "K",
            block:  "L"
        };
        facingRight = false;
        label = "P2";
        log("[P2] Ready!  Controls: Arrows = move, Up = jump, K = attack, L = block");
    }
}

function onUpdate(dt) {
    // -- Hitstun lockout ------------------------------------------------------
    // After taking damage the fighter is briefly unable to act. This gives
    // attacks weight and prevents instant counter-attacks.
    if (hitstunTimer > 0) {
        hitstunTimer -= dt;

        // During hitstun, zero out horizontal velocity so the fighter stops
        var rb = actor.getComponent("Rigidbody2D");
        if (rb) {
            rb.velocityX = 0;
        }
        return;
    }

    // -- Decrement cooldown ---------------------------------------------------
    if (cooldownTimer > 0) {
        cooldownTimer -= dt;
    }

    // -- Blocking -------------------------------------------------------------
    // A fighter can block while grounded and not mid-attack. Holding the block
    // key keeps the stance active; releasing it drops the guard.
    if (Input.isKeyHeld(keys.block) && isGrounded && !isAttacking) {
        isBlocking = true;
    } else {
        isBlocking = false;
    }

    // -- Horizontal movement --------------------------------------------------
    // Cannot move while blocking or during an attack.
    var rb = actor.getComponent("Rigidbody2D");

    if (!isBlocking && !isAttacking) {
        var vx = 0;

        if (Input.isKeyHeld(keys.left)) {
            vx = -moveSpeed;
        }
        if (Input.isKeyHeld(keys.right)) {
            vx = moveSpeed;
        }

        if (rb) {
            rb.velocityX = vx;
        }
    } else {
        // Stop horizontal movement while blocking or attacking
        if (rb) {
            rb.velocityX = 0;
        }
    }

    // -- Jumping --------------------------------------------------------------
    // Can jump when grounded and not blocking. Jumping during an attack is
    // allowed (fighting games often permit jump-attacks).
    if (!isBlocking && isGrounded) {
        if (Input.isKeyPressed(keys.up)) {
            if (rb) {
                rb.velocityY = jumpForce;
            }
            isGrounded = false;
        }
    }

    // -- Attack initiation ----------------------------------------------------
    // Start a new attack if the attack key is pressed, the cooldown has
    // elapsed, and the fighter is not already attacking or blocking.
    if (Input.isKeyPressed(keys.attack) && cooldownTimer <= 0 && !isAttacking && !isBlocking) {
        attack();
    }

    // -- Attack timer processing ----------------------------------------------
    // While attacking, count down the attack window. When the timer expires,
    // perform the hit check against the opponent.
    if (isAttacking) {
        attackTimer -= dt;

        if (attackTimer <= 0) {
            // Attack window ended — check if the opponent is in range
            checkHit();
            isAttacking = false;
        }
    }

    // -- Arena bounds clamping ------------------------------------------------
    // Keep the fighter within the playable area so they cannot walk off-screen.
    if (actor.transform.x < arenaLeft) {
        actor.transform.x = arenaLeft;
    }
    if (actor.transform.x > arenaRight) {
        actor.transform.x = arenaRight;
    }

    // -- Facing direction update ----------------------------------------------
    // The fighter should always face toward the opponent. Look up the other
    // player and compare X positions.
    updateFacing();
}

// =============================================================================
// Attack
// =============================================================================

// Begins the attack sequence. Sets the attacking flag and starts the timer.
// The actual hit check happens when the timer expires (end of attack window).
function attack() {
    isAttacking = true;
    attackTimer = attackDuration;
    cooldownTimer = attackCooldown;
    log("[" + label + "] attacks!");
}

// =============================================================================
// Hit detection
// =============================================================================

// Called at the end of the attack window. Finds the opponent actor and checks
// if they are within striking distance. If so, deals damage.
function checkHit() {
    // Determine the opponent actor name based on our own tag
    var opponentName = (actor.tag === "Player1") ? "Player2" : "Player1";

    var opponent = Scene.find(opponentName);
    if (!opponent) {
        return;
    }

    // Calculate horizontal distance between the two fighters
    var dx = Math.abs(actor.transform.x - opponent.transform.x);
    var dy = Math.abs(actor.transform.y - opponent.transform.y);

    // Strike range check — must be close enough horizontally and vertically
    // to register a hit. The 80px horizontal range accounts for the fighters'
    // widths plus a small reach. The 60px vertical tolerance allows hits to
    // land even when one fighter is slightly above or below.
    if (dx < 80 && dy < 60) {
        // Hit confirmed — call the opponent's takeDamage function
        opponent.takeDamage(attackDamage);
        log("[" + label + "] landed a hit!");
    } else {
        log("[" + label + "] attack missed! (distance: " + Math.floor(dx) + "px)");
    }
}

// =============================================================================
// Damage and death
// =============================================================================

// Called when this fighter is hit by the opponent's attack. Applies damage
// (reduced if blocking) and triggers hitstun. Notifies the FightManager
// of the health change or KO.
function takeDamage(amount) {
    // Blocking absorbs most of the damage
    if (isBlocking) {
        amount = amount * (1 - blockDamageReduction);
        log("[" + label + "] blocked! Reduced damage to " + Math.floor(amount));
    }

    health -= amount;

    // Prevent health from going below zero
    if (health < 0) {
        health = 0;
    }

    // Enter hitstun — the fighter cannot act for a brief moment
    hitstunTimer = hitstunDuration;

    // Notify the FightManager of the health change so it can update the
    // health bar display in the console
    var manager = Scene.find("FightManager");
    if (manager) {
        manager.onHealthChanged(actor.tag, health, maxHealth);
    }

    // Check for KO
    if (health <= 0) {
        die();
    }
}

// Called when health reaches zero. Logs the KO and notifies the FightManager.
function die() {
    health = 0;
    log("[" + label + "] is KO'd!");

    var manager = Scene.find("FightManager");
    if (manager) {
        manager.onPlayerKO(actor.tag);
    }
}

// =============================================================================
// Facing direction
// =============================================================================

// Updates the facing direction so the fighter always faces toward the
// opponent. This affects which direction attacks travel and keeps the
// visuals consistent.
function updateFacing() {
    var opponentName = (actor.tag === "Player1") ? "Player2" : "Player1";
    var opponent = Scene.find(opponentName);

    if (opponent) {
        if (opponent.transform.x > actor.transform.x) {
            facingRight = true;
        } else {
            facingRight = false;
        }
    }
}

// =============================================================================
// Collision callbacks
// =============================================================================

// Called when the fighter first touches another collider.
function onCollisionEnter(other) {
    // Ground detection — enables jumping again after landing.
    if (other.tag === "Ground") {
        isGrounded = true;
    }
}

// Called when the fighter stops touching a collider.
function onCollisionExit(other) {
    // Left the ground — mark as airborne.
    if (other.tag === "Ground") {
        isGrounded = false;
    }
}
