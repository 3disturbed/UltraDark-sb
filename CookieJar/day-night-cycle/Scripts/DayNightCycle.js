// DayNightCycle.js -- the clock, and the dark.
// Attach to one manager actor and tag it "Clock".
//
// A day is a fraction from 0 to 1. Four thresholds cut it into phases, each
// announced once as it is entered, and a tinted overlay dims the world through
// dusk and back at dawn.
//
// The overlay is pinned to an actor rather than to the camera because the
// shared scripting contract exposes no viewport: a script cannot know how wide
// the window is, so it cannot size or place anything against the screen. A
// large enough quad centred on the player covers the view at any resolution,
// which is the same trick `floating-status-bars` uses for the opposite reason.
//
// This script does not decide what night MEANS. It publishes a curve and a day
// number; a spawner, a temperature system or an AI reads them and makes the
// night its own.

// ---------------------------------------------------------------------------
// The clock
// ---------------------------------------------------------------------------
var dayLength = 150;      // seconds for a whole day, dawn to dawn
var duskAt    = 0.58;     // the light starts to go
var nightAt   = 0.70;     // fully dark from here
var dawnAt    = 0.94;     // and starts coming back here

var startAt   = 0.0;      // where the first day begins; 0.05 starts you at dawn

// ---------------------------------------------------------------------------
// The dark
// ---------------------------------------------------------------------------
var followTag  = "Player";
var overlaySize = 4200;   // must comfortably exceed the widest view you expect
var nightDark  = 175;     // alpha at the dead of night, 0-255
var darkR = 6, darkG = 8, darkB = 20;

// Draw order: high layerDepth is drawn first, so this must be LOWER than the
// world it dims and HIGHER than anything that should stay bright through it --
// a fire, a torch, a HUD.
var overlayDepth = 0.2;

var announce = true;      // log each phase change once

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------
var timeOfDay = 0;
var dayNumber = 1;
var phase = "day";

var follow = null;
var overlay = null;
var overlaySprite = null;

function onStart() {
    timeOfDay = startAt * dayLength;
    follow = Scene.findFirstByTag(followTag);

    phase = phaseFor(timeOfDay / dayLength);
    createOverlay();
}

function onUpdate(dt) {
    timeOfDay += dt;
    if (timeOfDay >= dayLength) {
        timeOfDay -= dayLength;
        dayNumber++;
        onNewDay(dayNumber);
    }

    var next = phaseFor(timeOfDay / dayLength);
    if (next !== phase) {
        var previous = phase;
        phase = next;
        onPhaseChanged(previous, next);
    }
}

function onLateUpdate(dt) {
    if (!overlay) { return; }

    // Re-find the target if it was respawned, so a death does not strand the
    // dark somewhere on the far side of the level.
    if (!follow || follow.active !== true) { follow = Scene.findFirstByTag(followTag); }

    if (follow) {
        overlay.transform.x = follow.transform.x;
        overlay.transform.y = follow.transform.y;
    }

    if (overlaySprite) {
        overlaySprite.tint = {
            R: darkR, G: darkG, B: darkB,
            A: Math.floor(darkness() * nightDark)
        };
    }
}

// ---------------------------------------------------------------------------
// The curve
// ---------------------------------------------------------------------------

function phaseFor(f) {
    if (f >= nightAt && f < dawnAt) { return "night"; }
    if (f >= duskAt  && f < dawnAt) { return "dusk"; }
    return "day";
}

// 0 in full daylight, 1 in the dead of night, ramped through dusk and dawn.
// Anything that should feel worse after dark can just multiply by this.
function darkness() {
    var f = timeOfDay / dayLength;
    if (f < duskAt)  { return 0; }
    if (f < nightAt) { return (f - duskAt) / (nightAt - duskAt); }
    if (f < dawnAt)  { return 1; }
    return 1 - (f - dawnAt) / (1 - dawnAt);
}

function createOverlay() {
    var x = follow ? follow.transform.x : 0;
    var y = follow ? follow.transform.y : 0;

    overlay = Scene.createActor("NightOverlay", x, y);
    if (!overlay) { return; }
    overlay.tag = "Overlay";

    // Scene.addComponent, not overlay.addComponent: a proxy from createActor
    // has no addComponent of its own.
    Scene.addComponent(overlay, "SpriteRenderer", {
        Tint: { R: darkR, G: darkG, B: darkB, A: 0 },
        Size: [overlaySize, overlaySize],
        LayerDepth: overlayDepth
    });
    overlaySprite = overlay.getComponent("SpriteRenderer");
}

// ---------------------------------------------------------------------------
// Readouts
// ---------------------------------------------------------------------------

function getDarkness()   { return darkness(); }
function getDarkness01() { return darkness(); }     // for floating-status-bars
function getDayNumber()  { return dayNumber; }
function getPhase()      { return phase; }          // "day" | "dusk" | "night"
function isNight()       { return phase === "night" ? 1 : 0; }
function getTimeOfDay01(){ return timeOfDay / dayLength; }

// Skip to a point in the day: a bed, a debug key, a cutscene.
function setTimeOfDay01(f) {
    timeOfDay = Math.max(0, Math.min(0.999, Number(f) || 0)) * dayLength;
    phase = phaseFor(timeOfDay / dayLength);
}

// ---------------------------------------------------------------------------
// Seams
// ---------------------------------------------------------------------------

function onPhaseChanged(from, to) {
    if (!announce) { return; }
    if (to === "dusk")       { log("The light is going."); }
    else if (to === "night") { log("Night " + dayNumber + "."); }
    else                     { log("Dawn."); }
}

function onNewDay(day) {
    if (announce) { log("=== Day " + day + " ==="); }
}
