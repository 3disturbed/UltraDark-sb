// Pilot.js -- the ship you fly, and everything that hangs off it.
// Attach to the Player actor, tag it "Player".
//
// Eight pilots share one script because they differ only in numbers: a weapon
// block and an ability. Adding a ninth is a row in PILOT_* and a case in
// useAbility, not a new file.
//
// Mods stack by design. p_mods is a list of ids with duplicates left in, and
// stats are recomputed from that list every time it changes -- never edited in
// place. Deduplicating mods is the one change that would quietly break half the
// draft pool, so nothing here ever does it.

// ===========================================================================
// Tuning -- movement
// ===========================================================================
var baseSpeed     = 268;    // px/sec
var accel         = 16;     // how fast we reach target velocity (1/sec)
var baseHpMax     = 100;

var dashSpeed     = 940;
var dashTime      = 0.16;
var dashCooldown  = 1.15;
var dashIFrames   = 0.22;   // invulnerable for slightly longer than the dash

var hitIFrames    = 0.55;   // grace after taking a hit

// ===========================================================================
// Tuning -- aiming
//
// The scripting contract exposes no viewport, so a script cannot convert a
// screen pixel into a world position. Auto-aim is therefore the primary aim:
// it is exact at every resolution, works with a pad and works on a phone.
//
// Mouse aim is offered as a refinement and needs one assumption -- that the
// canvas is DESIGN_W x DESIGN_H at zoom 1, which is what ProjectSettings asks
// for on both engines. Resize the window and it drifts, which is why TAB
// switches back and why auto is the default.
// ===========================================================================
var aimMode       = 0;      // 0 = auto (nearest), 1 = mouse
// Wider than the longest enemy range (the sniper's 640), so auto-aim can always
// answer whatever is shooting at you. Under that, a sniper outranges the pilot's
// own aim and there is nothing the player can do about it.
var aimRange      = 700;
var DESIGN_W      = 1280;
var DESIGN_H      = 720;

// ===========================================================================
// Tuning -- the eight pilots
//
// cd      seconds between shots        dmg     per projectile
// spd     projectile speed             pellets projectiles per shot
// spread  radians of cone              life    seconds a projectile lives
// rad     projectile radius            pierce  extra enemies each shot passes
// ===========================================================================
var PILOT_NAME    = ["BINK", "BLAZE", "AMBER", "DAVE", "SPARKS", "RIGG", "KELVIN", "HAWK"];
var PILOT_CD      = [0.090, 0.560, 0.230, 0.400, 0.320, 0.165, 0.270, 0.880];
var PILOT_DMG     = [6,     5,     10,    22,    9,     7,     6,     48   ];
var PILOT_SPD     = [980,   790,   900,   0,     0,     860,   700,   1750 ];
var PILOT_PELLETS = [1,     8,     1,     1,     1,     2,     3,     1    ];
var PILOT_SPREAD  = [0.055, 0.400, 0.020, 0,     0,     0.075, 0.260, 0.004];
var PILOT_LIFE    = [0.80,  0.34,  0.95,  0,     0,     0.75,  0.55,  1.20 ];
var PILOT_RAD     = [4,     4,     5,     0,     0,     4,     6,     6    ];
var PILOT_PIERCE  = [0,     0,     0,     0,     0,     0,     0,     99   ];
var PILOT_HP      = [96,    112,   92,    150,   88,    104,   100,   82   ];
var PILOT_SPEEDM  = [1.06,  0.95,  1.02,  0.88,  1.00,  0.98,  1.00,  0.94 ];

// Hull colour per pilot, and the symbol colour on the nose.
var PILOT_R       = [90,  255, 255, 226, 190, 150, 120, 245];
var PILOT_G       = [220, 150, 200, 78,  120, 230, 210, 235];
var PILOT_B       = [255, 60,  70,  70,  255, 90,  255, 200];

// Ability cooldowns, in the same order.
var PILOT_ACD     = [12,   10,    9,     11,    14,    16,    13,    12   ];

// ===========================================================================
// Tuning -- draw order (higher is nearer on both engines)
// ===========================================================================
var depthHull  = 0.50;
var depthNose  = 0.52;
var depthBlade = 0.53;
var depthFlash = 0.85;      // above the dark: muzzle flash is a light source

// ===========================================================================
// Tuning -- mods. Stackable, all of them.
// ===========================================================================
var MOD_COUNT = 24;

// ===========================================================================
// State
// ===========================================================================
var pilot = 0;

var hp = 100, hpMax = 100;
var shield = 0, shieldMax = 60;     // overshield, absorbs before hp
var alive = 1;

var vx = 0, vy = 0;
var aimAngle = 0;

var shotTimer = 0;
var shotCount = 0;
var dashTimer = 0, dashCdTimer = 0, iFrames = 0;
var abilityTimer = 0, abilityActive = 0;

var mods = [];              // ids, duplicates kept
var consumables = [];       // up to 3 ids

// Derived stats, recomputed by computeStats() whenever mods change.
var sDmg = 1, sCd = 1, sSpeed = 1, sProjSpd = 1, sPierce = 0, sPellets = 0;
var sLifesteal = 0, sRegen = 0, sAbilityCdr = 1, sCoreBonus = 1;
var sBlades = 0, sShockwave = 0, sChill = 0, sBurn = 0, sDeadMan = 0;
var sKineticDash = 0, sReactive = 0, sAdrenaline = 0, sCoreTap = 0, sMagnet = 0;

var bladeActors = [];
var bladeAngle = 0;

var noseActor = null;
var hullSprite = null;

var mouseIdle = 99;
var lastMouseX = 0, lastMouseY = 0;

var regenCarry = 0;
var killsForCoreTap = 0;

var bullets = null, swarm = null, director = null, camera = null;

var burnTint = 0;

// ===========================================================================
// Lifecycle
// ===========================================================================

function onStart() {
    hullSprite = actor.getComponent("SpriteRenderer");

    // A square hull makes rotation invisible. The nose is a second, smaller
    // actor riding in front of the hull so facing reads at a glance.
    noseActor = Scene.createActor("PilotNose", actor.transform.x, actor.transform.y);
    if (noseActor) {
        noseActor.tag = "Hud";
        Scene.addComponent(noseActor, "SpriteRenderer", {
            Tint: { R: 255, G: 255, B: 255, A: 255 },
            Size: [9, 9],
            LayerDepth: depthNose
        });
    }

    setPilot(pilot);
    resolveManagers();
}

function resolveManagers() {
    if (!bullets)  { var b = Scene.findFirstByTag("Bullets");  if (b) { bullets  = b.getComponent("ScriptComponent"); } }
    if (!swarm)    { var s = Scene.findFirstByTag("Swarm");    if (s) { swarm    = s.getComponent("ScriptComponent"); } }
    if (!director) { var d = Scene.findFirstByTag("Director"); if (d) { director = d.getComponent("ScriptComponent"); } }
    if (!camera)   { var c = Scene.findFirstByTag("MainCamera"); if (c) { camera = c; } }
}

function onUpdate(dt) {
    resolveManagers();

    if (!alive) { return; }

    // The Director parks the pilot during the hangar, the draft and the shop.
    var frozen = 0;
    if (director) { frozen = director.call("pilotFrozen"); }

    tickTimers(dt);
    readAim(dt);

    if (!frozen) {
        move(dt);
        if (wantsFire() && shotTimer <= 0) { fire(); }
        if (Input.isKeyPressed("Space")) { useAbility(); }
        if (Input.isKeyPressed("F")) { useConsumable(); }
    } else {
        vx = vx * 0.82;
        vy = vy * 0.82;
        applyVelocity(dt);
    }

    if (Input.isKeyPressed("Tab")) {
        aimMode = aimMode === 0 ? 1 : 0;
        log(aimMode === 0 ? "Aim: AUTO" : "Aim: MOUSE");
    }

    regen(dt);
    spinBlades(dt);
    faceNose();
}

// ===========================================================================
// Timers, regeneration, statuses
// ===========================================================================

function tickTimers(dt) {
    if (shotTimer > 0)     { shotTimer -= dt; }
    if (dashTimer > 0)     { dashTimer -= dt; }
    if (dashCdTimer > 0)   { dashCdTimer -= dt; }
    if (iFrames > 0)       { iFrames -= dt; }
    if (abilityTimer > 0)  { abilityTimer -= dt; }
    if (abilityActive > 0) { abilityActive -= dt; }
    if (burnTint > 0)      { burnTint -= dt; }
    mouseIdle += dt;
}

function regen(dt) {
    if (sRegen <= 0 || hp >= hpMax) { return; }
    regenCarry += sRegen * dt;
    if (regenCarry >= 1) {
        var whole = Math.floor(regenCarry);
        regenCarry -= whole;
        hp = Math.min(hpMax, hp + whole);
    }
}

// ===========================================================================
// Movement
// ===========================================================================

function move(dt) {
    var ix = 0, iy = 0;

    if (Input.isKeyHeld("W") || Input.isKeyHeld("Up"))    { iy -= 1; }
    if (Input.isKeyHeld("S") || Input.isKeyHeld("Down"))  { iy += 1; }
    if (Input.isKeyHeld("A") || Input.isKeyHeld("Left"))  { ix -= 1; }
    if (Input.isKeyHeld("D") || Input.isKeyHeld("Right")) { ix += 1; }

    // The on-screen stick, for touch. Guarded because the bridge throws with no
    // touch state on a headless host.
    var jx = 0, jy = 0;
    try { jx = Input.joystickX; jy = Input.joystickY; } catch (e) { jx = 0; jy = 0; }
    if (jx * jx + jy * jy > 0.04) { ix = jx; iy = jy; }

    var len = Math.sqrt(ix * ix + iy * iy);
    if (len > 1) { ix /= len; iy /= len; }

    // Dash: a burst along the current input, or straight ahead when standing.
    if (Input.isKeyPressed("LeftShift") && dashCdTimer <= 0 && dashTimer <= 0) {
        var dx = ix, dy = iy;
        if (dx === 0 && dy === 0) { dx = Math.cos(aimAngle); dy = Math.sin(aimAngle); }
        vx = dx * dashSpeed;
        vy = dy * dashSpeed;
        dashTimer = dashTime;
        dashCdTimer = dashCooldown;
        iFrames = Math.max(iFrames, dashIFrames);

        if (sKineticDash > 0 && swarm) {
            swarm.call("damageCircle", actor.transform.x, actor.transform.y, 74,
                       16 * sKineticDash * sDmg, 1);
        }
    }

    if (dashTimer <= 0) {
        var target = baseSpeed * PILOT_SPEEDM[pilot] * sSpeed;
        var tx = ix * target, ty = iy * target;
        var k = Math.min(1, accel * dt);
        vx += (tx - vx) * k;
        vy += (ty - vy) * k;
    }

    applyVelocity(dt);
}

function applyVelocity(dt) {
    actor.transform.x += vx * dt;
    actor.transform.y += vy * dt;
    clampToArena();
}

function clampToArena() {
    if (!director) { return; }
    var half = director.call("getArenaHalf");
    var m = 26;
    if (actor.transform.x < -half + m) { actor.transform.x = -half + m; if (vx < 0) { vx = 0; } }
    if (actor.transform.x >  half - m) { actor.transform.x =  half - m; if (vx > 0) { vx = 0; } }
    if (actor.transform.y < -half + m) { actor.transform.y = -half + m; if (vy < 0) { vy = 0; } }
    if (actor.transform.y >  half - m) { actor.transform.y =  half - m; if (vy > 0) { vy = 0; } }
}

// Called by MAGNET enemies: a steady pull toward a point.
function pullToward(x, y, force) {
    var dx = x - actor.transform.x, dy = y - actor.transform.y;
    var d = Math.sqrt(dx * dx + dy * dy);
    if (d < 1) { return 0; }
    vx += (dx / d) * force;
    vy += (dy / d) * force;
    return 1;
}

// ===========================================================================
// Aiming
// ===========================================================================

function readAim(dt) {
    var mx = Input.mouseX, my = Input.mouseY;
    if (Math.abs(mx - lastMouseX) > 1 || Math.abs(my - lastMouseY) > 1) { mouseIdle = 0; }
    lastMouseX = mx;
    lastMouseY = my;

    if (aimMode === 1 && mouseIdle < 2.5 && camera) {
        // Screen to world, under the design-resolution assumption above.
        var wx = camera.transform.x + (mx - DESIGN_W / 2);
        var wy = camera.transform.y + (my - DESIGN_H / 2);
        aimAngle = Math.atan2(wy - actor.transform.y, wx - actor.transform.x);
        return;
    }

    // Auto: the nearest live enemy inside aimRange.
    if (swarm) {
        var tx = swarm.call("nearestX", actor.transform.x, actor.transform.y, aimRange);
        if (tx > -900000) {
            var ty = swarm.call("nearestY", actor.transform.x, actor.transform.y, aimRange);
            aimAngle = Math.atan2(ty - actor.transform.y, tx - actor.transform.x);
            return;
        }
    }

    // Nothing to shoot: face the way we are going, so the ship never reads dead.
    if (vx * vx + vy * vy > 400) { aimAngle = Math.atan2(vy, vx); }
}

function faceNose() {
    actor.transform.rotation = aimAngle;
    if (!noseActor) { return; }
    noseActor.transform.x = actor.transform.x + Math.cos(aimAngle) * 15;
    noseActor.transform.y = actor.transform.y + Math.sin(aimAngle) * 15;
    noseActor.transform.rotation = aimAngle;
}

function wantsFire() {
    if (Input.isMouseHeld(0)) { return 1; }
    if (Input.isKeyHeld("Enter")) { return 1; }
    return 0;
}

// ===========================================================================
// Firing
// ===========================================================================

function fire() {
    var cd = PILOT_CD[pilot] * sCd;
    if (abilityActive > 0 && pilot === 0) { cd *= 0.5; }     // BINK overclock
    shotTimer = cd;
    shotCount++;

    // TWIN LINK: every fifth shot is free and doubled.
    var twin = 0;
    if (sPellets >= 0 && hasMod(21) && shotCount % 5 === 0) { twin = 1; shotTimer = 0; }

    var dmgMul = sDmg * (twin ? 2 : 1);
    if (sAdrenaline > 0 && hp < hpMax * 0.4) { dmgMul *= 1 + 0.22 * sAdrenaline; }
    if (abilityActive > 0 && pilot === 7) { dmgMul *= 2; }   // HAWK mark

    if (pilot === 3) { cleave(dmgMul); return; }
    if (pilot === 4) { chain(dmgMul); return; }

    var pellets = PILOT_PELLETS[pilot] + sPellets;
    var spread  = PILOT_SPREAD[pilot] + sPellets * 0.03;
    var speed   = PILOT_SPD[pilot] * sProjSpd;
    var life    = PILOT_LIFE[pilot] * sProjSpd;
    var pierce  = PILOT_PIERCE[pilot] + sPierce + (abilityActive > 0 && pilot === 7 ? 3 : 0);
    var dmg     = PILOT_DMG[pilot] * dmgMul;

    // Projectiles leave from the ship's CENTRE, not from the end of the nose.
    //
    // A 17 px muzzle offset put the first frame of every shot past the collider
    // of anything standing on top of the pilot, so an enemy that closed to
    // contact could not be shot at all -- it sat on the ship, dealing contact
    // damage, immune to the gun pointed at it. It showed up first on the pilots
    // with a slow cadence, because the fast ones killed things before they
    // arrived, and it reads as "sometimes I cannot kill the thing on me".
    //
    // The muzzle flash still draws out at the nose, so it looks the same.
    for (var i = 0; i < pellets; i++) {
        var off = pellets === 1 ? 0 : (i / (pellets - 1) - 0.5) * spread * 2;
        off += (Math.random() - 0.5) * spread * 0.35;
        if (bullets) {
            bullets.call("fire",
                actor.transform.x,
                actor.transform.y,
                aimAngle + off, speed, dmg, 0, PILOT_RAD[pilot], life, pierce,
                effectCode());
        }
    }

    flash();
}

// One integer carrying every on-hit rider, because only primitives cross a
// script boundary reliably.
function effectCode() {
    var code = 0;
    if (sChill > 0)   { code += 1; }
    if (sBurn > 0)    { code += 2; }
    if (sDeadMan > 0) { code += 4; }
    if (pilot === 6)  { code += 1; }   // KELVIN chills by nature
    return code;
}

// DAVE: a melee arc rather than a projectile.
function cleave(dmgMul) {
    if (!swarm) { return; }
    var cx = actor.transform.x + Math.cos(aimAngle) * 40;
    var cy = actor.transform.y + Math.sin(aimAngle) * 40;
    swarm.call("damageCircle", cx, cy, 66, PILOT_DMG[pilot] * dmgMul, 1);
    spawnEffect(cx, cy, 66, 226, 90, 70, 0.14);
}

// SPARKS: a bolt to the nearest enemy, which arcs on to its neighbours.
function chain(dmgMul) {
    if (!swarm) { return; }
    var links = 3 + Math.floor(sPierce);
    swarm.call("chainFrom", actor.transform.x, actor.transform.y,
               PILOT_DMG[pilot] * dmgMul, links, 300);
    spawnEffect(actor.transform.x, actor.transform.y, 26, 190, 120, 255, 0.10);
}

function flash() {
    spawnEffect(actor.transform.x + Math.cos(aimAngle) * 22,
                actor.transform.y + Math.sin(aimAngle) * 22,
                14, 255, 240, 190, 0.055);
}

// A short-lived bright square. Depth is above the dark on purpose: muzzle
// flash and explosions are the only light in a late wave.
function spawnEffect(x, y, size, r, g, b, life) {
    if (!director) { return; }
    director.call("spawnEffect", x, y, size, r, g, b, life);
}

// ===========================================================================
// Abilities
// ===========================================================================

function useAbility() {
    if (abilityTimer > 0) { return; }
    abilityTimer = PILOT_ACD[pilot] * sAbilityCdr;

    var x = actor.transform.x, y = actor.transform.y;

    if (pilot === 0) {                       // BINK -- OVERCLOCK
        abilityActive = 4.0;
        log("OVERCLOCK");
    } else if (pilot === 1) {                // BLAZE -- BACKBLAST
        if (swarm) { swarm.call("damageCircle", x, y, 150, 34 * sDmg, 1); }
        if (swarm) { swarm.call("knockCircle", x, y, 190, 520); }
        spawnEffect(x, y, 150, 255, 150, 60, 0.20);
        log("BACKBLAST");
    } else if (pilot === 2) {                // AMBER -- BEACON WARP + HEAL
        heal(26);
        if (swarm) { swarm.call("slowCircle", x, y, 200, 2.2); }
        spawnEffect(x, y, 200, 255, 200, 70, 0.22);
        log("BEACON");
    } else if (pilot === 3) {                // DAVE -- SLAM
        if (swarm) { swarm.call("damageCircle", x, y, 176, 40 * sDmg, 1); }
        if (swarm) { swarm.call("stunCircle", x, y, 176, 1.6); }
        spawnEffect(x, y, 176, 226, 78, 70, 0.24);
        log("SLAM");
    } else if (pilot === 4) {                // SPARKS -- PYLON
        if (director) { director.call("spawnPylon", x, y, 12, 10 * sDmg); }
        log("PYLON");
    } else if (pilot === 5) {                // RIGG -- TURRET
        if (director) { director.call("spawnTurret", x, y, 14, 7 * sDmg); }
        log("TURRET");
    } else if (pilot === 6) {                // KELVIN -- CRYO BURST
        if (swarm) { swarm.call("slowCircle", x, y, 250, 3.4); }
        if (swarm) { swarm.call("damageCircle", x, y, 250, 14 * sDmg, 1); }
        spawnEffect(x, y, 250, 120, 210, 255, 0.26);
        log("CRYO BURST");
    } else {                                 // HAWK -- MARK
        abilityActive = 3.0;
        log("MARK");
    }
}

// ===========================================================================
// Consumables -- carry three, use the oldest with F
// ===========================================================================

function giveConsumable(id) {
    if (consumables.length >= 3) { return 0; }
    consumables.push(id);
    return 1;
}

function useConsumable() {
    if (consumables.length === 0) { return 0; }
    var id = consumables.shift();
    var x = actor.transform.x, y = actor.transform.y;

    if (id === 0) {                                  // REPAIR
        heal(Math.floor(hpMax * 0.42));
        log("REPAIR");
    } else if (id === 1) {                           // OVERSHIELD
        shield = shieldMax;
        log("OVERSHIELD");
    } else if (id === 2) {                           // FRENZY
        abilityActive = Math.max(abilityActive, 5.0);
        log("FRENZY");
    } else if (id === 3) {                           // STASIS
        if (swarm) { swarm.call("stunCircle", x, y, 900, 2.6); }
        log("STASIS");
    } else {                                         // BOMB
        if (swarm) { swarm.call("damageCircle", x, y, 420, 120 * sDmg, 1); }
        spawnEffect(x, y, 420, 255, 210, 120, 0.30);
        log("BOMB");
    }
    return 1;
}

function consumableCount() { return consumables.length; }

// ===========================================================================
// Damage
// ===========================================================================

function hurt(amount) {
    if (!alive || iFrames > 0) { return 0; }

    var dmg = Number(amount) || 0;

    if (shield > 0) {
        var absorbed = Math.min(shield, dmg);
        shield -= absorbed;
        dmg -= absorbed;
    }

    if (dmg > 0) {
        hp -= dmg;
        iFrames = hitIFrames;
        if (sReactive > 0) { shield = Math.min(shieldMax, shield + 12 * sReactive); }
        if (director) { director.call("onPlayerHit"); }
    }

    if (hp <= 0) { hp = 0; die(); }
    return 1;
}

function heal(amount) {
    hp = Math.min(hpMax, hp + (Number(amount) || 0));
    return hp;
}

// Called by Bullets when a player projectile kills something, for VAMPIRE.
function onDamageDealt(amount) {
    if (sLifesteal > 0) { heal((Number(amount) || 0) * 0.02 * sLifesteal); }
    return 1;
}

function onKill() {
    if (sCoreTap > 0) {
        killsForCoreTap++;
        if (killsForCoreTap >= 12) {
            killsForCoreTap = 0;
            if (director) { director.call("addCores", sCoreTap); }
        }
    }
    return 1;
}

function die() {
    if (!alive) { return; }
    alive = 0;
    log("PILOT DOWN");
    if (director) { director.call("notePlayerDead"); }
}

// ===========================================================================
// Orbital blades -- the mod that used to work and look dead.
// They are drawn from the same angle the damage is dealt at, so what you see
// is what hits.
// ===========================================================================

function spinBlades(dt) {
    bladeAngle += dt * 2.6;

    while (bladeActors.length < sBlades) {
        var b = Scene.createActor("Blade", actor.transform.x, actor.transform.y);
        if (!b) { break; }
        b.tag = "Hud";
        Scene.addComponent(b, "SpriteRenderer", {
            Tint: { R: 210, G: 240, B: 255, A: 235 },
            Size: [16, 6],
            LayerDepth: depthBlade
        });
        bladeActors.push(b);
    }
    while (bladeActors.length > sBlades) {
        var gone = bladeActors.pop();
        if (gone) { gone.destroy(); }
    }

    for (var i = 0; i < bladeActors.length; i++) {
        var a = bladeAngle + (i / bladeActors.length) * Math.PI * 2;
        var bx = actor.transform.x + Math.cos(a) * 56;
        var by = actor.transform.y + Math.sin(a) * 56;
        bladeActors[i].transform.x = bx;
        bladeActors[i].transform.y = by;
        bladeActors[i].transform.rotation = a + Math.PI / 2;
        if (swarm) { swarm.call("damageCircle", bx, by, 18, 26 * sDmg * dt, 0); }
    }
}

// ===========================================================================
// Mods
// ===========================================================================

function addMod(id) {
    mods.push(Number(id) | 0);       // duplicates kept: stacking IS the design
    computeStats();
    return mods.length;
}

function hasMod(id) {
    for (var i = 0; i < mods.length; i++) { if (mods[i] === id) { return 1; } }
    return 0;
}

function countMod(id) {
    var n = 0;
    for (var i = 0; i < mods.length; i++) { if (mods[i] === id) { n++; } }
    return n;
}

function modCount() { return mods.length; }

// Every stat is derived here, from the mod list, every time it changes. Nothing
// mutates a stat anywhere else, so a stack is always exactly N applications.
function computeStats() {
    sDmg = 1; sCd = 1; sSpeed = 1; sProjSpd = 1; sPierce = 0; sPellets = 0;
    sLifesteal = 0; sRegen = 0; sAbilityCdr = 1; sCoreBonus = 1;
    sBlades = 0; sShockwave = 0; sChill = 0; sBurn = 0; sDeadMan = 0;
    sKineticDash = 0; sReactive = 0; sAdrenaline = 0; sCoreTap = 0; sMagnet = 0;

    var bonusHp = 0;

    for (var i = 0; i < mods.length; i++) {
        var m = mods[i];
        if      (m === 0)  { sCd *= 0.92; }
        else if (m === 1)  { sDmg *= 1.12; }
        else if (m === 2)  { sSpeed *= 1.09; }
        else if (m === 3)  { bonusHp += 18; }
        else if (m === 4)  { sPellets += 1; }
        else if (m === 5)  { sProjSpd *= 1.18; }
        else if (m === 6)  { sPierce += 1; }
        else if (m === 7)  { sLifesteal += 1; }
        else if (m === 8)  { sBlades += 1; }
        else if (m === 9)  { sKineticDash += 1; }
        else if (m === 10) { sCoreBonus *= 1.25; }
        else if (m === 11) { sMagnet += 1; }
        else if (m === 12) { sAdrenaline += 1; }
        else if (m === 13) { sCoreBonus *= 1.05; }
        else if (m === 14) { sShockwave += 1; }
        else if (m === 15) { sChill += 1; }
        else if (m === 16) { sBurn += 1; }
        else if (m === 17) { sReactive += 1; }
        else if (m === 18) { sRegen += 0.6; }
        else if (m === 19) { sDmg *= 1.30; bonusHp -= 12; }
        else if (m === 20) { sAbilityCdr *= 0.88; }
        else if (m === 22) { sDeadMan += 1; }
        else if (m === 23) { sCoreTap += 1; }
    }

    var newMax = Math.max(20, PILOT_HP[pilot] + bonusHp);
    if (newMax > hpMax) { hp += newMax - hpMax; }
    hpMax = newMax;
    if (hp > hpMax) { hp = hpMax; }

    if (sCd < 0.25) { sCd = 0.25; }      // a floor, so cooldown stacking cannot reach zero
}

// ===========================================================================
// Pilot selection -- the hangar
// ===========================================================================

function setPilot(index) {
    pilot = Math.max(0, Math.min(7, Number(index) | 0));
    hpMax = PILOT_HP[pilot];
    hp = hpMax;
    computeStats();
    if (hullSprite) {
        hullSprite.tint = { R: PILOT_R[pilot], G: PILOT_G[pilot], B: PILOT_B[pilot], A: 255 };
        hullSprite.size = { x: 30, y: 20 };
        hullSprite.layerDepth = depthHull;
    }
    return pilot;
}

function getPilot()     { return pilot; }
function getPilotName() { return PILOT_NAME[pilot]; }

// Full reset between runs, without reloading the scene.
function resetRun() {
    mods = [];
    consumables = [];
    shotCount = 0;
    shield = 0;
    alive = 1;
    vx = 0; vy = 0;
    abilityTimer = 0; abilityActive = 0;
    dashTimer = 0; dashCdTimer = 0; iFrames = 0;
    killsForCoreTap = 0;
    setPilot(pilot);
    actor.transform.x = 0;
    actor.transform.y = 0;
    return 1;
}

// ===========================================================================
// Readouts -- FloatingBars and the Director read these. Numbers only.
// ===========================================================================

function getHealth01()    { return hpMax > 0 ? hp / hpMax : 0; }
function getShield01()    { return shieldMax > 0 ? shield / shieldMax : 0; }
function getAbility01()   { var cd = PILOT_ACD[pilot] * sAbilityCdr; return cd > 0 ? 1 - abilityTimer / cd : 1; }
function getDash01()      { return dashCooldown > 0 ? 1 - dashCdTimer / dashCooldown : 1; }
function getConsumable01(){ return consumables.length / 3; }
function isAlive()        { return alive; }
function getHp()          { return hp; }
function getShockwave()   { return sShockwave; }
function getCoreBonus()   { return sCoreBonus; }
function getMagnet()      { return sMagnet; }
function getDamageMul()   { return sDmg; }

// Forwarded from the Director so one FloatingBars can show the whole HUD: the
// cookie reads every bar off a single target script, and the pilot is the
// actor the bars follow.
function getMult01()      { return director ? director.call("getMult01") : 0; }
function getCores01()     { return director ? director.call("getCores01") : 0; }
