// Director.js -- the run. Waves, the draft, the shop, the score, and the dark.
// Attach to a manager actor and tag it "Director".
//
// The shape of a run:
//
//   HANGAR -> WAVE -> DRAFT -> WAVE -> ... -> WAVE(boss) -> SHOP -> WAVE ...
//
// There is no final wave. Bosses cycle, the scaling keeps climbing, and the
// only way a run ends is a wipe. Anything that looks like a victory condition
// has been removed on purpose -- reintroducing one is the single change that
// would most change what this game is.
//
// This script also owns everything that is nobody else's: the effect pool, the
// player's deployables, pickups, and the darkness the whole title is named
// after.

// ===========================================================================
// Tuning -- the arena
// ===========================================================================
var arenaHalf   = 950;       // a square, half-width in px
var tileStep    = 190;       // grid spacing of the floor marks

// ===========================================================================
// Tuning -- the waves
//
// budget is spend, not headcount: a bruiser eats twelve of it and a swarmling
// one, so a wave gets harder without simply getting longer.
// ===========================================================================
// The shape of a run -- how much a wave is worth, when to spend it, when it is
// over, when a boss is due, and the stall-breaker -- belongs to the
// `wave-director` cookie on the Waves actor. Everything below is CONTENT: what
// this game puts in a wave, and what its numbers mean.
var intermission  = 4.5;     // seconds after a wave clears, before the draft

// One drafted mod a wave is not enough. A wave's total health grows with the
// budget curve AND with per-wave scaling, which compounds twice; a pilot picking
// one card a wave grows once. Left alone, wave 14 took three minutes -- not
// harder, just longer, which is the failure mode of every wave spawner.
//
// So every intermission also GRANTS a mod, free, on top of the draft. It is what
// the original UltraDark does with its class-mod grant, and it is the cheapest
// place to put the pilot's half of the curve.
var grantPerIntermission = 1;
var shopSeconds   = 40;      // how long the core shop stays open

var hpPerWave     = 0.065;   // enemy hp multiplier growth
var spdPerWave    = 0.014;   // ...and speed, much slower

// ===========================================================================
// Tuning -- bosses
// ===========================================================================
var bossBaseHp    = 900;
// A boss should be a wall, not a wait. At +210 a wave-15 boss was 4050 health
// against a door cycle that only lets damage in half the time, which is a
// two-minute fight nobody chose. The pilot now takes two mods a wave; this is
// the boss curve that keeps up with that rather than outrunning it.
var bossHpPerWave = 115;

// ===========================================================================
// Tuning -- the dark
//
// The title arrives on wave 16. Fourteen and fifteen are the warning.
// ===========================================================================
var darkFirstWarn = 14;
var darkFullWave  = 16;
var darkPerWave   = 0.030;   // creeps in further every wave after that
var darkMax       = 1.0;

// The cookie's darkness curve is flat until duskAt and full at nightAt, so a
// darkness of d is exactly this point in its day. Driving it this way means the
// clock never has to run.
var COOKIE_DUSK  = 0.58;
var COOKIE_NIGHT = 0.70;

// ===========================================================================
// Tuning -- score, multiplier, cores
// ===========================================================================
var multMax       = 8;
var multPerKill   = 0.09;
var multDecay     = 0.32;    // per second, once the decay grace has run out
var multGrace     = 2.2;     // seconds after a kill before decay resumes
var multLossOnHit = 0.45;    // fraction of the multiplier lost when hit

var coreDropChance = 0.09;
var consumableChance = 0.34; // from chunky enemies only

// ===========================================================================
// Tuning -- draw order
// ===========================================================================
var depthFloor   = 0.05;
var depthGrid    = 0.10;
var depthWall    = 0.15;
var depthPickup  = 0.30;
var depthDeploy  = 0.44;
var depthEffect  = 0.85;     // ABOVE the dark: effects are the light source

// ===========================================================================
// The mods -- 24, in six families of four.
//
// A card has no text on it, because the contract has no font. Its family is
// the colour and its member is a row of pips, so "orange, three pips" is a
// name you can read across the arena. The log carries the words.
// ===========================================================================
var MOD_NAME = [
    "RAPID FEED", "HEAVY SLUG", "THRUSTERS", "PLATING",
    "SPLIT SHOT", "LONG BARREL", "PIERCER", "VAMPIRE",
    "ORBITAL BLADE", "KINETIC PLATING", "SCAVENGER", "MAGNETIC",
    "ADRENALINE", "OVERDRIVE CELL", "SHOCKWAVE", "COLD ROUNDS",
    "INCENDIARY", "REACTIVE ARMOUR", "REGENERATOR", "GLASS CANNON",
    "SWIFT RELOAD", "TWIN LINK", "DEAD MAN'S TRIGGER", "CORE TAP"
];

// family: 0 offence 1 rate 2 defence 3 mobility 4 elemental 5 economy
var MOD_FAMILY = [1, 0, 3, 2, 1, 1, 5, 2, 3, 3, 5, 3, 0, 5, 4, 4, 4, 2, 2, 0, 1, 0, 4, 5];
var MOD_PIP    = [1, 1, 1, 1, 2, 3, 4, 4, 4, 2, 1, 3, 3, 2, 3, 1, 2, 3, 2, 2, 4, 4, 4, 3];
var MOD_COST   = [26, 28, 22, 24, 34, 26, 34, 30, 40, 26, 24, 22, 28, 22, 32, 26, 28, 28, 26, 36, 26, 38, 34, 26];

var FAM_R = [226, 255, 110, 90,  190, 255];
var FAM_G = [70,  160, 210, 220, 110, 205];
var FAM_B = [70,  60,  120, 255, 245, 80 ];

var MOD_COUNT = 24;

// ===========================================================================
// Phases
// ===========================================================================
var P_HANGAR = 0, P_WAVE = 1, P_DRAFT = 2, P_SHOP = 3, P_DEAD = 4;

// ===========================================================================
// State
// ===========================================================================
var phase = P_HANGAR;
var wave = 0;
var score = 0;
var cores = 0;
var best = 0;

var mult = 1;
var multTimer = 0;

var waveTimer = 0;           // the draft / shop countdown, not the wave's
var bossUp = 0;
var bossActor = null;

var darkOverride = -1;

var draftIds = [-1, -1, -1];
var shopIds = [-1, -1, -1, -1];
var shopTimer = 0;

var swarm = null, bullets = null, pilot = null, player = null;
var clock = null, board = null, waves = null;

// effect pool
var fxActor = [], fxSprite = [], fxLife = [], fxMax = [], fxSize = [];
var fxN = 0;
var fxPool = [];

// pickups: 0 = core, 1 = consumable
var puActor = [], puKind = [], puValue = [], puLife = [];
var puN = 0;

// deployables: 0 = pylon, 1 = turret
var dpActor = [], dpKind = [], dpLife = [], dpTimer = [], dpDmg = [];
var dpN = 0;

var hangarShips = [];

// ===========================================================================
// Lifecycle
// ===========================================================================

function onStart() {
    resolve();
    buildArena();
    buildHangar();
    log("=====================================");
    log("           U L T R A D A R K");
    log("=====================================");
    log("  WASD / arrows  move");
    log("  hold LMB       fire      TAB  aim mode");
    log("  SHIFT          dash");
    log("  SPACE          ability   F    consumable");
    log("  1-8            pick a pilot in the hangar");
    log("  ENTER          launch");
    log("=====================================");
}

function resolve() {
    if (!swarm)   { var s = Scene.findFirstByTag("Swarm");   if (s) { swarm = s.getComponent("ScriptComponent"); } }
    if (!bullets) { var b = Scene.findFirstByTag("Bullets"); if (b) { bullets = b.getComponent("ScriptComponent"); } }
    if (!clock)   { var c = Scene.findFirstByTag("Clock");   if (c) { clock = c.getComponent("ScriptComponent"); } }
    if (!board)   { var d = Scene.findFirstByTag("Draft");   if (d) { board = d.getComponent("ScriptComponent"); } }
    if (!waves)   { var wv = Scene.findFirstByTag("Waves");  if (wv) { waves = wv.getComponent("ScriptComponent"); } }
    if (!player)  { player = Scene.findFirstByTag("Player"); if (player) { pilot = player.getComponent("ScriptComponent"); } }
}

function onUpdate(dt) {
    resolve();

    tickEffects(dt);
    tickPickups(dt);
    tickDeployables(dt);
    driveDark();

    if      (phase === P_HANGAR) { hangar(dt); }
    else if (phase === P_WAVE)   { runWave(dt); }
    else if (phase === P_DRAFT)  { runDraft(dt); }
    else if (phase === P_SHOP)   { runShop(dt); }
    else                         { runDead(dt); }
}

// ===========================================================================
// The arena -- built in script, because a scene file full of tiles is a scene
// file nobody can read a diff of.
// ===========================================================================

function buildArena() {
    var floor = Scene.createActor("Floor", 0, 0);
    if (floor) {
        floor.tag = "Fx";
        Scene.addComponent(floor, "SpriteRenderer", {
            Tint: { R: 15, G: 16, B: 22, A: 255 },
            Size: [arenaHalf * 2, arenaHalf * 2],
            LayerDepth: depthFloor
        });
    }

    // A sparse grid, so movement reads as movement even in an empty arena.
    for (var x = -arenaHalf + tileStep; x < arenaHalf; x += tileStep) {
        line(x, 0, 2, arenaHalf * 2, 30, 32, 44);
    }
    for (var y = -arenaHalf + tileStep; y < arenaHalf; y += tileStep) {
        line(0, y, arenaHalf * 2, 2, 30, 32, 44);
    }

    // The boundary. Movement is clamped in script, so these are a picture of
    // the edge rather than a collider anything relies on.
    line(0, -arenaHalf, arenaHalf * 2, 14, 90, 96, 130);
    line(0,  arenaHalf, arenaHalf * 2, 14, 90, 96, 130);
    line(-arenaHalf, 0, 14, arenaHalf * 2, 90, 96, 130);
    line( arenaHalf, 0, 14, arenaHalf * 2, 90, 96, 130);
}

// The grid and the boundary are two layers, so they are two names. One name at
// two depths is how a draw-order check stops being able to tell you anything.
function line(x, y, w, h, r, g, b) {
    var boundary = (w > 8 && h > 8);
    var a = Scene.createActor(boundary ? "Wall" : "Grid", x, y);
    if (!a) { return; }
    a.tag = "Fx";
    Scene.addComponent(a, "SpriteRenderer", {
        Tint: { R: r, G: g, B: b, A: 255 },
        Size: [w, h],
        LayerDepth: boundary ? depthWall : depthGrid
    });
}

// ===========================================================================
// The hangar -- eight ships in a row, and you fly the one you walk to.
// ===========================================================================

function buildHangar() {
    for (var i = 0; i < 8; i++) {
        var a = Scene.createActor("Hangar" + i, (i - 3.5) * 96, -280);
        if (!a) { continue; }
        a.tag = "Fx";
        Scene.addComponent(a, "SpriteRenderer", {
            Tint: { R: 90, G: 90, B: 110, A: 255 },
            Size: [42, 30],
            LayerDepth: depthPickup
        });
        hangarShips.push(a);
    }
}

function hangar(dt) {
    var chosen = pilot ? pilot.call("getPilot") : 0;

    for (var i = 0; i < 8; i++) {
        if (Input.isKeyPressed("D" + (i + 1))) {
            if (pilot) { pilot.call("setPilot", i); log("PILOT: " + pilot.call("getPilotName")); }
        }
    }

    // Walking to a ship selects it, so a pad or a phone can choose too.
    if (player) {
        for (var j = 0; j < hangarShips.length; j++) {
            var dx = player.transform.x - hangarShips[j].transform.x;
            var dy = player.transform.y - hangarShips[j].transform.y;
            if (dx * dx + dy * dy < 46 * 46 && j !== chosen) {
                if (pilot) { pilot.call("setPilot", j); log("PILOT: " + pilot.call("getPilotName")); }
            }
        }
    }

    for (var k = 0; k < hangarShips.length; k++) {
        var s = hangarShips[k].getComponent("SpriteRenderer");
        if (s) {
            var on = (k === chosen);
            s.tint = { R: on ? 240 : 80, G: on ? 240 : 80, B: on ? 255 : 100, A: 255 };
        }
    }

    if (Input.isKeyPressed("Enter") || Input.isKeyPressed("Space")) { launch(); }
}

function launch() {
    for (var i = 0; i < hangarShips.length; i++) { hangarShips[i].destroy(); }
    hangarShips = [];
    wave = 0;
    score = 0;
    cores = 0;
    mult = 1;
    if (pilot) { log("LAUNCH -- " + pilot.call("getPilotName")); }
    if (waves) { waves.call("begin"); }
}

// ===========================================================================
// Waves -- this half only
//
// The Waves actor (the `wave-director` cookie) decides how big a wave is, when
// to spend it, when it is over and when a boss is due. The functions below are
// the other side of that contract: what THIS game puts in a wave. Everything
// down to runWave is called BY the cookie, never by this script.
// ===========================================================================

// Spawn one thing for at most `budget`, and say what it cost. Returning 0 tells
// the director there is nothing left worth buying, so it stops holding change.
function spawnOne(w, budget) {
    var kind = pickKind(w, budget);
    if (kind < 0) { return 0; }

    // A rooted enemy dropped on the far wall of the arena is not a threat, it is
    // an errand: it never comes to you, so the wave cannot end until you have
    // walked across the map to it. FORGE and TURRET are area denial, so they go
    // into the area -- near enough to matter, far enough to be a decision.
    var pos = isRooted(kind) ? nearSpawn() : edgeSpawn();

    if (swarm) {
        swarm.call("spawnKind", kind, pos.x, pos.y,
                   1 + w * hpPerWave, 1 + w * spdPerWave);
    }
    return kindCost(kind);
}

function aliveCount() { return swarm ? swarm.call("alive") : 0; }
function bossAlive()  { return bossUp; }

function startBoss(w, index) {
    spawnBoss(index);
    return 1;
}

function onWaveStart(w, isBoss) {
    wave = Number(w) || 1;
    phase = P_WAVE;
    if (swarm) { swarm.call("setHunt", 0); }
    log(isBoss ? ("=== WAVE " + wave + " -- BOSS ===") : ("=== WAVE " + wave + " ==="));
    return 1;
}

function onWaveCleared(w, isBoss) {
    if (isBoss) { openShop(); } else { openDraft(); }
    return 1;
}

// Nothing left to send and something still out there: the survivors come and
// find you, rather than being hunted across the arena.
function onWaveStalled(w) {
    if (swarm) { swarm.call("setHunt", 1); }
    return 1;
}

// What is left of the old wave loop: the multiplier, which is a thing you hold
// on to rather than a thing you have.
function runWave(dt) {
    if (multTimer > 0) { multTimer -= dt; }
    else if (mult > 1) { mult = Math.max(1, mult - multDecay * dt); }

    if (bossActor && bossActor.active !== true) { bossActor = null; }
}

function isRooted(kind) { return (kind === 6 || kind === 11) ? 1 : 0; }

// A ring around the pilot: close enough to be part of the fight, never on top
// of them.
function nearSpawn() {
    var px = player ? player.transform.x : 0;
    var py = player ? player.transform.y : 0;
    var a = Math.random() * Math.PI * 2;
    var r = 360 + Math.random() * 200;
    var m = arenaHalf - 60;
    return {
        x: Math.max(-m, Math.min(m, px + Math.cos(a) * r)),
        y: Math.max(-m, Math.min(m, py + Math.sin(a) * r))
    };
}

function bossHp(w) {
    return Math.floor(bossBaseHp + bossHpPerWave * w);
}

// Unlock schedule. Every kind arrives on its own wave, so a player can learn
// one thing at a time and name what changed.
function kindUnlocked(kind, w) {
    if (kind === 0)  { return 1; }              // GRUNT
    if (kind === 10) { return 1; }              // SWARMLING
    if (kind === 1)  { return w >= 2 ? 1 : 0; } // RUSHER
    if (kind === 2)  { return w >= 3 ? 1 : 0; } // SPITTER
    if (kind === 8)  { return w >= 4 ? 1 : 0; } // LEECH
    if (kind === 4)  { return w >= 6 ? 1 : 0; } // GHOST
    if (kind === 11) { return w >= 7 ? 1 : 0; } // TURRET
    if (kind === 7)  { return w >= 8 ? 1 : 0; } // MAGNET
    if (kind === 3)  { return w >= 9 ? 1 : 0; } // SNIPER
    if (kind === 5)  { return w >= 11 ? 1 : 0; }// WARDEN
    if (kind === 9)  { return w >= 12 ? 1 : 0; }// BRUISER
    if (kind === 6)  { return w >= 13 ? 1 : 0; }// FORGE
    return 0;
}

function kindCost(kind) {
    var COST = [3, 4, 5, 7, 6, 12, 16, 6, 3, 14, 1, 8];
    return COST[kind];
}

function pickKind(w, budget) {
    var choices = [];
    for (var k = 0; k < 12; k++) {
        if (!kindUnlocked(k, w)) { continue; }
        if (kindCost(k) > budget) { continue; }
        choices.push(k);
        // Weight the cheap infantry up so a wave is mostly things to shoot.
        if (k === 0 || k === 10) { choices.push(k); choices.push(k); }
    }
    if (choices.length === 0) { return -1; }
    return choices[Math.floor(Math.random() * choices.length)];
}

// Spawn on the edge, on the far side from the player: an enemy that appears on
// top of you is not difficulty, it is a bug you cannot see.
function edgeSpawn() {
    var px = player ? player.transform.x : 0;
    var py = player ? player.transform.y : 0;

    for (var tries = 0; tries < 6; tries++) {
        var edge = Math.floor(Math.random() * 4);
        var m = arenaHalf - 40;
        var x = 0, y = 0;
        if (edge === 0)      { x = rand(-m, m); y = -m; }
        else if (edge === 1) { x = rand(-m, m); y =  m; }
        else if (edge === 2) { x = -m;          y = rand(-m, m); }
        else                 { x =  m;          y = rand(-m, m); }

        var dx = x - px, dy = y - py;
        if (dx * dx + dy * dy > 420 * 420) { return { x: x, y: y }; }
    }
    return { x: -px, y: -py };
}

function rand(lo, hi) { return lo + Math.random() * (hi - lo); }

// ===========================================================================
// Bosses
// ===========================================================================

function spawnBoss(index) {
    var kind = Math.max(0, Math.min(4, Number(index) | 0));
    var a = Scene.createActor("Boss", 0, -arenaHalf * 0.55);
    if (!a) { return; }
    a.tag = "Boss";

    Scene.addComponent(a, "SpriteRenderer", {
        Tint: { R: 255, G: 255, B: 255, A: 255 },
        Size: [100, 80],
        LayerDepth: 0.45
    });
    Scene.addComponent(a, "BoxCollider2D", { Size: [100, 80] });
    Scene.addComponent(a, "ScriptComponent", { ScriptPath: "Scripts/Boss.js" });

    var sc = a.getComponent("ScriptComponent");
    if (sc) { sc.call("configure", kind, bossHp(wave), wave); }

    bossActor = a;
    bossUp = 1;
}

function onBossKilled(kind, x, y) {
    bossUp = 0;
    bossActor = null;

    var reward = 40 + wave * 4;
    addCores(reward);
    score += 2000 * wave;
    log("BOSS DOWN -- +" + reward + " cores");

    for (var i = 0; i < 5; i++) {
        dropPickup(1, x + rand(-90, 90), y + rand(-90, 90), Math.floor(Math.random() * 5));
    }
    return 1;
}

// ===========================================================================
// Kills, score, cores
// ===========================================================================

function onEnemyKilled(kind, x, y, points, chunky) {
    score += Math.floor((Number(points) || 0) * mult);

    mult = Math.min(multMax, mult + multPerKill);
    multTimer = multGrace;

    var bonus = pilot ? pilot.call("getCoreBonus") : 1;

    if (Math.random() < coreDropChance * bonus) {
        dropPickup(0, x, y, 1 + Math.floor(wave / 6));
    }
    if (chunky && Math.random() < consumableChance) {
        dropPickup(1, x, y, Math.floor(Math.random() * 5));
    }
    return 1;
}

function onPlayerHit() {
    mult = Math.max(1, mult * (1 - multLossOnHit));
    multTimer = 0;
    return 1;
}

function addCores(amount) {
    cores += Math.max(0, Math.floor(Number(amount) || 0));
    return cores;
}

// ===========================================================================
// Pickups
// ===========================================================================

function dropPickup(kind, x, y, value) {
    var a = Scene.createActor("Pickup", x, y);
    if (!a) { return; }
    a.tag = "Fx";
    var isCore = (kind === 0);
    Scene.addComponent(a, "SpriteRenderer", {
        Tint: isCore ? { R: 255, G: 210, B: 90, A: 255 } : { R: 120, G: 240, B: 200, A: 255 },
        Size: isCore ? [13, 13] : [17, 17],
        LayerDepth: depthPickup
    });
    puActor[puN] = a;
    puKind[puN] = kind;
    puValue[puN] = value;
    puLife[puN] = 22;
    puN++;
}

function tickPickups(dt) {
    if (!player) { return; }
    var px = player.transform.x, py = player.transform.y;
    var magnet = pilot ? pilot.call("getMagnet") : 0;
    var reach = 34 + magnet * 130;

    for (var i = puN - 1; i >= 0; i--) {
        var a = puActor[i];
        if (!a || a.active !== true) { removePickup(i); continue; }

        puLife[i] -= dt;
        if (puLife[i] <= 0) { a.destroy(); removePickup(i); continue; }

        var dx = px - a.transform.x, dy = py - a.transform.y;
        var d2 = dx * dx + dy * dy;

        if (magnet > 0 && d2 < reach * reach && d2 > 1) {
            var d = Math.sqrt(d2);
            a.transform.x += (dx / d) * 420 * dt;
            a.transform.y += (dy / d) * 420 * dt;
        }

        if (d2 < 34 * 34) {
            if (puKind[i] === 0) { addCores(puValue[i]); }
            else if (pilot) { pilot.call("giveConsumable", puValue[i]); }
            spawnEffect(a.transform.x, a.transform.y, 26, 255, 240, 180, 0.12);
            a.destroy();
            removePickup(i);
        }
    }
}

function removePickup(i) {
    var last = puN - 1;
    if (i !== last) {
        puActor[i] = puActor[last]; puKind[i] = puKind[last];
        puValue[i] = puValue[last]; puLife[i] = puLife[last];
    }
    puN--;
    puActor[puN] = null;
}

// ===========================================================================
// Deployables -- SPARKS' pylon and RIGG's turret
// ===========================================================================

function spawnPylon(x, y, life, dmg) { return deploy(0, x, y, life, dmg); }
function spawnTurret(x, y, life, dmg) { return deploy(1, x, y, life, dmg); }

function deploy(kind, x, y, life, dmg) {
    var a = Scene.createActor(kind === 0 ? "Pylon" : "Turret", x, y);
    if (!a) { return 0; }
    a.tag = "Fx";
    Scene.addComponent(a, "SpriteRenderer", {
        Tint: kind === 0 ? { R: 190, G: 130, B: 255, A: 255 } : { R: 150, G: 230, B: 90, A: 255 },
        Size: [20, 20],
        LayerDepth: depthDeploy
    });
    dpActor[dpN] = a;
    dpKind[dpN] = kind;
    dpLife[dpN] = Number(life) || 10;
    dpTimer[dpN] = 0;
    dpDmg[dpN] = Number(dmg) || 8;
    dpN++;
    return 1;
}

function tickDeployables(dt) {
    for (var i = dpN - 1; i >= 0; i--) {
        var a = dpActor[i];
        if (!a || a.active !== true) { removeDeploy(i); continue; }

        dpLife[i] -= dt;
        if (dpLife[i] <= 0) { a.destroy(); removeDeploy(i); continue; }

        dpTimer[i] -= dt;
        if (dpTimer[i] > 0) { continue; }

        if (dpKind[i] === 0) {
            dpTimer[i] = 0.55;
            if (swarm) { swarm.call("chainFrom", a.transform.x, a.transform.y, dpDmg[i], 3, 260); }
        } else {
            dpTimer[i] = 0.32;
            if (swarm && bullets) {
                var tx = swarm.call("nearestX", a.transform.x, a.transform.y, 520);
                if (tx > -900000) {
                    var ty = swarm.call("nearestY", a.transform.x, a.transform.y, 520);
                    var ang = Math.atan2(ty - a.transform.y, tx - a.transform.x);
                    a.transform.rotation = ang;
                    bullets.call("fire", a.transform.x, a.transform.y, ang, 780, dpDmg[i], 0, 4, 0.8, 0, 0);
                }
            }
        }
    }
}

function removeDeploy(i) {
    var last = dpN - 1;
    if (i !== last) {
        dpActor[i] = dpActor[last]; dpKind[i] = dpKind[last];
        dpLife[i] = dpLife[last]; dpTimer[i] = dpTimer[last]; dpDmg[i] = dpDmg[last];
    }
    dpN--;
    dpActor[dpN] = null;
}

// ===========================================================================
// Effects -- pooled, and drawn ABOVE the dark on purpose.
//
// In a late wave the muzzle flash, the explosions and the boss's rings are the
// only light in the arena. Putting them under the overlay would dim the only
// thing lighting the fight.
// ===========================================================================

function spawnEffect(x, y, size, r, g, b, life) {
    var a;
    if (fxPool.length > 0) { a = fxPool.pop(); }
    else {
        a = Scene.createActor("Fx", 0, 0);
        if (!a) { return 0; }
        a.tag = "Fx";
        Scene.addComponent(a, "SpriteRenderer", {
            Tint: { R: 255, G: 255, B: 255, A: 255 },
            Size: [10, 10],
            LayerDepth: depthEffect
        });
    }

    a.active = true;
    a.transform.x = Number(x) || 0;
    a.transform.y = Number(y) || 0;

    fxActor[fxN] = a;
    fxSprite[fxN] = a.getComponent("SpriteRenderer");
    fxLife[fxN] = Number(life) || 0.15;
    fxMax[fxN] = fxLife[fxN];
    fxSize[fxN] = Number(size) || 20;

    if (fxSprite[fxN]) {
        fxSprite[fxN].size = { x: fxSize[fxN], y: fxSize[fxN] };
        fxSprite[fxN].tint = { R: Number(r) | 0, G: Number(g) | 0, B: Number(b) | 0, A: 255 };
    }

    fxN++;
    return 1;
}

function tickEffects(dt) {
    for (var i = fxN - 1; i >= 0; i--) {
        fxLife[i] -= dt;
        if (fxLife[i] <= 0) {
            fxActor[i].active = false;
            fxPool.push(fxActor[i]);
            var last = fxN - 1;
            if (i !== last) {
                fxActor[i] = fxActor[last]; fxSprite[i] = fxSprite[last];
                fxLife[i] = fxLife[last]; fxMax[i] = fxMax[last]; fxSize[i] = fxSize[last];
            }
            fxN--;
            fxActor[fxN] = null;
            continue;
        }

        var f = fxLife[i] / fxMax[i];
        var s = fxSprite[i];
        if (s) {
            var grow = fxSize[i] * (1.15 - f * 0.15);
            s.size = { x: grow, y: grow };
            var t = s.tint;
            s.tint = { R: t.r, G: t.g, B: t.b, A: Math.floor(240 * f) };
        }
    }
}

// ===========================================================================
// The dark
// ===========================================================================

function darknessForWave(w) {
    if (w < darkFirstWarn) { return 0; }
    if (w < darkFullWave) {
        return 0.34 * (w - darkFirstWarn + 1) / (darkFullWave - darkFirstWarn + 1);
    }
    return Math.min(darkMax, 0.62 + (w - darkFullWave) * darkPerWave);
}

function driveDark() {
    if (!clock) { return; }
    var d = darkOverride >= 0 ? darkOverride : darknessForWave(wave);
    if (phase === P_HANGAR) { d = 0; }
    clock.call("setTimeOfDay01", COOKIE_DUSK + d * (COOKIE_NIGHT - COOKIE_DUSK));
}

// -1 releases the override. THE ULTRADARK uses this, and gives it back when it
// dies -- including if it is destroyed some other way.
function setDarkOverride(value) {
    darkOverride = Number(value);
    return 1;
}

// ===========================================================================
// The draft
// ===========================================================================

function openDraft() {
    phase = P_DRAFT;
    waveTimer = intermission;

    draftIds[0] = randomMod(-1, -1);
    draftIds[1] = randomMod(draftIds[0], -1);
    draftIds[2] = randomMod(draftIds[0], draftIds[1]);

    if (board) {
        board.call("open", 3);
        for (var i = 0; i < 3; i++) {
            var id = draftIds[i];
            var f = MOD_FAMILY[id];
            board.call("setCard", i, FAM_R[f], FAM_G[f], FAM_B[f], MOD_PIP[id]);
        }
    }

    for (var k = 0; k < grantPerIntermission; k++) { grantMod(); }

    log("--- DRAFT --- 1/2/3, or walk into one");
    for (var j = 0; j < 3; j++) { log("   " + (j + 1) + ": " + MOD_NAME[draftIds[j]]); }
}

// Free, unchosen, and announced. The draft is the decision; this is the pilot
// keeping pace with the curve.
function grantMod() {
    var id = randomMod(-1, -1);
    if (pilot) { pilot.call("addMod", id); }
    log("SALVAGE: " + MOD_NAME[id]);
    return id;
}

function randomMod(notA, notB) {
    for (var tries = 0; tries < 40; tries++) {
        var id = Math.floor(Math.random() * MOD_COUNT);
        if (id !== notA && id !== notB) { return id; }
    }
    return 0;
}

function runDraft(dt) {
    var picked = board ? board.call("getPicked") : -1;

    if (picked >= 0 && picked < 3) {
        var id = draftIds[picked];
        if (pilot) { pilot.call("addMod", id); }
        log("TAKEN: " + MOD_NAME[id]);
        if (board) { board.call("close"); }
        nextWave();
        return;
    }

    // The intermission is a floor, not a limit: it runs out only if nothing is
    // chosen, and then the first card is taken so a run can never stall.
    waveTimer -= dt;
    if (waveTimer <= 0) {
        if (pilot) { pilot.call("addMod", draftIds[0]); }
        log("TAKEN (default): " + MOD_NAME[draftIds[0]]);
        if (board) { board.call("close"); }
        nextWave();
    }
}

// ===========================================================================
// The shop -- after a boss, spend the cores
// ===========================================================================

function openShop() {
    phase = P_SHOP;
    shopTimer = shopSeconds;
    rollShop();
    log("--- CORE SHOP --- " + cores + " cores. 1-4 to buy, ENTER to leave.");
}

function rollShop() {
    for (var i = 0; i < 4; i++) {
        shopIds[i] = randomMod(shopIds[(i + 1) % 4], shopIds[(i + 2) % 4]);
    }
    if (board) {
        board.call("open", 4);
        for (var j = 0; j < 4; j++) {
            var id = shopIds[j];
            var f = MOD_FAMILY[id];
            var affordable = cores >= MOD_COST[id];
            board.call("setCard", j,
                       affordable ? FAM_R[f] : Math.floor(FAM_R[f] * 0.35),
                       affordable ? FAM_G[f] : Math.floor(FAM_G[f] * 0.35),
                       affordable ? FAM_B[f] : Math.floor(FAM_B[f] * 0.35),
                       MOD_PIP[id]);
        }
    }
    for (var k = 0; k < 4; k++) {
        log("   " + (k + 1) + ": " + MOD_NAME[shopIds[k]] + "  " + MOD_COST[shopIds[k]] + " cores");
    }
}

function runShop(dt) {
    shopTimer -= dt;

    var picked = board ? board.call("getPicked") : -1;
    if (picked >= 0 && picked < 4) {
        buy(picked);
        return;
    }

    if (Input.isKeyPressed("Enter") || shopTimer <= 0) {
        if (board) { board.call("close"); }
        nextWave();
    }
}

function buy(slot) {
    var id = shopIds[slot];
    if (cores < MOD_COST[id]) {
        log("Not enough cores for " + MOD_NAME[id] + " (" + MOD_COST[id] + ")");
        rollShop();
        return;
    }
    cores -= MOD_COST[id];
    if (pilot) { pilot.call("addMod", id); }
    log("BOUGHT: " + MOD_NAME[id] + "  --  " + cores + " cores left");
    rollShop();
}

// ===========================================================================
// Death and restart
// ===========================================================================

function notePlayerDead() {
    if (phase === P_DEAD) { return 1; }
    phase = P_DEAD;
    if (waves) { waves.call("stop"); }
    if (score > best) { best = score; }

    log("=====================================");
    log("            R U N   O V E R");
    log("  wave  " + wave);
    log("  score " + score);
    log("  best  " + best);
    log("  R to fly again");
    log("=====================================");
    return 1;
}

function runDead(dt) {
    if (!Input.isKeyPressed("R")) { return; }

    if (swarm)   { swarm.call("clearAll"); }
    if (bullets) { bullets.call("clearAll"); }
    if (board)   { board.call("close"); }
    if (waves)   { waves.call("stop"); }
    if (bossActor) { bossActor.destroy(); bossActor = null; }

    for (var i = puN - 1; i >= 0; i--) { if (puActor[i]) { puActor[i].destroy(); } removePickup(i); }
    for (var j = dpN - 1; j >= 0; j--) { if (dpActor[j]) { dpActor[j].destroy(); } removeDeploy(j); }

    darkOverride = -1;
    bossUp = 0;
    mult = 1;
    if (pilot) { pilot.call("resetRun"); }

    phase = P_HANGAR;
    wave = 0;
    score = 0;
    cores = 0;
    buildHangar();
    log("--- HANGAR --- 1-8 to pick a pilot, ENTER to launch");
}

// ===========================================================================
// Readouts
// ===========================================================================

function getArenaHalf()  { return arenaHalf; }
function getPhase()      { return phase; }
function getWave()       { return wave; }
function getScore()      { return score; }
function getCores()      { return cores; }
function getBest()       { return best; }
function getMult()       { return mult; }
function getMult01()     { return (mult - 1) / (multMax - 1); }
function getCores01()    { return Math.min(1, cores / 120); }
function getDarkness()   { return darkOverride >= 0 ? darkOverride : darknessForWave(wave); }
function getBudgetLeft() { return waves ? waves.call("getBudgetLeft") : 0; }
function getConcurrentCap() { return waves ? waves.call("getConcurrentCap") : 0; }

// The next wave starts when the player has finished being asked a question.
function nextWave() {
    phase = P_WAVE;
    if (waves) { waves.call("resume"); }
    return wave;
}
function isBossAlive()   { return bossUp; }

// The pilot is parked in the hangar, the draft and the shop -- everywhere the
// player is being asked a question rather than fighting.
function pilotFrozen()   { return (phase === P_DRAFT || phase === P_SHOP) ? 1 : 0; }

// Test seams: a harness drives a run without touching the keyboard.
function forceWave(w)    {
    phase = P_WAVE;
    if (waves) { waves.call("forceWave", w); }
    return wave;
}
function forceLaunch()   { launch(); return 1; }
// Stops the current wave sending anything else, so a test can work with the
// enemies it placed rather than with whatever the wave felt like adding.
function forceBudget(n)  { return waves ? waves.call("forceBudget", n) : 0; }
function forcePick(slot) {
    if (phase !== P_DRAFT) { return 0; }
    var id = draftIds[Math.max(0, Math.min(2, Number(slot) | 0))];
    if (pilot) { pilot.call("addMod", id); }
    if (board) { board.call("close"); }
    nextWave();
    return id;
}
