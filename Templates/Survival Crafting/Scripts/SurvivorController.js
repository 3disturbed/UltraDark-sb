// SurvivorController.js -- Player movement, gathering, inventory, and crafting
// Attach to the Player actor.
//
// Core survival loop:  GATHER resources --> CRAFT tools & supplies --> SURVIVE the night
//
// Controls:
//   WASD  - Move
//   E     - Gather from nearby resource
//   I     - Show inventory
//   C     - Open craft menu
//   1/2/3 - Craft a recipe (while craft menu is open)
//   R     - Restart after death

// ---------------------------------------------------------------------------
// Movement
// ---------------------------------------------------------------------------
var moveSpeed = 150;

// ---------------------------------------------------------------------------
// Survival stats
// ---------------------------------------------------------------------------
var health    = 100;
var maxHealth = 100;
var hunger    = 100;
var maxHunger = 100;

// Hunger drains steadily; when it hits 0, health drains instead.
var hungerDrainRate   = 2;   // per second
var hungerDamageRate  = 5;   // health lost per second when starving

// ---------------------------------------------------------------------------
// Inventory  (gather --> craft --> survive the night)
// ---------------------------------------------------------------------------
var inventory = { wood: 0, stone: 0, food: 0 };

// ---------------------------------------------------------------------------
// Interaction state
// ---------------------------------------------------------------------------
var nearbyResource = null;   // set by trigger enter/exit
var isAlive        = true;
var craftMenuOpen  = false;

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

function onStart() {
    log("=== Survival Crafting ===");
    log("Controls: WASD move | E gather | I inventory | C craft menu");
    log("Health: " + health + "/" + maxHealth + "  Hunger: " + hunger + "/" + maxHunger);
    log("Gather resources during the day, craft supplies, and survive the night!");
}

function onUpdate(dt) {
    if (!isAlive) {
        // Allow restart
        if (Input.isKeyPressed("R")) {
            restart();
        }
        return;
    }

    // ----- Movement -----
    handleMovement(dt);

    // ----- Hunger & starvation -----
    updateHunger(dt);

    // ----- Input: Gather -----
    if (Input.isKeyPressed("E")) {
        gather();
    }

    // ----- Input: Inventory -----
    if (Input.isKeyPressed("I")) {
        showInventory();
    }

    // ----- Input: Craft menu -----
    if (Input.isKeyPressed("C")) {
        craftMenuOpen = !craftMenuOpen;
        if (craftMenuOpen) {
            showCraftMenu();
        } else {
            log("Craft menu closed.");
        }
    }

    // ----- Input: Craft recipes (1, 2, 3) -----
    if (craftMenuOpen) {
        if (Input.isKeyPressed("D1") || Input.isKeyPressed("1")) { craft(1); }
        if (Input.isKeyPressed("D2") || Input.isKeyPressed("2")) { craft(2); }
        if (Input.isKeyPressed("D3") || Input.isKeyPressed("3")) { craft(3); }
    }
}

// ---------------------------------------------------------------------------
// Movement
// ---------------------------------------------------------------------------

function handleMovement(dt) {
    var vx = 0;
    var vy = 0;

    if (Input.isHeld("Left")  || Input.isKeyHeld("A")) { vx = -moveSpeed; }
    if (Input.isHeld("Right") || Input.isKeyHeld("D")) { vx =  moveSpeed; }
    if (Input.isHeld("Up")    || Input.isKeyHeld("W")) { vy = -moveSpeed; }
    if (Input.isHeld("Down")  || Input.isKeyHeld("S")) { vy =  moveSpeed; }

    // Normalize diagonal movement so the player doesn't move faster diagonally
    if (vx !== 0 && vy !== 0) {
        var factor = 0.7071; // 1 / sqrt(2)
        vx *= factor;
        vy *= factor;
    }

    var rb = actor.getComponent("Rigidbody2D");
    if (rb) {
        rb.velocityX = vx;
        rb.velocityY = vy;
    }
}

// ---------------------------------------------------------------------------
// Hunger / starvation
// ---------------------------------------------------------------------------

function updateHunger(dt) {
    // Hunger drains over time
    hunger -= hungerDrainRate * dt;
    if (hunger < 0) { hunger = 0; }

    // When starving, health starts to drain
    if (hunger <= 0) {
        health -= hungerDamageRate * dt;
        if (health <= 0) {
            health = 0;
            die();
        }
    }
}

// ---------------------------------------------------------------------------
// Gathering  (the first step of the survival loop)
// ---------------------------------------------------------------------------

function gather() {
    if (!nearbyResource) {
        log("No resource nearby. Walk up to a tree, rock, or bush and press E.");
        return;
    }

    // Ask the ResourceNode for its type via its script
    var nodeScript = nearbyResource.getComponent("ScriptComponent");
    if (!nodeScript) { return; }

    var resourceType = nodeScript.call("harvest");
    if (!resourceType) { return; }

    // Add to inventory
    if (inventory[resourceType] !== undefined) {
        inventory[resourceType] += 1;
    }

    log("Gathered " + resourceType + "! " + resourceType + ": " + inventory[resourceType]);
}

// ---------------------------------------------------------------------------
// Inventory display
// ---------------------------------------------------------------------------

function showInventory() {
    log("--- Inventory ---");
    log("  Wood : " + inventory.wood);
    log("  Stone: " + inventory.stone);
    log("  Food : " + inventory.food);
    log("  Health: " + Math.floor(health) + "/" + maxHealth +
        "  Hunger: " + Math.floor(hunger) + "/" + maxHunger);
}

// ---------------------------------------------------------------------------
// Crafting  (gather --> CRAFT --> survive the night)
// ---------------------------------------------------------------------------

function showCraftMenu() {
    log("=== Craft Menu ===");
    log("  1) Campfire   : 5 wood          (restores 30 hunger)");
    log("  2) Stone Wall : 3 stone + 2 wood (place a wall)");
    log("  3) Bandage    : 2 food           (restores 25 health)");
    log("Press 1, 2, or 3 to craft. C to close.");
}

function craft(recipe) {
    if (recipe === 1) {
        // Campfire -- restores hunger so you can survive longer
        if (inventory.wood < 5) {
            log("Not enough wood! Need 5, have " + inventory.wood + ".");
            return;
        }
        inventory.wood -= 5;
        eat(30);
        log("Crafted Campfire! Hunger restored by 30. Hunger: " + Math.floor(hunger) + "/" + maxHunger);

    } else if (recipe === 2) {
        // Stone Wall -- defensive structure for the night phase
        if (inventory.stone < 3 || inventory.wood < 2) {
            log("Not enough materials! Need 3 stone + 2 wood. Have stone:" +
                inventory.stone + " wood:" + inventory.wood);
            return;
        }
        inventory.stone -= 3;
        inventory.wood  -= 2;
        // Spawn a wall actor near the player
        var wall = Scene.createActor("StoneWall");
        if (wall) {
            wall.transform.x = actor.transform.x + 40;
            wall.transform.y = actor.transform.y;
            var sr = wall.addComponent("SpriteRenderer");
            if (sr) { sr.tint = { R: 160, G: 160, B: 160, A: 255 }; }
            // A collider with no Rigidbody2D is static geometry on both engines.
            var col = wall.addComponent("BoxCollider2D");
            if (col) { col.size = { x: 32, y: 32 }; }
        }
        log("Crafted Stone Wall! Placed near you for defense.");

    } else if (recipe === 3) {
        // Bandage -- heals so you can survive enemy attacks at night
        if (inventory.food < 2) {
            log("Not enough food! Need 2, have " + inventory.food + ".");
            return;
        }
        inventory.food -= 2;
        health = Math.min(maxHealth, health + 25);
        log("Crafted Bandage! Health restored by 25. Health: " + Math.floor(health) + "/" + maxHealth);
    }
}

// ---------------------------------------------------------------------------
// Combat / damage
// ---------------------------------------------------------------------------

function takeDamage(amount) {
    if (!isAlive) { return; }
    health -= amount;
    if (health < 0) { health = 0; }
    log("Took " + amount + " damage! Health: " + Math.floor(health) + "/" + maxHealth);
    if (health <= 0) {
        die();
    }
}

// ---------------------------------------------------------------------------
// Eating (restore hunger)
// ---------------------------------------------------------------------------

function eat(amount) {
    hunger = Math.min(maxHunger, hunger + amount);
}

// ---------------------------------------------------------------------------
// Death & restart
// ---------------------------------------------------------------------------

function die() {
    isAlive = false;
    var rb = actor.getComponent("Rigidbody2D");
    if (rb) {
        rb.velocityX = 0;
        rb.velocityY = 0;
    }
    Debug.error("You have perished! Press R to restart.");
}

function restart() {
    health  = maxHealth;
    hunger  = maxHunger;
    isAlive = true;
    inventory.wood  = 0;
    inventory.stone = 0;
    inventory.food  = 0;
    actor.transform.x = 640;
    actor.transform.y = 360;
    log("Restarted! Gather resources and survive the night.");
}

// ---------------------------------------------------------------------------
// Trigger callbacks -- detect nearby resources
// ---------------------------------------------------------------------------

function onTriggerEnter(other) {
    if (other.tag === "Resource") {
        nearbyResource = other;
    }
}

function onTriggerExit(other) {
    if (nearbyResource === other) {
        nearbyResource = null;
    }
}
