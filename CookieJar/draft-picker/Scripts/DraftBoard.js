// DraftBoard.js -- pick one of N cards, in world space.
// Attach to a manager actor and tag it "Draft".
//
// Between rounds a roguelite stops and asks a question. The contract makes
// that awkward twice over: there is no viewport, so a card cannot be pinned to
// the screen, and there is no font, so a card cannot be labelled with words.
//
// Both are answered the same way. The cards are laid out in world space in
// front of the actor they follow -- correct at any resolution, on a phone, and
// wherever the camera happens to be -- and a card says what it is in colour and
// in a row of pips, which is a number you can read at a glance without a font.
//
// The board knows nothing about what is on the cards. The caller opens it,
// dresses each slot, and polls for a pick.
//
//     board.call("open", 3);
//     board.call("setCard", 0, 255, 200, 80, 2);   // amber, two pips
//     ...
//     var picked = board.call("getPicked");        // -1 until something is chosen
//     board.call("close");

// ===========================================================================
// Tuning
// ===========================================================================
var followTag  = "Player";

var cardW      = 118;
var cardH      = 150;
var cardGap    = 26;
var boardY     = -230;      // how far above the followed actor the row sits

var pipSize    = 13;
var pipGap     = 5;
var pipRow     = 5;         // pips per row before wrapping
var pipTop     = -52;       // pip block offset from the card centre

var walkInRadius = 62;      // walking into a card picks it, for touch and pads
var walkInDelay  = 0.45;    // ...but not instantly, or you pick on the way in

// Draw order: above everything, including the dark and the status bars.
var depthCard   = 0.95;
var depthBorder = 0.94;
var depthPip    = 0.97;

var maxCards = 6;

// ===========================================================================
// State
// ===========================================================================
var open01 = 0;
var count = 0;
var picked = -1;
var openTime = 0;

var borders = [], cards = [], cardSprites = [], borderSprites = [];
var pips = [];              // pips[slot] = array of actors
var pipCounts = [];

var follow = null;

var KEYS = ["D1", "D2", "D3", "D4", "D5", "D6"];

// ===========================================================================
// Lifecycle
// ===========================================================================

function onStart() {
    follow = Scene.findFirstByTag(followTag);
    build();
    hideAll();
}

// Every actor the board will ever need is built once at start and then shown
// and hidden. Creating them on open would churn the scene at the exact moment
// the player is looking at it.
function build() {
    for (var i = 0; i < maxCards; i++) {
        var b = Scene.createActor("CardBorder" + i, 0, 0);
        var c = Scene.createActor("Card" + i, 0, 0);
        if (!b || !c) { return; }
        b.tag = "Hud";
        c.tag = "Hud";

        Scene.addComponent(b, "SpriteRenderer", {
            Tint: { R: 235, G: 235, B: 245, A: 255 },
            Size: [cardW + 8, cardH + 8],
            LayerDepth: depthBorder
        });
        Scene.addComponent(c, "SpriteRenderer", {
            Tint: { R: 120, G: 120, B: 140, A: 255 },
            Size: [cardW, cardH],
            LayerDepth: depthCard
        });

        borders.push(b);
        cards.push(c);
        borderSprites.push(b.getComponent("SpriteRenderer"));
        cardSprites.push(c.getComponent("SpriteRenderer"));

        var row = [];
        for (var p = 0; p < 10; p++) {
            var pip = Scene.createActor("Pip" + i + "_" + p, 0, 0);
            if (!pip) { break; }
            pip.tag = "Hud";
            Scene.addComponent(pip, "SpriteRenderer", {
                Tint: { R: 20, G: 20, B: 26, A: 255 },
                Size: [pipSize, pipSize],
                LayerDepth: depthPip
            });
            row.push(pip);
        }
        pips.push(row);
        pipCounts.push(0);
    }
}

function onUpdate(dt) {
    if (!open01) { return; }
    openTime += dt;

    if (!follow || follow.active !== true) { follow = Scene.findFirstByTag(followTag); }

    layout();

    if (picked >= 0) { return; }

    for (var i = 0; i < count; i++) {
        if (Input.isKeyPressed(KEYS[i])) { pick(i); return; }
    }

    if (openTime > walkInDelay && follow) {
        for (var j = 0; j < count; j++) {
            var dx = follow.transform.x - cards[j].transform.x;
            var dy = follow.transform.y - cards[j].transform.y;
            if (dx * dx + dy * dy < walkInRadius * walkInRadius) { pick(j); return; }
        }
    }
}

function pick(slot) {
    picked = slot;
    // The chosen card goes white so the choice is visibly registered before
    // the caller closes the board.
    if (borderSprites[slot]) { borderSprites[slot].tint = { R: 255, G: 255, B: 255, A: 255 }; }
}

// ===========================================================================
// Layout -- centred on the followed actor, so it is always on screen
// ===========================================================================

function layout() {
    var cx = follow ? follow.transform.x : 0;
    var cy = (follow ? follow.transform.y : 0) + boardY;

    var span = count * cardW + (count - 1) * cardGap;
    var startX = cx - span / 2 + cardW / 2;

    for (var i = 0; i < count; i++) {
        var x = startX + i * (cardW + cardGap);
        cards[i].transform.x = x;
        cards[i].transform.y = cy;
        borders[i].transform.x = x;
        borders[i].transform.y = cy;
        layoutPips(i, x, cy);
    }
}

function layoutPips(slot, x, y) {
    var row = pips[slot];
    var count2 = pipCounts[slot];
    for (var p = 0; p < row.length; p++) {
        var s = row[p].getComponent("SpriteRenderer");
        if (p >= count2) {
            if (s) { s.tint = { R: 0, G: 0, B: 0, A: 0 }; }
            continue;
        }
        var col = p % pipRow;
        var line = Math.floor(p / pipRow);
        var wide = Math.min(count2, pipRow);
        row[p].transform.x = x + (col - (wide - 1) / 2) * (pipSize + pipGap);
        row[p].transform.y = y + pipTop + line * (pipSize + pipGap);
        if (s) { s.tint = { R: 18, G: 18, B: 24, A: 255 }; }
    }
}

// ===========================================================================
// The API
// ===========================================================================

function open(howMany) {
    count = Math.max(1, Math.min(maxCards, Number(howMany) | 0));
    picked = -1;
    open01 = 1;
    openTime = 0;

    for (var i = 0; i < count; i++) {
        setVisible(i, 1);
        if (borderSprites[i]) { borderSprites[i].tint = { R: 235, G: 235, B: 245, A: 255 }; }
    }
    for (var j = count; j < maxCards; j++) { setVisible(j, 0); }

    layout();
    return count;
}

function setCard(slot, r, g, b, pipCount) {
    var i = Number(slot) | 0;
    if (i < 0 || i >= maxCards) { return 0; }
    pipCounts[i] = Math.max(0, Math.min(10, Number(pipCount) | 0));
    if (cardSprites[i]) {
        cardSprites[i].tint = { R: Number(r) | 0, G: Number(g) | 0, B: Number(b) | 0, A: 255 };
    }
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
}

function setVisible(i, on) {
    if (i >= cards.length) { return; }
    var a = on ? 255 : 0;
    if (cardSprites[i])   { var t = cardSprites[i].tint;   cardSprites[i].tint   = { R: t.r !== undefined ? t.r : 120, G: t.g !== undefined ? t.g : 120, B: t.b !== undefined ? t.b : 140, A: a }; }
    if (borderSprites[i]) { borderSprites[i].tint = { R: 235, G: 235, B: 245, A: a }; }
    if (!on) {
        var row = pips[i];
        if (row) {
            for (var p = 0; p < row.length; p++) {
                var s = row[p].getComponent("SpriteRenderer");
                if (s) { s.tint = { R: 0, G: 0, B: 0, A: 0 }; }
            }
        }
    }
}
