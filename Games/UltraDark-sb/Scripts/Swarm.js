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
// The roster
//
// 0 GRUNT      walks at you, hits you for touching it
// 1 RUSHER     winds up, then throws itself along a fixed line
// 2 SPITTER    keeps its distance and lobs bolts
// 3 SNIPER     charges a beam down a telegraphed line, then fires it
// 4 GHOST      goes translucent and untargetable on a cycle
// 5 WARDEN     shields every enemy near it; slow and worth killing first
// 6 FORGE      does not move, builds grunts
// 7 MAGNET     drags you toward it
// 8 LEECH      latches on and drains while it holds
// 9 BRUISER    heavy, slow, slams
// 10 SWARMLING tiny, fast, arrives in numbers
// 11 TURRET    rooted, fires bursts along its facing
// ===========================================================================
var K_GRUNT = 0, K_RUSHER = 1, K_SPITTER = 2, K_SNIPER = 3, K_GHOST = 4,
    K_WARDEN = 5, K_FORGE = 6, K_MAGNET = 7, K_LEECH = 8, K_BRUISER = 9,
    K_SWARM = 10, K_TURRET = 11;

var KIND_COUNT = 12;

var KIND_HP     = [26,  30,  24,  30,  34,  70,  120, 44,  22,  190, 9,   60 ];
var KIND_SPD    = [96,  150, 74,  58,  116, 52,  0,   64,  178, 46,  228, 0  ];
var KIND_SIZE   = [24,  20,  22,  22,  24,  32,  40,  28,  14,  50,  11,  28 ];
var KIND_TOUCH  = [11,  17,  6,   6,   10,  8,   6,   7,   4,   26,  5,   6  ];
var KIND_SCORE  = [10,  14,  14,  18,  18,  36,  50,  22,  10,  70,  4,   26 ];
var KIND_RANGE  = [0,   250, 380, 640, 0,   0,   0,   300, 0,   0,   0,   440];

var KIND_R      = [224, 255, 150, 120, 190, 90,  255, 200, 140, 200, 250, 255];
var KIND_G      = [64,  140, 220, 130, 190, 200, 170, 90,  240, 60,  120, 90 ];
var KIND_B      = [64,  40,  120, 255, 220, 160, 40,  255, 120, 40,  180, 140];

// Chunky enemies drop consumables; everything else can drop cores.
var KIND_CHUNKY = [0,   0,   0,   0,   0,   1,   1,   0,   0,   1,   0,   1  ];

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
    if (hunt) {
        // A rooted enemy has no speed of its own, so hunting gives it one.
        speed = (speed > 0 ? speed * 1.45 : 60);
    }

    if (kind === K_GRUNT || kind === K_SWARM) {
        chase(i, nx, ny, speed, dt);
    } else if (kind === K_RUSHER) {
        stepRusher(i, dt, nx, ny, dist, speed);
    } else if (kind === K_SPITTER) {
        stepRanged(i, dt, ax, ay, nx, ny, dist, speed, 1.55, 300, 8, 12, 150, 220, 120);
    } else if (kind === K_SNIPER) {
        stepSniper(i, dt, ax, ay, nx, ny, dist, speed);
    } else if (kind === K_GHOST) {
        stepGhost(i, dt, nx, ny, speed);
    } else if (kind === K_WARDEN) {
        chase(i, nx, ny, speed * 0.8, dt);
        stepWarden(i, dt, ax, ay);
    } else if (kind === K_FORGE) {
        stepForge(i, dt, ax, ay);
        if (hunt) { creep(i, nx, ny, speed, dt); }
    } else if (kind === K_MAGNET) {
        stepMagnet(i, dt, nx, ny, dist, speed, playerAlive);
    } else if (kind === K_LEECH) {
        chase(i, nx, ny, speed, dt);
    } else if (kind === K_BRUISER) {
        stepBruiser(i, dt, ax, ay, nx, ny, dist, speed);
    } else if (kind === K_TURRET) {
        stepRanged(i, dt, ax, ay, nx, ny, dist, speed * 0, 1.15, 420, 9, 10, 255, 90, 140);
        if (hunt) { creep(i, nx, ny, speed, dt); }
    }

    separate(i);
    contact(i, dist, playerAlive, dt);
    paint(i);
}

function drift(i, dt) {
    if (eVx[i] === 0 && eVy[i] === 0) { return; }
    eActor[i].transform.x += eVx[i] * dt;
    eActor[i].transform.y += eVy[i] * dt;
    eVx[i] *= 0.86;
    eVy[i] *= 0.86;
    clampIn(i);
}

// Straight-line movement with no facing change, for something that was never
// meant to move and is only doing it because the wave has to end.
function creep(i, nx, ny, speed, dt) {
    eActor[i].transform.x += nx * speed * dt;
    eActor[i].transform.y += ny * speed * dt;
    clampIn(i);
}

function chase(i, nx, ny, speed, dt) {
    eActor[i].transform.x += (nx * speed + eVx[i]) * dt;
    eActor[i].transform.y += (ny * speed + eVy[i]) * dt;
    eActor[i].transform.rotation = Math.atan2(ny, nx);
    eVx[i] *= 0.88;
    eVy[i] *= 0.88;
    clampIn(i);
}

// ---------------------------------------------------------------------------
// RUSHER: wind up in place, then commit to a straight line. The wind-up is the
// whole fight -- it is readable, and sidestepping it is free.
// ---------------------------------------------------------------------------
function stepRusher(i, dt, nx, ny, dist, speed) {
    if (eState[i] === 0) {
        chase(i, nx, ny, speed * 0.55, dt);
        if (dist < KIND_RANGE[K_RUSHER] && eT[i] <= 0) {
            eState[i] = 1;
            eT[i] = 0.55;
            eVx[i] = nx * 1.0;
            eVy[i] = ny * 1.0;     // remembered direction, not a live target
        }
    } else if (eState[i] === 1) {
        if (eT[i] <= 0) { eState[i] = 2; eT[i] = 0.62; }
    } else {
        eActor[i].transform.x += eVx[i] * 700 * dt;
        eActor[i].transform.y += eVy[i] * 700 * dt;
        clampIn(i);
        if (eT[i] <= 0) { eState[i] = 0; eT[i] = 1.3; eVx[i] = 0; eVy[i] = 0; }
    }
}

// ---------------------------------------------------------------------------
// SPITTER and TURRET: hold a range band and fire on a cadence.
// ---------------------------------------------------------------------------
function stepRanged(i, dt, ax, ay, nx, ny, dist, speed, cadence, band, dmg, rad, r, g, b) {
    if (speed > 0) {
        var want = dist > band + 40 ? 1 : (dist < band - 60 ? -1 : 0);
        eActor[i].transform.x += nx * speed * want * dt;
        eActor[i].transform.y += ny * speed * want * dt;
        clampIn(i);
    }
    eActor[i].transform.rotation = Math.atan2(ny, nx);

    if (eT[i] <= 0 && dist < KIND_RANGE[eKind[i]] && bullets) {
        eT[i] = cadence;
        bullets.call("fire", ax + nx * 20, ay + ny * 20, Math.atan2(ny, nx),
                     330, dmg, 1, rad, 2.6, 0, 0);
    }
}

// ---------------------------------------------------------------------------
// SNIPER: a telegraph you can leave, then a beam down the line it drew. The
// line is an actor so the threat is visible, not a number in a log.
// ---------------------------------------------------------------------------
function stepSniper(i, dt, ax, ay, nx, ny, dist, speed) {
    eActor[i].transform.rotation = Math.atan2(ny, nx);

    if (eState[i] === 0) {
        if (dist > 420) {
            eActor[i].transform.x += nx * speed * dt;
            eActor[i].transform.y += ny * speed * dt;
            clampIn(i);
        }
        if (eT[i] <= 0 && dist < KIND_RANGE[K_SNIPER]) {
            eState[i] = 1;
            eT[i] = 1.05;
            eVx[i] = nx; eVy[i] = ny;
            showTell(i, ax, ay, nx, ny, 700);
        }
    } else {
        if (eT[i] <= 0) {
            eState[i] = 0;
            eT[i] = 2.4;
            hideTell(i);
            if (bullets) {
                bullets.call("fire", ax + eVx[i] * 22, ay + eVy[i] * 22,
                             Math.atan2(eVy[i], eVx[i]), 1500, 20, 1, 5, 0.9, 9, 0);
            }
        }
    }
}

function showTell(i, ax, ay, nx, ny, len) {
    if (eTell[i]) { hideTell(i); }
    var t = Scene.createActor("Tell", ax + nx * len / 2, ay + ny * len / 2);
    if (!t) { return; }
    t.tag = "Fx";
    t.transform.rotation = Math.atan2(ny, nx);
    Scene.addComponent(t, "SpriteRenderer", {
        Tint: { R: 120, G: 160, B: 255, A: 110 },
        Size: [len, 3],
        LayerDepth: depthTell
    });
    eTell[i] = t;
}

function hideTell(i) {
    if (eTell[i]) { eTell[i].destroy(); eTell[i] = null; }
}

// ---------------------------------------------------------------------------
// GHOST: untargetable while phased, so a bullet passes through. The collider
// goes with the visibility -- what you can see is what you can hit.
// ---------------------------------------------------------------------------
function stepGhost(i, dt, nx, ny, speed) {
    if (eT[i] <= 0) {
        eState[i] = eState[i] === 0 ? 1 : 0;
        eT[i] = eState[i] === 1 ? 1.6 : 2.6;
        var col = eActor[i].getComponent("BoxCollider2D");
        if (col) { col.isTrigger = eState[i] === 1; }   // a trigger is invisible to overlapCircle
    }
    chase(i, nx, ny, eState[i] === 1 ? speed * 1.45 : speed, dt);
}

// ---------------------------------------------------------------------------
// WARDEN: refreshes a damage shield on everything near it, itself included.
// Kill it first or kill it slowly -- that is the whole decision.
// ---------------------------------------------------------------------------
function stepWarden(i, dt, ax, ay) {
    if (eT[i] > 0) { return; }
    eT[i] = 0.5;
    for (var j = 0; j < n; j++) {
        if (!eActor[j]) { continue; }
        var dx = eActor[j].transform.x - ax, dy = eActor[j].transform.y - ay;
        if (dx * dx + dy * dy < 210 * 210) { eShield[j] = 0.85; }
    }
}

// ---------------------------------------------------------------------------
// FORGE: rooted, and builds grunts until it is dealt with.
// ---------------------------------------------------------------------------
function stepForge(i, dt, ax, ay) {
    if (eT[i] > 0) { return; }
    eT[i] = 3.1;
    if (n >= maxEnemies) { return; }
    var a = Math.random() * Math.PI * 2;
    spawnKind(K_GRUNT, ax + Math.cos(a) * 54, ay + Math.sin(a) * 54, eT2[i], 1);
}

// ---------------------------------------------------------------------------
// MAGNET: pulls, which is worse than chasing -- it takes your positioning away
// rather than your health.
// ---------------------------------------------------------------------------
function stepMagnet(i, dt, nx, ny, dist, speed, playerAlive) {
    if (dist > 260) {
        eActor[i].transform.x += nx * speed * dt;
        eActor[i].transform.y += ny * speed * dt;
        clampIn(i);
    }
    if (playerAlive && dist < KIND_RANGE[K_MAGNET] && pilot) {
        pilot.call("pullToward", eActor[i].transform.x, eActor[i].transform.y, 260 * dt);
    }
}

// ---------------------------------------------------------------------------
// BRUISER: slow, huge, and a slam that reaches further than its body.
// ---------------------------------------------------------------------------
function stepBruiser(i, dt, ax, ay, nx, ny, dist, speed) {
    chase(i, nx, ny, speed, dt);
    if (dist < 130 && eT[i] <= 0) {
        eT[i] = 2.7;
        if (director) { director.call("spawnEffect", ax, ay, 190, 200, 60, 40, 0.22); }
        if (pilot && dist < 190) { pilot.call("hurt", 22); }
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
    if (dist > eSize[i] * 0.5 + 16) { return; }
    if (eT2[i] > 0 && eKind[i] !== K_FORGE) { return; }

    if (eKind[i] === K_LEECH) {
        // A leech drains continuously rather than hitting, which is why it is
        // worth breaking away from rather than tanking.
        pilot.call("hurt", 9);
        eT2[i] = contactCooldown * 0.6;
        return;
    }

    pilot.call("hurt", eTouch[i]);
    eT2[i] = contactCooldown;
}

function clampIn(i) {
    if (!director) { return; }
    var half = director.call("getArenaHalf");
    var t = eActor[i].transform;
    if (t.x < -half) { t.x = -half; }
    if (t.x >  half) { t.x =  half; }
    if (t.y < -half) { t.y = -half; }
    if (t.y >  half) { t.y =  half; }
}

// Colour carries state: shielded is paler, phased is faint, burning is hot.
function paint(i) {
    var s = eSprite[i];
    if (!s) { return; }

    var r = KIND_R[eKind[i]], g = KIND_G[eKind[i]], b = KIND_B[eKind[i]];
    var a = 255;

    if (eKind[i] === K_GHOST && eState[i] === 1) { a = 70; }
    if (eShield[i] > 0) { r = (r + 255) >> 1; g = (g + 255) >> 1; b = (b + 255) >> 1; }
    if (eSlow[i] > 0)   { b = Math.min(255, b + 70); r = Math.floor(r * 0.7); }
    if (eBurn[i] > 0)   { r = Math.min(255, r + 60); g = Math.floor(g * 0.8); }
    if (eState[i] === 1 && eKind[i] === K_RUSHER) { r = 255; g = 255; b = 255; }

    s.tint = { R: r, G: g, B: b, A: a };
}

// ===========================================================================
// Spawning
// ===========================================================================

function spawnKind(kind, x, y, hpMul, spdMul) {
    var k = Math.max(0, Math.min(KIND_COUNT - 1, Number(kind) | 0));
    if (n >= maxEnemies) { return -1; }

    var a = Scene.createActor("Enemy", Number(x) || 0, Number(y) || 0);
    if (!a) { return -1; }
    a.tag = "Enemy";

    var size = KIND_SIZE[k];
    Scene.addComponent(a, "SpriteRenderer", {
        Tint: { R: KIND_R[k], G: KIND_G[k], B: KIND_B[k], A: 255 },
        Size: [size + 6, size],
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
    eTouch[n]  = KIND_TOUCH[k];
    eT[n]      = Math.random() * 1.2;
    eT2[n]     = hm;                  // forges reuse this as the hp multiplier they pass on
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

    if (director) {
        director.call("onEnemyKilled", kind, x, y, KIND_SCORE[kind], KIND_CHUNKY[kind]);
        director.call("spawnEffect", x, y, eSize[i] + 10, KIND_R[kind], KIND_G[kind], KIND_B[kind], 0.16);
    }
    if (pilot) {
        pilot.call("onKill");
        var shock = pilot.call("getShockwave");
        if (shock > 0) {
            queueBlast(x, y, 90 + 18 * shock, 18 * shock);
            if (director) { director.call("spawnEffect", x, y, 90 + 18 * shock, 255, 190, 90, 0.14); }
        }
    }

    eActor[i].destroy();
    removeAt(i);
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
        if (eKind[i] === K_GHOST && eState[i] === 1) { continue; }
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
            if (eKind[i] === K_GHOST && eState[i] === 1) { continue; }
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
        if (eKind[i] === K_GHOST && eState[i] === 1) { continue; }
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
