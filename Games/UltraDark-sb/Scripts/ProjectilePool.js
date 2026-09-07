// ProjectilePool.js -- every projectile in the scene, in one script, over a pool.
// Attach to one manager actor and tag it whatever your callers look up; the
// examples in AGENT.md use "Projectiles".
//
// Two things make a bullet expensive, and this avoids both.
//
// A ScriptComponent per bullet means a Jint engine per bullet; two hundred of
// them is a slideshow. So a projectile here is a row in parallel arrays, and
// one onUpdate flies all of them.
//
// Creating and destroying an actor per shot churns the scene every frame. So
// actors are pooled: a spent projectile is deactivated and handed back, and
// firing reuses it. The pool only ever grows to the most that were in flight
// at once.
//
// Collision is Physics.overlapCircle, which is engine-side and sees only
// non-trigger colliders. A hit is reported to ONE configured script by actor
// id -- one cross-script call per hit, not per projectile per frame, which is
// what keeps the marshalling cost proportional to what actually happens.

// ===========================================================================
// Tuning
// ===========================================================================
var maxLive      = 320;      // hard ceiling; a shot over it is dropped
var depthPlayer  = 0.62;
var depthEnemy   = 0.60;
var hitRadius    = 15;       // enemy projectile vs the player, in px

// The `code & 4` rider: a projectile that detonates instead of stopping.
var burstRadius  = 82;
var burstShare   = 0.8;      // of the projectile's own damage

// Who to tell about a hit. Each is looked up by tag, and each is optional: a
// tag that matches nothing simply means projectiles never hit that thing.
var enemySinkTag = "Swarm";  // gets damageActor(actorId, dmg, code)
var bossTag      = "Boss";   // gets takeDamage(dmg, code) on its own script
var playerTag    = "Player"; // gets hurt(dmg)

// Where the world ends. A projectile past this is recycled rather than flown
// for ever. If a script on `boundsTag` answers `boundsCall` with a half-extent
// that is used; otherwise `boundsHalf` is.
var boundsTag  = "Director";
var boundsCall = "getArenaHalf";
var boundsHalf = 4000;
var boundsPad  = 80;

// Optional: something that draws an effect, called as
// fx(x, y, size, r, g, b, life). Leave the tag empty for no effects at all.
var fxTag  = "Director";
var fxCall = "spawnEffect";

// ===========================================================================
// The ledger
// ===========================================================================
var n = 0;
var bActor = [], bSprite = [], bX = [], bY = [], bVx = [], bVy = [];
var bLife = [], bDmg = [], bTeam = [], bRad = [], bPierce = [], bCode = [], bLastHit = [];

// Pool of deactivated actors, by team so the tint and depth stay put.
var pool0 = [], pool1 = [];

var swarm = null, player = null, pilot = null, fx = null, bounds = null;
var bossActor = null, bossScript = null;

var TEAM_R = [255, 255], TEAM_G = [230, 120], TEAM_B = [120, 90];

// ===========================================================================
// Lifecycle
// ===========================================================================

function onStart() { resolve(); }

function resolve() {
    if (!swarm)    { var s = Scene.findFirstByTag(enemySinkTag); if (s) { swarm = s.getComponent("ScriptComponent"); } }
    if (!player)   { player = Scene.findFirstByTag(playerTag); if (player) { pilot = player.getComponent("ScriptComponent"); } }
    if (!fx && fxTag !== "") { var f = Scene.findFirstByTag(fxTag); if (f) { fx = f.getComponent("ScriptComponent"); } }
    if (!bounds && boundsTag !== "") { var d = Scene.findFirstByTag(boundsTag); if (d) { bounds = d.getComponent("ScriptComponent"); } }

    // The boss comes and goes, so this is re-checked rather than cached once.
    if (!bossActor || bossActor.active !== true) {
        bossActor = Scene.findFirstByTag(bossTag);
        bossScript = bossActor ? bossActor.getComponent("ScriptComponent") : null;
    }
}

function onUpdate(dt) {
    resolve();

    for (var i = n - 1; i >= 0; i--) {
        bLife[i] -= dt;

        if (bLife[i] <= 0) {
            if ((bCode[i] & 4) && bTeam[i] === 0) { burst(i); }
            recycle(i);
            continue;
        }

        if (sweep(i, dt)) { continue; }

        var a = bActor[i];
        if (a) { a.transform.x = bX[i]; a.transform.y = bY[i]; }

        if (outOfArena(i)) { recycle(i); continue; }
    }
}

// ---------------------------------------------------------------------------
// Movement, in steps small enough that nothing is jumped over.
//
// A projectile is tested where it lands, not along where it went, so a frame's
// travel longer than the capture window passes straight through whatever was in
// between. HAWK's railgun moves 29 px in a frame against a 24 px window -- it
// missed every enemy it was aimed at, at every range, and no test on either side
// of the engine says a word about it.
//
// So the frame is walked in steps of at most the capture window. A slow bolt is
// one step and costs exactly what it did before; only something genuinely fast
// pays for more.
// ---------------------------------------------------------------------------
function sweep(i, dt) {
    var travel = Math.sqrt(bVx[i] * bVx[i] + bVy[i] * bVy[i]) * dt;
    var window = bRad[i] + 6;

    var steps = 1;
    if (travel > window) { steps = Math.ceil(travel / window); }
    if (steps > 12) { steps = 12; }        // a ceiling, so a silly speed cannot stall a frame

    var sub = dt / steps;

    for (var s = 0; s < steps; s++) {
        bX[i] += bVx[i] * sub;
        bY[i] += bVy[i] * sub;

        if (bTeam[i] === 0) {
            if (hitEnemies(i)) { return 1; }
        } else {
            if (hitPlayer(i)) { return 1; }
        }
    }
    return 0;
}

// ===========================================================================
// Firing
// ===========================================================================

function fire(x, y, angle, speed, dmg, team, radius, life, pierce, code) {
    if (n >= maxLive) { return 0; }

    var t = (Number(team) | 0) === 1 ? 1 : 0;
    var r = Number(radius) || 4;
    var a = take(t, r);
    if (!a) { return 0; }

    var ang = Number(angle) || 0;
    var sp = Number(speed) || 0;

    bActor[n]  = a;
    bSprite[n] = a.getComponent("SpriteRenderer");
    bX[n]      = Number(x) || 0;
    bY[n]      = Number(y) || 0;
    bVx[n]     = Math.cos(ang) * sp;
    bVy[n]     = Math.sin(ang) * sp;
    bLife[n]   = Number(life) || 1;
    bDmg[n]    = Number(dmg) || 0;
    bTeam[n]   = t;
    bRad[n]    = r;
    bPierce[n] = Number(pierce) || 0;
    bCode[n]   = Number(code) | 0;
    bLastHit[n] = -1;

    a.transform.x = bX[n];
    a.transform.y = bY[n];
    a.transform.rotation = ang;
    a.active = true;

    if (bSprite[n]) {
        // Oblong along the direction of travel, so a shot reads as a shot.
        bSprite[n].size = { x: r * 3.2, y: r * 1.6 };
        bSprite[n].tint = { R: TEAM_R[t], G: TEAM_G[t], B: TEAM_B[t], A: 255 };
    }

    n++;
    return 1;
}

// ===========================================================================
// The pool
// ===========================================================================

function take(team, r) {
    var pool = team === 0 ? pool0 : pool1;
    if (pool.length > 0) { return pool.pop(); }

    // Named per team: the two draw at different depths, and a single name at two
    // depths makes the draw-order check unable to say which is which.
    var a = Scene.createActor(team === 0 ? "Shot" : "ShotEnemy", 0, 0);
    if (!a) { return null; }
    a.tag = "Shot";
    Scene.addComponent(a, "SpriteRenderer", {
        Tint: { R: TEAM_R[team], G: TEAM_G[team], B: TEAM_B[team], A: 255 },
        Size: [r * 3.2, r * 1.6],
        LayerDepth: team === 0 ? depthPlayer : depthEnemy
    });
    return a;
}

function give(a, team) {
    if (!a) { return; }
    a.active = false;
    if (team === 0) { pool0.push(a); } else { pool1.push(a); }
}

function recycle(i) {
    give(bActor[i], bTeam[i]);

    var last = n - 1;
    if (i !== last) {
        bActor[i] = bActor[last]; bSprite[i] = bSprite[last];
        bX[i] = bX[last]; bY[i] = bY[last]; bVx[i] = bVx[last]; bVy[i] = bVy[last];
        bLife[i] = bLife[last]; bDmg[i] = bDmg[last]; bTeam[i] = bTeam[last];
        bRad[i] = bRad[last]; bPierce[i] = bPierce[last]; bCode[i] = bCode[last];
        bLastHit[i] = bLastHit[last];
    }
    n--;
    bActor[n] = null;
}

// ===========================================================================
// Collision
// ===========================================================================

function hitEnemies(i) {
    var found = Physics.overlapCircle(bX[i], bY[i], bRad[i] + 6);
    if (!found || found.length === 0) { return 0; }

    for (var k = 0; k < found.length; k++) {
        var a = found[k];
        if (!a) { continue; }

        // A piercing projectile keeps flying, so without this it damages the
        // same enemy again on the very next sweep step -- three times a frame
        // for a railgun, which reads as a weapon doing triple its stated damage
        // and only to whatever it happens to be overlapping.
        if (a.id === bLastHit[i]) { continue; }

        if (a.tag === "Enemy") {
            if (swarm) { swarm.call("damageActor", a.id, bDmg[i], bCode[i]); }
        } else if (a.tag === "Boss") {
            if (bossScript) { bossScript.call("takeDamage", bDmg[i], bCode[i]); }
        } else {
            continue;
        }

        bLastHit[i] = a.id;

        if (bPierce[i] > 0) {
            bPierce[i]--;
            continue;
        }

        if (bCode[i] & 4) { burst(i); }
        recycle(i);
        return 1;
    }
    return 0;
}

function hitPlayer(i) {
    if (!player || !pilot) { return 0; }
    var dx = player.transform.x - bX[i], dy = player.transform.y - bY[i];
    var r = bRad[i] + hitRadius;
    if (dx * dx + dy * dy > r * r) { return 0; }

    pilot.call("hurt", bDmg[i]);
    recycle(i);
    return 1;
}

// DEAD MAN'S TRIGGER: the projectile detonates instead of simply stopping.
function burst(i) {
    if (swarm) { swarm.call("damageCircle", bX[i], bY[i], burstRadius, bDmg[i] * burstShare, 0); }
    if (fx) { fx.call(fxCall, bX[i], bY[i], burstRadius, 255, 200, 120, 0.13); }
}

function outOfArena(i) {
    var half = boundsHalf;
    if (bounds) {
        var asked = bounds.call(boundsCall);
        if (asked > 0) { half = asked; }
    }
    half += boundsPad;
    return (bX[i] < -half || bX[i] > half || bY[i] < -half || bY[i] > half) ? 1 : 0;
}

// ===========================================================================
// Readouts
// ===========================================================================

function count()     { return n; }
function poolSize()  { return pool0.length + pool1.length; }

function clearAll() {
    for (var i = n - 1; i >= 0; i--) { recycle(i); }
    n = 0;
    return 1;
}
