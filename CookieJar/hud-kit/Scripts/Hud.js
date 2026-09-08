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
var stats = [];

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
var trackers = [];

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
var built = 0;

var overlay = null;
var overlayTitle = null;
var overlayHint = null;
var paused = false;

function onStart() {
    player = Scene.findFirstByTag(targetTag);
    if (player) { playerScript = player.getComponent("ScriptComponent"); }

    // ensureBuilt, not build: something may already have spoken to this HUD from
    // its own onStart, and building again would replace the root it landed on.
    ensureBuilt();
}

/**
 * Builds the tree if it is not there yet.
 *
 * Every entry point below goes through this, because a manager that says
 * something to the HUD from its OWN onStart is ordinary -- a Director that opens
 * on a menu does exactly that -- and start order between two actors in one scene
 * is not something to rely on.
 *
 * With the flat UI a too-early call merely added an element. `UI.build` REPLACES
 * this script's root, so an overlay added before it was silently thrown away the
 * moment this script started: the game opened on a menu with no menu on it, and
 * nothing anywhere said so.
 */
function ensureBuilt() {
    if (!built) { build(); }
}

// ---------------------------------------------------------------------------
// Building
// ---------------------------------------------------------------------------

function build() {
    built = 1;

    // A column sized to its own content. This used to be a hand-summed height --
    // `marginY + (title ? 22 : 0) + rows * rowHeight + 10` -- which had to be kept
    // in step with every row added below it, and a bar whose width was
    // `panelWidth - 20 - labelWidth - 30`: three magic subtrahends for "whatever
    // the label and the number leave". Both are now the layout engine's job.
    var children = [];

    if (title !== "") {
        children.push({ name: "title", kind: "label", text: title,
                        scale: titleScale, tint: textColour });
    }

    for (var si = 0; si < stats.length; si++) {
        children.push(statRow(stats[si], si));
    }

    for (var bi = 0; bi < bars.length; bi++) {
        children.push(barRow(bars[bi], bi));
    }

    UI.build({
        name: "hud",
        children: [
            {
                name: "panel", absolute: true, anchor: anchor,
                x: signedX(marginX), y: signedY(marginY),
                width: panelWidth, height: "auto",
                layout: "column", gap: 4, padding: 10,
                background: panelColour,
                children: children,
            },
            {
                name: "message", kind: "label", text: "", visible: false,
                absolute: true, anchor: "bottom", y: -marginY - 6,
                scale: 2, tint: textColour,
            },
            { name: "trackers", absolute: true, anchor: "top", y: trackerTop,
              layout: "column", gap: trackerGap, crossAlign: "center", children: [] },
        ],
    });

    panel = UI.find("panel");
    titleLabel = UI.find("title");
    messageLabel = UI.find("message");

    for (si = 0; si < stats.length; si++) {
        statRows.push({
            label: UI.find("statLabel" + si), valueLabel: UI.find("statValue" + si),
            source: stats[si].source, from: stats[si].from || targetTag, script: null,
            prefix: stats[si].prefix || "", suffix: stats[si].suffix || "",
            decimals: stats[si].decimals || 0,
        });
    }

    for (bi = 0; bi < bars.length; bi++) {
        rows.push({
            label: UI.find("barLabel" + bi), bar: UI.find("bar" + bi),
            valueLabel: UI.find("barValue" + bi), source: bars[bi].source,
        });
    }

    buildTrackers();
}

/** A label and its number, with the number pushed to the right-hand edge. */
function statRow(stat, index) {
    return {
        layout: "row", gap: 6, crossAlign: "center",
        children: [
            { name: "statLabel" + index, kind: "label", text: stat.label,
              width: labelWidth, scale: textScale, tint: dimColour },
            { name: "statValue" + index, kind: "label", text: "-", grow: 1,
              align: "end", scale: textScale, tint: textColour },
        ],
    };
}

/** A label, a bar that takes whatever is left, and a number. */
function barRow(row, index) {
    return {
        layout: "row", gap: 6, crossAlign: "center",
        children: [
            { name: "barLabel" + index, kind: "label", text: row.label,
              width: labelWidth, scale: textScale, tint: dimColour },
            { name: "bar" + index, kind: "bar", grow: 1, height: 8, value: 1,
              tint: row.colour, background: trackColour },
            { name: "barValue" + index, kind: "label", text: "100", width: 30,
              align: "end", scale: textScale, tint: dimColour },
        ],
    };
}

/**
 * A margin is an inset from whichever edge the anchor names, so a right-anchored
 * panel moves left and a bottom-anchored one moves up.
 */
function signedX(margin) {
    return anchor.indexOf("right") >= 0 ? -margin : margin;
}

function signedY(margin) {
    return anchor.indexOf("bottom") >= 0 ? -margin : margin;
}

function buildTrackers() {
    var host = UI.find("trackers");

    for (var i = 0; i < trackers.length; i++) {
        var t = trackers[i];

        // A column with a gap, rather than `trackerTop + i * (height + gap + 8 * scale + 4)`
        // computed per row -- that trailing `8 * scale + 4` was this file's guess at how
        // tall a label is, which is a thing the layout engine can simply measure.
        var group = host.add({
            name: "tracker" + i, layout: "column", gap: 2, crossAlign: "center",
            visible: false,
            children: [
                // A fraction of the viewport, clamped, which used to be four lines of
                // imperative arithmetic re-run sixty times a second in updateTrackers.
                { name: "trackerBar" + i, kind: "bar", value: 1,
                  width: percent(trackerWidth), minWidth: trackerMin, maxWidth: trackerMax,
                  height: trackerHeight, align: "center",
                  tint: t.colour || "#ff4d4d", background: trackColour },
                { name: "trackerName" + i, kind: "label", text: "",
                  scale: trackerScale, tint: textColour, align: "center" },
            ],
        });

        trackerRows.push({
            group: group, bar: UI.find("trackerBar" + i), nameLabel: UI.find("trackerName" + i),
            tag: t.tag, source: t.source || "getHealth01",
            name: t.name || "", flag: t.flag || "", flagText: t.flagText || "",
            colour: t.colour || "#ff4d4d", colourSource: t.colourSource || "",
        });
    }
}

/** A fraction of the parent, in the spelling the layout engine reads. */
function percent(fraction) { return (fraction * 100) + "%"; }


// ---------------------------------------------------------------------------
// Frame
// ---------------------------------------------------------------------------

function onUpdate(dt) {
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

    for (var i = 0; i < trackerRows.length; i++) {
        var row = trackerRows[i];
        var host = row.tag ? Scene.findFirstByTag(row.tag) : null;
        var script = host ? host.getComponent("ScriptComponent") : null;

        if (!script) {
            // One write hides the bar and its heading together, because they are one
            // group now rather than two elements that happen to share a column.
            row.group.visible = false;
            continue;
        }

        var value = Number(script.call(row.source));
        if (!(value >= 0)) { value = 0; }       // NaN too: a getter that is not there
        if (value > 1) { value = 1; }

        row.bar.value = value;
        row.group.visible = true;

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
    ensureBuilt();
    if (!titleLabel) { return 0; }      // built with title "", so there is no row to write to
    titleLabel.text = String(text);
    return 1;
}

/** One line along the bottom of the screen, gone a few seconds later. */
function say(text) {
    ensureBuilt();
    if (!messageLabel) { return; }
    messageLabel.text = String(text);
    messageLabel.visible = true;
    messageTimer = messageSeconds;
}

/** The pause / game-over overlay. `hint` is the small line under the heading. */
function setPaused(on, heading, hint) {
    ensureBuilt();
    paused = Boolean(on);

    if (!paused) {
        if (overlay) { overlay.remove(); overlay = null; overlayTitle = null; overlayHint = null; }
        return;
    }

    if (overlay) {
        // Already showing. Swap the words rather than ignoring the call: going
        // from one overlay straight to another -- game over to a menu, paused to
        // dead -- is ordinary, and keeping the previous heading is a lie on
        // screen that nothing else will correct.
        overlayTitle.text = heading || "PAUSED";
        overlayHint.text = hint || "";
        return;
    }

    // `width: "*"` is what covers the screen, and it keeps covering it through a
    // resize, a rotation or going fullscreen. This used to be a viewport-sized panel
    // rebuilt from UI.width every frame in onUpdate, because a panel is only full
    // screen at the size it was made at -- invisible until somebody played in a
    // window that was not the one it was built in.
    //
    // `modal` traps focus inside the overlay, so a pad or a TV remote cannot walk
    // out of a pause menu into the HUD behind it.
    overlay = UI.root.add({
        name: "overlay", width: "*", height: "*", background: overlayColour,
        layout: "column", mainAlign: "center", crossAlign: "center", gap: 10,
        order: 100, modal: true,
        children: [
            { name: "overlayTitle", kind: "label", text: heading || "PAUSED",
              scale: 4, tint: textColour, align: "center" },
            { name: "overlayHint", kind: "label", text: hint || "",
              scale: 2, tint: dimColour, align: "center" },
        ],
    });

    overlayTitle = UI.find("overlayTitle");
    overlayHint = UI.find("overlayHint");
}

function isPaused() { return paused ? 1 : 0; }

/** Tidy up, so a scene change does not leave the last scene's HUD on screen. */
function onDestroy() { UI.clear(); }
