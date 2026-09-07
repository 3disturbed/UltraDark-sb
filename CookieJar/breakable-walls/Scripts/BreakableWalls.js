// BreakableWalls.js -- walls you can chop a hole through, without paying for it first.
// Attach to a single manager actor and tag it "Walls".
//
// A rectangle cannot lose a piece: it is all or nothing. The usual answer is to
// build every wall out of tiles up front, which means a city of intact walls
// costs thirty actors per wall for a destructibility nobody has used yet.
//
// So: a wall is ONE actor until something hits it. On the first hit it is
// replaced by a grid of chunks, and the chunks inside the blast are dropped.
// Nothing pays for destruction that has not happened, and a wall you have
// chopped through costs what it actually needed.
//
// The hole is real. Chunks carry their own colliders, so what is left still
// stops you and what is gone lets everything through -- which is the point: the
// door stops being the only way in.
//
//   var w = Scene.findFirstByTag("Walls").getComponent("ScriptComponent");
//   var removed = w.call("damage", x, y, 15);   // 0 means you hit nothing

// ---------------------------------------------------------------------------
// Dials
// ---------------------------------------------------------------------------
var wallTag  = "Wall";    // what may be broken
var chunkTag = "Chunk";   // what it becomes

var chunkSize = 10;       // the grid a wall breaks into. Smaller looks better and costs more.

// The ceiling, and it is the important number here. Without it a long enough
// session turns every wall in the world into thirty actors and the frame rate
// goes with it. Past the ceiling a wall simply takes the hit and stays whole.
var maxShattered = 48;

// Chunks vary a little, so a broken wall reads as rubble rather than as the same
// wall with bites taken out of it.
var shadeLow  = 0.86;
var shadeHigh = 1.14;

// Height, if you are using the depth-2-5d cookie. Zero draws flat chunks.
var chunkHeight = 0;
var faceShade   = 0.56;   // must match Depth25D's, and only used when height > 0

// ---------------------------------------------------------------------------
var shattered = 0;
var seed = 1;

/**
 * Damages whatever wall is near a point.
 *
 * @returns how many chunks were removed, so a caller can tell a hit from a miss
 *          and play a different sound for each.
 */
function damage(x, y, radius) {
    var hits = Physics.overlapCircle(x, y, radius);
    var removed = 0;

    for (var i = 0; i < hits.length; i++) {
        var target = hits[i];
        if (!target) { continue; }

        if (target.tag === chunkTag) {
            // Already broken up: just take the piece out.
            if (within(target, x, y, radius)) { Scene.destroyActor(target); removed++; }
        } else if (target.tag === wallTag) {
            removed += shatter(target, x, y, radius);
        }
    }
    return removed;
}

/** How many walls are currently broken up, against the ceiling. */
function shatteredCount() { return shattered; }
function shatterCeiling() { return maxShattered; }

// ---------------------------------------------------------------------------
// The subdivision
// ---------------------------------------------------------------------------

function shatter(wall, x, y, radius) {
    // The FOOTPRINT, from the collider -- never the sprite.
    //
    // In a 2.5D game the sprite is the extruded side face and is taller than the
    // wall is deep, so dividing it scatters rubble up the screen into the air
    // above the hole. The collider is the footprint and always was, on both
    // engines, whatever the picture is doing.
    var body = wall.getComponent("BoxCollider2D");
    var sprite = wall.getComponent("SpriteRenderer");
    if (!body || !body.size || !sprite || !sprite.tint) { return 0; }

    if (shattered >= maxShattered) { return 0; }
    shattered++;

    var w = body.size.x;
    var h = body.size.y;

    // Back out any shading the extrusion applied, so chunks start from the
    // wall's own colour rather than from the colour of its shaded side.
    var lift = chunkHeight > 0 ? faceShade : 1;
    var tr = sprite.tint.r / lift;
    var tg = sprite.tint.g / lift;
    var tb = sprite.tint.b / lift;

    var cols = Math.max(1, Math.round(w / chunkSize));
    var rows = Math.max(1, Math.round(h / chunkSize));
    var cw = w / cols;
    var ch = h / rows;

    var left = wall.transform.x - w / 2;
    var top  = wall.transform.y - h / 2;

    // A top face is a separate actor with no collider and no script, so nothing
    // on it can notice that its wall has gone. Tell whoever owns it.
    releaseCap(wall);
    Scene.destroyActor(wall);

    var removed = 0;
    for (var cy = 0; cy < rows; cy++) {
        for (var cx = 0; cx < cols; cx++) {
            var px = left + cw * (cx + 0.5);
            var py = top + ch * (cy + 0.5);

            var dx = px - x;
            var dy = py - y;
            if (dx * dx + dy * dy <= radius * radius) { removed++; continue; }

            var shade = shadeLow + rand() * (shadeHigh - shadeLow);
            makeChunk(px, py, cw, ch, tr * shade, tg * shade, tb * shade);
        }
    }
    return removed;
}

function makeChunk(x, y, w, h, r, g, b) {
    if (chunkHeight > 0 && depth()) {
        // No cap: a chunk is smaller than the lit top face would be, and one
        // shattered wall is dozens of these. The wall you broke must not cost
        // more actors than the street it stood in.
        depth().call("standing", "Chunk", chunkTag, x, y, w, h,
            r / faceShade, g / faceShade, b / faceShade, chunkHeight, true, false);
        return;
    }

    var a = Scene.createActor("Chunk", x, y);
    if (!a) { return; }
    a.tag = chunkTag;

    Scene.addComponent(a, "SpriteRenderer", {
        Tint: { R: clampByte(r), G: clampByte(g), B: clampByte(b), A: 255 },
        Size: [w, h]
    });
    // Every chunk keeps a collider, or the hole is the whole wall.
    Scene.addComponent(a, "BoxCollider2D", { Size: [w, h] });
}

// ---------------------------------------------------------------------------
// Optional: the depth-2-5d cookie, if this world has height
// ---------------------------------------------------------------------------
var depthScript = null;
var depthLooked = false;

function depth() {
    if (!depthLooked) {
        depthLooked = true;
        var host = Scene.findFirstByTag("Depth");
        if (host) { depthScript = host.getComponent("ScriptComponent"); }
    }
    return depthScript;
}

function releaseCap(wall) {
    var d = depth();
    if (d) { d.call("dropCap", wall.id); }
}

// ---------------------------------------------------------------------------
function within(target, x, y, radius) {
    var dx = target.transform.x - x;
    var dy = target.transform.y - y;
    return dx * dx + dy * dy <= radius * radius;
}

function rand() {
    seed = (seed * 16807) % 2147483647;
    return (seed - 1) / 2147483646;
}

function clampByte(v) {
    var n = Math.round(v);
    if (n < 0) { return 0; }
    if (n > 255) { return 255; }
    return n;
}
