# Breakable Walls

Walls you can chop a hole through, without paying for it before anybody has.

## Why

A rectangle cannot lose a piece — it is all or nothing. The usual answer is to build every wall out
of tiles up front, and then a world of intact walls costs thirty actors per wall for a
destructibility nobody has used yet. On a city-sized map that is the whole frame budget spent on
something that has not happened.

So a wall is **one actor until something hits it**. On the first hit it is replaced by a grid of
chunks and the chunks inside the blast are dropped. Nothing pays for destruction that has not
occurred, and a wall you have actually chopped through costs what it needed.

The hole is real: every chunk keeps its own collider, so what is left still stops you and what is
gone lets you — and everything chasing you — through. That is the point of the mechanic. The door
stops being the only way in, which changes what a building *is*: a place you can be cornered in
rather than a place you can hold.

## Wiring it up

1. Put `BreakableWalls.js` on a manager actor, tagged `Walls`.
2. Tag your destructible scenery `Wall`, and give each one a `BoxCollider2D`.
3. Call `damage(x, y, radius)` from whatever swings, explodes or crashes.

```js
var w = Scene.findFirstByTag("Walls").getComponent("ScriptComponent");
var removed = w.call("damage", hitX, hitY, 15);
if (removed > 0) { /* splinters, dust, a different sound */ }
```

`damage` returns the number of chunks removed, so **0 means you hit nothing** — that is your "clang
off a solid wall" versus "bite out of it" without a second query.

## The ceiling is the important dial

`maxShattered` (default 48) caps how many walls may be broken up at once. Past it a wall takes the
hit and stays whole.

This is not a nicety. Without it a long enough session turns every wall in the world into thirty
actors, and the frame rate goes with it — slowly, over twenty minutes, in a way that looks like a
memory leak and is really just a mechanic with no upper bound. Pick the number from the frame
budget you have, not from how much destruction feels generous.

## Read the collider, never the sprite

`shatter` takes the wall's footprint from its `BoxCollider2D`.

If your world has height — see the `depth-2-5d` cookie — the sprite is the extruded side face and
is *taller than the wall is deep*. Subdividing that scatters rubble up the screen into the air
above the hole, and every automated gate passes it: the scene loads, no script errors, the wall
really did break. Only an eye catches it.

## With the depth-2-5d cookie

Set `chunkHeight` above zero and this finds the `Depth` manager on its own, builds chunks as
standing rubble, and tells it to drop the broken wall's top face — otherwise the roof hangs in the
air over your new doorway. Keep `faceShade` equal to `Depth25D`'s, or the rubble comes out a
different colour from the wall it came from.

Chunks deliberately get **no top face**: one shattered wall is dozens of them, and the wall you
broke must not cost more actors than the street it stood in.

## What it deliberately does not do

No structural integrity — a wall with its middle removed does not fall down, and the piece above a
hole is happy to float. Doing that properly means a connectivity pass on every hit and a different
cookie.

No damage levels either. A hit removes chunks or it does not; there is no "cracked" state. Cracks
want art, and this is a mechanic for a prototype that does not have any yet.
