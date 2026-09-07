// DraftBoard.js -- pick one of N cards, in screen space, with real text.
// Attach to a manager actor and tag it "Draft".
//
// Between rounds a roguelite stops and asks a question, and the answer has to be
// readable or it is not a question at all -- it is a guess.
//
// This used to be world-space sprites labelled with a colour and a row of pips,
// because the scripting contract had no viewport and no font. It has both now,
// so a card says what it is in words. Anything that was clever about the old
// version was a workaround, and workarounds should not outlive the problem.
//
// The board knows nothing about what is on the cards. The caller opens it,
// writes each slot, and polls for a pick.
//
//     board.call("open", 3);
//     board.call("setCard", 0, "HEAVY SLUG", "+12% damage", "", "#e24646");
//     board.call("setFooter", 0, "owned x2");
//     ...
//     var picked = board.call("getPicked");     // -1 until something is chosen
//     board.call("close");

// ===========================================================================
// Tuning -- shape
// ===========================================================================
var maxCards = 6;

var cardW    = 210;
var cardH    = 168;
var cardGap  = 18;

var headingY = -150;        // from the middle of the screen
var cardY    = 0;
var hintY    = 120;

var titleScale   = 2;
var bodyScale    = 1;
var headingScale = 3;

// ===========================================================================
// Tuning -- palette
// ===========================================================================
var cardColour     = "#171a21";
var cardHover      = "#232833";
var cardPicked     = "#e8e6e1";
var stripHeight    = 6;
var textColour     = "#e8e6e1";
var dimColour      = "#9a978f";
var headingColour  = "#e8e6e1";

var heading = "";
var hint    = "1-9 or click";

// ===========================================================================
// State
// ===========================================================================
var open01 = 0;
var count = 0;
var picked = -1;

var slots = [];             // { panel, strip, title, line1, line2, footer, button }

var KEYS = ["D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9"];

var headingLabel = null;
var hintLabel = null;

// ===========================================================================
// Lifecycle
// ===========================================================================

function onStart() {
    build();
    hideAll();
}

// Every element the board will ever need is built once and then shown and
// hidden. Creating them on open would allocate at the exact moment the player is
// looking straight at it.
function build() {
    headingLabel = UI.label(0, headingY, "", {
        anchor: "center", scale: headingScale, tint: headingColour,
        align: "center", width: 600, visible: false,
    });
    hintLabel = UI.label(0, hintY, hint, {
        anchor: "center", scale: bodyScale, tint: dimColour,
        align: "center", width: 400, visible: false,
    });

    for (var i = 0; i < maxCards; i++) {
        var panel = UI.panel(0, cardY, cardW, cardH, {
            anchor: "center", background: cardColour, visible: false,
        });
        var strip = UI.panel(0, cardY, cardW, stripHeight, {
            anchor: "center", background: "#888888", visible: false,
        });
        var title = UI.label(0, cardY, "", {
            anchor: "center", scale: titleScale, tint: textColour,
            align: "center", width: cardW, visible: false,
        });
        var line1 = UI.label(0, cardY, "", {
            anchor: "center", scale: bodyScale, tint: dimColour,
            align: "center", width: cardW, visible: false,
        });
        var line2 = UI.label(0, cardY, "", {
            anchor: "center", scale: bodyScale, tint: dimColour,
            align: "center", width: cardW, visible: false,
        });
        var footer = UI.label(0, cardY, "", {
            anchor: "center", scale: bodyScale, tint: dimColour,
            align: "center", width: cardW, visible: false,
        });

        // The button is the whole card and carries no text of its own: the
        // labels above it are what the player reads, and this is what they hit.
        var button = UI.button(0, cardY, cardW, cardH, "", {
            anchor: "center", background: "#00000000", visible: false,
        });

        slots.push({
            panel: panel, strip: strip, title: title,
            line1: line1, line2: line2, footer: footer, button: button,
        });
    }
}

function onUpdate(dt) {
    if (!open01) { return; }

    layout();
    if (picked >= 0) { return; }

    for (var i = 0; i < count; i++) {
        if (Input.isKeyPressed(KEYS[i])) { pick(i); return; }
        if (slots[i].button && slots[i].button.clicked) { pick(i); return; }
    }
}

function pick(slot) {
    picked = slot;
    // The chosen card goes bright before the caller closes the board, so the
    // choice is visibly registered rather than the row just vanishing.
    if (slots[slot].panel) { slots[slot].panel.background = cardPicked; }
    if (slots[slot].title) { slots[slot].title.tint = cardColour; }
}

// ===========================================================================
// Layout -- centred on the viewport, which can change size at any moment
// ===========================================================================

function layout() {
    var span = count * cardW + (count - 1) * cardGap;
    var startX = -span / 2 + cardW / 2;

    for (var i = 0; i < count; i++) {
        var s = slots[i];
        var x = startX + i * (cardW + cardGap);

        s.panel.x = x;
        s.panel.y = cardY;
        s.button.x = x;
        s.button.y = cardY;

        s.strip.x = x;
        s.strip.y = cardY - cardH / 2 + stripHeight / 2;

        s.title.x = x;
        s.title.y = cardY - cardH / 2 + 26;
        s.line1.x = x;
        s.line1.y = cardY - 10;
        s.line2.x = x;
        s.line2.y = cardY + 6;
        s.footer.x = x;
        s.footer.y = cardY + cardH / 2 - 22;

        // Hover is the only thing that says a card is clickable at all.
        if (picked < 0) {
            s.panel.background = s.button.hovered ? cardHover : cardColour;
        }
    }
}

// ===========================================================================
// The API
// ===========================================================================

function open(howMany) {
    count = Math.max(1, Math.min(maxCards, Number(howMany) | 0));
    picked = -1;
    open01 = 1;

    for (var i = 0; i < count; i++) { setVisible(i, 1); }
    for (var j = count; j < maxCards; j++) { setVisible(j, 0); }

    if (headingLabel) { headingLabel.visible = heading !== ""; }
    if (hintLabel) { hintLabel.visible = true; }

    layout();
    return count;
}

/** Title, up to two body lines, and the colour of the strip along the top. */
function setCard(slot, title, line1, line2, colour) {
    var i = Number(slot) | 0;
    if (i < 0 || i >= maxCards) { return 0; }
    var s = slots[i];

    s.title.text = String(title === undefined ? "" : title);
    s.line1.text = String(line1 === undefined ? "" : line1);
    s.line2.text = String(line2 === undefined ? "" : line2);
    s.strip.background = String(colour === undefined ? "#888888" : colour);
    s.title.tint = textColour;
    s.panel.background = cardColour;
    return 1;
}

/** The bottom line: a price, how many you already own, a warning. */
function setFooter(slot, text) {
    var i = Number(slot) | 0;
    if (i < 0 || i >= maxCards) { return 0; }
    slots[i].footer.text = String(text === undefined ? "" : text);
    return 1;
}

/** Dim a card the player cannot take, without hiding what it is. */
function setEnabled(slot, on) {
    var i = Number(slot) | 0;
    if (i < 0 || i >= maxCards) { return 0; }
    var live = (Number(on) | 0) ? 1 : 0;
    slots[i].title.tint = live ? textColour : dimColour;
    slots[i].footer.tint = live ? dimColour : "#7a4040";
    return 1;
}

function setHeading(text) {
    heading = String(text === undefined ? "" : text);
    if (headingLabel) {
        headingLabel.text = heading;
        headingLabel.visible = open01 ? (heading !== "") : false;
    }
    return 1;
}

function setHint(text) {
    hint = String(text === undefined ? "" : text);
    if (hintLabel) { hintLabel.text = hint; }
    return 1;
}

function getPicked() { return open01 ? picked : -1; }
function isOpen()    { return open01; }

function close() {
    open01 = 0;
    picked = -1;
    hideAll();
    return 1;
}

function hideAll() {
    for (var i = 0; i < maxCards; i++) { setVisible(i, 0); }
    if (headingLabel) { headingLabel.visible = false; }
    if (hintLabel) { hintLabel.visible = false; }
}

// Shown and hidden by `visible`, which is what a UI element has. The world-space
// version of this cookie had to use `active` on the actor, because a transparent
// tint hides a sprite in the browser and not natively.
function setVisible(i, on) {
    if (i >= slots.length) { return; }
    var live = on ? true : false;
    var s = slots[i];
    s.panel.visible = live;
    s.strip.visible = live;
    s.title.visible = live;
    s.line1.visible = live;
    s.line2.visible = live;
    s.footer.visible = live;
    s.button.visible = live;
}

// Drops this script's elements only, so a scene change cannot leave a board on
// screen and cannot take another script's UI with it.
function onDestroy() { UI.clear(); }
