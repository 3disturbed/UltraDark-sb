// Stage3D.js -- the game, in three dimensions, without touching the game.
// Attach to an empty actor and tag it "Stage3D".
//
// UltraDark is a top-down 2D game and stays one: the physics, the AI, the wave
// budget, the collision and every rule are untouched. This is a PRESENTATION
// layer over them, built on the one mapping a top-down game needs --
//
//     game x  ->  world X          game y  ->  world Z          height -> world Y
//
// -- so an actor keeps its 2D transform, which is what everything else reads,
// and gains a Transform3D that mirrors it.
//
// It works by adoption rather than by editing the twelve places that make a
// sprite. Every one of those already says what it is drawing -- a size, a tint,
// a name -- and a mirror can read all three. The alternative is the same four
// lines pasted into Director, Swarm, Pilot, ProjectilePool, PickupDrops, Boss
// and DayNightCycle, which is seven files to keep in step and seven chances to
// miss one; a pooled actor would need it in two places each.
//
// The pilot and the boss are the exception, and the reason this exists: they are
// MakeChibi characters, not boxes, so their sprite is switched off and a
// character follows them instead.

// ===========================================================================
// Tuning
// ===========================================================================
var enabled = 1;              // 0 leaves the game exactly as it was, in 2D

// The camera: above and behind the pilot, looking down at it.
var camHeight = 620;
var camBack   = 470;
var camPitch  = -54;          // degrees; negative looks down
var camFov    = 45;
var camNear   = 5;
var camFar    = 6000;
var camLag    = 8;            // how quickly it catches up, per second

// A chibi is about one unit tall and the arena is measured in pixels, so the
// character is scaled rather than the world -- one number here instead of a
// division at every position, velocity and radius in the game.
var chibiScale = 30;
var bossChibiScale = 62;

// How high off the floor each thing floats. A top-down game has no Y, so this is
// the whole of the third dimension: it is what stops the shots, the pickups and
// the enemies from z-fighting with the ground they are all standing on.
var yFloor   = 0;
var yGrid    = 0.5;
var yWall    = 26;
var yZone    = 1.5;
var yPickup  = 10;
var yEnemy   = 13;
var yTell    = 1.0;
var yDeploy  = 12;
var yShot    = 14;
var yFx      = 18;

var groundColour = "#2a3040";

// The dark. In 2D it is a huge sprite pinned over the player; in 3D a
// world-space quad over a perspective camera is the wrong shape entirely -- it
// would be a slab you can see the edge of, and it would dim the things nearest
// the camera hardest. The dark is a property of the VIEW, so in 3D it is a
// screen-space panel, which is also the only way it can cover a horizon.
var darkColour = "#05060a";
var darkMax = 0.86;           // alpha at full darkness

// ===========================================================================
// State
// ===========================================================================
var director = null, player = null, pilot = null;

var camera = null;
var ground = null;
var darkPanel = null;

// The adopted: parallel arrays, the same ledger shape the rest of the game uses,
// because this can be several hundred rows and a row per object would be several
// hundred objects to allocate and collect.
var aActor = [], aSprite = [], aT3d = [], aMesh = [], aKind = [], aY = [], aTint = [];
var aX = [], aZ = [], aW = [], aH = [], aRot = [];
var aN = 0;
var adopted = {};             // actor id -> 1, so a rescan is not a re-adopt

// Every tag the game draws under. A drawn actor with a tag not on this list is
// simply never adopted, and shows as a 2D sprite over the 3D world -- which is
// what the "nothing drawn stays a sprite" check is looking for.
var TAGS = ["Fx", "Enemy", "Shot", "Player", "Hud", "Boss", "Overlay"];

// The two characters.
var pilotChibi = null, pilotChibiClip = "";
var bossChibi = null, bossActor = null;

// Kinds, so the per-frame loop is a number test rather than a name test.
var K_FLAT = 0, K_BOX = 1, K_BALL = 2, K_HIDE = 3;

function onStart() {
    if (!enabled) { log("Stage3D: off"); return; }
    resolve();
    buildCamera();
    buildLights();
    buildGround();
    buildDark();
    log("Stage3D: on");
}

function resolve() {
    if (!director) { var d = Scene.findFirstByTag("Director"); if (d) { director = d.getComponent("ScriptComponent"); } }
    if (!player)   { player = Scene.findFirstByTag("Player"); if (player) { pilot = player.getComponent("ScriptComponent"); } }
}

// ===========================================================================
// The stage itself
// ===========================================================================

function buildCamera() {
    camera = Scene.createActor("Camera3D", 0, 0);
    if (!camera) { return; }

    // MainCamera3D exactly: the native Camera3D.Main accepts that tag and no
    // other, so a camera tagged anything else renders nothing and says nothing.
    camera.tag = "MainCamera3D";
    Scene.addComponent(camera, "Transform3D", {});
    Scene.addComponent(camera, "Camera3D", {
        FieldOfView: camFov, NearClip: camNear, FarClip: camFar
    });
    camera.transform3d.set(0, camHeight, camBack, 1, 1, 1);
    camera.transform3d.rotX = camPitch;
}

function buildLights() {
    // A key and a fill. The key is warm and comes over the player's shoulder so
    // the characters have a lit side and a shadow side; the fill is cold, weak
    // and opposite, so the shadow side is readable rather than black.
    var key = Scene.createActor("Sun", 0, 0);
    if (key) {
        Scene.addComponent(key, "Transform3D", {});
        Scene.addComponent(key, "Light3D", {
            Type: "Directional", Intensity: 0.85,
            Color: { R: 255, G: 246, B: 232, A: 255 }
        });
        key.transform3d.rotX = -52;
        key.transform3d.rotY = -28;
    }

    var fill = Scene.createActor("Fill", 0, 0);
    if (fill) {
        Scene.addComponent(fill, "Transform3D", {});
        Scene.addComponent(fill, "Light3D", {
            Type: "Directional", Intensity: 0.28,
            Color: { R: 150, G: 180, B: 255, A: 255 }
        });
        fill.transform3d.rotX = -12;
        fill.transform3d.rotY = 152;
    }

    var sky = Scene.createActor("Sky", 0, 0);
    if (sky) {
        Scene.addComponent(sky, "Transform3D", {});
        Scene.addComponent(sky, "SkyLight", {
            Intensity: 0.35,
            SkyColor: { R: 92, G: 104, B: 140, A: 255 },
            GroundColor: { R: 26, G: 28, B: 36, A: 255 }
        });
    }
}

function buildGround() {
    // One plane under the whole arena. The 2D floor sprite is adopted as well and
    // sits just above this; this is what a shot that leaves the arena flies over,
    // and what stops the horizon being empty space.
    ground = Scene.createActor("Ground3D", 0, 0);
    if (!ground) { return; }

    Scene.addComponent(ground, "Transform3D", {});
    Scene.addComponent(ground, "MeshRenderer", { MeshType: "Cube" });

    // Primitives are UNIT sized, so scale IS the size.
    var half = director ? num(director.call("getArenaHalfW"), 1024) : 1024;
    ground.transform3d.set(0, -1, 0, half * 3, 1, half * 3);
    var gm = ground.getComponent("MeshRenderer");
    if (gm) {
        gm.albedoColor = groundColour;
        gm.roughness = 0.96;
        gm.ignoreCulling = true;    // it is bigger than the frustum test is comfortable with
    }
}

function buildDark() {
    // Behind every other script's UI: order is what decides that now, rather than
    // whichever script happened to start first.
    UI.order = -50;
    UI.build({
        name: "stage", children: [
            { name: "dark", width: "*", height: "*", background: darkColour, opacity: 0 },
        ],
    });
    darkPanel = UI.find("dark");
}

// ===========================================================================
// Frame
// ===========================================================================

/**
 * All of it in onLateUpdate, not onUpdate.
 *
 * Presentation runs after the game has finished moving things, for the same
 * reason screen-effects does: a mirror that reads a position the game is about
 * to change is a frame behind. And it is the only place that sees everything --
 * physics, collisions and the deaths and effects they cause all happen after
 * onUpdate, so a scan there missed anything born in them and left a flat 2D
 * rectangle painted over the 3D world for a frame.
 */
function onLateUpdate(dt) {
    if (!enabled) { return; }
    resolve();

    scan();
    sync();
    followPilot(dt);
    followBoss();
    followCamera(dt);
    fade();
}

/** The dark, as a screen-space panel rather than a sprite over the world. */
function fade() {
    if (!darkPanel || !director) { return; }
    darkPanel.opacity = num(director.call("getDarkness"), 0) * darkMax;
}

/**
 * Finds anything drawn that this has not met yet.
 *
 * By TAG, and every frame. The game tags everything it draws -- seven tags cover
 * the lot -- which is seven scene walks rather than the nineteen a walk per
 * actor NAME would be, and cheap enough not to need an interval.
 *
 * It did have an interval, and the interval was visible: a pooled actor comes
 * back with its mesh, but a genuinely new one -- the enemy shot that grows the
 * pool mid-wave -- spent up to a tenth of a second as a flat 2D rectangle
 * painted over the 3D world before this met it. Nine frames of the wrong game.
 */
function scan() {
    for (var i = 0; i < TAGS.length; i++) {
        var list = Scene.findByTag(TAGS[i]);
        if (!list) { continue; }
        for (var j = 0; j < list.length; j++) { adopt(list[j], list[j].name); }
    }
}

/** Gives one actor a mesh, once. */
function adopt(a, name) {
    if (!a) { return; }
    var id = a.id;
    if (adopted[id]) { return; }
    adopted[id] = 1;

    var sprite = a.getComponent("SpriteRenderer");
    if (!sprite) { return; }

    var kind = kindOf(name);
    var y = heightOf(name);

    // The pilot, its nose, its blades and the boss are characters or nothing, so
    // their sprite is switched off and no mesh replaces it.
    if (kind === K_HIDE) {
        sprite.enabled = false;
        if (name === "Boss") { bossActor = a; }
        return;
    }

    // Off, not resized to nothing: the 2D pass draws over the 3D one, so a sprite
    // left on is a flat coloured rectangle sitting on top of the world it is
    // meant to have become.
    sprite.enabled = false;

    Scene.addComponent(a, "Transform3D", {});
    // MeshType only. A `Materials: [{...}]` property bag is accepted natively and
    // silently produces a BLACK material -- the whole world rendered as nothing on
    // a black background, with no warning, while the chibis beside it drew fine.
    // The renderer's own albedoColor/roughness/metallic are what MakeChibi's own
    // builder writes, and they work; they are also cheaper, because setting one is
    // a colour rather than a fresh array and object.
    Scene.addComponent(a, "MeshRenderer", { MeshType: kind === K_BALL ? "Sphere" : "Cube" });

    var t3d = a.transform3d;
    var mesh = a.getComponent("MeshRenderer");
    if (!t3d || !mesh) { return; }

    mesh.roughness = kind === K_FLAT ? 0.95 : 0.55;
    mesh.metallic = kind === K_FLAT ? 0.0 : 0.15;

    aActor[aN] = a; aSprite[aN] = sprite; aT3d[aN] = t3d;
    aMesh[aN] = mesh; aKind[aN] = kind; aY[aN] = y; aTint[aN] = ""; aX[aN] = NaN; aZ[aN] = NaN; aW[aN] = NaN; aH[aN] = NaN; aRot[aN] = NaN;
    aN++;
}

/**
 * Mirrors every adopted actor: where it is, how big, what colour.
 *
 * Backwards, and by swap, because an actor can be destroyed at any point in the
 * game's own update -- the same four rules the enemy ledger runs on.
 */
function sync() {
    for (var i = aN - 1; i >= 0; i--) {
        var a = aActor[i];
        var t3d = aT3d[i];

        // A destroyed actor still answers, so the check is the transform coming
        // back null rather than a flag nobody sets.
        if (!a || !t3d) { drop(i); continue; }

        var tr = a.transform;
        var sp = aSprite[i];
        if (!tr || !sp) { drop(i); continue; }

        var w = axis(sp.size, 0);
        var h = axis(sp.size, 1);
        if (!(w > 0)) { w = 1; }
        if (!(h > 0)) { h = 1; }

        var kind = aKind[i];
        // A flat thing is a slab: as wide and deep as the sprite was, and barely
        // tall. Anything else stands up, and takes its height from the smaller of
        // the two so a wide sprite does not become a wall.
        var tall = kind === K_FLAT ? 1.5 : Math.min(w, h) * 0.9;

        // Written only when it has actually moved or changed size. Most of what is
        // adopted is scenery -- a floor, a grid, four walls -- and a `set` of six
        // numbers plus a rotation that allocates a vector, for every adopted actor
        // every frame, is what put the 130-enemy ceiling over a 60fps frame.
        if (tr.x !== aX[i] || tr.y !== aZ[i] || w !== aW[i] || h !== aH[i]) {
            aX[i] = tr.x; aZ[i] = tr.y; aW[i] = w; aH[i] = h;
            t3d.set(tr.x, aY[i] + (kind === K_FLAT ? 0 : tall * 0.5), tr.y, w, tall, h);
        }

        if (tr.rotation !== aRot[i]) {
            aRot[i] = tr.rotation;
            t3d.rotY = -tr.rotation * 57.2957795;   // radians on the 2D side, degrees here
        }

        // Repaint only when the colour has actually changed. Writing a material
        // allocates an array and an object, and at the 130-enemy ceiling doing
        // that every frame for every adopted actor cost four milliseconds -- for
        // a floor, a wall and a grid line that have been one colour all game.
        // Compare before converting. A colour comes back as a hex string on one
        // engine and an object on the other; turning either into "#rrggbb" every
        // frame for every adopted actor is three allocations each, and at the
        // ceiling that alone put the frame over budget -- to repaint a floor that
        // has been one colour all game.
        var raw = sp.tint;
        var key = typeof raw === "string" ? raw : packColour(raw);
        if (key !== aTint[i]) {
            aTint[i] = key;
            var colour = readColour(raw);
            if (colour !== "") { aMesh[i].albedoColor = colour; }
        }
    }
}

/** Removes row `i`, by swapping the last row into it. */
function drop(i) {
    var last = aN - 1;
    aActor[i] = aActor[last]; aSprite[i] = aSprite[last]; aT3d[i] = aT3d[last];
    aMesh[i] = aMesh[last]; aKind[i] = aKind[last]; aY[i] = aY[last];
    aTint[i] = aTint[last]; aX[i] = aX[last]; aZ[i] = aZ[last];
    aW[i] = aW[last]; aH[i] = aH[last]; aRot[i] = aRot[last];
    aN--;
}

/**
 * One axis of a component's size, whatever shape that component hands back.
 *
 * A Vector2 read off a component crosses as an ARRAY in the browser and as an
 * object under Jint, and the object's keys are lower-cased there. Reading one
 * shape gets `undefined` on the other engine, which becomes NaN, which falls
 * through to a default -- so every mesh in the game was a one-unit cube eight
 * hundred units from the camera, and the world rendered as nothing at all.
 */
function axis(value, index) {
    if (!value) { return 0; }

    // Named axes FIRST. A Vector2 has a `length` -- its magnitude -- so testing
    // for one to spot an array matches every vector as well, and then reads
    // element 0 of a thing with no elements. That is a zero, which falls through
    // to the 1x1 default, which is a one-unit cube where a 2048-unit floor should
    // be. The engines disagree about the shape here, so this takes all of them.
    var named = index === 0 ? num(value.x, num(value.X, NaN)) : num(value.y, num(value.Y, NaN));
    if (named === named) { return named; }

    return num(value[index], 0);
}

/**
 * A component's colour as "#rrggbb", whatever shape it comes back as.
 *
 * Same divergence, worse consequence: the browser hands back a hex STRING and
 * Jint hands back `{r, g, b, a}` -- lower case, where a script WRITES `{R, G, B}`.
 * Reading `.R` natively gives undefined, and undefined painted through a hex
 * conversion is #000000, so every mesh was painted pure black on a black
 * background. The game rendered perfectly and was invisible.
 */
/** A colour object as one integer, for comparing without allocating. */
function packColour(value) {
    if (!value) { return 0; }
    var r = num(value.R, num(value.r, 0));
    var g = num(value.G, num(value.g, 0));
    var b = num(value.B, num(value.b, 0));
    return ((r | 0) << 16) | ((g | 0) << 8) | (b | 0);
}

function readColour(value) {
    if (!value) { return ""; }
    if (typeof value === "string") { return value.substring(0, 7); }

    var r = num(value.R, num(value.r, -1));
    var g = num(value.G, num(value.g, -1));
    var b = num(value.B, num(value.b, -1));
    if (r < 0 || g < 0 || b < 0) { return ""; }
    return "#" + hex(r) + hex(g) + hex(b);
}

function hex(n) {
    var v = Math.max(0, Math.min(255, Number(n) | 0));
    var s = v.toString(16);
    return s.length < 2 ? "0" + s : s;
}

// ===========================================================================
// The characters
// ===========================================================================

function followPilot(dt) {
    if (!player) { return; }

    if (!pilotChibi) {
        var index = pilot ? num(pilot.call("getPilot"), 0) : 0;
        pilotChibi = Chibi.random(4200 + index, player.transform.x, 0, player.transform.y);
        if (!pilotChibi) { return; }
        pilotChibi.transform3d.set(player.transform.x, 0, player.transform.y,
                                   chibiScale, chibiScale, chibiScale);

    }

    var tr = player.transform;
    var t3d = pilotChibi.transform3d;
    if (!t3d) { pilotChibi = null; return; }

    t3d.x = tr.x;
    t3d.z = tr.y;

    // It faces where it is aiming, which in a twin-stick game is not where it is
    // going -- the 2D rotation is the aim, so that is what the body turns to.
    t3d.rotY = -tr.rotation * 57.2957795 + 180;

    // The clip is the pilot's speed, so a walk cycle is a walk and a sprint is a
    // run rather than the same cycle played faster.
    var moving = pilot ? num(pilot.call("getSpeed01"), 0) : 0;
    var want = !alive() ? "die" : moving > 0.55 ? "run" : moving > 0.06 ? "walk" : "idle";
    if (want !== pilotChibiClip) { pilotChibiClip = want; Chibi.play(pilotChibi, want, 0.15); }
}

function alive() { return pilot ? num(pilot.call("isAlive"), 1) > 0 : 1; }

function followBoss() {
    var boss = Scene.findFirstByTag("Boss");

    if (!boss) {
        if (bossChibi) { bossChibi.destroy(); bossChibi = null; }
        return;
    }

    if (!bossChibi) {
        bossChibi = Chibi.random(9100, boss.transform.x, 0, boss.transform.y);
        if (!bossChibi) { return; }
        bossChibi.transform3d.set(boss.transform.x, 0, boss.transform.y,
                                  bossChibiScale, bossChibiScale, bossChibiScale);
        Chibi.play(bossChibi, "idle");
    }

    var t3d = bossChibi.transform3d;
    if (!t3d) { bossChibi = null; return; }
    t3d.x = boss.transform.x;
    t3d.z = boss.transform.y;
    t3d.rotY = t3d.rotY + 24 * Time.deltaTime;
}

// ===========================================================================
// The camera
// ===========================================================================

function followCamera(dt) {
    if (!camera || !player) { return; }
    var t3d = camera.transform3d;
    if (!t3d) { return; }

    var wantX = player.transform.x;
    var wantZ = player.transform.y + camBack;

    // Lagged, so the view is not welded to a ship that dashes 900 px/s: the
    // camera arrives a moment later, which is what makes a dash read as fast.
    var k = Math.min(1, camLag * dt);
    t3d.x = t3d.x + (wantX - t3d.x) * k;
    t3d.z = t3d.z + (wantZ - t3d.z) * k;
    t3d.y = camHeight;
}

// ===========================================================================
// Bits
// ===========================================================================

function kindOf(name) {
    if (name === "Player" || name === "PilotNose" || name === "Blade"
        || name === "Boss" || name === "BossBar" || name === "BossBarFill"
        || name === "NightOverlay") { return K_HIDE; }
    if (name === "Floor" || name === "Grid" || name === "Zone") { return K_FLAT; }
    if (name === "Shot" || name === "ShotEnemy" || name === "Pickup") { return K_BALL; }
    return K_BOX;
}

function heightOf(name) {
    if (name === "Floor") { return yFloor; }
    if (name === "Grid") { return yGrid; }
    if (name === "Wall") { return yWall; }
    if (name === "Zone") { return yZone; }
    if (name === "Pickup") { return yPickup; }
    if (name === "Tell") { return yTell; }
    if (name === "Pylon" || name === "Turret") { return yDeploy; }
    if (name === "Shot" || name === "ShotEnemy") { return yShot; }
    if (name === "Fx") { return yFx; }
    return yEnemy;
}

function num(value, fallback) {
    var v = Number(value);
    return v === v ? v : fallback;
}

// ===========================================================================
// Readouts, for the tests
// ===========================================================================
function isOn()          { return enabled ? 1 : 0; }
function adoptedCount()  { return aN; }
function hasPilotChibi() { return pilotChibi ? 1 : 0; }
function hasBossChibi()  { return bossChibi ? 1 : 0; }
function getClip()       { return pilotChibiClip; }
function getDark01()    { return darkPanel ? darkPanel.opacity : 0; }
