// EnemyWander.js -- Night-time enemy that wanders or chases the player
// Spawned by WorldManager during the night phase.
//
// Behaviour:
//   - If the player is within chaseRange, move toward them.
//   - Otherwise, wander randomly, changing direction every few seconds.
//   - On collision with the player, deal damage.
//
// Part of the survival loop: enemies are the threat you gather and craft to
// survive against each night.

var speed      = 60;
var health     = 40;
var chaseRange = 200;
var damage     = 10;

// Wander state
var wanderTimer = 0;
var wanderDirX  = 0;
var wanderDirY  = 0;
var wanderInterval = 2.0; // seconds between direction changes

function onStart() {
    pickRandomDirection();
}

function onUpdate(dt) {
    var player = Scene.findFirstByTag("Player");
    if (!player) {
        wander(dt);
        return;
    }

    // Distance to player
    var dx   = player.transform.x - actor.transform.x;
    var dy   = player.transform.y - actor.transform.y;
    var dist = Math.sqrt(dx * dx + dy * dy);

    if (dist < chaseRange && dist > 0) {
        // Chase the player
        var nx = dx / dist;
        var ny = dy / dist;

        var rb = actor.getComponent("Rigidbody2D");
        if (rb) {
            rb.velocityX = nx * speed;
            rb.velocityY = ny * speed;
        }
    } else {
        // Wander aimlessly
        wander(dt);
    }
}

// ---------------------------------------------------------------------------
// Wander behaviour
// ---------------------------------------------------------------------------

function wander(dt) {
    wanderTimer += dt;
    if (wanderTimer >= wanderInterval) {
        wanderTimer = 0;
        pickRandomDirection();
    }

    var rb = actor.getComponent("Rigidbody2D");
    if (rb) {
        rb.velocityX = wanderDirX * speed;
        rb.velocityY = wanderDirY * speed;
    }
}

function pickRandomDirection() {
    var angle  = Math.random() * Math.PI * 2;
    wanderDirX = Math.cos(angle);
    wanderDirY = Math.sin(angle);
}

// ---------------------------------------------------------------------------
// Combat
// ---------------------------------------------------------------------------

function takeDamage(amount) {
    health -= amount;
    if (health <= 0) {
        // Notify the WorldManager that an enemy was killed
        var manager = Scene.findFirstByTag("Manager");
        if (manager) {
            var ms = manager.getComponent("ScriptComponent");
            if (ms) { ms.call("onEnemyKilled"); }
        }
        Scene.destroyActor(actor);
    }
}

function onCollisionEnter(other) {
    if (other.tag === "Player") {
        var playerScript = other.getComponent("ScriptComponent");
        if (playerScript) {
            playerScript.call("takeDamage", damage);
        }
    }
}
