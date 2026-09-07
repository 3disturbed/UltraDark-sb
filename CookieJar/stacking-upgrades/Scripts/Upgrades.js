// Upgrades.js -- a bag of upgrades that stack, and the stats they add up to.
// Attach to whatever owns the stats -- usually the player -- or to a manager,
// and read the results with stat().
//
// The rule the whole thing rests on: NOTHING is ever deduplicated, and no stat
// is ever edited in place. `taken` is a list of ids with the duplicates left in,
// and every stat is recomputed from that list whenever it changes. Two copies of
// an upgrade are therefore exactly two applications of it, always, with no
// special case anywhere -- and a "you already have this" check is the one change
// that would quietly remove half of a draft pool's decisions.
//
// Effects are a TABLE, not a switch. A switch means adding an upgrade is editing
// code in two places and hoping; a table means it is one row, and the row is
// readable by whatever draws your cards.

// ===========================================================================
// The upgrades
//
// One entry per upgrade, in id order. `effects` is a list of what taking it
// does, so an upgrade with a cost -- more damage for less health -- is one row
// rather than a special case.
//
//   { stat: "damage", op: "mul", value: 1.12 }   multiplies, so stacking compounds
//   { stat: "maxHp",  op: "add", value: 18 }     adds, so stacking is linear
//   { stat: "blades", op: "add", value: 1 }      a count is just an added stat
//
// A stat nobody mentions is 1 for `mul` and 0 for `add`; declare its base in
// BASES below when that is wrong.
// ===========================================================================
var UPGRADES = [
    { id: 0, name: "HEAVY SLUG", effects: [{ stat: "damage", op: "mul", value: 1.12 }] },
    { id: 1, name: "PLATING",    effects: [{ stat: "maxHp",  op: "add", value: 18 }] },
    { id: 2, name: "GLASS CANNON", effects: [
        { stat: "damage", op: "mul", value: 1.30 },
        { stat: "maxHp",  op: "add", value: -12 },
    ] },
];

// Stats whose "nothing taken" value is not the default (1 for mul, 0 for add).
var BASES = {};

// A floor and a ceiling per stat, applied after everything else. A cooldown
// multiplier that stacks its way to zero is a weapon with no cadence at all.
var CLAMPS = {};        // e.g. { cooldown: { min: 0.25, max: 1 } }

// ===========================================================================
// State
// ===========================================================================
var taken = [];         // ids, duplicates kept: stacking IS the design
var stats = {};

// ===========================================================================
// Lifecycle
// ===========================================================================

function onStart() { recompute(); }

// ===========================================================================
// Taking things
// ===========================================================================

/** Take one. Returns how many are now held of that id. */
function add(id) {
    taken.push(Number(id) | 0);
    recompute();
    return count(id);
}

/** Take one only if it is not already held. For the rare upgrade that is a flip. */
function addOnce(id) {
    if (count(id) > 0) { return 0; }
    add(id);
    return 1;
}

/** Give one back. Returns 1 if there was one to give. */
function remove(id) {
    var want = Number(id) | 0;
    for (var i = taken.length - 1; i >= 0; i--) {
        if (taken[i] === want) {
            taken.splice(i, 1);
            recompute();
            return 1;
        }
    }
    return 0;
}

function clearAll() {
    taken = [];
    recompute();
    return 1;
}

function count(id) {
    var want = Number(id) | 0;
    var n = 0;
    for (var i = 0; i < taken.length; i++) { if (taken[i] === want) { n++; } }
    return n;
}

function has(id)  { return count(id) > 0 ? 1 : 0; }
function total()  { return taken.length; }

/** The id at a position, so a caller can list what is held without an array. */
function idAt(index) {
    var i = Number(index) | 0;
    return (i >= 0 && i < taken.length) ? taken[i] : -1;
}

// ===========================================================================
// The stats
//
// Recomputed from `taken`, from scratch, every time it changes. This is the
// whole reason stacking is trustworthy: there is no accumulated state to drift,
// so removing an upgrade is as exact as adding one.
// ===========================================================================

function recompute() {
    stats = {};

    for (var i = 0; i < taken.length; i++) {
        var up = byId(taken[i]);
        if (!up || !up.effects) { continue; }

        for (var e = 0; e < up.effects.length; e++) {
            var effect = up.effects[e];
            var name = effect.stat;

            if (stats[name] === undefined) { stats[name] = baseFor(name, effect.op); }

            if (effect.op === "mul") { stats[name] = stats[name] * effect.value; }
            else { stats[name] = stats[name] + effect.value; }
        }
    }

    for (var key in CLAMPS) {
        if (stats[key] === undefined) { continue; }
        var clamp = CLAMPS[key];
        if (clamp.min !== undefined && stats[key] < clamp.min) { stats[key] = clamp.min; }
        if (clamp.max !== undefined && stats[key] > clamp.max) { stats[key] = clamp.max; }
    }

    onStatsChanged();
    return 1;
}

function baseFor(name, op) {
    if (BASES[name] !== undefined) { return BASES[name]; }
    return op === "mul" ? 1 : 0;
}

/** The value of a stat, or its base if nothing taken has touched it. */
function stat(name) {
    var key = String(name);
    if (stats[key] !== undefined) { return stats[key]; }
    if (BASES[key] !== undefined) { return BASES[key]; }
    return 0;
}

/** Like stat(), but for a multiplier, so an untouched one reads 1 rather than 0. */
function mul(name) {
    var key = String(name);
    if (stats[key] !== undefined) { return stats[key]; }
    if (BASES[key] !== undefined) { return BASES[key]; }
    return 1;
}

// ===========================================================================
// Readouts for whatever draws the cards
// ===========================================================================

function nameOf(id) {
    var up = byId(id);
    return up ? up.name : "";
}

function upgradeCount() { return UPGRADES.length; }

function idOfIndex(index) {
    var i = Number(index) | 0;
    return (i >= 0 && i < UPGRADES.length) ? UPGRADES[i].id : -1;
}

function byId(id) {
    var want = Number(id) | 0;
    for (var i = 0; i < UPGRADES.length; i++) { if (UPGRADES[i].id === want) { return UPGRADES[i]; } }
    return null;
}

// ===========================================================================
// The seam
//
// Called after every recompute. Anything that has to be re-derived rather than
// read -- a max-health change that should also heal, a re-sized collider, a
// visual that reflects a count -- goes here, and nowhere else.
// ===========================================================================

function onStatsChanged() { }
