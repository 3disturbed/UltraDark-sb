// Social.js -- Darks Games: who you are, what you are doing, and what you have done.
// Attach to an empty actor and tag it "Social".
//
// Everything here is optional at runtime. `DG` is installed by both engines
// whether or not the hub is reachable, and every member is safe signed out, so
// a build with no slug, a player who is offline, and the headless harness all
// run every line of this file and simply do nothing. That is the reason this is
// a script and not a special case inside the Director.
//
// It reads the run rather than being told about it. The Director already
// publishes everything a HUD needs -- phase, wave, score, kills, the multiplier
// -- so this polls those getters twice a second instead of threading a call
// into every place a thing can happen. A kill is the hottest event in the game
// and it does not need to know the hub exists.

// ---------------------------------------------------------------------------
// The achievements, exactly as the hub has them registered
// ---------------------------------------------------------------------------
//
// These keys must match `tools/dg-achievements.mjs`, which is what registers
// the definitions. A key with no definition on the hub is a 404 per report,
// which is silent from in here -- so the two lists are one list, kept in step
// by `tools/dg-check.mjs`.
var A_FIRST_BLOOD = "first_blood";     // one kill
var A_FIRST_BOSS  = "first_boss";      // any boss down
var A_THE_DARK    = "the_dark";        // reach the wave the lights go out
var A_DEEP_RUN    = "deep_run";        // reach wave 25
var A_TENFOLD     = "tenfold";         // the multiplier at its ceiling
var A_CURSED      = "cursed";          // take a cursed mod knowingly
var A_FULL_ROSTER = "full_roster";     // fly all eight pilots (counting, target 8)
var A_EXTERMINATE = "exterminator";    // 1000 kills (counting, target 1000)
var A_BOSS_SLAYER = "boss_slayer";     // 5 bosses (counting, target 5)
var A_ULTRADARK   = "ultradark";       // kill THE ULTRADARK itself

var DARK_WAVE = 16;      // matches the Director's darkFullWave
var DEEP_WAVE = 25;
var MULT_MAX  = 10;
var PILOTS    = 8;

// ---------------------------------------------------------------------------
// Tuning
// ---------------------------------------------------------------------------
var pollSeconds = 0.5;          // how often the run is read
var greetSeconds = 4;           // how long the sign-in line stays up

// Phase numbers, from the Director.
var P_HANGAR = 0, P_WAVE = 1, P_DRAFT = 2, P_SHOP = 3, P_DEAD = 4;

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------
var director = null, pilot = null, hud = null, upgrades = null;

var pollTimer = 0;
var wasSignedIn = 0;
var greeted = 0;

// What the hub already knows, so a repeat is not sent. `DG.achievement` is
// idempotent on the hub, but an unlocked achievement re-reported every half
// second is a request every half second.
var done = {};

// Counting achievements send the DIFFERENCE, not the total: the hub adds an
// increment to what it holds, so sending 400 twice is 800 kills.
var sentKills = 0;
var sentBosses = 0;

// Which pilots have been flown, as a bit per pilot. Carried in the cloud save,
// because "fly all eight" is a thing you do across runs and across devices.
var flownMask = 0;

var lastPhase = -1;
var lastState = "";

function onStart() {
    resolve();

    // The save arrives on an event rather than as a return value -- Jint cannot
    // await, and one contract has to describe both engines.
    DG.on("save", onCloudSave);
}

function resolve() {
    if (!director) { var d = Scene.findFirstByTag("Director"); if (d) { director = d.getComponent("ScriptComponent"); } }
    if (!pilot)    { var p = Scene.findFirstByTag("Player");   if (p) { pilot = p.getComponent("ScriptComponent"); } }
    if (!hud)      { var h = Scene.findFirstByTag("Hud");      if (h) { hud = h.getComponent("ScriptComponent"); } }
    if (!upgrades) { var u = Scene.findFirstByTag("Upgrades"); if (u) { upgrades = u.getComponent("ScriptComponent"); } }
}

// ---------------------------------------------------------------------------
// Frame
// ---------------------------------------------------------------------------

function onUpdate(dt) {
    pollTimer -= dt;
    if (pollTimer > 0) { return; }
    pollTimer = pollSeconds;

    resolve();
    if (!director) { return; }

    signIn();

    var phase = num(director.call("getPhase"), P_HANGAR);
    presence(phase);
    check(phase);

    // The run ended between this poll and the last one: bank it.
    if (phase === P_DEAD && lastPhase !== P_DEAD) { writeSave(); }
    lastPhase = phase;
}

/** Notices the moment the player signs in, and asks for their save once. */
function signIn() {
    var now = DG.signedIn ? 1 : 0;
    if (now === wasSignedIn) { return; }
    wasSignedIn = now;

    if (!now) { greeted = 0; return; }

    // displayName is never null, even signed out, so this line always reads.
    if (hud && !greeted) { hud.call("say", "WELCOME  " + DG.displayName); greeted = 1; }
    DG.loadSave();
}

// ---------------------------------------------------------------------------
// Presence -- what a friend sees on their list
// ---------------------------------------------------------------------------

function presence(phase) {
    var state = "";
    var detail = "";

    if (phase === P_HANGAR) {
        state = "hangar";
        detail = "picking a pilot";
    } else if (phase === P_DEAD) {
        state = "run over";
        detail = "wave " + num(director.call("getWave"), 0)
               + " - " + num(director.call("getScore"), 0);
    } else {
        var wave = num(director.call("getWave"), 0);
        state = "wave " + wave;

        // The two things worth saying out loud. A friends list is one line, so
        // it says the most interesting true thing rather than everything.
        if (num(director.call("isBossAlive"), 0) > 0) { detail = "boss fight"; }
        else if (wave >= DARK_WAVE) { detail = "in the dark"; }
        else if (pilot) { detail = String(pilot.call("getPilotName")); }
    }

    // An identical update is dropped by the engine anyway; skipping it here
    // keeps this from being the reason a script call happens at all.
    if (state === lastState) { return; }
    lastState = state;

    DG.presence({ state: state, detail: detail });
}

// ---------------------------------------------------------------------------
// Achievements
// ---------------------------------------------------------------------------

function check(phase) {
    var kills = num(director.call("getKills"), 0);
    var bosses = num(director.call("getBossKills"), 0);
    var wave = num(director.call("getWave"), 0);

    if (kills > 0) { unlock(A_FIRST_BLOOD); }
    if (bosses > 0) { unlock(A_FIRST_BOSS); }

    // Reaching the dark and the deep run are about the wave you REACHED, so
    // they are read while the run is live rather than after it ends.
    if (phase !== P_HANGAR) {
        if (wave >= DARK_WAVE) { unlock(A_THE_DARK); }
        if (wave >= DEEP_WAVE) { unlock(A_DEEP_RUN); }
    }

    if (num(director.call("getMult"), 1) >= MULT_MAX - 0.001) { unlock(A_TENFOLD); }
    if (num(director.call("killedUltradark"), 0) > 0) { unlock(A_ULTRADARK); }

    if (upgrades && !done[A_CURSED] && cursedHeld()) { unlock(A_CURSED); }

    // The counting ones. Only the new part is sent.
    if (kills > sentKills) { DG.achievement(A_EXTERMINATE, kills - sentKills); sentKills = kills; }
    if (bosses > sentBosses) { DG.achievement(A_BOSS_SLAYER, bosses - sentBosses); sentBosses = bosses; }

    notePilot();
}

/** True while the pilot is holding any of the rarity-3 cursed mods. */
function cursedHeld() {
    var total = num(upgrades.call("total"), 0);
    for (var i = 0; i < total; i++) {
        var id = num(upgrades.call("idAt", i), -1);
        if (id >= 0 && num(upgrades.call("isCursed", id), 0) > 0) { return 1; }
    }
    return 0;
}

/**
 * Counts a pilot the first time they are flown.
 *
 * A bit per pilot rather than a count, because a count cannot tell eight runs
 * as BINK from one run as each of the eight -- and the difference is the whole
 * achievement.
 */
function notePilot() {
    if (!pilot) { return; }
    var index = num(pilot.call("getPilot"), -1);
    if (index < 0 || index >= PILOTS) { return; }

    var bit = 1 << index;
    if (flownMask & bit) { return; }
    flownMask = flownMask | bit;

    DG.achievement(A_FULL_ROSTER, 1);
    if (bits(flownMask) >= PILOTS) { done[A_FULL_ROSTER] = 1; }
}

/** Sends an unlock once per session. */
function unlock(key) {
    if (done[key]) { return; }
    done[key] = 1;
    DG.achievement(key);
}

// ---------------------------------------------------------------------------
// The cloud save
// ---------------------------------------------------------------------------
//
// Small on purpose: the best of a run and which pilots have been flown. A
// roguelite's run does not survive a reload, so there is nothing else here that
// would still be true tomorrow.

function onCloudSave(data) {
    if (!data) { return; }

    var mask = num(data.flownMask, 0);
    // Merged, not replaced. Another device's mask is a different subset of the
    // same eight, and whichever save arrives last must not be the only one that
    // counted.
    flownMask = flownMask | mask;

    var best = num(data.bestScore, 0);
    if (best > 0 && director) { director.call("setBest", best); }

    var kills = num(data.kills, 0);
    if (kills > sentKills) { sentKills = kills; }
    var bosses = num(data.bosses, 0);
    if (bosses > sentBosses) { sentBosses = bosses; }

    log("DG: save restored -- best " + best + ", " + bits(flownMask) + "/8 pilots flown");
}

function writeSave() {
    if (!DG.signedIn || !director) { return; }
    DG.saveCloud({
        bestScore: num(director.call("getBest"), 0),
        bestWave: num(director.call("getWave"), 0),
        kills: sentKills,
        bosses: sentBosses,
        flownMask: flownMask,
    }, 1);
}

// ---------------------------------------------------------------------------
// Bits and pieces
// ---------------------------------------------------------------------------

/**
 * A cross-script read that has not initialised yet returns `undefined`, and
 * `undefined` in arithmetic is NaN, which spreads in silence.
 */
function num(value, fallback) {
    var v = Number(value);
    return v === v ? v : fallback;
}

function bits(mask) {
    var n = 0;
    for (var i = 0; i < 32; i++) { if (mask & (1 << i)) { n++; } }
    return n;
}

// ---------------------------------------------------------------------------
// Readouts, for the tests and for anything that wants to show this
// ---------------------------------------------------------------------------
function isSignedIn()   { return DG.signedIn ? 1 : 0; }
function isAvailable()  { return DG.available ? 1 : 0; }
function getName()      { return DG.displayName; }
function getFlownMask() { return flownMask; }
function getFlownCount(){ return bits(flownMask); }
function isDone(key)    { return done[String(key)] ? 1 : 0; }
function getState()     { return lastState; }
