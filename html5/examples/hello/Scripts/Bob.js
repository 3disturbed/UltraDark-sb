// Bob.js — moves the actor in a slow horizontal arc.

var speed = 0.7;
var reach = 1.4;
var elapsed = 0;
var originX = null;

function onStart() {
    var t = actor.transform3d;
    if (t) originX = t.x;
}

function onUpdate(dt) {
    var t = actor.transform3d;
    if (!t || originX === null) return;

    elapsed += dt;
    t.x = originX + Math.sin(elapsed * speed) * reach;
}
