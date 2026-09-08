// ChibiMover.js — a chibi that walks where you push it and picks its own clip.
//
// Put this on an empty actor. It spawns the character, moves it, and chooses
// between idle, walk and run from how fast it is actually going.

// ---- The character -----------------------------------------------------------
var recipePath = "";          // an Assets/Characters/*.chibi, or "" for a seed
var seed = 0;                 // 0 with no recipe means the default character

// ---- Movement ----------------------------------------------------------------
var walkSpeed = 1.6;          // units per second
var runSpeed = 3.4;
var turnSpeed = 14;           // how fast it faces the way it is going
var acceleration = 12;

// ---- Clips -------------------------------------------------------------------
// A chibi at half speed should swing its arms half as far rather than play the
// same cycle slowly, which is what `intensity` on the animator is for.
var runsAbove = 2.2;          // speed at which walk becomes run
var stillBelow = 0.15;        // and below which it is idle

var chibi = null;
var velX = 0;
var velZ = 0;
var facing = 0;

function onStart() {
    chibi = seed !== 0 && !recipePath
        ? Chibi.random(seed, actor.transform3d.x, actor.transform3d.y, actor.transform3d.z)
        : Chibi.spawn(recipePath, actor.transform3d.x, actor.transform3d.y, actor.transform3d.z);

    if (!chibi) { warn("ChibiMover: no scene to spawn into."); return; }
    Chibi.play(chibi, "idle");
}

function onUpdate(dt) {
    if (!chibi) return;

    var wantX = Input.getAxis("Horizontal");
    var wantZ = Input.getAxis("Vertical");
    var length = Math.sqrt(wantX * wantX + wantZ * wantZ);
    if (length > 1) { wantX /= length; wantZ /= length; }

    var sprint = Input.isHeld("Sprint") ? runSpeed : walkSpeed;
    var targetX = wantX * sprint;
    var targetZ = -wantZ * sprint;   // -Z is forward, as it is for the camera

    var blend = Math.min(1, acceleration * dt);
    velX += (targetX - velX) * blend;
    velZ += (targetZ - velZ) * blend;

    chibi.transform3d.x += velX * dt;
    chibi.transform3d.z += velZ * dt;

    var speed = Math.sqrt(velX * velX + velZ * velZ);

    // Face the way it is going, but only while it is going somewhere: a character
    // that snaps back to north the instant you let go reads as broken.
    if (speed > stillBelow) {
        var wanted = Math.atan2(velX, velZ) * 180 / Math.PI;
        facing = turnTowards(facing, wanted, turnSpeed * dt * 60);
        chibi.transform3d.rotY = facing;
    }

    if (speed < stillBelow) Chibi.play(chibi, "idle", 0.18);
    else if (speed < runsAbove) Chibi.play(chibi, "walk", 0.18);
    else Chibi.play(chibi, "run", 0.18);
}

/** Rotates a towards b the short way round. */
function turnTowards(a, b, step) {
    var delta = ((b - a + 540) % 360) - 180;
    if (Math.abs(delta) <= step) return b;
    return a + (delta > 0 ? step : -step);
}

// ---- For other scripts --------------------------------------------------------

/** The character's actor, so a camera or a HUD can follow it. */
function getChibi() { return chibi; }

/** How fast it is moving, for a HUD bar or a footstep sound. */
function getSpeed() { return Math.sqrt(velX * velX + velZ * velZ); }

/** Plays a one-off clip — "wave", "hit", "jump", "cheer", "sit", "die". */
function act(clip) { if (chibi) Chibi.play(chibi, clip, 0.12); }
