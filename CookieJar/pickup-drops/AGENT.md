# Pickup Drops

Coins, hearts, ammo — things that fall on the floor and are worth walking over.

## Why it is one script over a pool

A good wave drops dozens. A `ScriptComponent` per coin is a script engine per coin, and creating
and destroying an actor for each one churns the scene at exactly the moment the screen is busiest.
So a drop is a row in parallel arrays, and the actors are pooled: the pool grows to the most that
were on the floor at once and then stops.

## Wiring it up

```
Pickups   (tag "Pickups")   Scripts/PickupDrops.js
```

```js
var drops = Scene.findFirstByTag("Pickups").getComponent("ScriptComponent");

drops.call("drop", 0, x, y, 1);              // kind 0, worth 1
drops.call("burst", 1, x, y, 0, 5, 90);      // five of kind 1, scattered 90px
```

`KINDS` is one row per thing that can drop — colour, size, how long it lasts, whether a magnet can
pull it.

## The whole coupling is one function

```js
// on your collector (or on `notifyTag`)
function onPickup(kind, value) {
    if (kind === 0) { cores += value; }
    else            { heal(value); }
}
```

One call **per pickup** — not per drop per frame. What a pickup means is not this cookie's
business; it carries a kind and a number and hands them over.

## Two details that are not decoration

**Drops blink before they expire.** A coin that silently stops existing while you are walking
towards it reads as the game taking something away from you. Three seconds of blinking turns that
into a decision about whether it is worth the trip.

**The magnet comes from an upgrade, not a switch.** `magnetSource` names a getter on the collector
returning how many stacks of a pull effect it has, and reach scales with it:

```js
var magnetSource = "getMagnet";     // returns 0, 1, 2 …
```

Zero stacks means no pull at all, so the same script serves a game where magnetism is an upgrade
and one where it is never available. Pair it with `stacking-upgrades` and the stat is already
there.

## Tuning

`collectRadius` (34) is how close is close enough — **make it larger than the collector moves in a
frame**, or a fast player runs straight through their own loot. `magnetReach` and `magnetSpeed`
are per stack. `maxLive` (200) is a ceiling. `depth` puts drops in your draw order; under the
things that walk over them is usually right.

## What it does not do

- **No physics.** Drops do not scatter, bounce or settle; `burst` places them at random offsets,
  which reads the same and costs nothing.
- **No rarity or drop tables.** Deciding *what* drops is the caller's job — it knows what died.
- **No inventory.** It hands you a kind and a value at the moment of collection and forgets.
- **No pickup priority.** Two drops in the same place are both collected on the same frame.
