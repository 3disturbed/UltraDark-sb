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
var title      = "ULTRADARK";     // "" hides the title row
var targetTag  = "Player";

// One row per bar: the label, the getter on the player's script, and a colour.
var bars = [
    { label: "HULL", source: "getHealth01",     colour: "#c63832" },
    { label: "SHLD", source: "getShield01",     colour: "#4e96d4" },
    { label: "ABIL", source: "getAbility01",    colour: "#be78ff" },
    { label: "DASH", source: "getDash01",       colour: "#78d4c0" },
    { label: "ITEM", source: "getConsumable01", colour: "#78f0c8" },
];

// Numeric readouts, drawn above the bars. A bar is a fraction of something; a
// stat is a number in its own right -- a wave, a score, a count of coins -- and
// showing one as a percentage of the largest it has ever been says nothing.
//
// `from` is a tag, so a stat can come from whatever script actually owns it
// rather than being forwarded through the player. Omit it for the player.
// `prefix` and `suffix` go either side; `pad` right-aligns to a width.
//
//   var stats = [
//       { label: "WAVE",  source: "getWave",  from: "Director" },
//       { label: "SCORE", source: "getScore", from: "Director" },
//       { label: "MULT",  source: "getMult",  from: "Director", prefix: "x", decimals: 1 },
//   ];
var stats = [
    { label: "WAVE",  source: "getWave",  from: "Director" },
    { label: "SCORE", source: "getScore", from: "Director" },
    { label: "CORES", source: "getCores", from: "Director" },
    { label: "MULT",  source: "getMult",  from: "Director", prefix: "x", decimals: 2 },
];

// A bar for something that is not always on screen: a boss, a captured point,
// a burning building. It appears when the thing does and is gone when it is.
//
// Each tracker names a TAG rather than a script, and that is the whole of it --
// the bar's lifetime IS the actor's lifetime, so nothing on the other side has
// to remember to show it, hide it, or tell the HUD that the fight is over. A
// boss that dies in a way nobody anticipated still takes its bar with it.
//
//   var trackers = [
//       { tag: "Boss", source: "getHealth01", name: "getName",
//         colour: "#ff4d4d", flag: "isInvuln", flagText: "SHIELDED" },
//   ];
//
// `source` is a 0..1 getter, as everywhere else. `name` is the heading: a
// getter name if the actor knows what it is called, or a plain string if you
// do. `flag` is a getter returning 1 while the target cannot be hurt -- the
// difference between "my shots are missing" and "my shots do not count", which
// a player cannot tell apart from a bar that simply is not moving.
//
// `colour` is a fixed hex string; `colourSource` names a getter returning one
// instead, for a bar that should be the colour of the thing it is measuring --
// which is how a player knows, without being told, that this is a different
// boss from the last one, or that the same boss has changed.
var trackers = [
    { tag: "Boss", source: "getHealth01", name: "getName", colourSource: "getBarColour",
      colour: "#ff4d4d", flag: "isInvuln", flagText: "-- SHIELDED" },
];

// ---------------------------------------------------------------------------
// Shape
// ---------------------------------------------------------------------------
var anchor      = "topleft";
var marginX     = 12;
var marginY     = 12;
var panelWidth  = 210;
var rowHeight   = 14;
var labelWidth  = 46;
var textScale   = 1;
var titleScale  = 2;

// The tracked bar: wide, top-centre, above everything the player is dodging.
// A fraction of the viewport rather than a fixed width, because a bar sized for
// a desktop is most of a phone.
var trackerWidth   = 0.42;      // of the viewport
var trackerMax     = 620;       // ...but never wider than this
var trackerMin     = 220;
var trackerHeight  = 14;
var trackerTop     = 16;
var trackerGap     = 6;
var trackerScale   = 2;

var panelColour = "#1a1d24";
var trackColour = "#2a2d34";
var textColour  = "#e8e6e1";
var dimColour   = "#9a978f";

// The pause overlay, in eight-digit hex so it can be see-through. Opaque black
// hides the game, which is right for a menu and wrong for anything the player
// is being asked to look at while it is up -- a ship to choose, a board to read.
var overlayColour = "#000000cc";

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
var statRows = [];             // { label, valueLabel, source, script, prefix, suffix, decimals }
var trackerRows = [];          // { bar, nameLabel, tag, source, name, flag, flagText }
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
    var height = marginY + (title !== "" ? 22 : 0)
               + stats.length * rowHeight + bars.length * rowHeight + 10;

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

    for (var si = 0; si < stats.length; si++) {
        var stat = stats[si];

        var statLabel = UI.label(marginX + 10, y + 1, stat.label, {
            anchor: anchor, scale: textScale, tint: dimColour,
        });
        var statValue = UI.label(marginX + 10 + labelWidth, y + 1, "-", {
            anchor: anchor, scale: textScale, tint: textColour,
        });

        // Resolved once. A stat whose script is not in the scene yet is looked
        // up again each frame rather than being dropped, because a manager
        // spawned at runtime is normal.
        statRows.push({
            label: statLabel, valueLabel: statValue, source: stat.source,
            from: stat.from || targetTag, script: null,
            prefix: stat.prefix || "", suffix: stat.suffix || "",
            decimals: stat.decimals || 0,
        });
        y += rowHeight;
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

    buildTrackers();
}

// The tracked bars are built once, hidden, and shown when their actor turns up.
// Building them on demand instead would put an allocation in the frame a boss
// arrives -- the frame that already spawns the boss, its collider and its
// script -- and that is the one frame of the fight a player will notice.
function buildTrackers() {
    for (var i = 0; i < trackers.length; i++) {
        var t = trackers[i];
        var top = trackerTop + i * (trackerHeight + trackerGap + 8 * trackerScale + 4);

        // x: 0 against a "top" anchor centres the element on its own width, so
        // neither of these needs to know how wide the window is.
        var bar = UI.bar(0, top, trackerWidth * 640, trackerHeight, 1, {
            anchor: "top", tint: t.colour || "#ff4d4d", background: trackColour,
            scale: 1, align: "center", visible: false,
        });

        var nameLabel = UI.label(0, top + trackerHeight + trackerGap, "", {
            anchor: "top", scale: trackerScale, tint: textColour,
            align: "center", visible: false,
        });

        trackerRows.push({
            bar: bar, nameLabel: nameLabel, top: top,
            tag: t.tag, source: t.source || "getHealth01",
            name: t.name || "", flag: t.flag || "", flagText: t.flagText || "",
            colour: t.colour || "#ff4d4d", colourSource: t.colourSource || "",
        });
    }
}


// ---------------------------------------------------------------------------
// Frame
// ---------------------------------------------------------------------------

function onUpdate(dt) {
    // The viewport can change at any moment -- a resize, a rotation, entering
    // fullscreen -- and a "full screen" panel is only full screen at the size it
    // was made at. Without this the overlay keeps whatever it was born with and
    // hangs off the edge, which is invisible until somebody plays in a window
    // that is not the one it was built in.
    if (overlay) {
        overlay.width = UI.width;
        overlay.height = UI.height;
    }

    updateTrackers();

    for (var si = 0; si < statRows.length; si++) {
        var stat = statRows[si];
        if (!stat.script) {
            var host = Scene.findFirstByTag(stat.from);
            if (host) { stat.script = host.getComponent("ScriptComponent"); }
        }
        if (!stat.script) { continue; }

        var raw = Number(stat.script.call(stat.source));
        if (!(raw === raw)) { raw = 0; }        // NaN, from a getter that is not there
        stat.valueLabel.text = stat.prefix + format(raw, stat.decimals) + stat.suffix;
    }

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

/**
 * One pass over the tracked bars: find the actor, read it, show or hide.
 *
 * The actor is looked up by tag EVERY frame and never cached. A boss is
 * destroyed by whatever kills it, and a cached proxy to a destroyed actor is a
 * bar that keeps reporting the health of something that is not there -- the
 * failure mode being a full red bar over an empty arena. A tag lookup is a scan
 * of the scene, so this is for a handful of things, not a hundred.
 */
function updateTrackers() {
    if (trackerRows.length === 0) { return; }

    // Sized from the viewport each frame for the same reason the overlay is:
    // the window can change under it.
    var barW = Math.round(UI.width * trackerWidth);
    if (barW > trackerMax) { barW = trackerMax; }
    if (barW < trackerMin) { barW = trackerMin; }
    if (barW > UI.width - 16) { barW = UI.width - 16; }

    for (var i = 0; i < trackerRows.length; i++) {
        var row = trackerRows[i];
        var host = row.tag ? Scene.findFirstByTag(row.tag) : null;
        var script = host ? host.getComponent("ScriptComponent") : null;

        if (!script) {
            row.bar.visible = false;
            row.nameLabel.visible = false;
            continue;
        }

        var value = Number(script.call(row.source));
        if (!(value >= 0)) { value = 0; }       // NaN too: a getter that is not there
        if (value > 1) { value = 1; }

        row.bar.width = barW;
        row.bar.value = value;
        row.bar.visible = true;

        // The heading: a getter name if the actor knows what it is called, the
        // string itself if it does not.
        var heading = row.name;
        if (heading !== "") {
            var named = script.call(row.name);
            if (named !== undefined && named !== null && named !== "") { heading = String(named); }
        }
        // Invulnerable is a state, not a smaller number. Said twice on purpose:
        // the fill goes grey, which is the half a player reads without looking,
        // and the heading says the word, which is the half that explains it.
        // Without either, a shielded boss reads as a bug in your gun.
        //
        // The word goes on the HEADING and not on the bar because a bar draws
        // its text in the same colour as its fill, so text on a bar is legible
        // over the empty part and invisible over the full part -- which is to
        // say invisible exactly while the boss is shielded and healthy.
        var shielded = row.flag !== "" && Number(script.call(row.flag)) > 0;
        if (shielded && row.flagText !== "") { heading = heading === "" ? row.flagText : heading + "  " + row.flagText; }

        row.nameLabel.text = heading;
        row.nameLabel.visible = heading !== "";
        row.nameLabel.tint = shielded ? dimColour : textColour;
        var colour = row.colour;
        if (row.colourSource !== "") {
            var live = script.call(row.colourSource);
            if (live !== undefined && live !== null && live !== "") { colour = String(live); }
        }
        row.bar.tint = shielded ? dimColour : colour;
    }
}

// ---------------------------------------------------------------------------
// Called from elsewhere
// ---------------------------------------------------------------------------

// Thousands separated, because a six-figure score read as one run of digits is
// a number nobody can compare to their last one at a glance.
function format(value, decimals) {
    var fixed = decimals > 0 ? value.toFixed(decimals) : String(Math.round(value));
    var dot = fixed.indexOf(".");
    var whole = dot < 0 ? fixed : fixed.substring(0, dot);
    var rest = dot < 0 ? "" : fixed.substring(dot);

    var negative = whole.charAt(0) === "-";
    if (negative) { whole = whole.substring(1); }

    var out = "";
    for (var i = 0; i < whole.length; i++) {
        if (i > 0 && (whole.length - i) % 3 === 0) { out += ","; }
        out += whole.charAt(i);
    }
    return (negative ? "-" : "") + out + rest;
}

/** Change the panel's heading — a class, a level name, whoever is being played. */
function setTitle(text) {
    if (!titleLabel) { return 0; }      // built with title "", so there is no row to write to
    titleLabel.text = String(text);
    return 1;
}

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

    if (overlay) {
        // Already showing. Swap the words rather than ignoring the call: going
        // from one overlay straight to another -- game over to a menu, paused to
        // dead -- is ordinary, and keeping the previous heading is a lie on
        // screen that nothing else will correct.
        overlay.width = UI.width;
        overlay.height = UI.height;
        if (overlayTitle) { overlayTitle.text = heading || "PAUSED"; }
        if (overlayHint) { overlayHint.text = hint || ""; }
        return;
    }

    // A panel the size of the viewport: the contract has no "full screen" flag,
    // but it does now have the viewport, which is the same thing said plainly.
    overlay = UI.panel(0, 0, UI.width, UI.height, { anchor: "topleft", background: overlayColour });
    overlayTitle = UI.label(0, -14, heading || "PAUSED", { anchor: "center", scale: 4, tint: textColour });
    overlayHint  = UI.label(0, 24, hint || "", { anchor: "center", scale: 2, tint: dimColour });
}

function isPaused() { return paused ? 1 : 0; }

/** Tidy up, so a scene change does not leave the last scene's HUD on screen. */
function onDestroy() { UI.clear(); }
