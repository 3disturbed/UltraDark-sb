// Swarm.js -- every enemy in the arena, in one script.
// Attach to a manager actor and tag it "Swarm".
//
// Twelve enemy kinds share one ledger of parallel arrays keyed by index. A
// wave of forty enemies is forty actors with a sprite and a collider and NO
// script of their own -- forty Jint engines is the thing that makes a wave
// shooter unaffordable, and none of these enemies needs private state that a
// column cannot hold.
//
// The collider is there for one reason: Bullets.js finds what it hit with
// Physics.overlapCircle, which only sees non-trigger colliders. Contact damage,
// separation and every behaviour below are distance checks in this file, so no
// enemy has a Rigidbody2D and nothing here depends on the physics step.

// ===========================================================================
// The roster -- UltraDark's twelve, straight from the game's shared/enemies.js.
//
// Names, colours, radii, hp, speed, score, core value and spawn cost are the
// real ones. HP is a small integer because the whole game is built on a small
// integer scale: the pilot has THREE hit points, a bullet does one damage, and
// a Brute takes six. Nothing here is on a hundred-point scale.
//
// The shape column is what the original draws (circle, dot, diamond, hex, gear,
// square, tri, crescent, pent, block, ghost, ring). The scripting contract draws
// tinted rectangles, so shape is approximated by the aspect ratio in SIZE_W/H --
// a Mite is a small square, a Weaver a wide diamond-ish sliver, a Forge a block.
// ===========================================================================
var K_DRONE = 0, K_MITE = 1, K_WEAVER = 2, K_BRUTE = 3, K_SPINNER = 4,
    K_MORTAR = 5, K_SNIPER = 6, K_LEECH = 7, K_WARDEN = 8, K_FORGE = 9,
    K_GHOST = 10, K_MAGNET = 11;

var KIND_COUNT = 12;

var KIND_NAME  = ["Drone", "Mite", "Weaver", "Brute", "Spinner", "Mortar",
                  "Sniper", "Leech", "Warden", "Forge", "Ghost", "Magnet"];

var KIND_HP    = [1,   1,   2,   6,   3,   4,   3,   2,   5,   8,   3,   4  ];
var KIND_SPD   = [65,  150, 85,  42,  70,  20,  18,  125, 35,  0,   100, 28 ];
var KIND_RAD   = [13,  8,   14,  26,  16,  18,  15,  12,  20,  24,  15,  18 ];
var KIND_SCORE = [10,  5,   25,  40,  30,  35,  45,  20,  50,  60,  40,  35 ];
var KIND_CORE  = [1,   1,   2,   3,   2,   3,   3,   2,   3,   4,   2,   3  ];
var KIND_COST  = [3,   2,   7,   12,  9,   10,  11,  6,   13,  16,  10,  10 ];
var KIND_DROP  = [0,   0,   0,   0.20,0.15,0.20,0.20,0,   0.30,0.35,0.15,0.20];

var KIND_R     = [255, 255, 91,  255, 255, 194, 255, 91,  143, 255, 201, 255];
var KIND_G     = [91,  177, 208, 140, 228, 107, 61,  255, 180, 177, 216, 228];
var KIND_B     = [110, 91,  255, 91,  91,  250, 240, 201, 255, 91,  255, 91 ];

// Aspect, standing in for the original's silhouettes.
var KIND_WIDE  = [1.0, 1.0, 1.7, 1.15, 1.0, 1.0, 1.4, 1.5, 1.1, 1.0, 1.2, 1.0];

// Per-kind cadences, from the same file.
var WEAVER_FIRE = 3.2;
var MORTAR_FIRE = 4.0,  MORTAR_DELAY = 1.2, MORTAR_R = 90;
var SNIPER_FIRE = 5.0,  SNIPER_AIM = 1.1;
var LEECH_DRAIN = 1.5;                  // multiplier per second, NOT health
var WARDEN_R    = 140;
var FORGE_EVERY = 6;
var GHOST_PHASE = 3.0,  GHOST_WINDOW = 1.6;
var MAGNET_PULL_R = 420, MAGNET_PULL = 70;
var BRUTE_SPLIT = 4;                    // Mites, on death

// ===========================================================================
// Tuning
// ===========================================================================
var separation      = 0.55;   // how hard enemies push out of each other
var contactCooldown = 0.62;   // seconds between contact hits from one enemy
var spawnFlashLife  = 0.30;

var depthEnemy = 0.40;
var depthTell  = 0.42;        // telegraphs, shields -- just above the body

// A hard ceiling. Collision is Physics.overlapCircle, a linear scan of every
// collider, so a frame costs projectiles x enemies: 220 enemies under heavy fire
// measured at 20 ms a frame on a server, which is a phone at about 4 fps.
// tools/perf.mjs is what says so.
var maxEnemies = 130;

// ===========================================================================
// The ledger
// ===========================================================================
var n = 0;
var eActor = [], eSprite = [], eKind = [], eHp = [], eHpMax = [];
var eSpd = [], eSize = [], eT = [], eT2 = [], eState = [];
var eSlow = [], eStun = [], eShield = [], eBurn = [], eVx = [], eVy = [];
var eTouch = [], eId = [], eTell = [], eTellSprite = [];
// The wave's hp scaling, kept per enemy so a Forge's output and a Brute's Mites
// are as tough as the wave that produced them rather than as tough as wave 1.
var eHpMul = [];

var player = null, pilot = null, director = null, bullets = null;

// Set by the Director when a wave has nothing left to send and something is
// still alive. Rooted enemies start creeping and everything speeds up, so a
// wave always terminates.
var hunt = 0;

var nearestIndex = -1;
var nearestBossActor = null;
var burnTick = 0;

// ---------------------------------------------------------------------------
// Deferred blasts.
//
// SHOCKWAVE makes a kill explode, and an explosion kills, and that kill
// explodes. Done directly that is kill -> damageCircle -> applyDamage -> kill,
// recursing once per link: a dense pack of weak enemies and a couple of stacks
// overflowed the JS stack outright. What that looks like from the outside is
// not a crash -- the engine catches it -- it is EVERY script hook in the frame
// failing from then on, so projectiles stop moving, damage stops landing and a
// boss simply never dies. It reads as a balance problem and it is not one.
//
// So a blast is queued rather than taken, and one loop at the top drains the
// queue. The chain still happens; it just happens flat, and it is bounded.
// ---------------------------------------------------------------------------
var blastX = [], blastY = [], blastR = [], blastD = [];
var blastN = 0;
var cascading = 0;
var maxCascade = 96;

// ===========================================================================
// Lifecycle
// ===========================================================================

function onStart() { resolve(); }

function resolve() {
    if (!player)   { player   = Scene.findFirstByTag("Player"); if (player) { pilot = player.getComponent("ScriptComponent"); } }
    if (!director) { var d = Scene.findFirstByTag("Director"); if (d) { director = d.getComponent("ScriptComponent"); } }
    if (!bullets)  { var b = Scene.findFirstByTag("Bullets");  if (b) { bullets  = b.getComponent("ScriptComponent"); } }
}

function onUpdate(dt) {
    resolve();
    if (!player) { return; }

    var px = player.transform.x, py = player.transform.y;
    var playerAlive = pilot ? pilot.call("isAlive") : 0;

    burnTick += dt;
    var burnNow = 0;
    if (burnTick >= 0.5) { burnTick -= 0.5; burnNow = 1; }

    for (var i = n - 1; i >= 0; i--) {
        if (!eActor[i] || eActor[i].active !== true) { removeAt(i); continue; }
        step(i, dt, px, py, playerAlive, burnNow);
    }

    drainBlasts();
}

// ===========================================================================
// One enemy, one frame
// ===========================================================================

function step(i, dt, px, py, playerAlive, burnNow) {
    // -- statuses ------------------------------------------------------------
    if (eSlow[i] > 0)  { eSlow[i] -= dt; }
    if (eStun[i] > 0)  { eStun[i] -= dt; }
    if (eShield[i] > 0){ eShield[i] -= dt; }
    if (eBurn[i] > 0) {
        eBurn[i] -= dt;
        // A burn that kills removes this row, and removeAt swaps the last enemy
        // into the slot -- so index `i` is somebody else, or past the end. The
        // rest of this function would then read a transform off null. Anything
        // other than 0 means the enemy that was here is not here any more.
        if (burnNow && applyDamage(i, 3, 0) !== 0) { drainBlasts(); return; }
    }
    if (eT[i] > 0)  { eT[i] -= dt; }
    if (eT2[i] > 0) { eT2[i] -= dt; }

    if (eStun[i] > 0) { drift(i, dt); paint(i); return; }

    var kind = eKind[i];
    var ax = eActor[i].transform.x, ay = eActor[i].transform.y;
    var dx = px - ax, dy = py - ay;
    var dist = Math.sqrt(dx * dx + dy * dy);
    var nx = dist > 0.001 ? dx / dist : 0;
    var ny = dist > 0.001 ? dy / dist : 0;

    var speed = eSpd[i] * (eSlow[i] > 0 ? 0.42 : 1);
    if (hunt) { speed = (speed > 0 ? speed * 1.45 : 60); }

    if      (kind === K_DRONE || kind === K_MITE) { seek(i, nx, ny, speed, dt); }
    else if (kind === K_WEAVER)  { weave(i, dt, ax, ay, nx, ny, dist, speed); }
    else if (kind === K_BRUTE)   { seek(i, nx, ny, speed, dt); }
    else if (kind === K_SPINNER) { wander(i, dt, speed); }
    else if (kind === K_MORTAR)  { mortar(i, dt, ax, ay, nx, ny, dist, speed); }
    else if (kind === K_SNIPER)  { snipe(i, dt, ax, ay, nx, ny, dist, speed); }
    else if (kind === K_LEECH)   { seek(i, nx, ny, speed, dt); }
    else if (kind === K_WARDEN)  { seek(i, nx, ny, speed * 0.9, dt); warden(i, dt, ax, ay); }
    else if (kind === K_FORGE)   { forge(i, dt, ax, ay); if (hunt) { creep(i, nx, ny, speed, dt); } }
    else if (kind === K_GHOST)   { ghost(i, dt, ax, ay, nx, ny, speed); }
    else if (kind === K_MAGNET)  { magnet(i, dt, nx, ny, dist, speed, playerAlive); }

    separate(i);
    contact(i, dist, playerAlive, dt);
    paint(i);
}

// ---------------------------------------------------------------------------
// Telegraphs. Everything that can hurt you draws itself first: the mortar's
// landing circle, the sniper's sightline. That rule is what makes the roster
// readable rather than unfair.
// ---------------------------------------------------------------------------

function showTell(i, x, y, w, h, r, g, b, alpha) {
    if (eTell[i]) { hideTell(i); }
    var t = Scene.createActor("Tell", x, y);
    if (!t) { return; }
    t.tag = "Fx";
    Scene.addComponent(t, "SpriteRenderer", {
        Tint: { R: r, G: g, B: b, A: alpha },
        Size: [w, h],
        LayerDepth: depthTell
    });
    eTell[i] = t;
}

function hideTell(i) {
    if (eTell[i]) { eTell[i].destroy(); eTell[i] = null; }
}

// Something rooted has to be reachable, or a wave cannot end.
function creep(i, nx, ny, speed, dt) {
    eActor[i].transform.x += nx * speed * dt;
    eActor[i].transform.y += ny * speed * dt;
    clampIn(i);
}

// ---------------------------------------------------------------------------
// The twelve behaviours. Each is UltraDark's `ai` field made literal.
// ---------------------------------------------------------------------------

// "seek" -- Drone, Mite, Brute, Leech, Warden. Straight at you.
function seek(i, nx, ny, speed, dt) {
    eActor[i].transform.x += (nx * speed + eVx[i]) * dt;
    eActor[i].transform.y += (ny * speed + eVy[i]) * dt;
    eActor[i].transform.rotation = Math.atan2(ny, nx);
    eVx[i] *= 0.88;
    eVy[i] *= 0.88;
    clampIn(i);
}

// "weave" -- closes while sliding sideways, and lobs a FAN. The weave is what
// makes it awkward to lead, which is the whole point of the kind.
function weave(i, dt, ax, ay, nx, ny, dist, speed) {
    eT2[i] += dt;
    var slide = Math.sin(eT2[i] * 2.6) * 0.85;
    eActor[i].transform.x += (nx + -ny * slide) * speed * dt;
    eActor[i].transform.y += (ny + nx * slide) * speed * dt;
    eActor[i].transform.rotation = Math.atan2(ny, nx);
    clampIn(i);

    if (eT[i] <= 0 && dist < 620) {
        eT[i] = WEAVER_FIRE;
        fan(ax, ay, Math.atan2(ny, nx), 4, 0.5, 285);
    }
}

// "wander" -- Spinner drifts; the threat is what it leaves behind when it dies.
function wander(i, dt, speed) {
    if (eT[i] <= 0) {
        eT[i] = 1.2 + Math.random() * 1.6;
        var a = Math.random() * Math.PI * 2;
        eState[i] = a;
    }
    eActor[i].transform.x += Math.cos(eState[i]) * speed * dt;
    eActor[i].transform.y += Math.sin(eState[i]) * speed * dt;
    eActor[i].transform.rotation += dt * 3;
    clampIn(i);
}

// "mortar" -- lobs a shell that lands where you WERE, after a telegraphed
// delay. The circle is drawn before anything happens, so standing in it is a
// decision rather than a surprise.
function mortar(i, dt, ax, ay, nx, ny, dist, speed) {
    if (dist > 520) {
        eActor[i].transform.x += nx * speed * dt;
        eActor[i].transform.y += ny * speed * dt;
        clampIn(i);
    }
    eActor[i].transform.rotation = Math.atan2(ny, nx);

    if (eState[i] === 0) {
        if (eT[i] <= 0) {
            eState[i] = 1;
            eT[i] = MORTAR_DELAY;
            eVx[i] = ax + nx * Math.min(dist, 520);   // the aim point, remembered
            eVy[i] = ay + ny * Math.min(dist, 520);
            showTell(i, eVx[i], eVy[i], MORTAR_R * 2, MORTAR_R * 2, 194, 107, 250, 90);
        }
    } else if (eT[i] <= 0) {
        eState[i] = 0;
        eT[i] = MORTAR_FIRE;
        hideTell(i);
        if (director) { director.call("spawnEffect", eVx[i], eVy[i], MORTAR_R * 2, 194, 107, 250, 0.22); }
        if (pilot && player) {
            var ddx = player.transform.x - eVx[i], ddy = player.transform.y - eVy[i];
            if (ddx * ddx + ddy * ddy < MORTAR_R * MORTAR_R) { pilot.call("hurt", 1); }
        }
    }
}

// "sniper" -- draws a sightline, holds it, then fires instantly down it. You
// leave the line or you take it.
function snipe(i, dt, ax, ay, nx, ny, dist, speed) {
    eActor[i].transform.rotation = Math.atan2(ny, nx);

    if (eState[i] === 0) {
        if (dist > 640) {
            eActor[i].transform.x += nx * speed * dt;
            eActor[i].transform.y += ny * speed * dt;
            clampIn(i);
        }
        if (eT[i] <= 0 && dist < 900) {
            eState[i] = 1;
            eT[i] = SNIPER_AIM;
            eVx[i] = nx; eVy[i] = ny;
            showTell(i, ax + nx * 450, ay + ny * 450, 900, 3, 255, 61, 240, 120);
            if (eTell[i]) { eTell[i].transform.rotation = Math.atan2(ny, nx); }
        }
    } else if (eT[i] <= 0) {
        eState[i] = 0;
        eT[i] = SNIPER_FIRE;
        hideTell(i);
        if (bullets) {
            bullets.call("fire", ax + eVx[i] * 20, ay + eVy[i] * 20,
                         Math.atan2(eVy[i], eVx[i]), 1500, 1, 1, 5, 0.9, 9, 0);
        }
    }
}

// "warden" -- refreshes a shield on everything near it, itself included.
function warden(i, dt, ax, ay) {
    if (eT[i] > 0) { return; }
    eT[i] = 0.5;
    for (var j = 0; j < n; j++) {
        if (!eActor[j]) { continue; }
        var dx = eActor[j].transform.x - ax, dy = eActor[j].transform.y - ay;
        if (dx * dx + dy * dy < WARDEN_R * WARDEN_R) { eShield[j] = 0.85; }
    }
}

// "forge" -- rooted, and building. The wave does not end while one lives.
function forge(i, dt, ax, ay) {
    if (eT[i] > 0) { return; }
    eT[i] = FORGE_EVERY;
    if (n >= maxEnemies) { return; }
    var a = Math.random() * Math.PI * 2;
    spawnKind(K_DRONE, ax + Math.cos(a) * 54, ay + Math.sin(a) * 54, eHpMul[i], 1);
}

// "ghost" -- phased and untouchable most of the time, solid only in the window
// where it fires. You do not out-shoot it; you wait for it.
function ghost(i, dt, ax, ay, nx, ny, speed) {
    if (eT[i] <= 0) {
        eState[i] = eState[i] === 1 ? 0 : 1;          // 1 = firing window
        eT[i] = eState[i] === 1 ? GHOST_WINDOW : GHOST_PHASE;

        var col = eActor[i].getComponent("BoxCollider2D");
        if (col) { col.isTrigger = eState[i] !== 1; }  // a trigger is invisible to overlapCircle

        if (eState[i] === 1) { fan(ax, ay, Math.atan2(ny, nx), 4, 0.5, 285); }
    }
    seek(i, nx, ny, eState[i] === 1 ? speed * 0.4 : speed, dt);
}

// "magnet" -- drags you, which takes your positioning rather than your health.
function magnet(i, dt, nx, ny, dist, speed, playerAlive) {
    if (dist > 300) {
        eActor[i].transform.x += nx * speed * dt;
        eActor[i].transform.y += ny * speed * dt;
        clampIn(i);
    }
    if (playerAlive && dist < MAGNET_PULL_R && pilot) {
        pilot.call("pullToward", eActor[i].transform.x, eActor[i].transform.y, MAGNET_PULL * dt);
    }
}

// The FAN pattern the Weaver and the Ghost both fire.
function fan(x, y, angle, count, spread, speed) {
    if (!bullets) { return; }
    for (var i = 0; i < count; i++) {
        var a = angle - spread / 2 + (count === 1 ? 0 : (i / (count - 1)) * spread);
        bullets.call("fire", x, y, a, speed, 1, 1, 5, 3.0, 0, 0);
    }
}

// The RING the Spinner leaves behind. Position before you kill it.
function ring(x, y) {
    if (!bullets) { return; }
    var count = 18;
    var off = Math.random() * Math.PI * 2;
    for (var i = 0; i < count; i++) {
        bullets.call("fire", x, y, off + (i / count) * Math.PI * 2, 160, 1, 1, 5, 3.4, 0, 0);
    }
}

// ---------------------------------------------------------------------------
// Separation and contact
// ---------------------------------------------------------------------------

// A cheap O(n) pass against a stride of neighbours: enough to stop forty
// enemies stacking into one sprite, cheap enough to run on all of them.
function separate(i) {
    var ax = eActor[i].transform.x, ay = eActor[i].transform.y;
    var r = eSize[i];
    var start = (i + 1) % n;
    var checked = 0;

    for (var j = start; checked < 8 && n > 1; checked++) {
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

function contact(i, dist, playerAlive, dt) {
    if (!playerAlive || !pilot) { return; }
    if (dist > eSize[i] * 0.5 + 14) { return; }

    // The Leech is the one that does not hurt you. It drains the MULTIPLIER,
    // which is the run's score, so breaking away from it is worth more than
    // tanking it -- and a player who does not know that will let it ride.
    if (eKind[i] === K_LEECH) {
        if (director) { director.call("drainMultiplier", LEECH_DRAIN * dt); }
        return;
    }

    if (eT2[i] > 0) { return; }
    // Everything else does exactly one damage. The pilot has three hit points
    // and a full second of invulnerability after a hit; that is the whole
    // damage model, and it is why nothing here carries a damage number.
    pilot.call("hurt", 1);
    eT2[i] = contactCooldown;
}

function clampIn(i) {
    if (!director) { return; }
    var hw = director.call("getArenaHalfW");
    var hh = director.call("getArenaHalfH");
    var t = eActor[i].transform;
    if (t.x < -hw) { t.x = -hw; }
    if (t.x >  hw) { t.x =  hw; }
    if (t.y < -hh) { t.y = -hh; }
    if (t.y >  hh) { t.y =  hh; }
}

// Colour carries state, and the colours themselves are UltraDark's.
function paint(i) {
    var s = eSprite[i];
    if (!s) { return; }

    var k = eKind[i];
    var r = KIND_R[k], g = KIND_G[k], b = KIND_B[k];
    var a = 255;

    // A phased Ghost is faint AND untouchable; the two say the same thing, so
    // a player never has to guess which frame it can be shot in.
    if (k === K_GHOST && eState[i] !== 1) { a = 60; }

    if (eShield[i] > 0) { r = (r + 255) >> 1; g = (g + 255) >> 1; b = (b + 255) >> 1; }
    if (eSlow[i] > 0)   { b = Math.min(255, b + 70); r = Math.floor(r * 0.7); }
    if (eBurn[i] > 0)   { r = Math.min(255, r + 60); g = Math.floor(g * 0.8); }
    if (eStun[i] > 0)   { r = (r + 200) >> 1; g = (g + 200) >> 1; b = (b + 200) >> 1; }

    s.tint = { R: r, G: g, B: b, A: a };
}

function spawnKind(kind, x, y, hpMul, spdMul) {
    var k = Math.max(0, Math.min(KIND_COUNT - 1, Number(kind) | 0));
    if (n >= maxEnemies) { return -1; }

    var a = Scene.createActor("Enemy", Number(x) || 0, Number(y) || 0);
    if (!a) { return -1; }
    a.tag = "Enemy";

    // Radius in the original, so the drawn box is a diameter. The aspect is
    // what stands in for the silhouette the original draws.
    var size = KIND_RAD[k] * 2;
    Scene.addComponent(a, "SpriteRenderer", {
        Tint: { R: KIND_R[k], G: KIND_G[k], B: KIND_B[k], A: 255 },
        Size: [size * KIND_WIDE[k], size],
        LayerDepth: depthEnemy
    });
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
    eTouch[n]  = 1;                   // everything does one damage
    eT[n]      = Math.random() * 1.2;
    eT2[n]     = 0;
    eHpMul[n]  = hm;
    eState[n]  = 0;
    eSlow[n]   = 0;
    eStun[n]   = 0;
    eShield[n] = 0;
    eBurn[n]   = 0;
    eVx[n]     = 0;
    eVy[n]     = 0;
    eId[n]     = a.id;
    eTell[n]   = null;

    n++;

    if (director) { director.call("spawnEffect", x, y, size + 14, 255, 255, 255, spawnFlashLife); }
    return a.id;
}

function removeAt(i) {
    hideTell(i);
    var last = n - 1;
    if (i !== last) {
        eActor[i] = eActor[last]; eSprite[i] = eSprite[last]; eKind[i] = eKind[last];
        eHp[i] = eHp[last]; eHpMax[i] = eHpMax[last]; eSpd[i] = eSpd[last];
        eSize[i] = eSize[last]; eT[i] = eT[last]; eT2[i] = eT2[last];
        eState[i] = eState[last]; eSlow[i] = eSlow[last]; eStun[i] = eStun[last];
        eShield[i] = eShield[last]; eBurn[i] = eBurn[last]; eVx[i] = eVx[last];
        eVy[i] = eVy[last]; eTouch[i] = eTouch[last]; eId[i] = eId[last];
        eHpMul[i] = eHpMul[last];
        eTell[i] = eTell[last];
    }
    n--;
    eActor[n] = null;
    eTell[n] = null;
}

function indexOfId(id) {
    for (var i = 0; i < n; i++) { if (eId[i] === id) { return i; } }
    return -1;
}

// ===========================================================================
// Damage
// ===========================================================================

// Returns 1 when the enemy died, 0 when it survived, -1 when there was nothing
// there. Callers that iterate backwards rely on -1 and 1 both meaning "the
// index you had is not what it was".
function applyDamage(i, dmg, code) {
    if (i < 0 || i >= n || !eActor[i]) { return -1; }

    var amount = Number(dmg) || 0;
    if (eShield[i] > 0) { amount *= 0.45; }

    eHp[i] -= amount;

    if (code & 1) { eSlow[i] = Math.max(eSlow[i], 1.6); }
    if (code & 2) { eBurn[i] = Math.max(eBurn[i], 2.5); }

    if (pilot) { pilot.call("onDamageDealt", amount); }

    if (eHp[i] > 0) { return 0; }

    kill(i);
    return 1;
}

function kill(i) {
    var x = eActor[i].transform.x, y = eActor[i].transform.y;
    var kind = eKind[i];
    var hpMul = eHpMul[i];

    if (director) {
        director.call("onEnemyKilled", kind, x, y, KIND_SCORE[kind], KIND_CORE[kind], KIND_DROP[kind]);
        director.call("spawnEffect", x, y, KIND_RAD[kind] * 2 + 10,
                      KIND_R[kind], KIND_G[kind], KIND_B[kind], 0.16);
    }
    if (pilot) {
        pilot.call("onKill");
        var shock = pilot.call("getShockwave");
        if (shock > 0) {
            queueBlast(x, y, 90 + 18 * shock, shock);
            if (director) { director.call("spawnEffect", x, y, 90 + 18 * shock, 255, 190, 90, 0.14); }
        }
    }

    eActor[i].destroy();
    removeAt(i);

    // Two kinds are not finished when they die, and both are a positioning
    // problem rather than a damage one.
    //
    // A Brute bursts into four Mites, so killing one in your face is worse than
    // killing it at range. A Spinner throws a ring of bullets outward, so where
    // it is standing when it dies is the decision -- the original's note on the
    // kind is "position before you kill".
    if (kind === K_BRUTE) {
        for (var m = 0; m < BRUTE_SPLIT; m++) {
            var a2 = (m / BRUTE_SPLIT) * Math.PI * 2;
            spawnKind(K_MITE, x + Math.cos(a2) * 26, y + Math.sin(a2) * 26, hpMul, 1);
        }
    } else if (kind === K_SPINNER) {
        ring(x, y);
    }
}

function queueBlast(x, y, r, d) {
    if (blastN >= maxCascade) { return 0; }
    blastX[blastN] = x; blastY[blastN] = y; blastR[blastN] = r; blastD[blastN] = d;
    blastN++;
    return 1;
}

// Called at every top-level entry point that can kill something. Re-entrant
// calls return at once, so a blast raised inside a blast joins the queue the
// outer loop is already draining rather than opening a new one.
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

// Called by Bullets when a projectile overlapped an actor tagged Enemy.
function damageActor(actorId, dmg, code) {
    var i = indexOfId(Number(actorId) | 0);
    if (i < 0) { return 0; }
    applyDamage(i, dmg, Number(code) | 0);
    drainBlasts();
    return 1;
}

// Area damage. `announce` is 1 for anything the player did deliberately, so a
// blade grinding away every frame does not spam the effect layer.
function damageCircle(x, y, r, dmg, announce) {
    var hits = 0;
    var rr = r * r;
    for (var i = n - 1; i >= 0; i--) {
        if (!eActor[i]) { continue; }
        // A Ghost is solid ONLY in its firing window (eState 1); phased it is
        // untouchable, and skipping it here is what makes that true for area
        // damage and for auto-aim as well as for a bullet.
        if (eKind[i] === K_GHOST && eState[i] !== 1) { continue; }
        var dx = eActor[i].transform.x - x, dy = eActor[i].transform.y - y;
        if (dx * dx + dy * dy > rr) { continue; }
        applyDamage(i, dmg, 0);
        hits++;
    }

    // Every caller is covered by draining here rather than at each call site:
    // a drain raised inside the loop above sees `cascading` and returns, so the
    // outermost damageCircle is always the one that flattens the chain.
    drainBlasts();
    return hits;
}

function slowCircle(x, y, r, secs) {
    var rr = r * r, hits = 0;
    for (var i = 0; i < n; i++) {
        if (!eActor[i]) { continue; }
        var dx = eActor[i].transform.x - x, dy = eActor[i].transform.y - y;
        if (dx * dx + dy * dy <= rr) { eSlow[i] = Math.max(eSlow[i], secs); hits++; }
    }
    return hits;
}

function stunCircle(x, y, r, secs) {
    var rr = r * r, hits = 0;
    for (var i = 0; i < n; i++) {
        if (!eActor[i]) { continue; }
        var dx = eActor[i].transform.x - x, dy = eActor[i].transform.y - y;
        if (dx * dx + dy * dy <= rr) { eStun[i] = Math.max(eStun[i], secs); hits++; }
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

// SPARKS' bolt: hop from the nearest enemy to its nearest untouched neighbour.
function chainFrom(x, y, dmg, links, range) {
    var used = [];
    var cx = x, cy = y, hits = 0;

    for (var link = 0; link < links; link++) {
        var best = -1, bestD = range * range;
        for (var i = 0; i < n; i++) {
            if (!eActor[i] || used[eId[i]]) { continue; }
            // A Ghost is solid ONLY in its firing window (eState 1); phased it is
        // untouchable, and skipping it here is what makes that true for area
        // damage and for auto-aim as well as for a bullet.
        if (eKind[i] === K_GHOST && eState[i] !== 1) { continue; }
            var dx = eActor[i].transform.x - cx, dy = eActor[i].transform.y - cy;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD) { bestD = d2; best = i; }
        }
        if (best < 0) { break; }

        var hx = eActor[best].transform.x, hy = eActor[best].transform.y;
        used[eId[best]] = 1;
        if (director) { director.call("spawnEffect", hx, hy, 30, 190, 140, 255, 0.10); }
        applyDamage(best, dmg, 0);
        hits++;
        cx = hx; cy = hy;
    }
    drainBlasts();
    return hits;
}

// ===========================================================================
// Queries
//
// nearestX finds and caches; nearestY returns the same enemy. Two scans could
// pick two different enemies in the same frame, which would aim between them.
// ===========================================================================

// The boss is not in this ledger -- it is one actor with its own script -- but
// it is absolutely a thing the pilot needs to aim at. Leaving it out meant
// auto-aim, which is the default aim, could not target a boss at all: on a boss
// wave with the adds cleared there was nothing to shoot at and the wave could
// not end. Nothing in the game said a word about it.
function nearestBoss() {
    var b = Scene.findFirstByTag("Boss");
    return (b && b.active === true) ? b : null;
}

function nearestX(x, y, range) {
    nearestIndex = -1;
    nearestBossActor = null;

    var bestD = range * range;
    for (var i = 0; i < n; i++) {
        if (!eActor[i]) { continue; }
        // A Ghost is solid ONLY in its firing window (eState 1); phased it is
        // untouchable, and skipping it here is what makes that true for area
        // damage and for auto-aim as well as for a bullet.
        if (eKind[i] === K_GHOST && eState[i] !== 1) { continue; }
        var dx = eActor[i].transform.x - x, dy = eActor[i].transform.y - y;
        var d2 = dx * dx + dy * dy;
        if (d2 < bestD) { bestD = d2; nearestIndex = i; }
    }

    var boss = nearestBoss();
    if (boss) {
        var bx = boss.transform.x - x, by = boss.transform.y - y;
        var bd2 = bx * bx + by * by;
        if (bd2 < bestD) { bestD = bd2; nearestIndex = -1; nearestBossActor = boss; }
    }

    if (nearestBossActor) { return nearestBossActor.transform.x; }
    return nearestIndex >= 0 ? eActor[nearestIndex].transform.x : -1000000;
}

// Returns the SAME target nearestX chose. Two independent scans in one frame
// can pick two different enemies, and the pilot then aims between them.
function nearestY(x, y, range) {
    if (nearestBossActor && nearestBossActor.active === true) { return nearestBossActor.transform.y; }
    if (nearestIndex < 0 || nearestIndex >= n || !eActor[nearestIndex]) { return -1000000; }
    return eActor[nearestIndex].transform.y;
}

function alive() { return n; }

function setHunt(on) { hunt = (Number(on) | 0) ? 1 : 0; return hunt; }
function isHunting() { return hunt; }

function clearAll() {
    for (var i = n - 1; i >= 0; i--) {
        hideTell(i);
        if (eActor[i]) { eActor[i].destroy(); }
    }
    n = 0;
    eActor = []; eTell = [];
    return 1;
}

function kindCount() { return KIND_COUNT; }
