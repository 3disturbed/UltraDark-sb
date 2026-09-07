// Hud.js -- the readout, in screen space, with words.
// Attach to an empty actor and tag it "Hud".
//
// Before the UI globals existed a prototype's HUD had to be world-space sprites
// following the player, with no text of any kind, because the scripting contract
// had no viewport and no way to draw a glyph. This is what replaces that: a
// panel anchored to a corner, bars with names next to them, a message line, and
// an overlay for pause and death.
//
// It reads the player rather than being told: each bar names a getter on the
// player's script that returns 0..1, which is one string of coupling per bar.

// ---------------------------------------------------------------------------
// What to show
// ---------------------------------------------------------------------------
var title      = "";              // "" hides the title row
var targetTag  = "Player";

// One row per bar: the label, the getter on the player's script, and a colour.
var bars = [
    { label: "HP",  source: "getHealth01",  colour: "#c63832" },
    { label: "STA", source: "getStamina01", colour: "#d8c88a" },
];

// ---------------------------------------------------------------------------
// Shape
// ---------------------------------------------------------------------------
var anchor      = "topleft";
var marginX     = 12;
var marginY     = 12;
var panelWidth  = 240;
var rowHeight   = 14;
var labelWidth  = 34;
var textScale   = 1;
var titleScale  = 2;

var panelColour = "#1a1d24";
var trackColour = "#2a2d34";
var textColour  = "#e8e6e1";
var dimColour   = "#9a978f";

// The message line: say("...") and it fades on its own.
var messageSeconds = 3.5;

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------
var player = null;
var playerScript = null;

var panel = null;
var titleLabel = null;
var rows = [];                 // { label, bar, valueLabel }
var messageLabel = null;
var messageTimer = 0;

var overlay = null;
var overlayTitle = null;
var overlayHint = null;
var paused = false;

function onStart() {
    player = Scene.findFirstByTag(targetTag);
    if (player) { playerScript = player.getComponent("ScriptComponent"); }

    build();
}

// ---------------------------------------------------------------------------
// Building
// ---------------------------------------------------------------------------

function build() {
    var top = marginY;
    var height = marginY + (title !== "" ? 22 : 0) + bars.length * rowHeight + 10;

    panel = UI.panel(marginX, marginY, panelWidth, height - marginY, {
        anchor: anchor, background: panelColour,
    });

    var y = marginY + 8;

    if (title !== "") {
        titleLabel = UI.label(marginX + 10, y, title, {
            anchor: anchor, scale: titleScale, tint: textColour,
        });
        y += 10 * titleScale;
    }

    for (var i = 0; i < bars.length; i++) {
        var row = bars[i];

        var label = UI.label(marginX + 10, y + 1, row.label, {
            anchor: anchor, scale: textScale, tint: dimColour,
        });

        var bar = UI.bar(marginX + 10 + labelWidth, y, panelWidth - 20 - labelWidth - 30, 8, 1, {
            anchor: anchor, tint: row.colour, background: trackColour,
        });

        var valueLabel = UI.label(marginX + panelWidth - 34, y + 1, "100", {
            anchor: anchor, scale: textScale, tint: dimColour,
        });

        rows.push({ label: label, bar: bar, valueLabel: valueLabel, source: row.source });
        y += rowHeight;
    }

    messageLabel = UI.label(0, -marginY - 6, "", {
        anchor: "bottom", scale: 2, tint: textColour, visible: false,
    });
}

// ---------------------------------------------------------------------------
// Frame
// ---------------------------------------------------------------------------

function onUpdate(dt) {
    if (playerScript) {
        for (var i = 0; i < rows.length; i++) {
            var row = rows[i];
            var value = row.source ? Number(playerScript.call(row.source)) : 1;
            if (!(value >= 0)) { value = 0; }
            if (value > 1) { value = 1; }

            row.bar.value = value;
            row.valueLabel.text = String(Math.round(value * 100));
        }
    }

    if (messageTimer > 0) {
        messageTimer -= dt;
        if (messageTimer <= 0) { messageLabel.visible = false; }
    }
}

// ---------------------------------------------------------------------------
// Called from elsewhere
// ---------------------------------------------------------------------------

/** One line along the bottom of the screen, gone a few seconds later. */
function say(text) {
    if (!messageLabel) { return; }
    messageLabel.text = String(text);
    messageLabel.visible = true;
    messageTimer = messageSeconds;
}

/** The pause / game-over overlay. `hint` is the small line under the heading. */
function setPaused(on, heading, hint) {
    paused = Boolean(on);

    if (!paused) {
        if (overlay) { overlay.destroy(); overlay = null; }
        if (overlayTitle) { overlayTitle.destroy(); overlayTitle = null; }
        if (overlayHint) { overlayHint.destroy(); overlayHint = null; }
        return;
    }
    if (overlay) { return; }

    // A panel the size of the viewport: the contract has no "full screen" flag,
    // but it does now have the viewport, which is the same thing said plainly.
    overlay = UI.panel(0, 0, UI.width, UI.height, { anchor: "topleft", background: "#000000" });
    overlayTitle = UI.label(0, -14, heading || "PAUSED", { anchor: "center", scale: 4, tint: textColour });
    overlayHint  = UI.label(0, 24, hint || "", { anchor: "center", scale: 2, tint: dimColour });
}

function isPaused() { return paused ? 1 : 0; }

/** Tidy up, so a scene change does not leave the last scene's HUD on screen. */
function onDestroy() { UI.clear(); }
