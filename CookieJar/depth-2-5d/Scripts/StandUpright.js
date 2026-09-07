// StandUpright.js -- keeps a thing that MOVES sorted against a world that does not.
// Attach to the player, to enemies, to vehicles: anything whose position changes.
//
// Static scenery is depthed once, when it is built. A mover cannot be: its
// relationship to every wall in the world changes with every step, and that is
// precisely the relationship 2.5D exists to express. So this runs every frame.
//
// The depth rule is duplicated here rather than called across to Depth25D on
// purpose. A cross-script call into a script that has not initialised yet
// returns undefined, and an undefined layerDepth is an actor that silently
// stops being drawn -- while it keeps moving, keeps colliding, and keeps
// chasing you. Copying five lines is the cheaper mistake. What must NOT drift
// is `worldSize` and the band; keep them equal to Depth25D's.

// ---------------------------------------------------------------------------
// Dials -- these three must match Depth25D
// ---------------------------------------------------------------------------
var worldSize    = 4096;
var D_STAND      = 0.22;
var D_STAND_SPAN = 0.74;

// How far south of its own feet this thing is sorted.
//
// Without it, standing against the inside of a wall puts that wall's base a few
// pixels below you, so the wall you are leaning on draws over your head. With
// it you clear anything close enough to touch and are still hidden by a wall on
// the far side of the room -- which is the distinction that actually matters.
// Roughly half the actor's own depth plus the wall thickness.
var peek = 26;

// How far up the screen the body is drawn from the feet that carry it. Small:
// this is a plan view, and something that floats a whole body length above its
// own shadow reads as a bug rather than as height.
var lift = 0;

// A shadow that stays on the floor while the rest of the thing stands up. Leave
// blank for no shadow.
var shadowName  = "Shadow";
var shadowW     = 15;
var shadowH     = 20;
var shadowAlpha = 90;
var shadowDepth = 0.17;   // Depth25D's D_SHADOW: under everything upright
var shadowSkew  = 3;      // offset down-right, the direction the light comes from

// ---------------------------------------------------------------------------
var sprite = null;
var shadow = null;

function onStart() {
    sprite = actor.getComponent("SpriteRenderer");

    if (shadowName) {
        shadow = Scene.createActor(shadowName, actor.transform.x, actor.transform.y);
        if (shadow) {
            shadow.tag = "Ground";
            Scene.addComponent(shadow, "SpriteRenderer", {
                Tint: { R: 8, G: 9, B: 14, A: shadowAlpha },
                Size: [shadowW, shadowH],
                LayerDepth: shadowDepth
            });
        }
    }
}

// Late, so it reads the position after everything that moves this actor has
// finished moving it. In onUpdate it would sort against where the thing was.
function onLateUpdate(dt) {
    var y = actor.transform.y;

    if (sprite) { sprite.layerDepth = groundDepth(y + peek); }

    if (lift !== 0) {
        // The lift is screen-space and is applied to the drawn position only, so
        // it must not be rolled into anything the physics reads.
        actor.transform.y = y;
    }

    if (shadow) {
        shadow.transform.x = actor.transform.x + shadowSkew;
        shadow.transform.y = y + shadowSkew;
        shadow.transform.rotation = actor.transform.rotation;
    }
}

/** The rig is a separate actor, so it has to be cleaned up by hand. */
function onDestroy() {
    if (shadow) { Scene.destroyActor(shadow); shadow = null; }
}

function groundDepth(y) {
    var t = y / worldSize;
    if (t < 0) { t = 0; } else if (t > 1) { t = 1; }
    return D_STAND + t * D_STAND_SPAN;
}

/**
 * The depth this thing is currently sorted at, for a script that hangs extra
 * parts on it -- a head, a held weapon, a health bar -- and needs them to sort
 * with the body rather than against it. Add a small amount for something drawn
 * over the body; subtract for something behind it.
 */
function getDepth() { return groundDepth(actor.transform.y + peek); }
