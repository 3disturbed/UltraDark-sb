# Stacking Upgrades

A bag of upgrades that stack, and the stats they add up to. The spine of a roguelite build.

## The one rule

**Nothing is ever deduplicated, and no stat is ever edited in place.**

`taken` is a list of ids with the duplicates left in. Every stat is recomputed from that list,
from scratch, whenever it changes. So two copies of an upgrade are exactly two applications of it,
always, with no special case anywhere — and removing one is as exact as adding one, because there
is no accumulated state to drift.

A "you already have this" check is the single change that would quietly remove half of a draft
pool's decisions. If an upgrade genuinely should be once-only, that is `addOnce`, and it should be
rare enough to notice.

## Effects are a table, not a switch

```js
var UPGRADES = [
    { id: 0, name: "HEAVY SLUG",   effects: [{ stat: "damage", op: "mul", value: 1.12 }] },
    { id: 1, name: "PLATING",      effects: [{ stat: "maxHp",  op: "add", value: 18 }] },
    { id: 2, name: "GLASS CANNON", effects: [
        { stat: "damage", op: "mul", value: 1.30 },
        { stat: "maxHp",  op: "add", value: -12 },
    ] },
];
```

A switch means adding an upgrade is editing code in two places and hoping. A table means it is one
row — and the row is *data*, so whatever draws your cards can read the name and the effects out of
the same place the maths comes from, instead of a second list that drifts.

An upgrade that costs you something is one row with two effects, not a special case.

## `mul` and `add`, and why the difference matters

`mul` compounds: three stacks of ×1.12 is ×1.40. `add` is linear: three stacks of +18 is +54.

That is the whole balance lever. A multiplicative damage stat rewards going all-in on damage; an
additive one rewards spreading out. Pick per stat, deliberately, and expect to change your mind
once you have watched somebody play.

A stat nobody has touched reads `1` from `mul()` and `0` from `stat()`. `BASES` overrides that
where neither is right.

## Reading them

```js
var up = Scene.findFirstByTag("Upgrades").getComponent("ScriptComponent");

var damage   = up.call("mul",  "damage");     // 1 when nothing touches it
var bonusHp  = up.call("stat", "maxHp");      // 0 when nothing touches it
var blades   = up.call("stat", "blades");     // a count is just an added stat
```

**Never cache the result.** It changes the moment anything is taken, which is the point.

`CLAMPS` is the floor and ceiling per stat, applied last: a cooldown multiplier that stacks its
way to zero is a weapon with no cadence at all, and that is a bug you find in a playtest rather
than in a test.

## Reading it from another script, and the trap that comes with that

If this lives on its own actor and something else reads it — which is the usual arrangement —
remember that **a cross-script call to a script that has not initialised yet returns `undefined`**,
and start order between two actors in the same scene is not something to rely on.

So a reader that does this:

```js
sDamage = upgrades.call("mul", "damage");        // undefined on the first frame
```

writes `undefined` into its own stat and keeps it until the next `add()` happens to recompute.
Every number derived from it is then `NaN`, silently — no error, no log line, and a weapon that
does no damage or a health bar that will not fill.

Give every read a default:

```js
function num(value, fallback) {
    var v = Number(value);
    return (v === v) ? v : fallback;      // NaN is the only value not equal to itself
}

sDamage = num(upgrades.call("mul", "damage"), 1);
sPierce = num(upgrades.call("stat", "pierce"), 0);
```

`1` for a multiplier and `0` for an adder are the same defaults this script uses when nothing has
touched a stat, so a reader that has not connected yet behaves exactly like one with no upgrades.

## `onStatsChanged()`

Called after every recompute. Anything that has to be **re-derived** rather than read goes here
and nowhere else — raising max health and healing by the difference, resizing a collider, adding
the sprite for an orbiting blade. Putting it at the call site instead means it happens for the
draft and not for the shop, or twice.

## What it does not do

- **It does not know what a stat means.** It adds numbers up and hands them over; whether
  `damage` multiplies a weapon or a spell is entirely yours.
- **No rarity, no pools, no weighting.** Choosing what to offer is a different job.
- **No costs and no currency.** A shop is this plus a price you check before calling `add`.
- **No persistence.** A run's build lives and dies with the run.
