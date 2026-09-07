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
// UltraDark's general mod pool: four families, three rarities, and the
// rarity-3 "cursed" ones that cost you something. Names and numbers are the
// game's own, from its shared/mods.js.
var UPGRADES = [
    // ---- Ballistics ------------------------------------------------------
    { id: 0,  name: "Piercer",       family: 0, rarity: 1, desc: "Bullets pass through +1 enemy.",   effects: [{ stat: "pierce", op: "add", value: 1 }] },
    { id: 1,  name: "Ricochet",      family: 0, rarity: 1, desc: "Bullets bounce off walls once.",   effects: [{ stat: "ricochet", op: "add", value: 1 }] },
    { id: 2,  name: "Splitter",      family: 0, rarity: 2, desc: "Fire +1 bullet in a spread.",      effects: [{ stat: "pellets", op: "add", value: 1 }] },
    { id: 3,  name: "Heavy Rounds",  family: 0, rarity: 2, desc: "+60% damage, -20% fire rate.",     effects: [{ stat: "damage", op: "mul", value: 1.6 }, { stat: "cooldown", op: "mul", value: 1.25 }] },
    { id: 4,  name: "Overclock",     family: 0, rarity: 1, desc: "+25% fire rate.",                  effects: [{ stat: "cooldown", op: "mul", value: 0.8 }] },
    { id: 5,  name: "Railshot",      family: 0, rarity: 1, desc: "+35% bullet speed.",               effects: [{ stat: "projSpeed", op: "mul", value: 1.35 }] },
    { id: 6,  name: "Long Barrel",   family: 0, rarity: 1, desc: "+40% bullet range.",               effects: [{ stat: "projSpeed", op: "mul", value: 1.4 }] },
    { id: 7,  name: "Railgun Coils", family: 0, rarity: 2, desc: "+60% bullet speed, pierce +1.",    effects: [{ stat: "projSpeed", op: "mul", value: 1.6 }, { stat: "pierce", op: "add", value: 1 }] },
    { id: 8,  name: "Gunslinger",    family: 0, rarity: 1, desc: "+15% fire rate, +10% damage.",     effects: [{ stat: "cooldown", op: "mul", value: 0.87 }, { stat: "damage", op: "mul", value: 1.1 }] },
    { id: 9,  name: "Heavyweight",   family: 0, rarity: 2, desc: "+30% damage, -8% move speed.",     effects: [{ stat: "damage", op: "mul", value: 1.3 }, { stat: "speed", op: "mul", value: 0.92 }] },
    // ---- Field -----------------------------------------------------------
    { id: 10, name: "Orbital",       family: 1, rarity: 2, desc: "A blade orbits you.",              effects: [{ stat: "blades", op: "add", value: 1 }] },
    { id: 11, name: "Twin Orbital",  family: 1, rarity: 3, desc: "TWO more orbiting blades.",        effects: [{ stat: "blades", op: "add", value: 2 }] },
    { id: 12, name: "Dash Nova",     family: 1, rarity: 2, desc: "Dashing releases a shockwave.",    effects: [{ stat: "kinetic", op: "add", value: 1 }] },
    { id: 13, name: "Nova Core",     family: 1, rarity: 2, desc: "Stronger dash wave, dash -0.2s.",  effects: [{ stat: "kinetic", op: "add", value: 1 }, { stat: "dashBonus", op: "add", value: 0.2 }] },
    { id: 14, name: "Static Coil",   family: 1, rarity: 2, desc: "Periodically zaps the nearest.",   effects: [{ stat: "static", op: "add", value: 1 }] },
    { id: 15, name: "Thorn Plating", family: 1, rarity: 1, desc: "Enemies that touch you take damage.", effects: [{ stat: "thorns", op: "add", value: 1 }] },
    { id: 16, name: "Yield Boost",   family: 1, rarity: 1, desc: "Smart bombs deal +1 damage.",      effects: [{ stat: "bombPower", op: "add", value: 1 }] },
    // ---- Chassis ---------------------------------------------------------
    { id: 17, name: "Thrusters",     family: 2, rarity: 1, desc: "+15% move speed.",                 effects: [{ stat: "speed", op: "mul", value: 1.15 }] },
    { id: 18, name: "Twin Dash",     family: 2, rarity: 2, desc: "+1 dash charge.",                  effects: [{ stat: "dashCharges", op: "add", value: 1 }] },
    { id: 19, name: "Featherframe",  family: 2, rarity: 1, desc: "+10% speed, faster dash recovery.", effects: [{ stat: "speed", op: "mul", value: 1.1 }, { stat: "dashBonus", op: "add", value: 0.3 }] },
    { id: 20, name: "Plating",       family: 2, rarity: 2, desc: "+1 max HP (and heal 1 now).",      effects: [{ stat: "maxHp", op: "add", value: 1 }] },
    { id: 21, name: "Overshield",    family: 2, rarity: 1, desc: "+50% longer invulnerability.",     effects: [{ stat: "iframes", op: "mul", value: 1.5 }] },
    { id: 22, name: "Sprinter",      family: 2, rarity: 1, desc: "+8% speed, +8% fire rate.",        effects: [{ stat: "speed", op: "mul", value: 1.08 }, { stat: "cooldown", op: "mul", value: 0.926 }] },
    // ---- Echo ------------------------------------------------------------
    { id: 23, name: "Bounty Chip",   family: 3, rarity: 1, desc: "+25% score from your kills.",      effects: [{ stat: "scoreBonus", op: "mul", value: 1.25 }] },
    { id: 24, name: "Volatile",      family: 3, rarity: 2, desc: "Your kills explode.",              effects: [{ stat: "shockwave", op: "add", value: 1 }] },
    { id: 25, name: "Shrapnel",      family: 3, rarity: 2, desc: "Bigger kill explosions.",          effects: [{ stat: "shockwave", op: "add", value: 1 }] },
    { id: 26, name: "Bloodrush",     family: 3, rarity: 1, desc: "Kills shave dash cooldown.",       effects: [{ stat: "bloodrush", op: "add", value: 0.15 }] },
    { id: 27, name: "Momentum",      family: 3, rarity: 1, desc: "+30% damage for 1s after dashing.", effects: [{ stat: "momentum", op: "add", value: 1 }] },
    { id: 28, name: "Kill Streak",   family: 3, rarity: 2, desc: "Every 25th kill grants a bomb.",   effects: [{ stat: "coreTap", op: "add", value: 1 }] },
    { id: 29, name: "Grudge Core",   family: 3, rarity: 2, desc: "Hits keep 70% of your multiplier.", effects: [{ stat: "grudge", op: "add", value: 1 }] },
    { id: 30, name: "Adrenal Loop",  family: 3, rarity: 1, desc: "+50% fire rate for 3s after a hit.", effects: [{ stat: "adrenaline", op: "add", value: 1 }] },
    { id: 31, name: "Scavenger",     family: 3, rarity: 1, desc: "+15% score, +5% cores.",           effects: [{ stat: "scoreBonus", op: "mul", value: 1.15 }, { stat: "coreBonus", op: "mul", value: 1.05 }] },
    // ---- Cursed: rarity 3, and every one of them costs you something -----
    { id: 32, name: "Glass Cannon",  family: 0, rarity: 3, cursed: 1, desc: "+80% damage. Dash cooldown +1s.", effects: [{ stat: "damage", op: "mul", value: 1.8 }, { stat: "dashBonus", op: "add", value: -1 }] },
    { id: 33, name: "Berserker",     family: 3, rarity: 3, cursed: 1, desc: "+40% fire rate. -1 max HP.",      effects: [{ stat: "cooldown", op: "mul", value: 0.714 }, { stat: "maxHp", op: "add", value: -1 }] },
    { id: 34, name: "Scattergun",    family: 0, rarity: 3, cursed: 1, desc: "+2 split bullets. -25% damage.",  effects: [{ stat: "pellets", op: "add", value: 2 }, { stat: "damage", op: "mul", value: 0.75 }] },
    { id: 35, name: "Turtle Shell",  family: 2, rarity: 3, cursed: 1, desc: "+2 max HP. -15% move speed.",     effects: [{ stat: "maxHp", op: "add", value: 2 }, { stat: "speed", op: "mul", value: 0.85 }] },
    { id: 36, name: "Gambler's Coil",family: 3, rarity: 3, cursed: 1, desc: "+50% score. A hit drops you to x1.", effects: [{ stat: "scoreBonus", op: "mul", value: 1.5 }, { stat: "gambler", op: "add", value: 1 }] },
];

var FAMILY_NAME = ["BALLISTICS", "FIELD", "CHASSIS", "ECHO"];
var FAMILY_HEX  = ["#ff7a3d", "#39f0ff", "#b8ff5e", "#c26bfa"];

function familyOf(id)    { var u = byId(id); return u ? u.family : 0; }
function familyName(id)  { return FAMILY_NAME[familyOf(id)]; }
function familyHex(id)   { return FAMILY_HEX[familyOf(id)]; }
function rarityOf(id)    { var u = byId(id); return u ? u.rarity : 1; }
function isCursed(id)    { var u = byId(id); return (u && u.cursed) ? 1 : 0; }
function descOf(id)      { var u = byId(id); return u ? u.desc : ""; }

// Stats whose "nothing taken" value is not the default (1 for mul, 0 for add).
var BASES = {};

// A floor and a ceiling per stat, applied after everything else. A cooldown
// multiplier that stacks its way to zero is a weapon with no cadence at all.
// Cooldown stacking has to stop somewhere: enough RAPID FEED and a weapon has
// no cadence at all, which is not a build, it is the absence of one.
// A fire-rate multiplier that stacks its way to nothing is a weapon with no
// cadence at all.
var CLAMPS = { cooldown: { min: 0.25 } };

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
