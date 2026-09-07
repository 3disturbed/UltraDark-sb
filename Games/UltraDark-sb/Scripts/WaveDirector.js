// WaveDirector.js -- endless escalating waves, a boss on a cadence, and a wave
// that always ends.
// Attach to one manager actor and tag it "Waves".
//
// This owns the shape of a run and nothing about its content. It decides how
// much a wave is worth, when to spend it, when the wave is over, when a boss is
// due and when something has gone wrong -- and it asks a script you name to do
// all the actual spawning. Swap that script and the same pacing drives a
// different game.
//
// The shape:
//
//   begin() -> WAVE 1 -> (cleared) -> WAITING -> resume() -> WAVE 2 -> ...
//
// It stops at WAITING rather than rolling straight on, because between waves is
// where a game wants to put a draft, a shop, a breather or a cutscene. Nothing
// happens again until the caller says resume().
//
// There is no last wave. `bossFor` cycles for ever and the budget keeps
// climbing, so a run ends when the player does.

// ===========================================================================
// Tuning -- how much a wave is worth
//
// A wave is a BUDGET, not a headcount. Content prices its own spawns, so a
// wave gets harder without simply getting longer: at the same budget a game can
// send forty weak things or four heavy ones, and the wave takes about as long.
// ===========================================================================
// UltraDark's own: budget = (50 + wave * 35) x (0.65 + 0.35 x players), and a
// solo run is exactly x1.0 of that. It is LINEAR -- the original does not
// compound, and it does not need to: a wave gets harder by sending more things,
// never by making the same things spongier.
var baseBudget    = 50;
var budgetPerWave = 35;
var budgetCurve   = 1.0;     // no compounding: the original's climb is linear

// ===========================================================================
// Tuning -- pacing
// ===========================================================================
var trickle       = 0.55;    // the original trickles a wave in over about 42s
var startDelay    = 0;       // seconds before the first spawn of a wave

// How many things may be alive at once. The budget is never capped -- a late
// wave still sends everything it was going to -- it just converts into spawns
// no faster than they are cleared. That turns a big wave from one unaffordable
// blob into sustained pressure, and it is a performance ceiling as much as a
// design one. 0 disables it.
var concurrentCap = 105;

// ===========================================================================
// Tuning -- bosses
// ===========================================================================
var bossEvery     = 5;       // 0 for no bosses at all
var bossKinds     = 5;       // how many distinct bosses to cycle through

// A boss wave still sends an escort, at this share of the ordinary budget. Too
// low and the arena is empty around the fight; too high and the boss is the
// least of your problems.
var bossBudgetShare = 0.45;

// ===========================================================================
// Tuning -- the stall-breaker
//
// A wave with nothing left to send and something still alive somewhere has to
// end, and it will not always end on its own: anything rooted, anything that
// keeps its distance, anything that wandered into a corner of a big arena. The
// player is then hunting one straggler across the map, which is not difficulty.
//
// After this many seconds, content is told to go and find them.
// ===========================================================================
var stallAfter    = 22;      // 0 to never fire

// ===========================================================================
// Who does the spawning
//
// The script on this tag must answer:
//
//   spawnOne(wave, budgetLeft)   spawn something; return what it cost (>0), or
//                                0 if it could not spawn anything at all
//   aliveCount()                 how many things are alive
//
// and may answer, if it wants to:
//
//   startBoss(wave, index)       create the boss; return 1 if one now exists
//   bossAlive()                  1 while it lives
//   onWaveStart(wave, isBoss)
//   onWaveCleared(wave, isBoss)  the seam for a draft, a shop, a breather
//   onWaveStalled(wave)                go and find the stragglers
// ===========================================================================
var contentTag = "Director";

// ===========================================================================
// Phases
// ===========================================================================
var IDLE = 0, RUNNING = 1, WAITING = 2;

// ===========================================================================
// State
// ===========================================================================
var phase = IDLE;
var wave = 0;
var budgetLeft = 0;
var spawnTimer = 0;
var waveTimer = 0;
var stalled = 0;
var bossThisWave = 0;

var content = null;

// ===========================================================================
// Lifecycle
// ===========================================================================

function onStart() { resolve(); }

function resolve() {
    if (content) { return; }
    var a = Scene.findFirstByTag(contentTag);
    if (a) { content = a.getComponent("ScriptComponent"); }
}

function onUpdate(dt) {
    resolve();
    if (phase !== RUNNING) { return; }
    if (!content) { return; }

    waveTimer += dt;

    if (budgetLeft > 0) {
        spawnTimer -= dt;
        if (spawnTimer <= 0) {
            spawnTimer = trickle;
            trySpawn();
        }
    }

    var alive = content.call("aliveCount");
    if (!(alive >= 0)) { alive = 0; }

    var bossUp = bossThisWave ? content.call("bossAlive") : 0;

    // Nothing left to send, something still out there, and it has been a while.
    if (!stalled && stallAfter > 0 && budgetLeft <= 0 && alive > 0 && waveTimer > stallAfter) {
        stalled = 1;
        content.call("onWaveStalled", wave);
    }

    if (budgetLeft <= 0 && alive <= 0 && !bossUp) { clearWave(); }
}

// ===========================================================================
// Spending
// ===========================================================================

function trySpawn() {
    // Hold the budget rather than spending it into a full arena: a spend that
    // produces nothing is a wave that gets shorter the worse it is going.
    if (concurrentCap > 0) {
        var alive = content.call("aliveCount");
        if (alive >= concurrentCap) { return 0; }
    }

    var cost = content.call("spawnOne", wave, budgetLeft);
    if (!(cost > 0)) {
        // Content could not spawn anything for what is left. Do not sit on it.
        budgetLeft = 0;
        return 0;
    }

    budgetLeft -= cost;
    if (budgetLeft < 0) { budgetLeft = 0; }
    return cost;
}

// ===========================================================================
// The wave
// ===========================================================================

function begin() {
    wave = 0;
    phase = WAITING;
    resume();
    return wave;
}

function resume() {
    wave++;
    startWave();
    return wave;
}

function startWave() {
    phase = RUNNING;
    waveTimer = 0;
    spawnTimer = startDelay;
    stalled = 0;
    bossThisWave = isBossWave(wave);

    // A boss wave still sends a thinner escort, so the arena is never empty
    // around the fight.
    budgetLeft = bossThisWave ? Math.floor(waveBudget(wave) * bossBudgetShare) : waveBudget(wave);

    if (content) {
        content.call("onWaveStart", wave, bossThisWave);
        if (bossThisWave) {
            var made = content.call("startBoss", wave, bossFor(wave));
            if (!made) { bossThisWave = 0; }   // content declined; treat it as an ordinary wave
        }
    }
    return wave;
}

function clearWave() {
    phase = WAITING;
    if (content) { content.call("onWaveCleared", wave, bossThisWave); }
    return wave;
}

function stop() { phase = IDLE; return 1; }

// ===========================================================================
// The curve
// ===========================================================================

function waveBudget(w) {
    var n = Number(w) || 0;
    return Math.floor((baseBudget + budgetPerWave * n) * Math.pow(budgetCurve, n));
}

function isBossWave(w) {
    if (bossEvery <= 0) { return 0; }
    return ((Number(w) | 0) % bossEvery === 0) ? 1 : 0;
}

// Which boss this wave is. Past the last one they cycle, for ever: there is no
// wave at which the run is won, and adding one is the single change that would
// most alter what an endless mode is.
function bossFor(w) {
    var index = Math.floor((Number(w) | 0) / bossEvery) - 1;
    if (index < 0) { index = 0; }
    return index % bossKinds;
}

// ===========================================================================
// Readouts and seams
// ===========================================================================

function getWave()       { return wave; }
function getPhase()      { return phase; }
function isRunning()     { return phase === RUNNING ? 1 : 0; }
function isWaiting()     { return phase === WAITING ? 1 : 0; }
function getBudgetLeft() { return budgetLeft; }
function getWaveTimer()  { return waveTimer; }
function isStalled()     { return stalled; }
function isBossThisWave(){ return bossThisWave; }
function getConcurrentCap() { return concurrentCap; }

// Jump straight to a wave. A test seam, a debug key, a "start at wave 20" mode.
function forceWave(w) {
    wave = Math.max(1, Number(w) | 0);
    startWave();
    return wave;
}

// Stop the current wave sending anything else, so a test can work with what it
// placed rather than with whatever the wave felt like adding.
function forceBudget(n) {
    budgetLeft = Math.max(0, Number(n) | 0);
    return budgetLeft;
}
