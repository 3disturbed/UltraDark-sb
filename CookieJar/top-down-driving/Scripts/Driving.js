// Driving.js -- get in, drive, run things over, get out.
// Attach to the PLAYER actor, alongside whatever moves them on foot.
//
// The rider stays the player. When you get in, the player actor is moved to the
// car's position every frame and its own sprite is hidden -- rather than the
// camera being told about cars, or "the player" becoming a different actor.
// That one decision is why camera-follow, enemy targeting, noise, spawn
// distance and every other "where is the player" query keep working with no
// idea that driving exists.
//
// The player's own movement script must ask before it moves anything:
//
//   if (drivingScript.call("isDriving")) { return; }
//
// ...or you accelerate on foot and in a car at the same time.

// ---------------------------------------------------------------------------
// Dials -- the feel lives here
// ---------------------------------------------------------------------------
var carTag     = "Car";
var reach      = 52;      // how close you must be to get in

var topSpeed   = 470;
var accel      = 320;
var brake      = 520;     // braking is harder than accelerating, as it should be
var reverseMax = 150;
var drag       = 1.4;     // coasting, when neither pedal is down
var turnRate   = 2.6;

// Steering scales with speed, and this is the single line that makes driving
// feel like driving rather than like sliding a box around: a car does not pivot
// on the spot. Below this fraction of top speed the wheel does progressively
// less; at a standstill it does nothing at all.
var gripSpeed  = 0.35;

var stopBelow  = 4;       // speeds under this snap to zero, so a car parks

// What it costs. An engine is the loudest thing in the world, and a car that is
// only an advantage is not a decision. Set noiseTag to "" to skip it.
var noiseTag    = "Director";
var noiseRadius = 520;
var noiseEvery  = 0.25;

// Anything in front of you above this loses.
var runOverSpeed  = 150;
var runOverTags   = ["Enemy"];
var runOverRadius = 26;

// ---------------------------------------------------------------------------
var car = null;
var driving = false;
var speed = 0;
var facing = 0;
var noiseTimer = 0;
var sprite = null;

function onStart() { sprite = actor.getComponent("SpriteRenderer"); }

// ---------------------------------------------------------------------------
// Getting in and out
// ---------------------------------------------------------------------------

/**
 * Tries to get into the nearest car.
 *
 * Make this win over any other use of the same key. Nobody stood beside a car
 * wants to search the pavement, and a context key that guesses wrong once is a
 * context key the player stops trusting.
 *
 * @returns true if they got in
 */
function enter() {
    if (driving) { return false; }

    var near = Physics.overlapCircle(actor.transform.x, actor.transform.y, reach);
    for (var i = 0; i < near.length; i++) {
        if (!near[i] || near[i].tag !== carTag) { continue; }

        car = near[i];
        driving = true;
        speed = 0;
        // Take the car's heading, not the player's: you get in facing the way it
        // is parked, and lurching sideways on entry is jarring.
        facing = car.transform.rotation;
        showRider(false);
        return true;
    }
    return false;
}

/** Steps out to the side, clear of where the car will roll on. */
function leave() {
    if (!car) { driving = false; return; }

    var side = facing + Math.PI / 2;
    actor.transform.x = car.transform.x + Math.cos(side) * 30;
    actor.transform.y = car.transform.y + Math.sin(side) * 30;

    var rb = car.getComponent("Rigidbody2D");
    if (rb) { rb.velocityX = 0; rb.velocityY = 0; }

    driving = false;
    car = null;
    speed = 0;
    showRider(true);
}

/** One key for both, which is what a player expects. */
function toggle() { return driving ? (leave(), false) : enter(); }

// ---------------------------------------------------------------------------
// Driving
// ---------------------------------------------------------------------------
function onUpdate(dt) {
    if (!driving) { return; }

    // A car destroyed underneath you puts you back on foot rather than throwing
    // on every frame from here on.
    if (!car || car.active !== true) { driving = false; car = null; showRider(true); return; }

    var forward = Input.isKeyHeld("W") || Input.isKeyHeld("Up");
    var back    = Input.isKeyHeld("S") || Input.isKeyHeld("Down");

    var jy = Input.joystickY;
    if (jy < -0.3) { forward = true; }
    if (jy > 0.3)  { back = true; }

    if (forward)   { speed += accel * dt; }
    else if (back) { speed -= brake * dt; }
    else           { speed -= speed * drag * dt; }

    if (speed > topSpeed)    { speed = topSpeed; }
    if (speed < -reverseMax) { speed = -reverseMax; }
    if (Math.abs(speed) < stopBelow) { speed = 0; }

    var steer = 0;
    if (Input.isKeyHeld("A") || Input.isKeyHeld("Left"))  { steer -= 1; }
    if (Input.isKeyHeld("D") || Input.isKeyHeld("Right")) { steer += 1; }

    var jx = Input.joystickX;
    if (jx < -0.3) { steer = -1; }
    if (jx > 0.3)  { steer = 1; }

    var grip = Math.min(1, Math.abs(speed) / (topSpeed * gripSpeed));
    // Reversing turns the other way, as it does in a real car.
    facing += steer * turnRate * grip * dt * (speed < 0 ? -1 : 1);

    car.transform.rotation = facing;

    // Through the body, not the transform: a car that is teleported each frame
    // walks through walls, and the collision that stops it is the whole reason
    // the streets mean anything.
    var rb = car.getComponent("Rigidbody2D");
    if (rb) {
        rb.velocityX = Math.cos(facing) * speed;
        rb.velocityY = Math.sin(facing) * speed;
    }

    // A car is the one solid thing in the world that moves, so if you are depth
    // sorting (the depth-2-5d cookie) it is the one thing whose depth has to be
    // recomputed rather than fixed when it was built.
    reDepth();

    // The rider goes where the car goes.
    actor.transform.x = car.transform.x;
    actor.transform.y = car.transform.y;
    actor.transform.rotation = facing;

    runOver();
    makeNoise(dt);
}

// ---------------------------------------------------------------------------
function runOver() {
    if (Math.abs(speed) < runOverSpeed) { return; }

    var ahead = Physics.overlapCircle(
        car.transform.x + Math.cos(facing) * 24,
        car.transform.y + Math.sin(facing) * 24,
        runOverRadius);

    for (var i = 0; i < ahead.length; i++) {
        var hit = ahead[i];
        if (!hit) { continue; }

        for (var t = 0; t < runOverTags.length; t++) {
            if (hit.tag !== runOverTags[t]) { continue; }

            var script = hit.getComponent("ScriptComponent");
            // Ask it to die rather than destroying it, so it can drop what it
            // was carrying and tell whatever was counting it.
            if (script) { script.call("kill"); }
            else { Scene.destroyActor(hit); }
        }
    }
}

function makeNoise(dt) {
    if (!noiseTag) { return; }

    noiseTimer -= dt;
    if (noiseTimer > 0) { return; }
    noiseTimer = noiseEvery;

    var host = Scene.findFirstByTag(noiseTag);
    if (!host) { return; }
    var script = host.getComponent("ScriptComponent");
    if (script) { script.call("emitNoise", car.transform.x, car.transform.y, noiseRadius); }
}

function showRider(on) {
    if (sprite) { sprite.enabled = on; }
    // A sprite hidden by alpha 0 is invisible in the browser and NOT in the
    // native renderer, so this must go through enabled or active -- never tint.
    if (!sprite) { actor.active = on; }
}

function reDepth() {
    var d = car.getComponent("SpriteRenderer");
    if (!d) { return; }
    var own = actor.getComponent("ScriptComponent");
    // StandUpright, if this project has it, already knows the band.
    if (own && typeof own.call === "function") { d.layerDepth = own.call("getDepth"); }
}

// ---------------------------------------------------------------------------
// Readouts
// ---------------------------------------------------------------------------
function isDriving() { return driving; }
function getSpeed()  { return Math.abs(Math.round(speed)); }
function getFacing() { return facing; }
