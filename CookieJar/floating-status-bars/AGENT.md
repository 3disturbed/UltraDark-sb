# Floating Status Bars

Health, stamina, hunger, a cooldown — as small bars riding above an actor's head, in world space.

## Why they float instead of sitting in a corner

Because a script cannot put them in a corner. The shared scripting contract exposes `transform`,
`Scene`, `Physics` and `Time`, and **no viewport**: there is no way to ask how wide the window is,
so there is no way to work out where its edge is. Everything a script positions, it positions in
world space.

Following the actor turns that into the right answer rather than a workaround. It is correct at
any window size, correct on a phone, correct when the camera zooms, and in a top-down game it puts
the numbers where the player is already looking.

## Wiring it up

Put `FloatingBars.js` on an empty actor. It builds its own sprites at start.

```js
var targetTag = "Player";
var sources   = ["getHealth01", "getStamina01", "getHunger01", "getThirst01"];
```

Each name in `sources` must be a function on the **target's** script returning a number from 0 to
1:

```js
// in your player script
function getHealth01()  { return health / healthMax; }
function getStamina01() { return stamina / staminaMax; }
```

That is the entire contract between the two scripts: one string per bar. Numbers cross the script
boundary identically on both engines, which objects do not reliably do — so getters that return a
plain number are what to write here, not one that returns a `{health, stamina}` object.

Bars stack upward in the order listed, so the first one is nearest the head.

## The pip

One optional square beside the top bar, for a state that is on or off:

```js
var pipSource = "getInfected";   // returns 1 or 0
```

Poison, infection, overheating, "reloading". Empty string turns it off.

## Tuning

`barWidth` (46), `barHeight` (4), `barGap` (5) and `firstY` (−26) are the shape. `hideWhenFull`
makes a bar vanish at 1.0, which is worth turning on for anything that is usually full — a stamina
bar that only appears when you are spending it is much quieter than one that is always there.

`colourR/G/B` are parallel arrays, one entry per bar, and they wrap if you list fewer colours than
bars. A palette that reads instantly: red health, yellow stamina, orange hunger, blue thirst,
white for anything about noise or alertness.

## Draw order

`depthBack` (0.12) and `depthFill` (0.10) are `layerDepth` values, and **high layerDepth is drawn
first**, so low means in front. The defaults sit in front of a world drawn at 0.2 and up. If you
also have a full-screen tint — a night overlay, a damage flash — give it a depth **above** these
or it will dim the bars along with everything else.

## What it does not do

- **No text and no numbers.** There is no text drawing in the scripting contract. A bar is the
  shape of the information; if you need "37/100" you need the engine, not a script.
- **It does not know what the bar means.** A bar that empties is bad and one that fills is good,
  as far as this is concerned. Invert in your getter if you want a "corruption" bar that fills.
- **One target.** For enemy health bars, put a copy on each enemy with `targetTag` unset and read
  the actor's own script — or, better, do not: a screen full of floating bars reads as clutter
  faster than you would expect.
