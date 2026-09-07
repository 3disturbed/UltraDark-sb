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

// ===========================================================================
// State
// ===========================================================================
var player = null;
var lastX = 0, lastY = 0;

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
}

// No shake here. The `screen-effects` cookie owns it, and owning it in two
// places is how a camera ends up stuttering: both would write the transform in
// onLateUpdate and take turns. Effects adds its offset on top of wherever this
// rig put the camera, and takes it back again next frame.
