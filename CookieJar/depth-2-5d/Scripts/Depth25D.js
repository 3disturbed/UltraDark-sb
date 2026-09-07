// Depth25D.js -- turns a top-down plan into a place with a near side and a far side.
// Attach to a single manager actor and tag it "Depth".
//
// 2.5D is not a look you paint on. It is one rule about draw order:
//
//     everything that lies FLAT keeps a fixed depth band,
//     everything that STANDS UP is sorted by where its feet are.
//
// A thing further down the screen is nearer the camera and covers what is
// behind it. That single rule is the difference between a diagram and a room --
// it is what lets a player walk behind the far wall and in front of the near
// one, which no amount of art will do on its own.
//
// Height is the second half, and it is the oldest trick in top-down games: the
// FOOTPRINT is what you collide with, the LIFT is what you see, and the two are
// never the same rectangle. Physics never learns any of this happened.
//
//   var d = Scene.findFirstByTag("Depth").getComponent("ScriptComponent");
//   d.call("standing", "Wall", "Wall", x, y, 300, 16, 128, 120, 112, 32, true);

// ---------------------------------------------------------------------------
// Dials
// ---------------------------------------------------------------------------
var worldSize = 4096;   // the tallest world coordinate. MUST match your map.

// The flat bands, in the order they stack. Everything here is scenery lying on
// the floor and never sorts against anything that stands.
var D_GROUND  = 0.02;   // the base surface
var D_MARKING = 0.05;   // painted on it: lanes, lines, decals
var D_KERB    = 0.08;   // raised edges that are still floor
var D_FLOOR   = 0.11;   // interiors, water, anything you walk on
var D_SHADOW  = 0.17;   // cast shadows: under everything upright, over the floor

// The standing band. Keep the top below D_OVERLAY or a full-screen tint will
// find half your world sorting over the top of it.
var D_STAND      = 0.22;
var D_STAND_SPAN = 0.74;
var D_OVERLAY    = 0.985;  // weather, darkness, damage flashes -- above the lot

// Shading. The side you see is away from the light; the top catches it. The gap
// between these two numbers is what reads as height, so do not narrow it much.
var faceShade = 0.56;
var capShade  = 1.16;

// ---------------------------------------------------------------------------
// The rule
// ---------------------------------------------------------------------------

/**
 * The draw depth for something standing on the ground at world Y.
 *
 * Every upright thing in the world goes through here, so they all sort against
 * each other by one rule and none of them needs to know what the others are.
 */
function groundDepth(y) {
    var t = y / worldSize;
    if (t < 0) { t = 0; } else if (t > 1) { t = 1; }
    return D_STAND + t * D_STAND_SPAN;
}

/** The flat bands, for callers that build floors and shadows. */
function groundBand()  { return D_GROUND; }
function markingBand() { return D_MARKING; }
function kerbBand()    { return D_KERB; }
function floorBand()   { return D_FLOOR; }
function shadowBand()  { return D_SHADOW; }
function overlayBand() { return D_OVERLAY; }

// ---------------------------------------------------------------------------
// Building something with height
// ---------------------------------------------------------------------------

// Top faces, keyed by the actor id of the thing they sit on. A cap is scenery:
// no collider, no script, nothing on it that could notice its wall had gone.
var capOwner = [];
var capActor = [];

/**
 * A thing with height: a footprint you collide with, drawn as the side face you
 * can see plus the top face that catches the light.
 *
 * The actor stays at the FOOTPRINT centre, so its collider is exactly the
 * rectangle it always was. Only the sprite moves, and it moves by its PIVOT
 * rather than by its position: a pivot y of 1 hangs a sprite upward from the
 * actor, so one [w, h + height] sprite whose bottom edge sits on the
 * footprint's south edge is the entire extrusion, in one actor.
 *
 * Doing it by position instead drags the collider up with the picture, and you
 * walk straight through the wall you can see.
 *
 * @param height how far up the screen the top face is drawn from the footprint
 * @param cap    pass false for something too small to show a lit top
 * @returns the face actor -- the one that collides, and the one to destroy
 */
function standing(name, tag, x, y, w, h, r, g, b, height, solid, cap) {
    // Feet, not centre. What decides whether this covers you is where it meets
    // the ground, and that is its south edge.
    var depth = groundDepth(y + h / 2);

    var face = Scene.createActor(name, x, y);
    if (!face) { return null; }
    face.tag = tag;

    // Measured down from the sprite's top edge, the actor sits (height + h/2)
    // into a sprite that is (height + h) tall.
    Scene.addComponent(face, "SpriteRenderer", {
        Tint: { R: clampByte(r * faceShade), G: clampByte(g * faceShade),
                B: clampByte(b * faceShade), A: 255 },
        Size: [w, h + height],
        Pivot: [0.5, (height + h / 2) / (height + h)],
        LayerDepth: depth
    });

    if (solid) { Scene.addComponent(face, "BoxCollider2D", { Size: [w, h] }); }

    // The top face. Its own actor because one sprite is one colour, and the
    // two-tone edge where the lit top meets the shaded side is the entire reason
    // this reads as height rather than as a wall that got taller.
    if (cap !== false) {
        var top = Scene.createActor(name + "Top", x, y - height);
        if (top) {
            top.tag = "Ground";
            Scene.addComponent(top, "SpriteRenderer", {
                Tint: { R: clampByte(r * capShade), G: clampByte(g * capShade),
                        B: clampByte(b * capShade), A: 255 },
                Size: [w, h],
                LayerDepth: depth + 0.002
            });
            linkCap(face, top);
        }
    }
    return face;
}

/**
 * Records a top face against the standing thing it belongs to.
 *
 * Call it for anything you drew on top of a `standing` yourself -- a tree's
 * canopy is just a cap that happens to be wider than its trunk.
 */
function linkCap(face, top) {
    if (face && top) { capOwner.push(face.id); capActor.push(top); }
}

/**
 * Removes the top face belonging to something that is about to stop existing.
 *
 * ALWAYS call this before destroying a standing actor. Miss it and the roof
 * stays in the air over the hole, which is the first thing anyone notices and
 * the last thing any test will tell you about.
 */
function dropCap(id) {
    for (var i = 0; i < capOwner.length; i++) {
        if (capOwner[i] !== id) { continue; }
        if (capActor[i]) { Scene.destroyActor(capActor[i]); }
        capOwner.splice(i, 1);
        capActor.splice(i, 1);
        return true;
    }
    return false;
}

/** Destroys a standing thing and its top face together. */
function demolish(target) {
    if (!target) { return; }
    dropCap(target.id);
    Scene.destroyActor(target);
}

/**
 * A flat thing: floors, roads, markings, shadows. No height, no sorting.
 */
function flat(name, tag, x, y, w, h, r, g, b, depth, alpha) {
    var a = Scene.createActor(name, x, y);
    if (!a) { return null; }
    a.tag = tag;
    Scene.addComponent(a, "SpriteRenderer", {
        Tint: { R: clampByte(r), G: clampByte(g), B: clampByte(b),
                A: alpha === undefined ? 255 : alpha },
        Size: [w, h],
        LayerDepth: depth
    });
    return a;
}

// ---------------------------------------------------------------------------
// Queries, for scripts that would rather ask than keep their own copy
// ---------------------------------------------------------------------------
function depthAt(y)     { return groundDepth(y); }
function getWorldSize() { return worldSize; }
function setWorldSize(v) {
    var n = Number(v);
    if (n === n && n > 0) { worldSize = n; }
}

function clampByte(v) {
    var n = Math.round(v);
    if (n < 0) { return 0; }
    if (n > 255) { return 255; }
    return n;
}
