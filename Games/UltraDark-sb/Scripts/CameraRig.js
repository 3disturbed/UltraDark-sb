// CameraRig.js -- the camera, following the pilot with a little lead.
// Attach to the Main Camera actor.
//
// The lead is the whole point: in a twin-stick game the thing you need to see
// is where you are going and what you are shooting, not where you are. Pushing
// the view a little ahead of the ship buys reaction time without moving the
// ship off centre far enough to feel loose.

// ===========================================================================
// Tuning
// ===========================================================================
var smoothSpeed = 6.0;      // how fast the camera catches up (1/sec)
var leadFactor  = 0.16;     // how far ahead of the ship the view sits
var leadMax     = 190;      // ...and the cap on that, in px

var shakeDecay  = 5.5;

// ===========================================================================
// State
// ===========================================================================
var player = null;
var lastX = 0, lastY = 0;
var shake = 0;

function onStart() {
    player = Scene.findFirstByTag("Player");
    if (player) {
        actor.transform.x = player.transform.x;
        actor.transform.y = player.transform.y;
        lastX = player.transform.x;
        lastY = player.transform.y;
    }
}

function onLateUpdate(dt) {
    if (!player || player.active !== true) { player = Scene.findFirstByTag("Player"); }
    if (!player) { return; }

    var px = player.transform.x, py = player.transform.y;

    // Velocity is measured rather than asked for, so the rig needs nothing
    // from the pilot's script and works behind anything it follows.
    var vx = dt > 0 ? (px - lastX) / dt : 0;
    var vy = dt > 0 ? (py - lastY) / dt : 0;
    lastX = px;
    lastY = py;

    var leadX = vx * leadFactor;
    var leadY = vy * leadFactor;
    var leadLen = Math.sqrt(leadX * leadX + leadY * leadY);
    if (leadLen > leadMax) {
        leadX = leadX / leadLen * leadMax;
        leadY = leadY / leadLen * leadMax;
    }

    var targetX = px + leadX;
    var targetY = py + leadY;

    var k = Math.min(1, smoothSpeed * dt);
    actor.transform.x += (targetX - actor.transform.x) * k;
    actor.transform.y += (targetY - actor.transform.y) * k;

    if (shake > 0) {
        shake -= shakeDecay * dt * shake;
        if (shake < 0.05) { shake = 0; }
        actor.transform.x += (Math.random() - 0.5) * shake;
        actor.transform.y += (Math.random() - 0.5) * shake;
    }
}

// Called by anything that wants the view to flinch.
function addShake(amount) {
    shake = Math.min(40, shake + (Number(amount) || 0));
    return shake;
}
