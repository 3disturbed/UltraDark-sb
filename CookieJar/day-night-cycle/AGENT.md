# Day / Night Cycle

A clock that turns into pressure. Phases announced once each, a darkness that follows the player,
and a curve every other script can read.

## Wiring it up

Put `DayNightCycle.js` on a manager actor, tag it `Clock`, and set `followTag` to whatever the
dark should stay centred on.

```
Clock   (tag "Clock")   Scripts/DayNightCycle.js
```

Then make night mean something, from anywhere:

```js
var clock = Scene.findFirstByTag("Clock").getComponent("ScriptComponent");
var dark  = clock.call("getDarkness");        // 0 by day, 1 at midnight
var day   = clock.call("getDayNumber");       // 1, 2, 3 …

var enemies = 12 + 5 * (day - 1) + Math.floor(dark * 14);
var speed   = 1 + dark * 0.3;
```

That is the intended shape: **this script publishes a curve, and every other system decides what
to do with it.** More enemies, faster ones, colder, hungrier, worse accuracy, better stealth. It
deliberately does none of that itself.

## Why the dark follows the player

Because a script cannot cover the screen. The shared scripting contract exposes no viewport — no
window size, no camera bounds — so there is no way to build a full-screen quad. A large enough
tinted square centred on the player covers the view at any resolution and any zoom, and costs one
actor.

`overlaySize` (4200) must comfortably exceed the widest view you expect. Too small and the player
sees the edge of the night as a square around them, which looks exactly as bad as it sounds.

## Draw order is the thing to get right

`overlayDepth` (0.2) is a `layerDepth`, and **high layerDepth is drawn first**, so:

| Depth | What belongs there |
|---|---|
| 0.4 – 0.95 | the world: ground, walls, props, actors — everything the dark should dim |
| **0.2** | **the overlay** |
| 0.1 – 0.19 | anything that must stay bright *through* the night: fire, torches, muzzle flash, HUD |

Getting this wrong is quiet and confusing: a fire drawn at 0.25 is behind the overlay, so the one
light source in the scene gets dimmed by the darkness it exists to push back.

## The shape of a day

`dayLength` (150s), `duskAt` (0.58), `nightAt` (0.70), `dawnAt` (0.94) are fractions of a day.
The defaults give roughly ninety seconds of daylight, twenty of failing light, thirty-five of
proper night, and a short dawn.

Tune `dayLength` against the loop, not against realism: a day should be long enough to do a
complete round of whatever the game asks for — gather, build, prepare — and short enough that a
bad night is not a twenty-minute punishment. If playtesters are idle before dusk, it is too long.

`startAt` shifts where the first day begins. Leave it at 0 to open in full daylight, which is the
kind thing to do while a player is still learning the keys.

## The API

- `getDarkness()` — 0..1, ramped. The one you will use most.
- `getDayNumber()` — 1, 2, 3 … Escalation lives on this, not on the clock.
- `getPhase()` — `"day"`, `"dusk"`, `"night"`.
- `isNight()` — 1 or 0, for a bar or a simple test.
- `getTimeOfDay01()` / `setTimeOfDay01(f)` — read or skip. A bed, a debug key, a cutscene.
- `onPhaseChanged(from, to)` and `onNewDay(day)` — seams. They log by default; replace them with
  audio, a screen flash, a save.

## What it does not do

- **No lighting.** This is a flat tint over everything, not a light model. Nothing casts, nothing
  falls off with distance, and a torch is not a torch — it is a bright sprite at a lower depth.
  Real 2D lighting is engine work.
- **No colour grading.** Dusk is the same navy as midnight, just weaker. Warming the tint through
  `darkR/G/B` over the curve is a few lines if you want a sunset.
- **It does not pause.** The clock runs during a menu unless you set `Time.timeScale = 0`.
