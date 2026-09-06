// Spinner.js — rotates the actor, and paints it while a key is held.
//
// Written in the flat-function style the C# engine's Jint bridge defines, so
// this file runs unchanged under both engines.

var degreesPerSecond = 45;
var elapsed = 0;

function onStart() {
    log('Spinner attached to ' + actor.name);
}

function onUpdate(dt) {
    elapsed += dt;

    var t = actor.transform3d;
    if (t) {
        t.rotY = t.rotY + degreesPerSecond * dt;
        t.y = 0.5 + Math.sin(elapsed * 1.6) * 0.15;
    }

    // Holding Space speeds it up, through the action map rather than a raw key,
    // so a gamepad button or an on-screen control drives it too.
    degreesPerSecond = Input.isHeld('Jump') ? 220 : 45;
}
