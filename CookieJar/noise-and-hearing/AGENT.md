# Noise and Hearing

Enemies that hunt by ear. A sound is broadcast as a **position and a radius**, and everything
inside it walks to that position — not to whoever made the sound.

That restraint is the whole mechanic. Because the memory is a place rather than a target:

- running away works, because they converge on where you shouted, and you are not there;
- throwing something works, without a "distraction" system existing;
- standing still works, which makes holding your nerve a real choice;
- and being loud becomes a decision rather than a free upgrade.

## Wiring it up

Two scripts, one hub and any number of ears.

```
Director   (tag "Director")   Scripts/NoiseDirector.js
Enemy      (tag "Enemy")      Scripts/NoiseInvestigator.js   + Rigidbody2D + a collider
```

The listener needs a `Rigidbody2D` (gravity scale 0 for a top-down game) and a collider — the
broadcast is a physics query, so a listener with no body is one that never hears anything.

Then make a noise from anywhere:

```js
var ds = Scene.findFirstByTag("Director").getComponent("ScriptComponent");
ds.call("emitNoise", actor.transform.x, actor.transform.y, 300);
```

Everything audible goes through that one call, so the game has one volume knob.

## What to make a noise with, and how loud

Loudness is just a radius, so the numbers are the design. A set that has played well:

| Action | Radius | Why |
|---|---|---|
| Walking | 95, every 0.45s | Enough to matter in a corridor, not in the open |
| Sprinting | 300, every 0.3s | Three streets. This is the trade the whole game turns on |
| Searching a container | 130 | Looting is not free |
| A melee swing | 200 | Hit or miss — the swing is what is loud |
| An explosion | 400+ | Solves one problem, creates another |

Footsteps want a timer rather than a call per frame: a noise every frame is sixty broadcasts a
second and reads as a single continuous shout anyway.

## The API

**`NoiseDirector.js`**

- `emitNoise(x, y, radius)` — tell everything in the radius. Returns how many heard.
- `alertAll(x, y)` — every listener, whatever the distance. An alarm, a scream, a scripted beat.
  The opposite of the mechanic above; use it once and deliberately.
- `setPace(value)` — multiply every listener's speeds. Night, a difficulty setting, a boss phase.
- `getNoise01()` — 0..1 of how loud things have been lately, for a HUD bar.

**`NoiseInvestigator.js`**

- `hearNoise(x, y, strength)` — what the director calls. Ignored while already chasing.
- `alertTo(x, y)` — straight to chase. Call this when the thing is hurt: being hit is the one
  event that tells it exactly where you are.
- `setPace(value)`, `getState()` — 0 wander, 1 investigate, 2 chase.
- `onSpotted(target, distance)` and `onChasing(target, distance, dt)` — empty seams. Attacks go in
  the second one. A call to `emitNoise` in the first is what makes a horde feel like a horde: one
  of them sees you, shouts, and the rest converge.

## Tuning

`sightRange` (172) and `sightCone` (1.9 rad) are deliberately mean — short enough that sight is
the fallback sense, narrow enough that you can walk behind one. Widen them and you have built a
game about line of sight instead, which is a different game.

`loseRange` (240) is how far you must break away before a chase decays back to investigating.
Investigating, not wandering: it still knows roughly where you went.

## Show the player the noise

The mechanic is invisible until you draw it. A bar fed from `getNoise01()` teaches the whole thing
without a word of tutorial — the player sprints, watches the bar spike, watches the shapes turn,
and never needs to be told again. `floating-status-bars` will draw it above the player's head.

## What it does not do

- **No pathfinding.** A listener steers straight at its goal and slides along whatever it hits
  (`unstick`). In open or wide-corridor levels that reads as shambling intent. In a maze it reads
  as a bug — that level wants real navigation, which is engine work, not a cookie.
- **No sound.** This is the *hearing* half. Play the audio yourself next to the `emitNoise` call;
  what a listener hears and what a player hears are not the same event and should not share a
  code path.
- **No occlusion.** A wall between the sound and the ear does not muffle it. `Physics.raycast`
  from the noise to the listener would, at one cast per listener per noise — worth it for a
  stealth game, wasted for a horde game.
