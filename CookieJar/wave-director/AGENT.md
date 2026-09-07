# Wave Director

Endless escalating waves, a boss every fifth one, and a wave that always ends.

It owns the **shape** of a run and nothing about its **content**. Point it at a script that knows
how to spawn things and the same pacing drives a different game.

## The shape

```
begin() ─► WAVE 1 ─► (cleared) ─► WAITING ─► resume() ─► WAVE 2 ─► …
```

It stops at `WAITING` instead of rolling straight on, because between waves is where a game wants
to put a draft, a shop, a breather or a cutscene. **Nothing happens again until you call
`resume()`** — which also means a game that forgets to is a game that stops. That is deliberate:
an automatic next wave would make an intermission impossible to hold open.

## A wave is a budget, not a headcount

```js
waveBudget(w) = (baseBudget + budgetPerWave * w) * budgetCurve ^ w
```

Content prices its own spawns, so at the same budget a game can send forty weak things or four
heavy ones and the wave takes about as long. That is what lets a wave get *harder* rather than
just *longer* — the failure mode of every headcount-based spawner is wave 20 taking four minutes
to shoot the same enemy sixty times.

`budgetCurve` (1.045) compounds on top of the linear climb, so difficulty accelerates. It is the
most sensitive number here: 1.05 and wave 30 is four times wave 20.

## The concurrent cap, which is also a performance ceiling

`concurrentCap` (105) is how many things may be alive at once. The **budget is never capped** — a
late wave still sends everything it was going to — it just converts into spawns no faster than
they are cleared.

Two things fall out of that. A big wave becomes sustained pressure instead of one blob that
arrives faster than anyone can shoot it. And the number of live things has a ceiling you chose,
which matters because collision is usually O(projectiles × enemies): in UltraDark, 220 enemies
under fire measured at 20 ms a frame and 105 at 7 ms. Set this from a measurement, not a feeling.

Set it to `0` to disable the cap.

## The stall-breaker, which is the part you will not think of

A wave with nothing left to send and something still alive **will not always end**. Anything
rooted, anything that keeps its distance, anything that wandered into a corner of a large arena —
the player ends up hunting one straggler across the map, which is not difficulty, it is an errand.

In UltraDark this was one enemy type: a turret with speed 0, spawned at the arena edge. The wave
it unlocked on became a wall that a twenty-minute automated run never got past, with no error
anywhere and every test passing.

So after `stallAfter` (22s) of a wave with an empty budget and something still alive, content is
told `onWaveStalled(wave)` and it is content's job to go and find them — speed everything up, make
the rooted things creep, teleport them in, or just kill them. Set `stallAfter` to `0` to turn it
off, and then make sure every spawn can reach the player under its own power.

## Wiring it up

```
Waves   (tag "Waves")   Scripts/WaveDirector.js
```

Set `contentTag` to the script that does the spawning, then implement the contract on it.

**Required:**

```js
// Spawn one thing for at most `budget`. Return what it cost, or 0 if there is
// nothing left worth buying — returning 0 ends the wave's spending.
function spawnOne(wave, budget) { … return cost; }

// How many things are alive.
function aliveCount() { … }
```

**Optional, and each is a seam worth having:**

```js
function startBoss(wave, index) { … return 1; }   // return 0 to decline; it becomes an ordinary wave
function bossAlive()            { … }             // 1 while it lives; the wave will not end without this
function onWaveStart(wave, isBoss)   { … }        // the banner, resetting per-wave state
function onWaveCleared(wave, isBoss) { … }        // YOUR DRAFT GOES HERE. Call resume() when done.
function onWaveStalled(wave)         { … }        // go and find the stragglers
```

Everything crossing that boundary is a number, because numbers are the only thing that marshal
identically under Jint and in the browser.

## Bosses

`bossEvery` (5) is the cadence and `bossKinds` (5) is how many you have. `bossFor(w)` cycles:

```
wave  5 → 0     wave 30 → 0
wave 10 → 1     wave 35 → 1
wave 25 → 4     wave 500 → …and on
```

**There is no last wave.** No `WAVE.MAX`, no victory phase, no wave at which the run is won. A run
ends when the player does. Adding a final wave is the single change that would most alter what an
endless mode is, so it is not a switch here — if you want a campaign, call `stop()` when your own
condition is met.

A boss wave still sends a thinner escort (35% of the budget), so the arena is never empty around
the fight.

## Tuning

`trickle` (0.36s between spawns) is how fast a wave fills. It is more sensitive than it looks:
dropping it from 0.42 to 0.30 in UltraDark moved the *whole* frame cost, because more things alive
means more collision pairs. `startDelay` (0) is a beat before the first spawn.

## Test seams

`forceWave(w)` jumps straight to a wave and `forceBudget(n)` stops the current one sending
anything else, so a test can work with what it placed rather than with whatever the wave felt like
adding. Both are worth keeping in a shipped build for a debug key.

## What it does not do

- **No spawn positions.** Where a thing appears is content's business, and it should be: an enemy
  that keeps its distance and one that charges want different answers.
- **No difficulty beyond the budget curve.** Health and speed scaling belong in content, which
  knows what it is scaling.
- **No score, no rewards, no drops.**
- **No pause.** It runs on `onUpdate`, so `Time.timeScale = 0` is how you stop it.
