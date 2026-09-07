# Depth 2.5D

Turns a top-down plan into a place with a near side and a far side.

## Why

A flat top-down game usually gives each *kind* of thing a fixed depth: ground at 0.1, walls at 0.4,
the player at 0.7. It reads as a diagram, and the reason is structural rather than artistic — a
wall at 0.4 is **always** behind a player at 0.7, so the player walks over every wall in the world,
including the ones between them and the camera. No amount of art fixes that.

2.5D is one rule instead:

> Everything that lies **flat** keeps a fixed band. Everything that **stands up** is sorted by
> where its feet are — further down the screen is nearer the camera.

That is the whole mechanic. It is what lets a survivor walk behind the far wall of a room and in
front of the near one, and it costs one function.

Height is the second half, and it is the oldest trick in the genre: **the footprint is what you
collide with, the lift is what you see**, and the two are never the same rectangle.

## Wiring it up

1. Put `Depth25D.js` on a manager actor, tagged `Depth`.
2. **Set `worldSize` to your map's tallest coordinate.** Everything else is a ratio of it. Get it
   wrong and the far end of the map clamps into a single depth, so a whole district stops sorting.
3. Build your scenery through `standing(...)` instead of creating sprites directly, and your floors
   through `flat(...)`.
4. Put `StandUpright.js` on anything that **moves** — the player, enemies, vehicles. Static things
   are depthed once when they are built; a mover has to be re-depthed every frame, because its
   relationship to every wall changes with every step.

```js
var d = Scene.findFirstByTag("Depth").getComponent("ScriptComponent");

d.call("flat", "Road", "Ground", cx, cy, 400, 400, 34, 36, 44, d.call("groundBand"));
d.call("standing", "Wall", "Wall", cx, cy, 300, 16, 128, 120, 112, 32, true);
```

## The pivot, which is the whole trick

A wall with height is **one sprite**, not two. `standing` gives it a size of `[w, h + height]` and
a pivot of `[0.5, (height + h/2) / (height + h)]`, which hangs the sprite upward so its bottom edge
lands on the footprint's south edge. The actor — and therefore its collider — never moves.

Lift the sprite by moving the **actor** instead and the collider goes with it: you walk straight
through the wall you can see, and stop dead in the street in front of it. Both engines apply the
pivot the same way, so this survives the trip to a native build.

The renderer's culling knows about this (`SpriteRenderer.CullRadius`), so an extruded wall is not
culled while the visible half of it is still on screen.

## Four things that will bite

**1. A top face is a separate actor.** It has no collider and no script, so nothing on it can
notice that the thing it sits on has gone. Destroy a wall directly and its roof hangs in the air
over the hole. Use `demolish(actor)`, or call `dropCap(actor.id)` first. Anything you draw on top
of a `standing` yourself — a tree's canopy is just a cap that is wider than its trunk — should be
registered with `linkCap`.

**2. Full-screen overlays must sit above the band, not inside it.** Darkness, weather and damage
flashes are usually parked at some comfortable-looking 0.8. That used to be above everything; here
it is the middle of the standing band, so half the world sorts over the top of it and stands in
full daylight after nightfall. Use `overlayBand()`.

**3. Read the collider for a footprint, never the sprite.** The sprite is now the extruded face and
is taller than the thing is deep. Anything that subdivides, snaps to, or measures a standing object
must ask its `BoxCollider2D`, or the results scatter up the screen into the air above it.

**4. Do not extrude anything that rotates.** The pivot is off-centre, and a sprite rotates about
its pivot, so an extruded car swings as it steers. Vehicles get sorted like everything else and
stay a plain centred sprite; they are low enough that nobody misses the side face.

## Tuning

| Dial | What it changes |
|---|---|
| `worldSize` | Must match your map. Not a look dial. |
| `D_STAND`, `D_STAND_SPAN` | The band. Leave room above it for `D_OVERLAY`. |
| `faceShade`, `capShade` | How hard the light is. The **gap** between them is what reads as height — narrow it and everything flattens out. |
| height per call | Keep it small. Tall walls in a plan view hide the street behind them, and a world you cannot see into is one you cannot play in. |

## What it deliberately does not do

No roofs, no floor levels, no occlusion fading. Buildings here are open-topped, which is what lets
you see into a room you are standing in — the moment you put a roof on, you need a system to take
it off again, and that is a different cookie.

It also does not sort by anything but Y. Depth-sorting by "feet" is an approximation that breaks
for very long objects lying across the view; keep standing things roughly as deep as they are tall,
or split them.
