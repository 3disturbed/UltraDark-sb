// EntityLedger.js -- many actors, one script, no per-actor script engines.
// Attach to a manager actor and tag it "Ledger".
//
// A ScriptComponent is a script engine. Forty enemies with a script each is
// forty engines, and that is the single most expensive thing a wave-based game
// can do to itself. So an entity here is a ROW: a column per thing it needs to
// remember, one update that walks all of them, and actors that carry only a
// sprite and a collider.
//
// **This is a file you edit.** Everything except `behave()` is the machinery --
// spawning, removal, statuses, separation, contact, area damage, queries, and
// the four rules below that are easy to get wrong. `behave()` is your game.
//
// The four rules, each of which has cost somebody a day:
//
// 1. REMOVAL SWAPS. removeAt(i) moves the last row into slot i, so after
//    anything that can kill, the index you were holding is somebody else -- or
//    past the end. Check, or return.
// 2. ITERATE BACKWARDS. A backwards loop with swap-removal visits every row
//    exactly once. Forwards, a removal skips the row that took its place.
// 3. A CASCADE MUST NOT RECURSE. If a death can cause a death -- an explosion
//    that kills, killing something else that explodes -- taking it directly
//    recurses one frame per link and overflows the stack. What that looks like
//    is not a crash: it is every script hook in the frame failing afterwards,
//    so damage silently stops landing. Blasts are queued and drained flat.
// 4. COLLIDERS ARE FOR QUERIES. Physics.overlapCircle only sees non-trigger
//    colliders, and it is how a projectile finds what it hit. Nothing here has
//    a Rigidbody2D; a collider on its own is static geometry that follows its
//    transform.

// ===========================================================================
// The roster
//
// One row per kind. Add a column when every kind needs it; put anything only
// one kind cares about in `eState`/`eT2` and say so in behave().
// ===========================================================================
var KIND_HP    = [26,  60 ];
var KIND_SPD   = [96,  74 ];
var KIND_SIZE  = [24,  22 ];
var KIND_TOUCH = [11,  6  ];
var KIND_SCORE = [10,  14 ];
var KIND_R     = [224, 150];
var KIND_G     = [64,  220];
var KIND_B     = [64,  120];

var KIND_COUNT = 2;

// ===========================================================================
// Tuning
// ===========================================================================
var targetTag  = "Player";     // what they move towards and touch
var notifyTag  = "Director";   // gets onEntityKilled(kind, x, y, score)

var separation      = 0.55;    // how hard they push out of each other
var separationScan  = 8;       // neighbours checked per entity per frame
var contactCooldown = 0.62;

var boundsTag  = "Director";   // asked for a half-extent, so nothing wanders off
var boundsCall = "getArenaHalf";
var boundsHalf = 4000;

var depth      = 0.40;
var maxLive    = 130;          // frame cost is roughly projectiles x entities

var maxCascade = 96;

// ===========================================================================
// The ledger
// ===========================================================================
var n = 0;
var eActor = [], eSprite = [], eKind = [], eHp = [], eHpMax = [];
var eSpd = [], eSize = [], eT = [], eT2 = [], eState = [];
var eSlow = [], eStun = [], eBurn = [], eVx = [], eVy = [], eId = [];

var target = null, targetScript = null, notify = null, bounds = null;

var nearestIndex = -1;
var burnTick = 0;

var blastX = [], blastY = [], blastR = [], blastD = [];
var blastN = 0, cascading = 0;

// ===========================================================================
// Lifecycle
// ===========================================================================

function onStart() { resolve(); }

function resolve() {
    if (!target || target.active !== true) {
        target = Scene.findFirstByTag(targetTag);
        targetScript = target ? target.getComponent("ScriptComponent") : null;
    }
    if (!notify && notifyTag !== "") {
        var a = Scene.findFirstByTag(notifyTag);
        if (a) { notify = a.getComponent("ScriptComponent"); }
    }
    if (!bounds && boundsTag !== "") {
        var b = Scene.findFirstByTag(boundsTag);
        if (b) { bounds = b.getComponent("ScriptComponent"); }
    }
}

function onUpdate(dt) {
    resolve();

    var tx = target ? target.transform.x : 0;
    var ty = target ? target.transform.y : 0;

    burnTick += dt;
    var burnNow = 0;
    if (burnTick >= 0.5) { burnTick -= 0.5; burnNow = 1; }

    // Rule 2: backwards, so a swap-removal cannot skip a row.
    for (var i = n - 1; i >= 0; i--) {
        if (!eActor[i] || eActor[i].active !== true) { removeAt(i); continue; }
        step(i, dt, tx, ty, burnNow);
    }

    drainBlasts();
}

function step(i, dt, tx, ty, burnNow) {
    if (eSlow[i] > 0) { eSlow[i] -= dt; }
    if (eStun[i] > 0) { eStun[i] -= dt; }
    if (eT[i] > 0)    { eT[i] -= dt; }
    if (eT2[i] > 0)   { eT2[i] -= dt; }

    if (eBurn[i] > 0) {
        eBurn[i] -= dt;
        // Rule 1: anything other than 0 means this row is not this entity now.
        if (burnNow && applyDamage(i, 3, 0) !== 0) { drainBlasts(); return; }
    }

    var ax = eActor[i].transform.x, ay = eActor[i].transform.y;
    var dx = tx - ax, dy = ty - ay;
    var dist = Math.sqrt(dx * dx + dy * dy);
    var nx = dist > 0.001 ? dx / dist : 0;
    var ny = dist > 0.001 ? dy / dist : 0;

    if (eStun[i] <= 0) {
        var speed = eSpd[i] * (eSlow[i] > 0 ? 0.42 : 1);
        behave(i, dt, nx, ny, dist, speed);
    }

    drift(i, dt);
    separate(i);
    contact(i, dist);
    paint(i);
}

// ===========================================================================
// YOUR GAME GOES HERE
//
// Called once per entity per frame, with the direction and distance to the
// target already worked out. Move it, shoot, wind up, whatever the kind does.
// Everything else in this file is machinery.
// ===========================================================================

function behave(i, dt, nx, ny, dist, speed) {
    var kind = eKind[i];

    if (kind === 0) {
        // Walks at you.
        eActor[i].transform.x += nx * speed * dt;
        eActor[i].transform.y += ny * speed * dt;
        eActor[i].transform.rotation = Math.atan2(ny, nx);
    } else {
        // Holds a range band instead of closing.
        var want = dist > 320 ? 1 : (dist < 240 ? -1 : 0);
        eActor[i].transform.x += nx * speed * want * dt;
        eActor[i].transform.y += ny * speed * want * dt;
        eActor[i].transform.rotation = Math.atan2(ny, nx);
    }

    clampIn(i);
}

// ===========================================================================
// Machinery
// ===========================================================================

// Knockback and any other impulse, decaying. Kept separate from behave() so a
// kind that ignores movement still gets pushed by an explosion.
function drift(i, dt) {
    if (eVx[i] === 0 && eVy[i] === 0) { return; }
    eActor[i].transform.x += eVx[i] * dt;
    eActor[i].transform.y += eVy[i] * dt;
    eVx[i] *= 0.88;
    eVy[i] *= 0.88;
    if (Math.abs(eVx[i]) < 1) { eVx[i] = 0; }
    if (Math.abs(eVy[i]) < 1) { eVy[i] = 0; }
    clampIn(i);
}

// O(n) against a rolling stride of neighbours rather than every pair: enough to
// stop forty entities stacking into one sprite, cheap enough to run on all of
// them.
function separate(i) {
    if (n < 2) { return; }
    var ax = eActor[i].transform.x, ay = eActor[i].transform.y;
    var r = eSize[i];
    var j = (i + 1) % n;

    for (var checked = 0; checked < separationScan; checked++) {
        if (j !== i && eActor[j]) {
            var dx = ax - eActor[j].transform.x, dy = ay - eActor[j].transform.y;
            var want = (r + eSize[j]) * 0.5;
            var d2 = dx * dx + dy * dy;
            if (d2 > 0.01 && d2 < want * want) {
                var d = Math.sqrt(d2);
                var push = (want - d) * separation;
                eActor[i].transform.x += (dx / d) * push;
                eActor[i].transform.y += (dy / d) * push;
            }
        }
        j = (j + 1) % n;
    }
}

function contact(i, dist) {
    if (!targetScript || eT2[i] > 0) { return; }
    if (dist > eSize[i] * 0.5 + 16) { return; }
    targetScript.call("hurt", eTouchOf(i));
    eT2[i] = contactCooldown;
}

function eTouchOf(i) { return KIND_TOUCH[eKind[i]]; }

function clampIn(i) {
    var half = boundsHalf;
    if (bounds) {
        var asked = bounds.call(boundsCall);
        if (asked > 0) { half = asked; }
    }
    var t = eActor[i].transform;
    if (t.x < -half) { t.x = -half; }
    if (t.x >  half) { t.x =  half; }
    if (t.y < -half) { t.y = -half; }
    if (t.y >  half) { t.y =  half; }
}

// Colour carries state, which is the cheapest readable feedback there is.
function paint(i) {
    var s = eSprite[i];
    if (!s) { return; }
    var k = eKind[i];
    var r = KIND_R[k], g = KIND_G[k], b = KIND_B[k];

    if (eSlow[i] > 0) { b = Math.min(255, b + 70); r = Math.floor(r * 0.7); }
    if (eBurn[i] > 0) { r = Math.min(255, r + 60); g = Math.floor(g * 0.8); }
    if (eStun[i] > 0) { r = (r + 200) >> 1; g = (g + 200) >> 1; b = (b + 200) >> 1; }

    s.tint = { R: r, G: g, B: b, A: 255 };
}

// ===========================================================================
// Spawning and removal
// ===========================================================================

function spawn(kind, x, y, hpMul, spdMul) {
    var k = Math.max(0, Math.min(KIND_COUNT - 1, Number(kind) | 0));
    if (n >= maxLive) { return -1; }

    var a = Scene.createActor("Entity", Number(x) || 0, Number(y) || 0);
    if (!a) { return -1; }
    a.tag = "Entity";

    var size = KIND_SIZE[k];
    Scene.addComponent(a, "SpriteRenderer", {
        Tint: { R: KIND_R[k], G: KIND_G[k], B: KIND_B[k], A: 255 },
        Size: [size + 6, size],
        LayerDepth: depth
    });
    // Rule 4: non-trigger, no rigidbody. This is what a projectile finds.
    Scene.addComponent(a, "BoxCollider2D", { Size: [size, size] });

    var hm = Number(hpMul) || 1;
    var sm = Number(spdMul) || 1;

    eActor[n]  = a;
    eSprite[n] = a.getComponent("SpriteRenderer");
    eKind[n]   = k;
    eHpMax[n]  = KIND_HP[k] * hm;
    eHp[n]     = eHpMax[n];
    eSpd[n]    = KIND_SPD[k] * sm;
    eSize[n]   = size;
    eT[n]      = Math.random() * 1.2;
    eT2[n]     = 0;
    eState[n]  = 0;
    eSlow[n]   = 0;
    eStun[n]   = 0;
    eBurn[n]   = 0;
    eVx[n]     = 0;
    eVy[n]     = 0;
    eId[n]     = a.id;

    n++;
    return a.id;
}

// Rule 1: the last row moves into slot i.
function removeAt(i) {
    var last = n - 1;
    if (i !== last) {
        eActor[i] = eActor[last]; eSprite[i] = eSprite[last]; eKind[i] = eKind[last];
        eHp[i] = eHp[last]; eHpMax[i] = eHpMax[last]; eSpd[i] = eSpd[last];
        eSize[i] = eSize[last]; eT[i] = eT[last]; eT2[i] = eT2[last];
        eState[i] = eState[last]; eSlow[i] = eSlow[last]; eStun[i] = eStun[last];
        eBurn[i] = eBurn[last]; eVx[i] = eVx[last]; eVy[i] = eVy[last];
        eId[i] = eId[last];
    }
    n--;
    eActor[n] = null;
}

function indexOfId(id) {
    var want = Number(id) | 0;
    for (var i = 0; i < n; i++) { if (eId[i] === want) { return i; } }
    return -1;
}

// ===========================================================================
// Damage
// ===========================================================================

// 1 = died, 0 = survived, -1 = there was nothing there. Anything but 0 means
// the index the caller was holding is no longer this entity.
function applyDamage(i, dmg, code) {
    if (i < 0 || i >= n || !eActor[i]) { return -1; }

    eHp[i] -= Number(dmg) || 0;

    if (code & 1) { eSlow[i] = Math.max(eSlow[i], 1.6); }
    if (code & 2) { eBurn[i] = Math.max(eBurn[i], 2.5); }

    if (eHp[i] > 0) { return 0; }

    kill(i);
    return 1;
}

function kill(i) {
    var x = eActor[i].transform.x, y = eActor[i].transform.y;
    var kind = eKind[i];

    if (notify) { notify.call("onEntityKilled", kind, x, y, KIND_SCORE[kind]); }

    eActor[i].destroy();
    removeAt(i);
}

/** Called by a projectile that overlapped one of these. */
function damageActor(actorId, dmg, code) {
    var i = indexOfId(actorId);
    if (i < 0) { return 0; }
    applyDamage(i, dmg, Number(code) | 0);
    drainBlasts();
    return 1;
}

function damageCircle(x, y, r, dmg, code) {
    var rr = r * r, hits = 0;
    for (var i = n - 1; i >= 0; i--) {
        if (!eActor[i]) { continue; }
        var dx = eActor[i].transform.x - x, dy = eActor[i].transform.y - y;
        if (dx * dx + dy * dy > rr) { continue; }
        applyDamage(i, dmg, Number(code) | 0);
        hits++;
    }
    // Rule 3: drained here, so every caller is covered. A drain raised inside
    // the loop sees `cascading` and returns; the outermost call flattens it.
    drainBlasts();
    return hits;
}

/** Queue an explosion rather than taking it. See rule 3. */
function queueBlast(x, y, r, d) {
    if (blastN >= maxCascade) { return 0; }
    blastX[blastN] = x; blastY[blastN] = y; blastR[blastN] = r; blastD[blastN] = d;
    blastN++;
    return 1;
}

function drainBlasts() {
    if (cascading) { return 0; }
    cascading = 1;

    var done = 0;
    while (blastN > 0 && done < maxCascade) {
        blastN--;
        done++;
        damageCircle(blastX[blastN], blastY[blastN], blastR[blastN], blastD[blastN], 0);
    }
    blastN = 0;

    cascading = 0;
    return done;
}

function slowCircle(x, y, r, secs) { return statusCircle(x, y, r, secs, 0); }
function stunCircle(x, y, r, secs) { return statusCircle(x, y, r, secs, 1); }

function statusCircle(x, y, r, secs, which) {
    var rr = r * r, hits = 0;
    for (var i = 0; i < n; i++) {
        if (!eActor[i]) { continue; }
        var dx = eActor[i].transform.x - x, dy = eActor[i].transform.y - y;
        if (dx * dx + dy * dy > rr) { continue; }
        if (which === 0) { eSlow[i] = Math.max(eSlow[i], secs); }
        else { eStun[i] = Math.max(eStun[i], secs); }
        hits++;
    }
    return hits;
}

function knockCircle(x, y, r, force) {
    var rr = r * r, hits = 0;
    for (var i = 0; i < n; i++) {
        if (!eActor[i]) { continue; }
        var dx = eActor[i].transform.x - x, dy = eActor[i].transform.y - y;
        var d2 = dx * dx + dy * dy;
        if (d2 > rr || d2 < 0.01) { continue; }
        var d = Math.sqrt(d2);
        eVx[i] += (dx / d) * force;
        eVy[i] += (dy / d) * force;
        hits++;
    }
    return hits;
}

// ===========================================================================
// Queries
//
// nearestX finds and caches; nearestY returns the SAME one. Two independent
// scans in one frame can pick two different entities, and whatever is aiming
// then points between them.
// ===========================================================================

function nearestX(x, y, range) {
    nearestIndex = -1;
    var best = range * range;
    for (var i = 0; i < n; i++) {
        if (!eActor[i]) { continue; }
        var dx = eActor[i].transform.x - x, dy = eActor[i].transform.y - y;
        var d2 = dx * dx + dy * dy;
        if (d2 < best) { best = d2; nearestIndex = i; }
    }
    return nearestIndex >= 0 ? eActor[nearestIndex].transform.x : -1000000;
}

function nearestY(x, y, range) {
    if (nearestIndex < 0 || nearestIndex >= n || !eActor[nearestIndex]) { return -1000000; }
    return eActor[nearestIndex].transform.y;
}

function alive() { return n; }

function clearAll() {
    for (var i = n - 1; i >= 0; i--) { if (eActor[i]) { eActor[i].destroy(); } }
    n = 0;
    eActor = [];
    return 1;
}
