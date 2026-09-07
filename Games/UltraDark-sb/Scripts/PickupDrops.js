// PickupDrops.js -- things that fall on the floor and are worth walking over.
// Attach to a manager actor and tag it "Pickups".
//
// A drop is a row in parallel arrays, not an actor with its own script: a good
// wave drops dozens, and a ScriptComponent per coin is a script engine per coin.
// The actors are pooled too, so a fight that drops two hundred coins allocates
// as many as were on the floor at once and then stops.
//
// Everything a drop does is here except what it MEANS. When one is collected,
// one call goes to the script you name:
//
//     onPickup(kind, value)     on the collector, or on `notifyTag`
//
// One call per pickup, never per drop per frame.

// ===========================================================================
// Kinds
//
// One row per kind of thing that can drop. `size` and the colour are how it
// reads on the floor; `life` is how long before it goes; `magnetic` is whether
// a magnet effect can pull it.
// ===========================================================================
var KINDS = [
    { name: "core",       r: 255, g: 210, b: 90,  size: 13, life: 22, magnetic: 1 },
    { name: "consumable", r: 120, g: 240, b: 200, size: 17, life: 22, magnetic: 1 },
];

// ===========================================================================
// Tuning
// ===========================================================================
var collectorTag = "Player";
var notifyTag    = "Director";  // the run owns the cores, not the pilot

// How close is close enough. A pickup radius smaller than the collector moves
// in a frame is a pickup you can run straight through at speed.
var collectRadius = 34;

// Magnet. `magnetSource` is a getter on the collector's script returning how
// many stacks of a pull effect it has; 0 turns it off. Reach and speed are per
// stack, so it is a real upgrade rather than a switch.
var magnetSource = "getMagnet";
var magnetReach  = 130;
var magnetSpeed  = 420;

// Drops blink before they go, so a coin never simply vanishes while you are
// walking to it.
var blinkFor     = 3.0;
var blinkRate    = 8;

var depth        = 0.30;

var maxLive      = 200;

// ===========================================================================
// State
// ===========================================================================
var n = 0;
var pActor = [], pSprite = [], pKind = [], pValue = [], pLife = [], pMax = [];
var pool = [];

var collector = null, collectorScript = null, notify = null;

// ===========================================================================
// Lifecycle
// ===========================================================================

function onStart() { resolve(); }

function resolve() {
    if (!collector || collector.active !== true) {
        collector = Scene.findFirstByTag(collectorTag);
        collectorScript = collector ? collector.getComponent("ScriptComponent") : null;
    }
    if (!notify) {
        if (notifyTag === "") { notify = collectorScript; }
        else {
            var a = Scene.findFirstByTag(notifyTag);
            if (a) { notify = a.getComponent("ScriptComponent"); }
        }
    }
}

function onUpdate(dt) {
    resolve();
    if (!collector) { return; }

    var cx = collector.transform.x, cy = collector.transform.y;

    var stacks = 0;
    if (magnetSource !== "" && collectorScript) {
        stacks = Number(collectorScript.call(magnetSource));
        if (!(stacks > 0)) { stacks = 0; }
    }
    var reach = collectRadius + stacks * magnetReach;

    for (var i = n - 1; i >= 0; i--) {
        var a = pActor[i];
        if (!a || a.active !== true) { removeAt(i); continue; }

        pLife[i] -= dt;
        if (pLife[i] <= 0) { recycle(i); continue; }

        var dx = cx - a.transform.x, dy = cy - a.transform.y;
        var d2 = dx * dx + dy * dy;

        if (stacks > 0 && d2 < reach * reach && d2 > 1) {
            var d = Math.sqrt(d2);
            a.transform.x += (dx / d) * magnetSpeed * dt;
            a.transform.y += (dy / d) * magnetSpeed * dt;
        }

        if (d2 < collectRadius * collectRadius) {
            if (notify) { notify.call("onPickup", pKind[i], pValue[i]); }
            recycle(i);
            continue;
        }

        blink(i);
    }
}

// About to expire: flash, so it is never a thing that silently was not there.
function blink(i) {
    var s = pSprite[i];
    if (!s) { return; }
    var k = KINDS[pKind[i]];
    var visible = 1;
    if (pLife[i] < blinkFor) {
        visible = (Math.floor(pLife[i] * blinkRate) % 2 === 0) ? 1 : 0;
    }
    s.tint = { R: k.r, G: k.g, B: k.b, A: visible ? 255 : 40 };
}

// ===========================================================================
// Dropping
// ===========================================================================

/** Drop one. `kind` indexes KINDS; `value` is whatever it is worth to you. */
function drop(kind, x, y, value) {
    var k = Math.max(0, Math.min(KINDS.length - 1, Number(kind) | 0));
    if (n >= maxLive) { return 0; }

    var a = take(k);
    if (!a) { return 0; }

    a.transform.x = Number(x) || 0;
    a.transform.y = Number(y) || 0;
    a.active = true;

    pActor[n]  = a;
    pSprite[n] = a.getComponent("SpriteRenderer");
    pKind[n]   = k;
    pValue[n]  = Number(value) || 0;
    pMax[n]    = KINDS[k].life;
    pLife[n]   = pMax[n];

    if (pSprite[n]) {
        pSprite[n].size = { x: KINDS[k].size, y: KINDS[k].size };
        pSprite[n].tint = { R: KINDS[k].r, G: KINDS[k].g, B: KINDS[k].b, A: 255 };
    }

    n++;
    return 1;
}

/** Several at once, scattered -- what a boss death wants. */
function burst(kind, x, y, value, howMany, spread) {
    var made = 0;
    var count = Math.max(1, Number(howMany) | 0);
    var r = Number(spread) || 90;
    for (var i = 0; i < count; i++) {
        made += drop(kind, x + (Math.random() - 0.5) * r * 2,
                        y + (Math.random() - 0.5) * r * 2, value);
    }
    return made;
}

// ===========================================================================
// The pool
// ===========================================================================

function take(kind) {
    if (pool.length > 0) { return pool.pop(); }

    var a = Scene.createActor("Pickup", 0, 0);
    if (!a) { return null; }
    a.tag = "Pickup";
    Scene.addComponent(a, "SpriteRenderer", {
        Tint: { R: KINDS[kind].r, G: KINDS[kind].g, B: KINDS[kind].b, A: 255 },
        Size: [KINDS[kind].size, KINDS[kind].size],
        LayerDepth: depth
    });
    return a;
}

function recycle(i) {
    var a = pActor[i];
    if (a) { a.active = false; pool.push(a); }
    removeAt(i);
}

function removeAt(i) {
    var last = n - 1;
    if (i !== last) {
        pActor[i] = pActor[last]; pSprite[i] = pSprite[last]; pKind[i] = pKind[last];
        pValue[i] = pValue[last]; pLife[i] = pLife[last]; pMax[i] = pMax[last];
    }
    n--;
    pActor[n] = null;
}

// ===========================================================================
// Readouts
// ===========================================================================

function count()    { return n; }
function poolSize() { return pool.length; }

function clearAll() {
    for (var i = n - 1; i >= 0; i--) { recycle(i); }
    n = 0;
    return 1;
}
