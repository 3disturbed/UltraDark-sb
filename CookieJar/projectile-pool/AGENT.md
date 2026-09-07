# Projectile Pool

Every projectile in the scene, in one script, over a pool of reused actors — and a sweep that
stops a fast shot walking through whatever it was aimed at.

## Why it is one script

A `ScriptComponent` is a script engine. One per bullet means two hundred script engines in a
wave, which is the single most expensive thing a shooter can do to itself. So a projectile here
is a **row in parallel arrays** — position, velocity, life, damage, team, radius, pierce — and one
`onUpdate` flies all of them.

It is the same trade as any ledger: you give up per-projectile state you cannot express as a
column, and you get two hundred bullets for the cost of one script.

## Why the actors are pooled

Firing creates an actor and hitting destroys it, which churns the scene every frame of every
fight. Here a spent projectile is deactivated and pushed back on a free list, and the next shot
takes it. The pool grows to the most that were ever in flight at once and then stops.

## The sweep, which is the part you cannot see going wrong

A projectile is tested where it **lands**, not along where it **went**. So a frame's travel longer
than the capture window steps straight over anything in between:

```
speed 1750 px/s  ->  29 px per frame
target 24 px wide + projectile radius 6  ->  a 24 px window
```

That shot misses. Not sometimes, and not at long range — every time, at every range, with no
error anywhere and every test still green. It is invisible because nothing in a test suite looks
at a trajectory; the bullet is fired, the bullet exists, the bullet expires.

So `sweep()` walks the frame in steps of at most `radius + 6`. A slow bolt is one step and costs
exactly what it did before. Only something genuinely fast pays for more, and it is capped at
twelve steps so an absurd speed cannot stall a frame.

**If you take one thing from this cookie, take that.** It is the bug worth knowing about.

## Wiring it up

```
Projectiles   (tag "Projectiles")   Scripts/ProjectilePool.js
```

Then from a weapon:

```js
var pool = Scene.findFirstByTag("Projectiles").getComponent("ScriptComponent");
pool.call("fire", x, y, angle, 900, 12, 0, 4, 0.8, 0, 0);
//                          speed dmg team rad life pierce code
```

`team` is `0` for the player's shots and `1` for everything shooting back. They are different
pools, different colours and different depths, and they collide against different things.

## What it needs from you

**A damage sink.** `enemySinkTag` (default `"Swarm"`) names the script that owns your enemies. It
needs one function:

```js
function damageActor(actorId, dmg, code) { … }   // returns 1 if it found that actor
```

One call per **hit**, not per projectile per frame, which is what keeps the cross-script
marshalling proportional to what actually happens rather than to how much is in the air.

**Colliders.** Collision is `Physics.overlapCircle`, which is engine-side and **cannot see
triggers**. Anything a projectile should hit needs a non-trigger collider. It does not need a
`Rigidbody2D` — a collider on its own is static geometry on both engines, and moving the transform
moves the collider with it.

That is also the switch for making something temporarily untouchable: set `isTrigger` on its
collider and projectiles pass through it, which is how a phasing enemy is built.

**Optionally, a boss.** `bossTag` (default `"Boss"`) is one actor with its own script and a
`takeDamage(dmg, code)`, for the thing in the scene big enough to deserve its own file.

**Optionally, bounds and effects.** `boundsTag`/`boundsCall` ask a script for the half-extent of
the world so a stray shot is recycled rather than flown for ever; without one, `boundsHalf`
(4000) is used. `fxTag`/`fxCall` is called as `(x, y, size, r, g, b, life)` when a projectile
detonates. Set either tag to `""` to do without.

## `code` — the on-hit riders

One integer, because only primitives cross a script boundary reliably. It is passed through to
your sink untouched, so bits 1 and 2 mean whatever you decide:

| bit | value | meaning here |
|---|---|---|
| 1 | 1 | passed to the sink — a chill, a slow, whatever you read it as |
| 2 | 2 | passed to the sink — a burn |
| 3 | 4 | **the pool acts on this one**: the projectile detonates at the end of its life or on its last hit |

## Pierce, and the thing that bites

`pierce` is how many *extra* things a shot passes through. A piercing projectile keeps flying, so
without care it damages the same enemy again on the very next sweep step — three times a frame for
a railgun, which reads as a weapon doing triple its stated damage, but only to whatever it happens
to be overlapping. `bLastHit` remembers the last actor each projectile damaged and skips it.

## Tuning

`maxLive` (320) is a hard ceiling; a shot over it is dropped rather than queued. `hitRadius` (15)
is how close an enemy projectile has to be to the player, which is a distance check rather than an
overlap because the player usually has no collider in this arrangement. `depthPlayer` and
`depthEnemy` place the two teams in your draw order.

## What it does not do

- **No homing, no gravity, no bouncing.** A projectile flies in a straight line at a constant
  velocity. All three are a few lines in `sweep()`, and none of them belongs in the default.
- **No trails, no impact effects** beyond the one optional `fx` call on a detonation.
- **No collision between projectiles.** They pass through each other.
- **It does not own damage numbers.** It carries `dmg` and hands it to your sink; armour,
  resistances and criticals are the sink's business.
