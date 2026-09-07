# Top-Down Driving

Get in, drive, run things over, get out.

## Why

The temptation is to make the car the player: swap actors, tell the camera about vehicles, give
enemies a second targeting path. Then every "where is the player" query in the game — camera
follow, enemy targeting, noise, spawn distance, the minimap, the save file — needs to learn what a
car is.

Here **the rider stays the player**. Getting in hides the player's sprite and moves the player
actor to the car's position every frame. Nothing else in the game finds out that driving exists.
That is the whole design decision, and it is why this drops into a finished game rather than
rippling through it.

## Wiring it up

1. Put `Driving.js` on the **player** actor, next to whatever moves them on foot.
2. Tag your cars `Car`. Each needs a `BoxCollider2D` and a `Rigidbody2D` — heavy, well damped,
   `FreezeRotation` on, `GravityScale` 0 for a top-down world. A parked car should shrug off being
   walked into and stop promptly when the driver lets go.
3. Make your on-foot movement stand down:

```js
if (driving.call("isDriving")) { return; }   // first line of your move()
```

4. Wire one context key. **Let the car win**:

```js
if (Input.isKeyPressed("E")) {
    if (driving.call("isDriving"))      { driving.call("leave"); }
    else if (!driving.call("enter"))    { search(); }
}
```

Nobody stood beside a car wants to search the pavement, and a context key that guesses wrong once
is a context key the player stops trusting.

## The one line that makes it feel like driving

```js
var grip = Math.min(1, Math.abs(speed) / (topSpeed * gripSpeed));
facing += steer * turnRate * grip * dt * (speed < 0 ? -1 : 1);
```

Steering scales with speed, so a car does not pivot on the spot, and reversing turns the other way.
Take that out and you have a box that slides; leave it in and you have a car. It is worth far more
than the acceleration curve, which nobody notices.

## What it costs, which is the point

An engine is the loudest thing in the world. `Driving.js` pulses a noise every quarter second at
`noiseRadius` (default 520) through the `noise-and-hearing` cookie's director, so a car crosses a
map fast and brings the neighbourhood with it. A vehicle that is only an advantage is not a
decision — it is a movement speed upgrade with a steering wheel on it. Set `noiseTag` to `""` if
your game has nothing to hear it.

## Things learned the hard way

**A car cannot wear scenery.** Shadows, windscreens, roof panels, an extruded side face — anything
built as a separate actor stays behind on the tarmac the moment somebody drives off. A car is one
sprite, and if you are depth-sorting it is sorted rather than extruded: the `depth-2-5d` pivot turns
a sprite about a point that is not its centre, so an extruded car swings as it steers.

**Drive through the body, not the transform.** Setting `transform.x` each frame teleports the car
through walls, and the collisions it skips are the entire reason the streets mean anything.

**Ask it to die, do not destroy it.** `runOver` calls `kill` on what it hits so the victim can drop
what it was carrying and tell whatever was counting it. Destroying the actor directly loses both,
silently.

**Test driving before anything that can hurt you.** In an integration harness, put the driving
checks first: everything after them tends to leave the player bitten, chased and eventually dead,
and a corpse cannot open a car door. It also needs a keyboard — driving is the first thing in most
games that cannot be tested by calling a function, because it only exists as a response to held
keys.

## What it deliberately does not do

No damage, no fuel, no handbrake, no passengers, and no traffic — parked cars stay parked until
somebody gets in. There is one car model's worth of handling; if you want a van to feel different
from a hatchback, read the dials off the car actor instead of from this file.
