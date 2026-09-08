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
    // One tree. The row centres itself, each card stacks its own lines, and the card
    // IS the button -- so the fully transparent overlay this used to need, purely
    // because only a `button` was hit-testable, is gone with the sixteen coordinate
    // writes per card per frame that kept it glued in place.
    var cards = [];
    for (var i = 0; i < maxCards; i++) {
        cards.push({
            name: "card" + i, kind: "button", visible: false,
            width: cardW, height: cardH, background: cardColour,
            layout: "column", gap: 6, padding: 10, crossAlign: "stretch",
            children: [
                { name: "strip" + i, height: stripHeight, background: "#888888" },
                { name: "title" + i, kind: "label", text: "",
                  scale: titleScale, tint: textColour, align: "center" },
                { name: "line1" + i, kind: "label", text: "",
                  scale: bodyScale, tint: dimColour, align: "center" },
                { name: "line2" + i, kind: "label", text: "",
                  scale: bodyScale, tint: dimColour, align: "center" },
                { kind: "spacer", grow: 1 },
                { name: "footer" + i, kind: "label", text: "",
                  scale: bodyScale, tint: dimColour, align: "center" },
            ],
        });
    }

    UI.build({
        name: "board", layout: "column", mainAlign: "center", crossAlign: "center",
        gap: 18, width: "*", height: "*",
        children: [
            { name: "heading", kind: "label", text: "", visible: false,
              scale: headingScale, tint: headingColour, align: "center" },
            { name: "cards", layout: "row", gap: cardGap, mainAlign: "center", children: cards },
            { name: "hint", kind: "label", text: hint, visible: false,
              scale: bodyScale, tint: dimColour, align: "center" },
        ],
    });

    headingLabel = UI.find("heading");
    hintLabel = UI.find("hint");

    for (i = 0; i < maxCards; i++) {
        slots.push({
            card: UI.find("card" + i), strip: UI.find("strip" + i),
            title: UI.find("title" + i), line1: UI.find("line1" + i),
            line2: UI.find("line2" + i), footer: UI.find("footer" + i),
        });
    }
}

function onUpdate(dt) {
    if (!open01) { return; }

    layout();
    if (picked >= 0) { return; }

    for (var i = 0; i < count; i++) {
        if (Input.isKeyPressed(KEYS[i])) { pick(i); return; }
        if (slots[i].card && slots[i].card.clicked) { pick(i); return; }
    }
}

function pick(slot) {
    picked = slot;
    // The chosen card goes bright before the caller closes the board, so the
    // choice is visibly registered rather than the row just vanishing.
    if (slots[slot].card) { slots[slot].card.background = cardPicked; }
    if (slots[slot].title) { slots[slot].title.tint = cardColour; }
}

// ===========================================================================
// Layout -- centred on the viewport, which can change size at any moment
// ===========================================================================

function layout() {
    // The row centres itself and each card stacks its own lines, so all that is left
    // here is the shade that says a card is clickable. This used to be sixteen
    // coordinate writes per card, every frame, purely to hold six children against a
    // parent that had none -- plus a `startX = -span / 2 + cardW / 2` whose trailing
    // half-card existed only because a centre-anchored element's origin is its centre.
    if (picked >= 0) { return; }

    for (var i = 0; i < count; i++) {
        slots[i].card.background = slots[i].card.hovered ? cardHover : cardColour;
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
    s.card.background = cardColour;
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
    // One write, because a card is a node with children now rather than seven
    // elements that happened to share a rectangle.
    slots[i].card.visible = live;
}

// Drops this script's elements only, so a scene change cannot leave a board on
// screen and cannot take another script's UI with it.
function onDestroy() { UI.clear(); }
