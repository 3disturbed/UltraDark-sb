# Entity Ledger

Forty enemies in one script, instead of forty script engines.

## Why

A `ScriptComponent` is a script engine. One per enemy is the single most expensive thing a
wave-based game can do to itself, and it is the default shape every template nudges you towards.

So an entity here is a **row**: a column per thing it needs to remember, one `onUpdate` that walks
all of them, and actors that carry nothing but a sprite and a collider. In UltraDark this is what
lets 130 enemies under 200 projectiles fit inside a 60 fps frame.

## This one you edit

Everything except `behave()` is machinery: spawning, removal, statuses, separation, contact
damage, area damage, knockback, bounds, queries. **`behave(i, dt, nx, ny, dist, speed)` is your
game**, and it is called once per entity per frame with the direction and distance to the target
already worked out.

Two worked kinds ship with it — one that walks at you and one that holds a range band — so the
shape is there to copy.

Add a column to the arrays when *every* kind needs it. When one kind needs a scratch value, use
`eState` and `eT2`, and say what they mean in `behave()` — that is where the reader will look.

## The four rules

These are why this file exists as a cookie rather than as advice. Each has cost somebody a day.

**1. Removal swaps.** `removeAt(i)` moves the last row into slot `i`, so after anything that can
kill, the index you were holding is somebody else — or past the end. `applyDamage` returns `1` for
died, `0` for survived and `-1` for nothing there, and anything but `0` means stop using that
index. Getting the sense of that check backwards reads a transform off null.

**2. Iterate backwards.** A backwards loop with swap-removal visits every row exactly once.
Forwards, a removal skips the row that took its place — which looks like enemies occasionally
ignoring an explosion.

**3. A cascade must not recurse.** If a death can cause a death — an explosion that kills something
that also explodes — taking it directly recurses one stack frame per link, and a dense pack
overflows. **What that looks like is not a crash.** The engine catches it, and every script hook in
the frame fails from then on, so damage silently stops landing and a boss simply never dies. It
reads exactly like a balance problem. Blasts are queued with `queueBlast` and drained flat by
`drainBlasts`, which every public entry point calls and which refuses to re-enter itself.

**4. Colliders are for queries.** `Physics.overlapCircle` only sees non-trigger colliders, and it
is how a projectile finds what it hit. Nothing here has a `Rigidbody2D`: a collider on its own is
static geometry on both engines and follows its transform. Setting `isTrigger` on one is therefore
how you make something temporarily unhittable — which is a phasing enemy, for free.

## Queries, and the one that bites

```js
var x = ledger.call("nearestX", fromX, fromY, range);   // -1000000 when there is nothing
var y = ledger.call("nearestY", fromX, fromY, range);
```

`nearestX` finds and **caches**; `nearestY` returns the same one. Two independent scans in one
frame can pick two different entities, and whatever is aiming then points between them — a wobble
nobody can reproduce on purpose.

If some other actor deserves to be a target too — a boss with its own script, say — it has to be
added to `nearestX` explicitly. Anything not in this ledger is invisible to it, and an auto-aim
that cannot see the boss is an auto-aim that cannot fight one.

## Tuning

`separationScan` (8) is how many neighbours each entity checks per frame: enough to stop forty
stacking into one sprite, cheap enough to run on all of them. `maxLive` (130) is a hard ceiling —
frame cost is roughly projectiles × entities, so choose it from a measurement rather than a
feeling. `contactCooldown` is how often one entity can hurt the target.

## What it does not do

- **No pathfinding.** `behave` gets a direction and a distance; walls are your problem.
- **No spawning schedule.** Pair it with `wave-director`, which decides when and how much.
- **No per-entity state beyond the columns.** Something that needs a real object of its own —
  a boss with phases — deserves its own script. One of those is a fair trade; forty is not.
