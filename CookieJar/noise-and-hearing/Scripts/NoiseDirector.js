// NoiseDirector.js -- the one place that knows a sound happened.
// Attach to a single manager actor and tag it "Director".
//
// A noise is a position and a radius. Everything inside that radius that can
// hear is told WHERE the sound was, and that is all it is told: not who made
// it, not where they are now. That one restraint is what makes the mechanic
// interesting, because it means a listener converges on a place you have
// already left.
//
// Anything that makes a sound calls through this rather than talking to
// listeners itself, so the game has a single volume knob and a single place to
// add a visual ping, a log line, or a cooldown.
//
//   var ds = Scene.findFirstByTag("Director").getComponent("ScriptComponent");
//   ds.call("emitNoise", actor.transform.x, actor.transform.y, 300);

// ---------------------------------------------------------------------------
// Dials
// ---------------------------------------------------------------------------
var listenerTag = "Enemy";   // who has ears
var loudest     = 300;       // the radius that counts as "as loud as it gets"
var decayPerSec = 240;       // how fast the readout below falls back to silence
var traceNoises = false;     // log every noise; useful once, deafening after

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------
var loudness = 0;            // the loudest thing heard recently, for a HUD bar

function onUpdate(dt) {
    if (loudness > 0) {
        loudness -= decayPerSec * dt;
        if (loudness < 0) { loudness = 0; }
    }
}

// ---------------------------------------------------------------------------
// The broadcast
//
// A physics query rather than a scan of every actor: the engine already keeps
// the colliders in a swept index, so this costs the same whether the level
// holds ten listeners or two hundred, and a listener with no body is one that
// was never going to walk anywhere.
// ---------------------------------------------------------------------------

function emitNoise(x, y, radius) {
    if (radius > loudness) { loudness = radius; }

    var heard = Physics.overlapCircle(x, y, radius);
    var told = 0;

    for (var i = 0; i < heard.length; i++) {
        var a = heard[i];
        if (!a || a.tag !== listenerTag) { continue; }

        var script = a.getComponent("ScriptComponent");
        if (script) { script.call("hearNoise", x, y, radius); told++; }
    }

    if (traceNoises) { log("noise " + Math.round(radius) + " at " + Math.round(x) + "," + Math.round(y) + " -> " + told); }
    return told;
}

// Every listener at once, whatever the distance: an alarm, a scream, a scripted
// beat. Use sparingly -- it is the opposite of the mechanic above.
function alertAll(x, y) {
    var all = Scene.findByTag(listenerTag);
    for (var i = 0; i < all.length; i++) {
        var script = all[i] ? all[i].getComponent("ScriptComponent") : null;
        if (script) { script.call("hearNoise", x, y, loudest); }
    }
    return all.length;
}

// Wind the whole population up or down -- night, a difficulty setting, a boss
// phase. Listeners multiply their speeds by this.
function setPace(value) {
    var all = Scene.findByTag(listenerTag);
    for (var i = 0; i < all.length; i++) {
        var script = all[i] ? all[i].getComponent("ScriptComponent") : null;
        if (script) { script.call("setPace", value); }
    }
}

// ---------------------------------------------------------------------------
// Readouts
// ---------------------------------------------------------------------------

// 0..1, for a "how loud am I" bar. Showing this to the player is what teaches
// the mechanic without a word of tutorial.
function getNoise01() { return Math.min(1, loudness / loudest); }
