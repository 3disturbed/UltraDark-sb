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
// Tuning -- movement, from UltraDark's PLAYER block
//
// THREE hit points. A bullet does one damage, a Brute takes six, a contact
// costs a third of your health, and a full second of invulnerability follows.
// That small-integer scale is the game; a hundred-point health bar is a
// different game that happens to look similar.
// ===========================================================================
var baseSpeed     = 300;    // PLAYER.SPEED
var accel         = 2600;   // PLAYER.ACCEL, units/s^2 -- up to speed in <80ms
var friction      = 3400;   // PLAYER.FRICTION
var baseHpMax     = 3;      // PLAYER.MAX_HP

var dashSpeed     = 900;    // PLAYER.DASH_SPEED
var dashTime      = 0.16;   // PLAYER.DASH_TIME
var dashCooldown  = 2.0;    // PLAYER.DASH_CD
var dashIFrames   = 0.28;   // PLAYER.DASH_IFRAMES
var hitIFrames    = 1.0;    // PLAYER.HIT_IFRAMES

var baseFireCd    = 0.14;   // PLAYER.FIRE_CD
var bulletSpeed   = 720;    // PLAYER.BULLET_SPEED
var bulletLife    = 1.4;    // PLAYER.BULLET_LIFE
var bulletDmg     = 1;      // PLAYER.BULLET_DMG

var startBombs    = 1;      // PLAYER.START_BOMBS
var maxBombs      = 3;      // PLAYER.MAX_BOMBS

// ===========================================================================
// Tuning -- aiming
//
// Mouse aim is exact now that the contract has a viewport. Auto-aim stays the
// default because it is what makes the game playable on a pad and a phone, and
// TAB switches. Auto also refuses to target a phased Ghost, which a mouse
// cannot know.
// ===========================================================================
var aimMode       = 0;
var aimRange      = 900;

// ===========================================================================
// Tuning -- the eight pilots, from UltraDark's PILOTS
//
// name / colour / symbol / speed multiplier / bonus max hp / ability cooldown,
// then the weapon: its own cadence owns fire rate, the baseline is only the
// blaster's.
// ===========================================================================
var PILOT_NAME  = ["BINK", "BLAZE", "AMBER", "DAVE", "SPARKS", "RIGG", "KELVIN", "HAWK"];
var PILOT_LEAN  = ["fast, skirmisher", "close range", "support, healer", "slow, tank, melee",
                   "chain lightning", "engineer, turrets", "control, chill", "sniper, railgun"];
var PILOT_ABIL  = ["BLINK VOLLEY", "FLAME ZONE", "BEACON WARP", "GRAVITY WELL",
                   "TESLA PYLON", "AUTO-TURRET", "FROST NOVA", "TRIPLE RAIL"];
var PILOT_SYM   = ["*", "^", "O", "@", "/", "T", "*", "+"];

var PILOT_R     = [57,  255, 184, 194, 255, 255, 143, 255];
var PILOT_G     = [240, 122, 255, 107, 228, 158, 216, 91 ];
var PILOT_B     = [255, 61,  94,  250, 91,  44,  255, 142];

var PILOT_SPEEDM= [1.18, 1.0,  1.0,  0.78, 1.0,  0.92, 1.0,  0.95];
var PILOT_BONUSHP=[0,    0,    0,    2,    0,    0,    0,    0   ];
var PILOT_ACD   = [12,   12,   8,    12,   14,   16,   11,   9   ];

// Weapon kinds: 0 smg, 1 shotgun, 2 blaster, 3 cleave, 4 arc, 5 lance, 6 rail
var W_SMG = 0, W_SHOTGUN = 1, W_BLASTER = 2, W_CLEAVE = 3, W_ARC = 4, W_LANCE = 5, W_RAIL = 6;
var PILOT_WKIND = [W_SMG, W_SHOTGUN, W_BLASTER, W_CLEAVE, W_ARC, W_BLASTER, W_LANCE, W_RAIL];
var PILOT_WNAME = ["SMG", "SCATTERGUN", "BLASTER", "CLEAVER", "ARC GUN", "BLASTER", "CHILL LANCE", "RAILGUN"];

var PILOT_CD    = [0.09, 0.65, 0.14, 0.5,  0.22, 0.16, 0.30, 0.9 ];
var PILOT_DMG   = [0.6,  0.8,  1,    3,    0.9,  1.1,  1.3,  4   ];
var PILOT_PELLETS=[1,    7,    1,    1,    1,    1,    1,    1   ];
var PILOT_SPREAD= [0.07, 0.38, 0,    0,    0,    0,    0,    0   ];   // BINK's is jitter
var PILOT_LIFE  = [1.4,  0.4,  1.4,  0,    0,    1.4,  1.4,  1.4 ];
var PILOT_SPDMUL= [1,    1,    1,    0,    0,    1,    1,    3   ];   // HAWK's rail is x3
var PILOT_PIERCE= [0,    0,    0,    0,    0,    0,    0,    3   ];

// DAVE's cleave, SPARKS' arc, KELVIN's chill -- the numbers their kinds need.
var CLEAVE_R = 95, CLEAVE_HALF = 1.05, CLEAVE_KNOCK = 60;
var ARC_CHAIN = 2, ARC_CHAIN_R = 140, ARC_CHAIN_DMG = 0.5;
var CHILL_SECONDS = 1.2;

// AMBER's aura, RIGG's turret, SPARKS' pylon, the orbital blades.
var AURA_R = 140, AURA_SELF = 10, AURA_ALLY = 5;
var TURRET_TTL = 8, TURRET_CD = 0.25, TURRET_DMG = 0.6, TURRET_RANGE = 700;
var PYLON_R = 200, PYLON_TTL = 8, PYLON_CD = 0.5, PYLON_DMG = 2;
var ORBITAL_R = 60, ORBITAL_BLADE = 14, ORBITAL_ROT = 4, ORBITAL_DPS = 4;

// ===========================================================================
// Tuning -- draw order (higher is nearer on both engines)
// ===========================================================================
var depthHull  = 0.50;
var depthNose  = 0.52;
var depthBlade = 0.53;
// No hull glow. A pale square one layer above the overlay was tried, to keep the
// patch around the ship readable once the dark is at full strength -- and on a
// native frame it dominated the screen instead of lifting it. A square is the
// wrong shape for a light, and the contract has no way to make a round one: a
// radial falloff needs a texture. The dark stays legible because everything that
// matters in it is drawn ABOVE it -- muzzle flash, explosions, the boss's rings
// and the HUD -- which is the whole reason for the depth ladder.

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

var consumables = [];       // up to 3 ids

// Derived stats, recomputed by computeStats() whenever mods change.
var sDmg = 1, sCd = 1, sSpeed = 1, sProjSpd = 1, sPierce = 0, sPellets = 0;
var sLifesteal = 0, sRegen = 0, sAbilityCdr = 1, sCoreBonus = 1;
var sBlades = 0, sShockwave = 0, sChill = 0, sBurn = 0, sDeadMan = 0;
var sKineticDash = 0, sReactive = 0, sAdrenaline = 0, sCoreTap = 0, sMagnet = 0;
var sTwinLink = 0;

var bladeActors = [];
var bladeAngle = 0;

var noseActor = null;
var hullSprite = null;

var mouseIdle = 99;
var lastMouseX = 0, lastMouseY = 0;

var regenCarry = 0;
var bombs = 1;
var frenzy = 0;
var killsForCoreTap = 0;

var bullets = null, swarm = null, director = null, camera = null, fx = null;
var upgrades = null;

var burnTint = 0;

// ===========================================================================
// Lifecycle
// ===========================================================================

function onStart() {
    resolveManagers();
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
    if (!fx)       { var e = Scene.findFirstByTag("Effects"); if (e) { fx = e.getComponent("ScriptComponent"); } }
    if (!upgrades) {
        var u = Scene.findFirstByTag("Upgrades");
        if (u) { upgrades = u.getComponent("ScriptComponent"); computeStats(); }
    }
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
        if (Input.isKeyPressed("Q")) { useBomb(); }
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
    if (frenzy > 0)        { frenzy -= dt; }
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
        // PLAYER.ACCEL / PLAYER.FRICTION are units per second squared, not a
        // lerp factor: 2600 gets you to full speed in under 80ms, and 3400
        // stops you faster than that. The ship is meant to feel immediate.
        var target = baseSpeed * PILOT_SPEEDM[pilot] * sSpeed;
        var tx = ix * target, ty = iy * target;
        var rate = (ix === 0 && iy === 0) ? friction : accel;

        var ddx = tx - vx, ddy = ty - vy;
        var d = Math.sqrt(ddx * ddx + ddy * ddy);
        var step = rate * dt;
        if (d <= step || d < 0.0001) { vx = tx; vy = ty; }
        else { vx += (ddx / d) * step; vy += (ddy / d) * step; }
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
    var hw = director.call("getArenaHalfW") - 26;
    var hh = director.call("getArenaHalfH") - 26;
    if (actor.transform.x < -hw) { actor.transform.x = -hw; if (vx < 0) { vx = 0; } }
    if (actor.transform.x >  hw) { actor.transform.x =  hw; if (vx > 0) { vx = 0; } }
    if (actor.transform.y < -hh) { actor.transform.y = -hh; if (vy < 0) { vy = 0; } }
    if (actor.transform.y >  hh) { actor.transform.y =  hh; if (vy > 0) { vy = 0; } }
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
        // Screen to world: the camera sits at the middle of the viewport, and a
        // pixel is 1/zoom world units away from it.
        var zoom = cameraZoom();
        var wx = camera.transform.x + (mx - UI.width / 2) / zoom;
        var wy = camera.transform.y + (my - UI.height / 2) / zoom;
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

function cameraZoom() {
    if (!camera) { return 1; }
    var cam = camera.getComponent("Camera2D");
    var z = cam ? Number(cam.zoom) : 1;
    return (z > 0) ? z : 1;
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
    if (frenzy > 0) { cd *= 0.5; }                           // FRENZY CORE
    if (abilityActive > 0 && pilot === 0) { cd *= 0.5; }     // BINK's blink window
    shotTimer = cd;
    shotCount++;

    var dmgMul = sDmg;
    if (sAdrenaline > 0 && hp <= 1) { dmgMul *= 1 + 0.22 * sAdrenaline; }

    var kind = PILOT_WKIND[pilot];
    if (kind === W_CLEAVE) { cleave(dmgMul); return; }
    if (kind === W_ARC)    { arc(dmgMul); return; }

    var pellets = PILOT_PELLETS[pilot] + sPellets;
    var spread  = PILOT_SPREAD[pilot];
    var speed   = bulletSpeed * PILOT_SPDMUL[pilot] * sProjSpd;
    var life    = PILOT_LIFE[pilot] * sProjSpd;
    var pierce  = PILOT_PIERCE[pilot] + sPierce;
    var dmg     = PILOT_DMG[pilot] * dmgMul;

    // KELVIN's lance chills whatever it touches; that is his whole identity.
    var code = effectCode();
    if (kind === W_LANCE) { code = code | 1; }

    for (var i = 0; i < pellets; i++) {
        var off = 0;
        if (pellets > 1) {
            // A scattergun spreads its pellets across the cone; an SMG jitters
            // each shot instead, which reads as spray rather than as a spread.
            off = (i / (pellets - 1) - 0.5) * spread * 2;
        }
        if (kind === W_SMG) { off += (Math.random() - 0.5) * spread * 2; }
        else if (pellets > 1) { off += (Math.random() - 0.5) * spread * 0.2; }

        if (bullets) {
            // From the ship's CENTRE: a muzzle offset puts the first frame of a
            // shot past the collider of anything standing on you.
            bullets.call("fire", actor.transform.x, actor.transform.y,
                         aimAngle + off, speed, dmg, 0, 5, life, pierce, code);
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

// DAVE's CLEAVER: an arc in front, with knockback. No projectile at all.
function cleave(dmgMul) {
    if (!swarm) { return; }
    var cx = actor.transform.x + Math.cos(aimAngle) * CLEAVE_R * 0.5;
    var cy = actor.transform.y + Math.sin(aimAngle) * CLEAVE_R * 0.5;
    swarm.call("damageCircle", cx, cy, CLEAVE_R, PILOT_DMG[pilot] * dmgMul, 1);
    swarm.call("knockCircle", cx, cy, CLEAVE_R, CLEAVE_KNOCK * 4);
    spawnEffect(cx, cy, CLEAVE_R * 1.6, 194, 107, 250, 0.12);
}

// SPARKS' ARC GUN: hits the nearest, then hops. Each hop is weaker.
function arc(dmgMul) {
    if (!swarm) { return; }
    var links = 1 + ARC_CHAIN + Math.floor(sPierce);
    swarm.call("chainFrom", actor.transform.x, actor.transform.y,
               PILOT_DMG[pilot] * dmgMul, links, ARC_CHAIN_R);
    spawnEffect(actor.transform.x, actor.transform.y, 26, 255, 228, 91, 0.09);
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

// UltraDark's eight abilities, by their own names.
function useAbility() {
    if (abilityTimer > 0) { return; }
    abilityTimer = PILOT_ACD[pilot] * sAbilityCdr;

    var x = actor.transform.x, y = actor.transform.y;

    if (pilot === 0) {
        // BLINK VOLLEY -- jump forward and spray on arrival.
        var bx = x + Math.cos(aimAngle) * 260;
        var by = y + Math.sin(aimAngle) * 260;
        spawnEffect(x, y, 60, 57, 240, 255, 0.16);
        actor.transform.x = bx;
        actor.transform.y = by;
        clampToArena();
        iFrames = Math.max(iFrames, 0.2);
        for (var i = 0; i < 12; i++) {
            if (bullets) {
                bullets.call("fire", actor.transform.x, actor.transform.y,
                             (i / 12) * Math.PI * 2, bulletSpeed, PILOT_DMG[0] * sDmg,
                             0, 5, bulletLife, sPierce, effectCode());
            }
        }
        abilityActive = 2.0;
    } else if (pilot === 1) {
        // FLAME ZONE -- a patch of ground that burns.
        if (director) { director.call("spawnFlame", x, y, 150, 6, 2 * sDmg); }
    } else if (pilot === 2) {
        // BEACON WARP -- heal, and slow what is on you.
        heal(1);
        if (swarm) { swarm.call("slowCircle", x, y, AURA_R, 2.2); }
        spawnEffect(x, y, AURA_R * 2, 184, 255, 94, 0.22);
    } else if (pilot === 3) {
        // GRAVITY WELL -- pull them in, then crush.
        if (swarm) { swarm.call("knockCircle", x, y, 260, -420); }
        if (swarm) { swarm.call("damageCircle", x, y, 200, 4 * sDmg, 1); }
        spawnEffect(x, y, 400, 194, 107, 250, 0.26);
    } else if (pilot === 4) {
        // TESLA PYLON -- a placed thing that zaps.
        if (director) { director.call("spawnPylon", x, y, PYLON_TTL, PYLON_DMG * sDmg); }
    } else if (pilot === 5) {
        // AUTO-TURRET -- a placed thing that shoots.
        if (director) { director.call("spawnTurret", x, y, TURRET_TTL, TURRET_DMG * sDmg); }
    } else if (pilot === 6) {
        // FROST NOVA -- everything near you stops.
        if (swarm) { swarm.call("slowCircle", x, y, 250, 3.4); }
        if (swarm) { swarm.call("damageCircle", x, y, 250, 1 * sDmg, 1); }
        spawnEffect(x, y, 500, 143, 216, 255, 0.26);
    } else {
        // TRIPLE RAIL -- three piercing rails at once.
        for (var r = -1; r <= 1; r++) {
            if (bullets) {
                bullets.call("fire", x, y, aimAngle + r * 0.10,
                             bulletSpeed * PILOT_SPDMUL[7], PILOT_DMG[7] * sDmg,
                             0, 6, bulletLife, PILOT_PIERCE[7] + sPierce, effectCode());
            }
        }
    }

    log(PILOT_ABIL[pilot]);
}

// ===========================================================================
// Consumables -- carry three, use the oldest with F
// ===========================================================================

function giveConsumable(id) {
    if (consumables.length >= 3) { return 0; }
    consumables.push(id);
    return 1;
}

// The original's five. Numbers are small because everything here is.
function useConsumable() {
    if (consumables.length === 0) { return 0; }
    var id = consumables.shift();
    var x = actor.transform.x, y = actor.transform.y;

    if (id === 0) {                                  // REPAIR KIT -- restore 1 HP
        heal(1);
        log("REPAIR KIT");
    } else if (id === 1) {                           // OVERSHIELD -- 3s invulnerable
        iFrames = Math.max(iFrames, 3.0);
        log("OVERSHIELD");
    } else if (id === 2) {                           // FRENZY CORE -- double fire rate, 6s
        abilityActive = Math.max(abilityActive, 6.0);
        frenzy = 6.0;
        log("FRENZY CORE");
    } else if (id === 3) {                           // STASIS CHARGE -- enemies slowed, 5s
        if (swarm) { swarm.call("slowCircle", x, y, 4000, 5.0); }
        log("STASIS CHARGE");
    } else {                                         // BOMB CELL -- +1 smart bomb
        bombs = Math.min(maxBombs, bombs + 1);
        log("BOMB CELL");
    }
    return 1;
}

// The smart bomb itself: clears the screen, and you only have three.
function useBomb() {
    if (bombs <= 0) { return 0; }
    bombs--;
    var x = actor.transform.x, y = actor.transform.y;
    if (swarm) { swarm.call("damageCircle", x, y, 4000, 6 * sDmg, 1); }
    if (bullets) { bullets.call("clearAll"); }
    spawnEffect(x, y, 900, 255, 240, 190, 0.4);
    if (fx) { fx.call("impact", 24); }
    log("SMART BOMB");
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

        // Scaled by what it cost: a scratch is a nudge, a big hit is a jolt, and
        // dropping below a quarter health flashes red rather than white.
        if (fx) {
            fx.call("shake", Math.min(18, 4 + dmg * 0.4));
            fx.call("flash", hp < hpMax * 0.25 ? "#ff3020" : "#ffffff", 0.09);
        }
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
    if (fx) { fx.call("impact", 26); }
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
    if (!upgrades) { return 0; }
    upgrades.call("add", id);        // duplicates kept: stacking IS the design
    computeStats();
    return upgrades.call("total");
}

function hasMod(id)   { return upgrades ? upgrades.call("has", id) : 0; }
function countMod(id) { return upgrades ? upgrades.call("count", id) : 0; }
function modCount()   { return upgrades ? upgrades.call("total") : 0; }

// Every stat is read from the `stacking-upgrades` cookie, which recomputes them
// from the list of taken mods whenever it changes. Nothing is accumulated here,
// so a stack is always exactly N applications and nothing can drift.
// A cross-script call to a script that has not initialised yet returns
// undefined, and script start order between two actors in the same scene is not
// something to rely on. Without a default here the pilot's very first
// computeStats -- which runs the moment the Upgrades actor is *found*, not the
// moment it is ready -- writes undefined into every stat and leaves it there
// until the next mod is taken.
function num(value, fallback) {
    var v = Number(value);
    return (v === v) ? v : fallback;
}

function computeStats() {
    if (!upgrades) { return; }

    sDmg        = num(upgrades.call("mul",  "damage"),     1);
    sCd         = num(upgrades.call("mul",  "cooldown"),   1);
    sSpeed      = num(upgrades.call("mul",  "speed"),      1);
    sProjSpd    = num(upgrades.call("mul",  "projSpeed"),  1);
    sCoreBonus  = num(upgrades.call("mul",  "coreBonus"),  1);
    sAbilityCdr = num(upgrades.call("mul",  "abilityCdr"), 1);

    sPierce      = num(upgrades.call("stat", "pierce"),     0);
    sPellets     = num(upgrades.call("stat", "pellets"),    0);
    sLifesteal   = num(upgrades.call("stat", "lifesteal"),  0);
    sRegen       = num(upgrades.call("stat", "regen"),      0);
    sBlades      = num(upgrades.call("stat", "blades"),     0);
    sShockwave   = num(upgrades.call("stat", "shockwave"),  0);
    sChill       = num(upgrades.call("stat", "chill"),      0);
    sBurn        = num(upgrades.call("stat", "burn"),       0);
    sDeadMan     = num(upgrades.call("stat", "deadMan"),    0);
    sKineticDash = num(upgrades.call("stat", "kinetic"),    0);
    sReactive    = num(upgrades.call("stat", "reactive"),   0);
    sAdrenaline  = num(upgrades.call("stat", "adrenaline"), 0);
    sCoreTap     = num(upgrades.call("stat", "coreTap"),    0);
    sMagnet      = num(upgrades.call("stat", "magnet"),     0);
    sTwinLink    = num(upgrades.call("stat", "twinLink"),   0);

    // Max health is the one that has to be re-derived rather than read: gaining
    // it should also grant it, or PLATING is a bar that got longer and emptier.
    // Three, plus whatever the pilot and the build add. Plating is +1, not +18.
    var newMax = Math.max(1, baseHpMax + PILOT_BONUSHP[pilot] + num(upgrades.call("stat", "maxHp"), 0));
    if (newMax > hpMax) { hp += newMax - hpMax; }
    hpMax = newMax;
    if (hp > hpMax) { hp = hpMax; }
}

// ===========================================================================
// Pilot selection -- the hangar
// ===========================================================================

function setPilot(index) {
    pilot = Math.max(0, Math.min(7, Number(index) | 0));
    hpMax = baseHpMax + PILOT_BONUSHP[pilot];
    hp = hpMax;
    computeStats();
    if (hullSprite) {
        // PLAYER.RADIUS is 14, so the hull is 28 across, oblong so its facing
        // reads without a nose.
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
    if (upgrades) { upgrades.call("clearAll"); }
    consumables = [];
    shotCount = 0;
    shield = 0;
    alive = 1;
    vx = 0; vy = 0;
    abilityTimer = 0; abilityActive = 0;
    dashTimer = 0; dashCdTimer = 0; iFrames = 0;
    killsForCoreTap = 0;
    bombs = startBombs;
    frenzy = 0;
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
function getBombs01()     { return bombs / maxBombs; }
function getBombs()       { return bombs; }
function isAlive()        { return alive; }
function getHp()          { return hp; }
function getShockwave()   { return sShockwave; }
function getCoreBonus()   { return sCoreBonus; }
function getMagnet()      { return sMagnet; }
function getDamageMul()   { return sDmg; }

