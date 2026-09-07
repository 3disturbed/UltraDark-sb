// FloatingBars.js -- status bars that live in the world, above an actor's head.
// Attach to any empty actor; it builds its own sprites at start.
//
// Why not pin them to a screen corner? Because a script cannot: the shared
// scripting contract exposes no viewport, so there is no way to know where the
// screen edge is. Anything a script positions must be positioned in world
// space. Following the actor turns that limitation into the right answer --
// correct at every window size, correct on a phone, and in a top-down game it
// is where the player's eyes already are.
//
// Each bar is fed by NAME: you list the functions on the target's script that
// return a number from 0 to 1, and this reads them each frame. The coupling is
// one string per bar and nothing else.

// ---------------------------------------------------------------------------
// What to show
//
// `sources` are function names on the target's ScriptComponent, each returning
// 0..1. They stack upward in this order, so the first is nearest the head.
// ---------------------------------------------------------------------------
var targetTag = "Player";
var sources   = ["getHealth01", "getShield01", "getAbility01", "getMult01", "getConsumable01"];

// One colour per bar, in the same order. Fewer colours than bars is fine -- it
// wraps.
var colourR = [206, 78,  190, 255, 120];
var colourG = [56,  150, 120, 205, 240];
var colourB = [52,  212, 255, 80,  200];

// An optional pip beside the top bar: a boolean-ish getter returning 0 or 1.
// Poison, infection, a curse, "reloading". Empty string turns it off.
var pipSource = "";
var pipR = 130, pipG = 210, pipB = 90;

// ---------------------------------------------------------------------------
// Shape
// ---------------------------------------------------------------------------
var barWidth  = 54;
var barHeight = 4;
var barGap    = 4;
var firstY    = -30;      // how far above the actor the first bar sits
var hideWhenFull = false; // true: a bar at 1.0 disappears, for a tidier screen

// Draw order: the renderer sorts ASCENDING by layerDepth and draws in that
// order, so these must be HIGH to sit in front of the world. Anything you want
// in front of the bars needs a higher one still.
var depthBack = 0.90;
var depthFill = 0.92;

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------
var backs = [];
var backSprites = [];
var fills = [];
var fillSprites = [];

var pip = null;
var pipSprite = null;

var target = null;
var targetScript = null;

function onStart() {
    target = Scene.findFirstByTag(targetTag);
    if (target) { targetScript = target.getComponent("ScriptComponent"); }

    if (!targetScript) {
        warn("FloatingBars: no actor tagged " + targetTag + " with a script to read.");
        return;
    }

    for (var i = 0; i < sources.length; i++) {
        var back = makeSprite("BarBack" + i, barWidth + 2, barHeight + 2, 18, 18, 20, 210, depthBack);
        var fill = makeSprite("BarFill" + i, barWidth, barHeight,
                              colourR[i % colourR.length],
                              colourG[i % colourG.length],
                              colourB[i % colourB.length], 255, depthFill);

        backs.push(back);
        backSprites.push(back ? back.getComponent("SpriteRenderer") : null);
        fills.push(fill);
        fillSprites.push(fill ? fill.getComponent("SpriteRenderer") : null);
    }

    if (pipSource !== "") {
        pip = makeSprite("BarPip", 7, 7, pipR, pipG, pipB, 0, depthFill);
        if (pip) { pipSprite = pip.getComponent("SpriteRenderer"); }
    }
}

function makeSprite(name, w, h, r, g, b, a, depth) {
    var s = Scene.createActor(name, 0, 0);
    if (!s) { return null; }
    s.tag = "Hud";

    // Scene.addComponent, not s.addComponent: a proxy from createActor has no
    // addComponent, which is the commonest way a spawner fails silently.
    Scene.addComponent(s, "SpriteRenderer", {
        Tint: { R: r, G: g, B: b, A: a },
        Size: [w, h],
        LayerDepth: depth
    });
    return s;
}

// Late, so the bars sit over where the actor ended the frame rather than
// trailing it by one.
function onLateUpdate(dt) {
    if (!target || !targetScript) { return; }

    var px = target.transform.x;
    var py = target.transform.y;

    for (var i = 0; i < sources.length; i++) {
        setBar(i, targetScript.call(sources[i]), px, py);
    }

    if (pip && pipSprite) {
        pip.transform.x = px - barWidth / 2 - 8;
        pip.transform.y = py + firstY;
        pipSprite.tint = { R: pipR, G: pipG, B: pipB, A: targetScript.call(pipSource) ? 255 : 0 };
    }
}

function setBar(index, value, px, py) {
    var v = Number(value);
    if (!(v >= 0)) { v = 0; }
    if (v > 1) { v = 1; }

    var y = py + firstY - index * (barHeight + barGap);
    var hidden = hideWhenFull && v >= 0.999;

    var back = backs[index];
    var backSprite = backSprites[index];
    if (back) {
        back.transform.x = px;
        back.transform.y = y;
    }
    if (backSprite) { backSprite.tint = { R: 18, G: 18, B: 20, A: hidden ? 0 : 210 }; }

    var fill = fills[index];
    var sprite = fillSprites[index];
    if (!fill || !sprite) { return; }

    var width = barWidth * v;
    if (width < 0.5) { width = 0.5; }

    // Sprites are drawn from their centre, so a bar that empties from the right
    // has to walk left as it shrinks.
    fill.transform.x = px - barWidth / 2 + width / 2;
    fill.transform.y = y;
    sprite.size = { x: width, y: barHeight };

    if (hidden) {
        sprite.tint = { R: 0, G: 0, B: 0, A: 0 };
    } else {
        sprite.tint = {
            R: colourR[index % colourR.length],
            G: colourG[index % colourG.length],
            B: colourB[index % colourB.length],
            A: 255
        };
    }
}
