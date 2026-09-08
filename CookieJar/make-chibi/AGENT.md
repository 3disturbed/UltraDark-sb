# MakeChibi Characters

A player character that picks its own walk cycle, and a village of distinct NPCs from one
integer.

## Why this exists

The engine gives you `Chibi.spawn`, `Chibi.random` and `Chibi.play`. What it does not give you
is the thing every game then writes: the mapping from *how fast the character is moving* to
*which clip is playing*, with a blend that does not pop, and a heading that turns rather than
snapping. That is thirty lines, it is the same thirty lines every time, and getting the turn
wrong is what makes a prototype character read as broken rather than as unfinished.

The crowd half exists for a different reason. `Chibi.random(seed)` gives a coordinated
character, but a village also needs positions, wander timers and a reason for anyone to move —
and if any of that is `Math.random()` at start-up then the village is different every run and
you cannot debug what you saw. Here the seed decides the person **and** where they stand.

## Wiring it up

Put `ChibiMover.js` on an **empty actor** and give it a character:

```js
var recipePath = "Assets/Characters/Hero.chibi";   // or leave "" and set a seed
var seed = 0;
```

It spawns the character itself. Do not add a `ChibiCharacter` to the same actor — you would get
two.

**The actor the script is on is not the character.** The script actor is a controller that stays
where it started; the character is a separate actor the script moves. A camera must follow
`getChibi()`, not the script's own actor:

```js
var hero = Scene.findFirstByTag("Player").getComponent("ScriptComponent");
var target = hero.getChibi();
actor.transform3d.x = target.transform3d.x;
```

## The API

```js
getChibi()          // the character's actor, for a camera or a HUD to follow
getSpeed()          // units per second, for a footstep timer or a stamina bar
act("wave")         // a one-off clip: wave, hit, jump, cheer, sit, die
```

`getSpeed` returns a plain number on purpose. Numbers cross the script boundary identically on
both engines, which objects do not reliably do.

## Tuning

| Knob | Default | What it does |
|---|---|---|
| `walkSpeed` / `runSpeed` | 1.6 / 3.4 | Units per second. A chibi is one unit tall, so 1.6 is a brisk walk at its own scale. |
| `runsAbove` | 2.2 | The speed at which the walk clip becomes the run clip. Keep it between the two speeds. |
| `stillBelow` | 0.15 | Below this it is idle. Too low and it twitches between idle and walk on the way to a stop. |
| `turnSpeed` | 14 | Degrees per frame at 60fps. Above about 25 it snaps; below about 6 it slides. |
| `acceleration` | 12 | How fast it reaches the target speed. Lower feels heavy, higher feels twitchy. |

For the crowd:

| Knob | Default | What it does |
|---|---|---|
| `count` / `firstSeed` | 16 / 1000 | Villagers are `firstSeed` to `firstSeed + count - 1`. Change `firstSeed` for a different village, not `count`. |
| `areaX` / `areaZ` | 9 / 6 | The box they are scattered in and turn back at. |
| `pauseSeconds` | 2.5 | Roughly how long one stands before moving again. |
| `greetChance` | 0.25 | How often a pause is a wave instead of standing. |
| `wander` | true | False leaves them standing, which is what you want for a shop or a crowd scene. |

## What it does not do

- **No physics.** Movement writes the transform directly. There is no gravity, no ground check
  and nothing stops a villager walking through a wall; add a `CharacterController3D` yourself if
  the game needs one.
- **No pathfinding.** The crowd wanders and bounces off the edge of its box. It does not avoid
  each other or anything else.
- **No input mapping.** `ChibiMover` reads `Horizontal`, `Vertical` and `Sprint` from the default
  action map. Install `input-mapping` if you want to rebind them.
- **It does not author characters.** A `.chibi` recipe is made in the editor's MakeChibi panel or
  by hand; this consumes one.
