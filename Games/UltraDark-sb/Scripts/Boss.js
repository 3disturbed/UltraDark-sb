// Boss.js -- the five bosses, one at a time.
// Spawned by the Director on to its own actor, tagged "Boss", and configured
// through configure(kind, hp, wave) before its first frame.
//
// A boss gets its own script where an enemy does not, because a boss is the
// one thing on screen with real state: phases, a pattern that remembers where
// it is in itself, a door cycle, an arena it is allowed to change. One Jint
// engine for the thing the whole wave is about is a fair trade.
//
// Every boss telegraphs. The rule the fights are built on is that nothing that
// can kill you arrives without a frame of warning first -- a wind-up, a ring,
// a line, a colour change.

// ===========================================================================
// The five
//
// 0 BRUTE PRIME    charges, and slams a ring out on landing
// 1 HEX PRIME      spiral fire, blinks away when you crowd it
// 2 FOUNDRY        builds; shielded until its doors open
// 3 NULL SHEPHERD  plants dark zones and shepherds adds into them
// 4 THE ULTRADARK  turns the lights off and does all of it
// ===========================================================================
var B_BRUTE = 0, B_HEX = 1, B_FOUNDRY = 2, B_SHEPHERD = 3, B_ULTRA = 4;

// The five, from UltraDark's own roster: names, radii, colours and speeds.
var BOSS_NAME = ["BRUTE PRIME", "HEXAGON PRIME", "FOUNDRY", "NULL SHEPHERD", "THE ULTRADARK"];
var BOSS_RAD  = [52,  56,  60,  54,  68 ];
var BOSS_R    = [255, 57,  255, 194, 122];
var BOSS_G    = [77,  240, 140, 107, 92 ];
var BOSS_B    = [77,  255, 91,  250, 255];
var BOSS_SPD  = [30,  14,  6,   26,  34 ];

// Their own cadences.
var BRUTE_FIRE = 4.0;
var HEX_FIRE   = 0.55;                   // rotating spokes
var FOUNDRY_CLOSED = 8, FOUNDRY_OPEN = 3, FOUNDRY_SPAWN = 4;
var SHEPHERD_FIRE = 3.0, SHEPHERD_DARK = 5.0;
var ULTRA_FIRE = 0.8;

// On death BRUTE PRIME bursts, like its lesser kind.
var BRUTE_PRIME_SPLIT = 6;

// ===========================================================================
// Tuning
// ===========================================================================
var depthBoss = 0.45;
var depthZone = 0.35;      // under the enemies standing in it, over the floor

var contactDamage   = 1;    // everything does one; the pilot has three
var contactCooldown = 0.85;

// ===========================================================================
// State
// ===========================================================================
var kind = 0;
var hp = 1000, hpMax = 1000;
var wave = 5;
var dead = 0;
var configured = 0;

var t = 0;              // primary cadence timer
var t2 = 0;             // secondary
var phase = 0;
var spin = 0;
var contactTimer = 0;
var invuln = 0;
// FOUNDRY's doors are a STATE, not a countdown. Driving them from `invuln`
// meant the timer that opened them was also the timer that closed them, so the
// vulnerable window never actually happened and the boss could not be killed.
var doorShut = 0;

var vx = 0, vy = 0;

var sprite = null;
var player = null, pilot = null, director = null, bullets = null, swarm = null;

var zones = [];         // NULL SHEPHERD's dark patches: actor, x, y, r, life

// ===========================================================================
// Lifecycle
// ===========================================================================

function onStart() {
    sprite = actor.getComponent("SpriteRenderer");
    resolve();
    if (configured) { dress(); }
}

function resolve() {
    if (!player)   { player = Scene.findFirstByTag("Player"); if (player) { pilot = player.getComponent("ScriptComponent"); } }
    if (!director) { var d = Scene.findFirstByTag("Director"); if (d) { director = d.getComponent("ScriptComponent"); } }
    if (!bullets)  { var b = Scene.findFirstByTag("Bullets");  if (b) { bullets = b.getComponent("ScriptComponent"); } }
    if (!swarm)    { var s = Scene.findFirstByTag("Swarm");    if (s) { swarm = s.getComponent("ScriptComponent"); } }
}

// Configured after the actor exists, because a script cannot be handed
// arguments at attach time. Primitives only, as always.
function configure(k, health, waveNumber) {
    kind = Math.max(0, Math.min(4, Number(k) | 0));
    hpMax = Math.max(1, Number(health) || 1000);
    hp = hpMax;
    wave = Number(waveNumber) || 5;
    dead = 0;
    phase = 0;
    doorShut = (kind === B_FOUNDRY) ? 1 : 0;
    t = doorShut ? FOUNDRY_CLOSED : 0;
    configured = 1;
    if (sprite) { dress(); }
    log(">>> " + BOSS_NAME[kind] + " <<<");
    return 1;
}

function dress() {
    if (!sprite) { return; }
    sprite.size = { x: BOSS_RAD[kind] * 2, y: BOSS_RAD[kind] * 2 * 0.85 };
    sprite.tint = { R: BOSS_R[kind], G: BOSS_G[kind], B: BOSS_B[kind], A: 255 };
    sprite.layerDepth = depthBoss;

    var col = actor.getComponent("BoxCollider2D");
    if (col) { col.size = { x: BOSS_RAD[kind] * 2, y: BOSS_RAD[kind] * 2 * 0.85 }; }
}

function onUpdate(dt) {
    if (dead || !configured) { return; }
    resolve();
    if (!player) { return; }

    if (t > 0) { t -= dt; }
    if (t2 > 0) { t2 -= dt; }
    if (contactTimer > 0) { contactTimer -= dt; }
    if (invuln > 0) { invuln -= dt; }
    spin += dt;

    // Half health opens the second phase for every boss: faster, and one more
    // thing to watch. It is announced so the change is never a mystery.
    if (phase === 0 && hp <= hpMax * 0.5) {
        phase = 1;
        log(BOSS_NAME[kind] + " -- second phase");
        if (director) { director.call("spawnEffect", actor.transform.x, actor.transform.y, 260, 255, 255, 255, 0.3); }
    }

    var px = player.transform.x, py = player.transform.y;
    var dx = px - actor.transform.x, dy = py - actor.transform.y;
    var dist = Math.sqrt(dx * dx + dy * dy);
    var nx = dist > 0.01 ? dx / dist : 0;
    var ny = dist > 0.01 ? dy / dist : 0;

    if      (kind === B_BRUTE)    { brute(dt, nx, ny, dist); }
    else if (kind === B_HEX)      { hex(dt, nx, ny, dist); }
    else if (kind === B_FOUNDRY)  { foundry(dt, nx, ny, dist); }
    else if (kind === B_SHEPHERD) { shepherd(dt, nx, ny, dist); }
    else                          { ultradark(dt, nx, ny, dist); }

    tickZones(dt, px, py);
    clampIn();
    touch(dist);
    paint();
}

// ===========================================================================
// BRUTE PRIME -- charge, land, ring.
// ===========================================================================
function brute(dt, nx, ny, dist) {
    var speed = BOSS_SPD[kind] * (phase ? 1.4 : 1);

    if (phase >= 0 && t <= 0 && dist < 620) {
        t = phase ? 2.4 : 3.4;
        t2 = 0.55;
        vx = nx; vy = ny;                       // committed direction, telegraphed
        invuln = 0;
    }

    if (t2 > 0) {
        // wind-up: hold still, and go white
        return;
    }

    if (t > (phase ? 1.6 : 2.4)) {
        actor.transform.x += vx * 620 * dt;
        actor.transform.y += vy * 620 * dt;
        if (t <= (phase ? 1.7 : 2.5)) { slam(); }
    } else {
        actor.transform.x += nx * speed * dt;
        actor.transform.y += ny * speed * dt;
    }
}

function slam() {
    var x = actor.transform.x, y = actor.transform.y;
    ring(x, y, 250, 220, 70, 60);
    if (swarm) { swarm.call("knockCircle", x, y, 300, 380); }
    if (pilot && player) {
        var dx = player.transform.x - x, dy = player.transform.y - y;
        // One, like everything else. A slam that took 30 was written against a
        // hundred-point health bar; against three hit points it is an instant
        // kill no amount of healing survives.
        if (dx * dx + dy * dy < 250 * 250) { pilot.call("hurt", 1); }
    }
}

// ===========================================================================
// HEX PRIME -- a spiral you walk out of, and a blink when you get close.
// ===========================================================================
function hex(dt, nx, ny, dist) {
    if (dist > 380) {
        actor.transform.x += nx * BOSS_SPD[kind] * dt;
        actor.transform.y += ny * BOSS_SPD[kind] * dt;
    }

    if (t <= 0 && bullets) {
        t = phase ? 0.10 : 0.16;
        var arms = phase ? 5 : 3;
        for (var a = 0; a < arms; a++) {
            var ang = spin * 2.2 + (a / arms) * Math.PI * 2;
            bullets.call("fire", actor.transform.x + Math.cos(ang) * 40,
                         actor.transform.y + Math.sin(ang) * 40,
                         ang, 250, 11, 1, 7, 4.0, 0, 0);
        }
    }

    // Crowding it is punished, not rewarded: it leaves and drops a ring.
    if (dist < 150 && t2 <= 0) {
        t2 = 4.0;
        ring(actor.transform.x, actor.transform.y, 190, 180, 90, 240);
        var ang2 = Math.random() * Math.PI * 2;
        var half = director ? director.call("getArenaHalf") - 120 : 900;
        actor.transform.x = Math.max(-half, Math.min(half, actor.transform.x + Math.cos(ang2) * 460));
        actor.transform.y = Math.max(-half, Math.min(half, actor.transform.y + Math.sin(ang2) * 460));
        if (director) { director.call("spawnEffect", actor.transform.x, actor.transform.y, 120, 180, 90, 240, 0.2); }
    }
}

// ===========================================================================
// FOUNDRY -- rooted, shielded while the doors are shut, and always building.
// The door cycle is the fight: damage only lands in the open window.
// ===========================================================================
function foundry(dt, nx, ny, dist) {
    if (t <= 0) {
        doorShut = doorShut ? 0 : 1;
        t = doorShut ? FOUNDRY_CLOSED : FOUNDRY_OPEN;
        log(doorShut ? "FOUNDRY: doors shut" : "FOUNDRY: doors open");
    }

    if (t2 <= 0 && swarm) {
        t2 = phase ? 1.6 : 2.4;
        // It builds Drones, and Mites once it is hurt. The wave cannot end
        // while it lives, so the adds are pressure rather than padding.
        var a = Math.random() * Math.PI * 2;
        var kindToBuild = phase ? (Math.random() < 0.4 ? 1 : 0) : 0;
        swarm.call("spawnKind", kindToBuild,
                   actor.transform.x + Math.cos(a) * 90,
                   actor.transform.y + Math.sin(a) * 90,
                   1 + wave * 0.04, 1);
    }
}

// ===========================================================================
// NULL SHEPHERD -- plants dark zones that hurt to stand in, and herds you
// between them.
// ===========================================================================
function shepherd(dt, nx, ny, dist) {
    // It circles rather than closes, which is what makes the zones matter.
    var tangentX = -ny, tangentY = nx;
    actor.transform.x += (tangentX * 0.8 + nx * 0.25) * BOSS_SPD[kind] * dt;
    actor.transform.y += (tangentY * 0.8 + ny * 0.25) * BOSS_SPD[kind] * dt;

    if (t <= 0 && player) {
        t = phase ? 2.0 : 3.0;
        plantZone(player.transform.x + (Math.random() - 0.5) * 220,
                  player.transform.y + (Math.random() - 0.5) * 220,
                  120, phase ? 7.0 : 5.5);
    }

    if (t2 <= 0 && swarm) {
        t2 = phase ? 3.0 : 4.5;
        for (var i = 0; i < (phase ? 3 : 2); i++) {
            var a = Math.random() * Math.PI * 2;
            swarm.call("spawnKind", 4,      // ghosts, which is the point of the dark
                       actor.transform.x + Math.cos(a) * 110,
                       actor.transform.y + Math.sin(a) * 110,
                       1 + wave * 0.05, 1);
        }
    }
}

function plantZone(x, y, r, life) {
    var a = Scene.createActor("Zone", x, y);
    if (!a) { return; }
    a.tag = "Fx";
    // Drawn at the full diameter even though the damage is a circle inside it:
    // over-drawing danger makes a player cautious, under-drawing it makes the
    // game unfair. The alpha is low so it reads as a haze rather than a wall --
    // at 150 two overlapping zones looked like level geometry.
    Scene.addComponent(a, "SpriteRenderer", {
        Tint: { R: 58, G: 14, B: 96, A: 130 },
        Size: [r * 2, r * 2],
        LayerDepth: depthZone
    });
    zones.push({ a: a, x: x, y: y, r: r, life: life });
}

function tickZones(dt, px, py) {
    for (var i = zones.length - 1; i >= 0; i--) {
        var z = zones[i];
        z.life -= dt;
        if (z.life <= 0) {
            if (z.a) { z.a.destroy(); }
            zones.splice(i, 1);
            continue;
        }
        var dx = px - z.x, dy = py - z.y;
        if (dx * dx + dy * dy < z.r * z.r && pilot && contactTimer <= 0) {
            pilot.call("hurt", 1);
            contactTimer = 0.5;
        }
    }
}

// ===========================================================================
// THE ULTRADARK -- the lights go out, and it does a little of everything.
// The darkness override is released in onDestroy, so a wipe or a kill both
// give the arena back.
// ===========================================================================
function ultradark(dt, nx, ny, dist) {
    if (director) { director.call("setDarkOverride", phase ? 0.98 : 0.86); }

    if (dist > 260) {
        actor.transform.x += nx * BOSS_SPD[kind] * dt;
        actor.transform.y += ny * BOSS_SPD[kind] * dt;
    }

    if (t <= 0 && bullets) {
        t = phase ? 0.9 : 1.4;
        var arms = phase ? 12 : 8;
        for (var a = 0; a < arms; a++) {
            var ang = spin + (a / arms) * Math.PI * 2;
            bullets.call("fire", actor.transform.x, actor.transform.y, ang,
                         220, 13, 1, 8, 5.0, 0, 0);
        }
        ring(actor.transform.x, actor.transform.y, 150, 235, 235, 255);
    }

    if (t2 <= 0) {
        t2 = phase ? 2.6 : 4.0;
        if (swarm) {
            for (var i = 0; i < 3; i++) {
                var a2 = Math.random() * Math.PI * 2;
                swarm.call("spawnKind", i === 0 ? 4 : 10,
                           actor.transform.x + Math.cos(a2) * 140,
                           actor.transform.y + Math.sin(a2) * 140,
                           1 + wave * 0.06, 1);
            }
        }
        if (player) { plantZone(player.transform.x, player.transform.y, 130, 4.0); }
    }
}

// ===========================================================================
// Shared
// ===========================================================================

// A ring is a bright expanding square above the dark: at wave 25 it is one of
// the few things you can see, so it doubles as the light.
function ring(x, y, r, cr, cg, cb) {
    if (director) { director.call("spawnEffect", x, y, r * 2, cr, cg, cb, 0.28); }
}

function touch(dist) {
    if (!pilot || contactTimer > 0) { return; }
    if (dist > BOSS_RAD[kind] + 18) { return; }
    pilot.call("hurt", 1);
    contactTimer = contactCooldown;
}

function clampIn() {
    if (!director) { return; }
    var hw = director.call("getArenaHalfW") - BOSS_RAD[kind];
    var hh = director.call("getArenaHalfH") - BOSS_RAD[kind];
    var tr = actor.transform;
    if (tr.x < -hw) { tr.x = -hw; }
    if (tr.x >  hw) { tr.x =  hw; }
    if (tr.y < -hh) { tr.y = -hh; }
    if (tr.y >  hh) { tr.y =  hh; }
}

function paint() {
    if (!sprite) { return; }
    var r = BOSS_R[kind], g = BOSS_G[kind], b = BOSS_B[kind];

    if (invuln > 0 || doorShut) { r = (r + 120) >> 1; g = (g + 120) >> 1; b = (b + 120) >> 1; }
    if (t2 > 0 && kind === B_BRUTE) { r = 255; g = 255; b = 255; }
    if (phase === 1) { r = Math.min(255, r + 30); }

    sprite.tint = { R: r, G: g, B: b, A: 255 };
}

// ===========================================================================
// Damage
// ===========================================================================

function takeDamage(dmg, code) {
    if (dead) { return 0; }
    // The door cycle, and only that.
    if (invuln > 0 || doorShut) { return 0; }

    hp -= Number(dmg) || 0;
    if (pilot) { pilot.call("onDamageDealt", Number(dmg) || 0); }

    if (hp <= 0) { hp = 0; die(); return 1; }
    return 0;
}

function die() {
    if (dead) { return; }
    dead = 1;
    log(BOSS_NAME[kind] + " DOWN");

    for (var i = 0; i < zones.length; i++) { if (zones[i].a) { zones[i].a.destroy(); } }
    zones = [];

    // BRUTE PRIME bursts into six Mites, the way its lesser kind bursts into
    // four -- killing it in your face is worse than killing it at range.
    if (kind === B_BRUTE && swarm) {
        for (var m = 0; m < BRUTE_PRIME_SPLIT; m++) {
            var ang = (m / BRUTE_PRIME_SPLIT) * Math.PI * 2;
            swarm.call("spawnKind", 1, actor.transform.x + Math.cos(ang) * 40,
                       actor.transform.y + Math.sin(ang) * 40, 1 + wave * 0.04, 1);
        }
    }

    if (director) {
        director.call("spawnEffect", actor.transform.x, actor.transform.y, 420, 255, 255, 255, 0.45);
        director.call("setDarkOverride", -1);
        director.call("onBossKilled", kind, actor.transform.x, actor.transform.y);
    }
    actor.destroy();
}

function onDestroy() {
    for (var i = 0; i < zones.length; i++) { if (zones[i].a) { zones[i].a.destroy(); } }
    zones = [];
    // Never leave the arena black because the boss left the scene some other way.
    if (director) { director.call("setDarkOverride", -1); }
}

// ===========================================================================
// Readouts
// ===========================================================================

function getHealth01() { return hpMax > 0 ? hp / hpMax : 0; }
function getName()     { return BOSS_NAME[kind]; }
function getKind()     { return kind; }
function isDead()      { return dead; }
function isInvuln()    { return (invuln > 0 || doorShut) ? 1 : 0; }
