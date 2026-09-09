// Effects.js -- shake, flash, fade, hit-stop.
// Attach to an empty actor and tag it "Effects".
//
//   var fx = Scene.findFirstByTag("Effects").getComponent("ScriptComponent");
//   fx.call("shake", 14);
//   fx.call("flash", "#ffffff", 0.12);
//   fx.call("hitStop", 0.06);
//
// The whole point of these four is that they cost nothing to add and change how
// a hit reads. Shake says "that connected", flash says "that was you", hit-stop
// says "that was heavy", and a fade is how a scene ends without a cut.

// ---------------------------------------------------------------------------
// Dials
// ---------------------------------------------------------------------------
var cameraTag   = "MainCamera";
var shakeScale = 1.0;    // set from the options screen
var shakeDecay  = 5.0;      // higher settles faster
var maxShake    = 40;       // a ceiling, so a bug cannot throw the camera off the map
var flashSeconds = 0.12;
var fadeSeconds  = 0.6;

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------
var shakeAmount = 0;
var appliedX = 0;           // what we added last frame, so it can be taken back
var appliedY = 0;

var flashPanel = null;
var flashTimer = 0;
var flashLength = 0;

var fadePanel = null;
var fadeTimer = 0;
var fadeLength = 0;
var fadeIn = false;         // true = covering the screen, false = revealing

var stopTimer = 0;

function onStart() {
    // Both overlays exist from the start and stay invisible: creating one at the
    // moment of a hit is the frame you least want to allocate on.
    //
    // `width: "*"` is what "cover the screen" means to the layout engine, so these
    // follow a resize, a rotation or going fullscreen on their own. This used to be
    // a resize() called first in onLateUpdate, forever, for two panels whose whole
    // intent was to fill the viewport.
    UI.build({
        children: [
            { name: "flash", width: "*", height: "*", background: "#ffffff", visible: false },
            { name: "fade",  width: "*", height: "*", background: "#000000", visible: false },
        ],
    });

    flashPanel = UI.find("flash");
    fadePanel  = UI.find("fade");
}

// ---------------------------------------------------------------------------
// Frame
//
// Late, so a camera-follow script has already written the camera position for
// this frame and the shake is an offset on top of it rather than a fight.
// ---------------------------------------------------------------------------

function onLateUpdate(dt) {
    updateShake(dt);
    updateFlash(dt);
    updateFade(dt);
    updateHitStop(dt);
}

// ---------------------------------------------------------------------------
// Shake
// ---------------------------------------------------------------------------

function updateShake(dt) {
    var camera = Scene.findFirstByTag(cameraTag);
    if (!camera) { return; }

    // Take back last frame's offset before adding this frame's, or the camera
    // walks away from the thing it is meant to be following.
    camera.transform.x = camera.transform.x - appliedX;
    camera.transform.y = camera.transform.y - appliedY;
    appliedX = 0;
    appliedY = 0;

    if (shakeAmount > 0.05) {
        appliedX = (Math.random() * 2 - 1) * shakeAmount;
        appliedY = (Math.random() * 2 - 1) * shakeAmount;

        camera.transform.x = camera.transform.x + appliedX;
        camera.transform.y = camera.transform.y + appliedY;

        // Exponential decay: a big hit and a small one settle over the same
        // time, which reads better than a fixed step.
        shakeAmount = shakeAmount - shakeAmount * shakeDecay * dt;
    } else {
        shakeAmount = 0;
    }
}

// ---------------------------------------------------------------------------
// Flash and fade
// ---------------------------------------------------------------------------

function updateFlash(dt) {
    if (flashTimer <= 0) { return; }

    flashTimer -= dt;
    var remaining = flashTimer / flashLength;
    if (remaining < 0) { remaining = 0; }

    if (flashTimer <= 0) { flashPanel.visible = false; }
    else { flashPanel.background = withAlpha(flashPanel.background, remaining); }
}

function updateFade(dt) {
    if (fadeTimer <= 0) { return; }

    fadeTimer -= dt;
    var done = 1 - Math.max(0, fadeTimer / fadeLength);
    var alpha = fadeIn ? done : 1 - done;

    fadePanel.background = withAlpha(fadePanel.background, alpha);
    if (fadeTimer <= 0 && !fadeIn) { fadePanel.visible = false; }
}

// ---------------------------------------------------------------------------
// Hit-stop
// ---------------------------------------------------------------------------

function updateHitStop(dt) {
    if (stopTimer <= 0) { return; }

    // Unscaled, or a freeze that sets timeScale to 0 can never time itself out.
    stopTimer -= Time.unscaledDeltaTime;
    if (stopTimer <= 0) { Time.timeScale = 1; }
}

// ---------------------------------------------------------------------------
// The API
// ---------------------------------------------------------------------------

/** Kick the camera. Strength is in pixels; 6 is a footstep, 20 is an explosion. */
function shake(strength) {
    // Scaled by whatever the options say. Zero is a real setting: motion is the
    // most common reason somebody cannot play a game like this at all.
    var amount = (Number(strength) || 0) * shakeScale;
    if (amount > maxShake) { amount = maxShake; }
    if (amount > shakeAmount) { shakeAmount = amount; }
}

/** Blink the whole screen. Use white for a hit taken, red for damage dealt. */
function flash(colour, seconds) {
    if (!flashPanel) { return; }
    flashLength = Number(seconds) || flashSeconds;
    flashTimer = flashLength;
    flashPanel.background = withAlpha(String(colour || "#ffffff"), 1);
    flashPanel.visible = true;
}

/** Cover the screen. Call before Scene.load so the cut has somewhere to hide. */
function fadeTo(seconds) {
    if (!fadePanel) { return; }
    fadeLength = Number(seconds) || fadeSeconds;
    fadeTimer = fadeLength;
    fadeIn = true;
    fadePanel.visible = true;
}

/** Reveal it again. Call from onStart of the scene that just loaded. */
function fadeFrom(seconds) {
    if (!fadePanel) { return; }
    fadeLength = Number(seconds) || fadeSeconds;
    fadeTimer = fadeLength;
    fadeIn = false;
    fadePanel.visible = true;
}

/** Freeze everything briefly. Sixty milliseconds is a lot; start there. */
function hitStop(seconds) {
    stopTimer = Number(seconds) || 0.06;
    Time.timeScale = 0;
}

/** All of it at once, for the moment a boss dies. */
function impact(strength) {
    shake(strength || 18);
    flash("#ffffff", 0.08);
    hitStop(0.05);
}

// ---------------------------------------------------------------------------
// Colour
// ---------------------------------------------------------------------------

/**
 * "#rrggbb" plus an alpha, as "#rrggbbaa".
 *
 * Eight-digit hex rather than rgba(): both engines already parse it — the C#
 * side takes 6 or 8 digits and CSS Color 4 takes the same — whereas rgba() is
 * browser-only, so a flash written that way would fade to nothing natively and
 * be invisible in exactly the build nobody looks at.
 */
function withAlpha(colour, alpha) {
    var text = String(colour || "#000000");
    var body = text.charAt(0) === "#" ? text.substring(1) : text;

    if (body.length === 3) {
        body = body.charAt(0) + body.charAt(0) + body.charAt(1) + body.charAt(1) + body.charAt(2) + body.charAt(2);
    }
    if (body.length < 6) { return text; }

    var a = Math.max(0, Math.min(1, Number(alpha)));

    var byteAlpha = Math.round(a * 255);
    var hex = byteAlpha.toString(16);
    if (hex.length < 2) { hex = "0" + hex; }

    return "#" + body.substring(0, 6) + hex;
}

/** How much of the configured shake to actually apply. 0 turns it off. */
function setShakeScale(scale) {
    var v = Number(scale);
    shakeScale = (v === v && v >= 0) ? v : 1;
    return shakeScale;
}

function getShakeScale() { return shakeScale; }
