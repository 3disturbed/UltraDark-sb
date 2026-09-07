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
// Tuning -- the arena, and the numbers UltraDark actually runs on
//
// These are the live game's, from its shared/constants.js. The one that changes
// everything is the damage scale: a pilot has THREE hit points, a bullet does
// one damage, and a Brute takes six. Nothing is on a hundred-point scale, so a
// single contact is a third of your health and the second of invulnerability
// afterwards is most of what keeps a wave survivable.
// ===========================================================================
var arenaW = 2048, arenaH = 1152;        // ARENA_W / ARENA_H
var arenaHalfW = arenaW / 2, arenaHalfH = arenaH / 2;
var wallPad = 24;                        // WALL_PAD
var tileStep = 192;

// MULT
var multMax       = 10;                  // MULT.MAX
var multPerKill   = 0.12;                // MULT.PER_KILL
var multDecay     = 0.35;                // MULT.DECAY_PER_S, after the grace
var multGrace     = 3;                   // MULT.DECAY_GRACE
var multHitFactor = 0.5;                 // MULT.HIT_FACTOR -- halved on any hit

// WAVE
var intermission  = 20;                  // WAVE.INTERMISSION_S

// The original grants a CLASS mod every intermission on top of the draft, so
// the pilot grows as fast as the wave does. This is that grant.
var grantPerIntermission = 1;
var spawnMinDist  = 320;                 // WAVE.SPAWN_MIN_DIST
var warpInSeconds = 0.5;                 // WAVE.WARP_IN_S -- the spawn telegraph
var shopSeconds   = 40;

// Enemy scaling. The original does not make enemies spongier as waves climb --
// it sends MORE of them, through the budget. Health scales only gently.
var hpPerWave     = 0.04;
var spdPerWave    = 0.012;

// ===========================================================================
// Tuning -- bosses
//
// Boss HP is the original's: the kind's own hp, plus ~6 a wave past 25 so a
// cycled boss never goes soft. Solo is x1.0 of that, which is what this port is.
// ===========================================================================
var bossHpPerWavePast25 = 6;

// ===========================================================================
// Tuning -- the dark
//
// The title arrives on wave 16. Fourteen and fifteen are the warning.
// ===========================================================================
var darkFirstWarn = 14;
var darkFullWave  = 16;                  // WAVE.DARK_START
var darkPerWave   = 0.030;   // creeps in further every wave after that
var darkMax       = 1.0;

// The cookie's darkness curve is flat until duskAt and full at nightAt, so a
// darkness of d is exactly this point in its day. Driving it this way means the
// clock never has to run.
var COOKIE_DUSK  = 0.58;
var COOKIE_NIGHT = 0.70;

// ===========================================================================
// Tuning -- drops
//
// Only the chunkier kinds drop consumables, and each carries its own chance
// from the roster (Warden 0.30, Forge 0.35, Brute 0.20 ...). Cores come off
// every kill at the kind's own core value.
// ===========================================================================

// ===========================================================================
// Tuning -- draw order
// ===========================================================================
// The world is lit, so that the dark has something to take away.
//
// It used to be near-black -- floor (15,16,22) against a night colour of
// (6,8,20) -- which read as atmospheric only because the renderer was
// compositing straight alpha as premultiplied and every translucent sprite was
// ADDING its colour. With that fixed the arithmetic is exact, and an overlay
// almost the same colour as the floor is an overlay that does nothing.
var depthFloor   = 0.05;
var depthGrid    = 0.10;
var depthWall    = 0.15;
var depthPickup  = 0.30;
var depthDeploy  = 0.44;
var depthEffect  = 0.85;     // ABOVE the dark: effects are the light source

// ===========================================================================
// The mods live in the Upgrades script now -- UltraDark's own pool, four
// families with three rarities and the cursed rarity-3 trade-offs. This script
// only asks it for a name, a description and a colour to put on a card.
// ===========================================================================
var MOD_COUNT = 37;

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
var clock = null, board = null, waves = null, hud = null, fx = null, pickups = null;
var upgrades = null;

// effect pool
var fxActor = [], fxSprite = [], fxLife = [], fxMax = [], fxSize = [];
var fxN = 0;
var fxPool = [];

// deployables: 0 = pylon, 1 = turret
var dpActor = [], dpKind = [], dpLife = [], dpTimer = [], dpDmg = [], dpRad = [];
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
    log("  ESC            pause");
    log("=====================================");
    if (hud) { hud.call("setPaused", true, "HANGAR", "1-8 pick a pilot   ENTER to launch"); }
}

function resolve() {
    if (!swarm)   { var s = Scene.findFirstByTag("Swarm");   if (s) { swarm = s.getComponent("ScriptComponent"); } }
    if (!bullets) { var b = Scene.findFirstByTag("Bullets"); if (b) { bullets = b.getComponent("ScriptComponent"); } }
    if (!clock)   { var c = Scene.findFirstByTag("Clock");   if (c) { clock = c.getComponent("ScriptComponent"); } }
    if (!board)   { var d = Scene.findFirstByTag("Draft");   if (d) { board = d.getComponent("ScriptComponent"); } }
    if (!waves)   { var wv = Scene.findFirstByTag("Waves");  if (wv) { waves = wv.getComponent("ScriptComponent"); } }
    if (!hud)     { var h = Scene.findFirstByTag("Hud");     if (h) { hud = h.getComponent("ScriptComponent"); } }
    if (!fx)      { var e = Scene.findFirstByTag("Effects"); if (e) { fx = e.getComponent("ScriptComponent"); } }
    if (!pickups) { var pk = Scene.findFirstByTag("Pickups"); if (pk) { pickups = pk.getComponent("ScriptComponent"); } }
    if (!upgrades) { var up = Scene.findFirstByTag("Upgrades"); if (up) { upgrades = up.getComponent("ScriptComponent"); } }
    if (!player)  { player = Scene.findFirstByTag("Player"); if (player) { pilot = player.getComponent("ScriptComponent"); } }
}

function onUpdate(dt) {
    resolve();

    if (handlePause()) { return; }

    tickEffects(dt);
    tickDeployables(dt);
    driveDark();

    if      (phase === P_HANGAR) { hangar(dt); }
    else if (phase === P_WAVE)   { runWave(dt); }
    else if (phase === P_DRAFT)  { runDraft(dt); }
    else if (phase === P_SHOP)   { runShop(dt); }
    else                         { runDead(dt); }
}

// ===========================================================================
// Pause
//
// The HUD draws the overlay; deciding what pauses is this script's job, which
// is what its AGENT.md says. Escape both ways, and never from the hangar or the
// game-over screen -- both already own the overlay, and pausing one of those
// leaves no way back.
//
// This owns Time.timeScale while paused. So does screen-effects' hit-stop; the
// two cannot overlap in practice because nothing is dealing damage while the
// game is stopped.
// ===========================================================================
var paused = 0;

function handlePause() {
    if (phase === P_HANGAR || phase === P_DEAD) { return 0; }

    if (Input.isKeyPressed("Escape")) {
        paused = paused ? 0 : 1;
        Time.timeScale = paused ? 0 : 1;
        if (hud) {
            if (paused) { hud.call("setPaused", true, "PAUSED", "Esc to resume"); }
            else { hud.call("setPaused", false); }
        }
    }
    return paused;
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
            Tint: { R: 32, G: 36, B: 50, A: 255 },
            Size: [arenaW, arenaH],
            LayerDepth: depthFloor
        });
    }

    // A sparse grid, so movement reads as movement even in an empty arena.
    for (var x = -arenaHalfW + tileStep; x < arenaHalfW; x += tileStep) {
        line(x, 0, 2, arenaH, 56, 60, 82);
    }
    for (var y = -arenaHalfH + tileStep; y < arenaHalfH; y += tileStep) {
        line(0, y, arenaW, 2, 56, 60, 82);
    }

    // The boundary. Movement is clamped in script, so these are a picture of
    // the edge rather than a collider anything relies on.
    line(0, -arenaHalfH, arenaW, 14, 112, 120, 160);
    line(0,  arenaHalfH, arenaW, 14, 112, 120, 160);
    line(-arenaHalfW, 0, 14, arenaH, 112, 120, 160);
    line( arenaHalfW, 0, 14, arenaH, 112, 120, 160);
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
    if (pilot) {
        log("LAUNCH -- " + pilot.call("getPilotName"));
        if (hud) { hud.call("setTitle", pilot.call("getPilotName")); }
    }
    if (hud) { hud.call("setPaused", false); hud.call("say", "LAUNCH"); }
    if (fx) { fx.call("fadeFrom", 0.5); }
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

    // Only the Forge is rooted, and dropping one on the far wall is an errand
    // rather than a threat -- it never comes to you, so the wave cannot end
    // until you have walked across the map to it. It goes into the fight.
    var pos = isRooted(kind) ? nearSpawn() : edgeSpawn();

    // Groups, so a Mite is never alone and a Warden always is.
    var group = kindGroup(kind);
    for (var i = 0; i < group; i++) {
        var jx = group > 1 ? (Math.random() - 0.5) * 70 : 0;
        var jy = group > 1 ? (Math.random() - 0.5) * 70 : 0;
        if (swarm) {
            swarm.call("spawnKind", kind, pos.x + jx, pos.y + jy,
                       1 + w * hpPerWave, 1 + w * spdPerWave);
        }
    }
    return kindCost(kind) * group;
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

    if (hud) { hud.call("say", isBoss ? ("WAVE " + wave + "  --  BOSS") : ("WAVE " + wave)); }
    if (isBoss && fx) { fx.call("flash", "#ff4030", 0.18); fx.call("shake", 12); }

    // The wave the title arrives on says so, once.
    if (wave === darkFullWave && hud) { hud.call("say", "THE DARK"); }
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

function isRooted(kind) { return kind === 9 ? 1 : 0; }   // the Forge, and only it

// A ring around the pilot: close enough to be part of the fight, never on top
// of them.
function nearSpawn() {
    var px = player ? player.transform.x : 0;
    var py = player ? player.transform.y : 0;
    var a = Math.random() * Math.PI * 2;
    var r = 360 + Math.random() * 200;
    var mw = arenaHalfW - 60, mh = arenaHalfH - 60;
    return {
        x: Math.max(-mw, Math.min(mw, px + Math.cos(a) * r)),
        y: Math.max(-mh, Math.min(mh, py + Math.sin(a) * r))
    };
}

// The boss's own hp from the roster, plus the original's ~6 a wave past 25 so
// a cycled boss never goes soft. Solo is x1.0 of that, which is what this is.
var BOSS_HP = [60, 90, 120, 140, 180];   // BRUTE PRIME, HEX PRIME, FOUNDRY, SHEPHERD, ULTRADARK

function bossHp(w) {
    var index = bossIndexFor(w);
    var past = w > 25 ? (w - 25) * bossHpPerWavePast25 : 0;
    return Math.round(BOSS_HP[index] + past);
}

function bossIndexFor(w) {
    var i = Math.floor(w / 5) - 1;
    if (i < 0) { i = 0; }
    return i % 5;
}

// The roster deepens on the original's schedule. Drones and Mites from the
// start; a Forge not until wave 14.
function kindUnlocked(kind, w) {
    if (kind === 0 || kind === 1) { return 1; }        // DRONE, MITE
    if (kind === 2)  { return w >= 2  ? 1 : 0; }       // WEAVER
    if (kind === 4)  { return w >= 3  ? 1 : 0; }       // SPINNER
    if (kind === 3)  { return w >= 4  ? 1 : 0; }       // BRUTE
    if (kind === 5)  { return w >= 6  ? 1 : 0; }       // MORTAR
    if (kind === 6)  { return w >= 7  ? 1 : 0; }       // SNIPER
    if (kind === 10) { return w >= 9  ? 1 : 0; }       // GHOST
    if (kind === 7)  { return w >= 11 ? 1 : 0; }       // LEECH
    if (kind === 11) { return w >= 12 ? 1 : 0; }       // MAGNET
    if (kind === 8)  { return w >= 13 ? 1 : 0; }       // WARDEN
    if (kind === 9)  { return w >= 14 ? 1 : 0; }       // FORGE
    return 0;
}

// Spawn cost, from the roster.
function kindCost(kind) {
    var COST = [3, 2, 7, 12, 9, 10, 11, 6, 13, 16, 10, 10];
    return COST[kind];
}

// Group size: Mites arrive six at a time, Drones three, and the heavy kinds
// alone. A wave is mostly small things, which is what makes the big ones read.
function kindGroup(kind) {
    if (kind === 1) { return 6; }                                  // MITE
    if (kind === 0) { return 3; }                                  // DRONE
    if (kind === 3 || kind === 9 || kind === 8) { return 1; }      // BRUTE, FORGE, WARDEN
    return 2;
}

function pickKind(w, budget) {
    var choices = [];
    for (var k = 0; k < 12; k++) {
        if (!kindUnlocked(k, w)) { continue; }
        if (kindCost(k) * kindGroup(k) > budget) { continue; }
        choices.push(k);
    }
    if (choices.length === 0) { return -1; }
    // The original picks uniformly from what is unlocked; the group sizes are
    // what weight a wave towards infantry, not the draw.
    return choices[Math.floor(Math.random() * choices.length)];
}

// Spawn on the edge, on the far side from the player: an enemy that appears on
// top of you is not difficulty, it is a bug you cannot see.
function edgeSpawn() {
    var px = player ? player.transform.x : 0;
    var py = player ? player.transform.y : 0;

    for (var tries = 0; tries < 8; tries++) {
        var edge = Math.floor(Math.random() * 4);
        var mw = arenaHalfW - wallPad, mh = arenaHalfH - wallPad;
        var x = 0, y = 0;
        if (edge === 0)      { x = rand(-mw, mw); y = -mh; }
        else if (edge === 1) { x = rand(-mw, mw); y =  mh; }
        else if (edge === 2) { x = -mw;           y = rand(-mh, mh); }
        else                 { x =  mw;           y = rand(-mh, mh); }

        // WAVE.SPAWN_MIN_DIST: nothing arrives on top of you. An enemy that
        // appears where you are standing is not difficulty, it is a hit you
        // could not have avoided.
        var dx = x - px, dy = y - py;
        if (dx * dx + dy * dy > spawnMinDist * spawnMinDist) { return { x: x, y: y }; }
    }
    return { x: -px, y: -py };
}

function rand(lo, hi) { return lo + Math.random() * (hi - lo); }

// ===========================================================================
// Bosses
// ===========================================================================

function spawnBoss(index) {
    var kind = Math.max(0, Math.min(4, Number(index) | 0));
    var a = Scene.createActor("Boss", 0, -arenaHalfH * 0.55);
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

    if (fx) { fx.call("impact", 24); }
    if (hud) { hud.call("say", "BOSS DOWN  --  +" + reward + " CORES"); }

    if (pickups) { pickups.call("burst", 1, x, y, Math.floor(Math.random() * 5), 5, 90); }
    return 1;
}

// ===========================================================================
// Kills, score, cores
// ===========================================================================

function onEnemyKilled(kind, x, y, points, coreValue, dropChance) {
    score += Math.floor((Number(points) || 0) * mult);

    mult = Math.min(multMax, mult + multPerKill);
    multTimer = multGrace;

    // Every kill is worth cores at the kind's own value; only the chunkier
    // kinds drop a consumable, each at its own chance from the roster.
    var bonus = pilot ? pilot.call("getCoreBonus") : 1;
    addCores(Math.round((Number(coreValue) || 1) * bonus));

    if (Math.random() < (Number(dropChance) || 0)) {
        dropPickup(1, x, y, rollConsumable());
    }
    return 1;
}

// The weighted table from the original: repairs common, bombs precious.
var CONSUMABLE_NAME = ["REPAIR KIT", "OVERSHIELD", "FRENZY CORE", "STASIS CHARGE", "BOMB CELL"];
var CONSUMABLE_WEIGHT = [3, 2, 2, 2, 1];

function rollConsumable() {
    var total = 0;
    for (var i = 0; i < CONSUMABLE_WEIGHT.length; i++) { total += CONSUMABLE_WEIGHT[i]; }
    var r = Math.random() * total;
    for (var k = 0; k < CONSUMABLE_WEIGHT.length; k++) {
        r -= CONSUMABLE_WEIGHT[k];
        if (r < 0) { return k; }
    }
    return 0;
}

function consumableName(id) { return CONSUMABLE_NAME[Math.max(0, Math.min(4, id | 0))]; }

// The Leech does not damage you, it eats the run. Called every frame it is on
// you, so the cost is a rate rather than a hit.
function drainMultiplier(amount) {
    mult = Math.max(1, mult - (Number(amount) || 0));
    multTimer = 0;
    return mult;
}

function onPlayerHit() {
    // MULT.HIT_FACTOR: halved, not scratched. With three hit points a hit is
    // already serious; this is what makes it cost the run as well.
    mult = Math.max(1, mult * multHitFactor);
    multTimer = 0;
    return 1;
}

function addCores(amount) {
    cores += Math.max(0, Math.floor(Number(amount) || 0));
    return cores;
}

// ===========================================================================
// Pickups
//
// The `pickup-drops` cookie owns the drops themselves: the pool, the lifetime,
// the blink before one expires and the magnet, whose reach comes from the
// pilot's MAGNETIC stacks. All this end has to do is say what one is worth.
// ===========================================================================

function dropPickup(kind, x, y, value) {
    if (!pickups) { return 0; }
    return pickups.call("drop", kind, x, y, value);
}

// The one call the cookie makes back, once per pickup collected.
function onPickup(kind, value) {
    if (kind === 0) {
        addCores(value);
    } else if (pilot) {
        pilot.call("giveConsumable", value);
    }
    spawnEffect(player ? player.transform.x : 0, player ? player.transform.y : 0,
                26, 255, 240, 180, 0.12);
    return 1;
}

// ===========================================================================
// Deployables -- SPARKS' pylon and RIGG's turret
// ===========================================================================

// BLAZE's FLAME ZONE: ground that burns whatever stands in it.
function spawnFlame(x, y, r, life, dps) { return deploy(2, x, y, life, dps, r); }

function spawnPylon(x, y, life, dmg) { return deploy(0, x, y, life, dmg); }
function spawnTurret(x, y, life, dmg) { return deploy(1, x, y, life, dmg); }

var DEPLOY_NAME = ["Pylon", "Turret", "Flame"];

function deploy(kind, x, y, life, dmg, radius) {
    var a = Scene.createActor(DEPLOY_NAME[kind], x, y);
    if (!a) { return 0; }
    a.tag = "Fx";
    var r = Number(radius) || 20;
    Scene.addComponent(a, "SpriteRenderer", {
        Tint: kind === 0 ? { R: 255, G: 228, B: 91, A: 255 }
            : kind === 1 ? { R: 255, G: 158, B: 44, A: 255 }
            : { R: 255, G: 122, B: 61, A: 110 },
        Size: kind === 2 ? [r * 2, r * 2] : [20, 20],
        LayerDepth: kind === 2 ? depthPickup : depthDeploy
    });
    dpActor[dpN] = a;
    dpKind[dpN] = kind;
    dpLife[dpN] = Number(life) || 10;
    dpTimer[dpN] = 0;
    dpDmg[dpN] = Number(dmg) || 8;
    dpRad[dpN] = r;
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

        if (dpKind[i] === 2) {
            dpTimer[i] = 0.5;
            if (swarm) { swarm.call("damageCircle", a.transform.x, a.transform.y, dpRad[i], dpDmg[i] * 0.5, 0); }
        } else if (dpKind[i] === 0) {
            dpTimer[i] = 0.5;
            if (swarm) { swarm.call("chainFrom", a.transform.x, a.transform.y, dpDmg[i], 3, 200); }
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
        dpRad[i] = dpRad[last];
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
        board.call("setHeading", "DRAFT");
        board.call("setHint", "1 2 3, or click a card");
        board.call("open", 3);
        for (var i = 0; i < 3; i++) {
            var id = draftIds[i];
            board.call("setCard", i, upgradeName(id), upgradeDesc(id),
                       upgradeFamily(id) + (upgradeCursed(id) ? "  -  CURSED" : ""),
                       upgradeHex(id));
            board.call("setFooter", i, ownedLabel(id));
            board.call("setEnabled", i, 1);
        }
    }

    for (var k = 0; k < grantPerIntermission; k++) { grantMod(); }

    if (hud) { hud.call("say", "DRAFT  --  pick one"); }
    log("--- DRAFT --- 1/2/3, or click one");
    for (var j = 0; j < 3; j++) { log("   " + (j + 1) + ": " + upgradeName(draftIds[j])); }
}

// Free, unchosen, and announced. The draft is the decision; this is the pilot
// keeping pace with the curve.
function grantMod() {
    var id = randomMod(-1, -1);
    if (pilot) { pilot.call("addMod", id); }
    log("SALVAGE: " + upgradeName(id));
    if (hud) { hud.call("say", "SALVAGE  " + upgradeName(id)); }
    return id;
}

// Stacking is the whole design, so a card says what taking it again would mean.
function ownedLabel(id) {
    if (!pilot) { return ""; }
    var owned = pilot.call("countMod", id);
    return owned > 0 ? ("owned x" + owned) : "";
}

// Everything a card says comes from the Upgrades script, so the pool and the
// card can never drift apart.
function upgradeName(id)   { return upgrades ? upgrades.call("nameOf", id) : ""; }
function upgradeDesc(id)   { return upgrades ? upgrades.call("descOf", id) : ""; }
function upgradeFamily(id) { return upgrades ? upgrades.call("familyName", id) : ""; }
function upgradeHex(id)    { return upgrades ? upgrades.call("familyHex", id) : "#888888"; }
function upgradeCursed(id) { return upgrades ? upgrades.call("isCursed", id) : 0; }

// Rarer costs more; a cursed one is cheap, because it is not a favour.
function modCost(id) {
    var r = upgrades ? upgrades.call("rarityOf", id) : 1;
    if (upgradeCursed(id)) { return 30; }
    return r === 1 ? 25 : r === 2 ? 40 : 60;
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
        log("TAKEN: " + upgradeName(id));
        if (hud) { hud.call("say", upgradeName(id)); }
        if (board) { board.call("close"); }
        nextWave();
        return;
    }

    // The intermission is a floor, not a limit: it runs out only if nothing is
    // chosen, and then the first card is taken so a run can never stall.
    waveTimer -= dt;
    if (waveTimer <= 0) {
        if (pilot) { pilot.call("addMod", draftIds[0]); }
        log("TAKEN (default): " + upgradeName(draftIds[0]));
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
    if (hud) { hud.call("say", "CORE SHOP  --  ENTER to leave"); }
    log("--- CORE SHOP --- " + cores + " cores. 1-4 to buy, ENTER to leave.");
}

function rollShop() {
    for (var i = 0; i < 4; i++) {
        shopIds[i] = randomMod(shopIds[(i + 1) % 4], shopIds[(i + 2) % 4]);
    }
    if (board) {
        board.call("setHeading", "CORE SHOP  --  " + cores);
        board.call("setHint", "1-4 to buy, ENTER to leave");
        board.call("open", 4);
        for (var j = 0; j < 4; j++) {
            var id = shopIds[j];
            var affordable = cores >= modCost(id) ? 1 : 0;
            board.call("setCard", j, upgradeName(id), upgradeDesc(id),
                       upgradeFamily(id) + (upgradeCursed(id) ? "  -  CURSED" : ""),
                       upgradeHex(id));
            board.call("setFooter", j, modCost(id) + " cores   " + ownedLabel(id));
            board.call("setEnabled", j, affordable);
        }
    }
    for (var k = 0; k < 4; k++) {
        log("   " + (k + 1) + ": " + upgradeName(shopIds[k]) + "  " + modCost(shopIds[k]) + " cores");
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
    if (cores < modCost(id)) {
        log("Not enough cores for " + upgradeName(id) + " (" + modCost(id) + ")");
        if (hud) { hud.call("say", "NOT ENOUGH CORES"); }
        rollShop();
        return;
    }
    cores -= modCost(id);
    if (pilot) { pilot.call("addMod", id); }
    log("BOUGHT: " + upgradeName(id) + "  --  " + cores + " cores left");
    if (hud) { hud.call("say", "BOUGHT " + upgradeName(id)); }
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

    if (fx) { fx.call("impact", 30); }
    if (hud) {
        hud.call("setPaused", true, "RUN OVER",
                 "wave " + wave + "   score " + score + "   R to fly again");
    }
    return 1;
}

function runDead(dt) {
    if (!Input.isKeyPressed("R")) { return; }

    if (swarm)   { swarm.call("clearAll"); }
    if (bullets) { bullets.call("clearAll"); }
    if (board)   { board.call("close"); }
    if (waves)   { waves.call("stop"); }
    if (bossActor) { bossActor.destroy(); bossActor = null; }

    if (pickups) { pickups.call("clearAll"); }
    for (var j = dpN - 1; j >= 0; j--) { if (dpActor[j]) { dpActor[j].destroy(); } removeDeploy(j); }

    darkOverride = -1;
    bossUp = 0;
    mult = 1;
    if (pilot) { pilot.call("resetRun"); }

    phase = P_HANGAR;
    paused = 0;
    Time.timeScale = 1;
    wave = 0;
    score = 0;
    cores = 0;
    buildHangar();
    if (hud) { hud.call("setPaused", true, "HANGAR", "1-8 pick a pilot   ENTER to launch"); }
    log("--- HANGAR --- 1-8 to pick a pilot, ENTER to launch");
}

// ===========================================================================
// Readouts
// ===========================================================================

// The projectile cookie takes a single half-extent, so it gets the larger one
// and a stray shot flies a little further before it is recycled. Everything
// that has to stay inside the arena asks for the axis it cares about.
function getArenaHalf()  { return arenaHalfW; }
function getArenaHalfW() { return arenaHalfW; }
function getArenaHalfH() { return arenaHalfH; }
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
