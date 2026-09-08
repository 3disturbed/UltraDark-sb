// ChibiCrowd.js — a village of distinct characters from one integer.
//
// Put this on an empty actor. Every villager comes from a seed, so the same
// numbers give the same village on every run and on both engines — which is what
// makes a crowd something you can debug rather than something that happens.

var count = 16;
var firstSeed = 1000;

// ---- Where they stand --------------------------------------------------------
var areaX = 9;                // they are scattered inside this box
var areaZ = 6;

// ---- What they do ------------------------------------------------------------
var wander = true;
var wanderSpeed = 0.5;
var pauseSeconds = 2.5;       // roughly how long one stands before moving again
var greetChance = 0.25;       // and how often a pause is a wave instead

var villagers = [];

function onStart() {
    for (var i = 0; i < count; i++) {
        var seed = firstSeed + i;

        // The same seed always draws the same place as well as the same person,
        // so a village is reproducible down to who stands where.
        var x = (fraction(seed, 1) - 0.5) * areaX;
        var z = (fraction(seed, 2) - 0.5) * areaZ;

        var villager = Chibi.random(seed, x, 0, z);
        if (!villager) continue;

        villagers.push({
            actor: villager,
            timer: fraction(seed, 3) * pauseSeconds,
            dirX: 0,
            dirZ: 0,
            moving: false,
            seed: seed,
        });
        Chibi.play(villager, "idle");
    }
    log("ChibiCrowd: " + villagers.length + " villagers");
}

function onUpdate(dt) {
    if (!wander) return;

    for (var i = 0; i < villagers.length; i++) {
        var v = villagers[i];
        v.timer -= dt;

        if (v.timer <= 0) {
            v.moving = !v.moving;
            v.timer = pauseSeconds * (0.6 + Math.random() * 0.8);

            if (v.moving) {
                var angle = Math.random() * Math.PI * 2;
                v.dirX = Math.sin(angle);
                v.dirZ = Math.cos(angle);
                v.actor.transform3d.rotY = angle * 180 / Math.PI;
                Chibi.play(v.actor, "walk", 0.2);
            } else if (Math.random() < greetChance) {
                Chibi.play(v.actor, "wave", 0.2);
            } else {
                Chibi.play(v.actor, "idle", 0.2);
            }
        }

        if (!v.moving) continue;

        var nextX = v.actor.transform3d.x + v.dirX * wanderSpeed * dt;
        var nextZ = v.actor.transform3d.z + v.dirZ * wanderSpeed * dt;

        // Turn back at the edge rather than walking to the horizon.
        if (Math.abs(nextX) > areaX / 2) { v.dirX = -v.dirX; nextX = v.actor.transform3d.x; }
        if (Math.abs(nextZ) > areaZ / 2) { v.dirZ = -v.dirZ; nextZ = v.actor.transform3d.z; }

        v.actor.transform3d.x = nextX;
        v.actor.transform3d.z = nextZ;
    }
}

/** A stable 0..1 from a seed and a channel, so layout is reproducible. */
function fraction(seed, channel) {
    var x = Math.sin(seed * 127.1 + channel * 311.7) * 43758.5453;
    return x - Math.floor(x);
}

// ---- For other scripts --------------------------------------------------------

/** How many villagers there are, for a HUD. */
function getVillagerCount() { return villagers.length; }

/** Makes everyone do the same thing at once — a cheer when the boss dies. */
function everyone(clip) {
    for (var i = 0; i < villagers.length; i++) Chibi.play(villagers[i].actor, clip, 0.2);
}
