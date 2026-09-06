// NoiseInvestigator.js -- something that hunts by ear.
// Attach to anything tagged with the director's `listenerTag` (default "Enemy").
//
// Three states, and the middle one is the point:
//   0 wander       drifting, aware of nothing
//   1 investigate  walking to a remembered POSITION, not to a target
//   2 chase        it has you, and holds on until you break the distance
//
// The sight cone is deliberately mean: short, and narrow enough to walk behind.
// Sight is the fallback sense here, not the primary one.
//
// This is a starting point, not a black box. Combat, health, animation and
// death are yours to add -- the parts to keep are the state machine, hearNoise
// and the unstick.

// ---------------------------------------------------------------------------
// Dials
// ---------------------------------------------------------------------------
var wanderSpeed      = 34;
var investigateSpeed = 54;
var chaseSpeed       = 96;

var targetTag  = "Player";
var sightRange = 172;      // short: it is not looking for you, it is just there
var sightCone  = 1.9;      // radians, total spread
var touchRange = 34;       // close enough that the cone stops mattering
var loseRange  = 240;      // past this it forgets you and heads for where you were

var arriveRange  = 26;     // how near the remembered spot counts as "checked"
var wanderMin    = 1.6;    // seconds between new drift directions
var wanderMax    = 4.0;

var pace = 1.0;            // a global multiplier the director can wind up

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------
var state = 0;
var facing = 0;
var targetX = 0;
var targetY = 0;

var wanderTimer = 0;
var wanderX = 0;
var wanderY = 0;

var stuckTimer = 0;
var lastX = 0;
var lastY = 0;
var dodgeTimer = 0;
var dodgeX = 0;
var dodgeY = 0;

function onStart() {
    facing = Math.random() * Math.PI * 2;
    pickWander();
    lastX = actor.transform.x;
    lastY = actor.transform.y;
}

function onUpdate(dt) {
    if (dodgeTimer > 0) { dodgeTimer -= dt; }

    var target = Scene.findFirstByTag(targetTag);
    if (target) { senseAndAct(target, dt); } else { wander(dt); }

    unstick(dt);
    actor.transform.rotation = facing;
}

// ---------------------------------------------------------------------------
// Senses
// ---------------------------------------------------------------------------

function senseAndAct(target, dt) {
    var dx = target.transform.x - actor.transform.x;
    var dy = target.transform.y - actor.transform.y;
    var dist = Math.sqrt(dx * dx + dy * dy);

    if (state === 2) {
        if (dist > loseRange) {
            // Lost them -- but it still knows where they were a moment ago.
            state = 1;
            targetX = target.transform.x;
            targetY = target.transform.y;
        }
    } else if (dist < sightRange && canSee(dx, dy, dist)) {
        state = 2;
        onSpotted(target, dist);
    }

    if (state === 2)      { chase(target, dx, dy, dist, dt); }
    else if (state === 1) { investigate(dt); }
    else                  { wander(dt); }
}

function canSee(dx, dy, dist) {
    if (dist < touchRange) { return true; }
    return Math.abs(angleDelta(facing, Math.atan2(dy, dx))) < sightCone * 0.5;
}

function angleDelta(from, to) {
    var d = to - from;
    while (d > Math.PI)  { d -= Math.PI * 2; }
    while (d < -Math.PI) { d += Math.PI * 2; }
    return d;
}

// ---------------------------------------------------------------------------
// Behaviours
// ---------------------------------------------------------------------------

function chase(target, dx, dy, dist, dt) {
    if (dist > 0.01) { drive(dx / dist, dy / dist, chaseSpeed * pace); }
    onChasing(target, dist, dt);
}

function investigate(dt) {
    var dx = targetX - actor.transform.x;
    var dy = targetY - actor.transform.y;
    var dist = Math.sqrt(dx * dx + dy * dy);

    if (dist < arriveRange) {
        // Nothing here. Whatever it was, it moved.
        state = 0;
        pickWander();
        return;
    }
    drive(dx / dist, dy / dist, investigateSpeed * pace);
}

function wander(dt) {
    wanderTimer -= dt;
    if (wanderTimer <= 0) { pickWander(); }
    drive(wanderX, wanderY, wanderSpeed * pace);
}

function pickWander() {
    var angle = Math.random() * Math.PI * 2;
    wanderX = Math.cos(angle);
    wanderY = Math.sin(angle);
    wanderTimer = wanderMin + Math.random() * (wanderMax - wanderMin);
}

// ---------------------------------------------------------------------------
// Motion
// ---------------------------------------------------------------------------

function drive(nx, ny, speed) {
    if (dodgeTimer > 0) { nx = dodgeX; ny = dodgeY; }

    var rb = actor.getComponent("Rigidbody2D");
    if (rb) {
        rb.velocityX = nx * speed;
        rb.velocityY = ny * speed;
    }
    if (nx !== 0 || ny !== 0) { facing = Math.atan2(ny, nx); }
}

// Walked into a wall? Slide along it for a moment rather than grinding into it.
// Without this, anything that steers straight at a target spends the whole level
// vibrating against the first corner between them: there is no pathfinding in
// the scripting contract, and this is the cheapest thing that reads as intent.
function unstick(dt) {
    stuckTimer += dt;
    if (stuckTimer < 0.5) { return; }
    stuckTimer = 0;

    var moved = Math.abs(actor.transform.x - lastX) + Math.abs(actor.transform.y - lastY);
    lastX = actor.transform.x;
    lastY = actor.transform.y;

    if (moved > 6 || dodgeTimer > 0) { return; }

    var side = Math.random() < 0.5 ? 1 : -1;
    dodgeX = Math.cos(facing + side * Math.PI / 2);
    dodgeY = Math.sin(facing + side * Math.PI / 2);
    dodgeTimer = 0.85;
}

// ---------------------------------------------------------------------------
// Called from outside
// ---------------------------------------------------------------------------

// The director's broadcast. A listener that already has the target ignores it:
// a sound cannot tell it anything it does not already know.
function hearNoise(x, y, strength) {
    if (state === 2) { return; }

    targetX = x;
    targetY = y;
    state = 1;

    // Turn toward it, or it spends the first second walking away from the thing
    // it just heard.
    facing = Math.atan2(y - actor.transform.y, x - actor.transform.x);
}

// Being hurt is the one thing that tells it exactly where you are.
function alertTo(x, y) {
    targetX = x;
    targetY = y;
    state = 2;
}

function setPace(value) { pace = value; }
function getState()     { return state; }

// ---------------------------------------------------------------------------
// Seams -- fill these in, or leave them empty
// ---------------------------------------------------------------------------

// Called once, on the frame it acquires the target. A shout here is what makes
// a horde feel like a horde: emit a noise and every listener nearby converges.
function onSpotted(target, distance) { }

// Called every frame while chasing. Attacks, bites and grabs go here.
function onChasing(target, distance, dt) { }
