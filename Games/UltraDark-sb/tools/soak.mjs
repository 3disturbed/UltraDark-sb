// soak.mjs -- play UltraDark headlessly for minutes of game time and report.
//
// The bundled template smoke test runs sixty frames with no host at all. This
// runs minutes of real play through the same host every other tool here uses
// (tools/harness.mjs), which is the point: a soak whose host is missing a
// system is a soak that reports the game works without ever running half of it.
//
//   node tools/soak.mjs                 # ~6 minutes of game time
//   node tools/soak.mjs --minutes 20    # a long run
//   node tools/soak.mjs --wave 14       # start in the dark
//   node tools/soak.mjs --pilot 3       # fly DAVE
//   node tools/soak.mjs --no-assist     # let the bot die

import { boot, VIEWPORT } from './harness.mjs';

// ---------------------------------------------------------------------------
// Arguments
// ---------------------------------------------------------------------------
const argv = process.argv.slice(2);
const arg = (name, fallback) => {
    const i = argv.indexOf(`--${name}`);
    return i >= 0 && argv[i + 1] !== undefined ? Number(argv[i + 1]) : fallback;
};
const minutes = arg('minutes', 6);
const startWave = arg('wave', 0);
const pilotIndex = arg('pilot', 0);
const verbose = argv.includes('--verbose');

// The bot is a bad player, and a bad player dies on the wave-5 boss for ever.
// That makes an unassisted soak a test of the bot rather than of the game: it
// never reaches wave 16, so the dark -- the thing the title is about -- and the
// late roster and the boss cycle are never exercised at all.
//
// So the default keeps it alive and lets the run go deep. --no-assist turns it
// off, which is a rough read on how far an indifferent player gets unaided.
const assist = !argv.includes('--no-assist');

const totalFrames = Math.round(minutes * 60 * 60);

const g = boot();
const { scene, errors, logs, find } = g;
const press = g.press;
const hold = g.hold;

const director = g.director();
const swarm = g.swarm();
const bullets = g.bullets();
const pilotScript = g.pilot();
if (!director || !swarm || !bullets || !pilotScript) {
    g.restore();
    console.error('soak: a manager script is missing from the scene');
    process.exit(1);
}

// ---------------------------------------------------------------------------
// The bot: kite the nearest enemy, hold fire, use everything on cooldown.
// It is not good at the game, and does not need to be -- it needs to make the
// systems interact for a long time without a human.
// ---------------------------------------------------------------------------
function playerActor() { return find('Player'); }

function steer() {
    const p = playerActor();
    if (!p) return;

    let nearest = null, bestD = Infinity;
    for (const a of scene.allActors) {
        if (a.isDestroyed || (a.tag !== 'Enemy' && a.tag !== 'Boss')) continue;
        const dx = a.transform.x - p.transform.x, dy = a.transform.y - p.transform.y;
        const d = dx * dx + dy * dy;
        if (d < bestD) { bestD = d; nearest = a; }
    }

    let wx = 0, wy = 0;
    if (nearest && bestD < 460 * 460) {
        // Back off, and strafe rather than running in a straight line, which is
        // what walks a player into the next thing.
        const dx = p.transform.x - nearest.transform.x;
        const dy = p.transform.y - nearest.transform.y;
        wx = dx - dy * 0.7;
        wy = dy + dx * 0.7;

        // Do not reverse into the wall: the arena edge is where a bot dies.
        if (Math.abs(p.transform.x) > 1050) { wx = -p.transform.x * 0.5; }
        if (Math.abs(p.transform.y) > 1050) { wy = -p.transform.y * 0.5; }
    } else {
        // Circle the arena so the bot keeps meeting things rather than parking.
        const t = frame / 90;
        wx = Math.cos(t) * 100 - p.transform.x * 0.01;
        wy = Math.sin(t) * 100 - p.transform.y * 0.01;
    }

    hold('W', wy < -20); hold('S', wy > 20);
    hold('A', wx < -20); hold('D', wx > 20);
}

// ---------------------------------------------------------------------------
// Run
// ---------------------------------------------------------------------------
const stats = {
    maxWave: 0, maxActors: 0, maxEnemies: 0, maxBullets: 0,
    drafts: 0, deaths: 0, darkSeen: 0, bossWaves: 0, runs: 0, wavesPlayed: 0,
};

let frame = 0;
g.mouse(0, true);                 // hold fire for the whole run

director.invoke('forceLaunch');
if (pilotIndex > 0) pilotScript.invoke('setPilot', pilotIndex);
if (startWave > 0) director.invoke('forceWave', startWave);

let lastPhase = -1;
let lastWave = 0;
let waveStartFrame = 0;
const waveSeconds = [];
const t0 = Date.now();

// The actor count with nothing in play. Anything the run cannot get back below
// this is something a pool forgot to return.
//
// Taken after the stage has settled, not at boot: the 3D stage builds a camera,
// two lights, a ground and -- once the pilot exists -- a MakeChibi character,
// which is forty-five actors of permanent scenery. Measuring before that reports
// the pilot's own body as a leak.
await g.step(30);
const baselineActors = scene.allActors.filter((a) => !a.isDestroyed).length;

for (frame = 0; frame < totalFrames; frame++) {
    steer();

    const phase = director.invoke('getPhase');

    // Take the middle card whenever the draft opens, and leave the shop after a
    // moment, so the run keeps moving without a keyboard.
    if (phase === 2 && lastPhase !== 2) { stats.drafts++; press('D2'); }
    if (phase === 3 && frame % 90 === 0) { press('D1'); press('Enter'); }
    if (phase === 4 && lastPhase !== 4) { stats.deaths++; press('R'); }

    // Back in the hangar after a wipe: launch again. Without this the soak
    // spends the rest of its run parked, and reports a wave ceiling that is
    // really just the bot dying once.
    if (phase === 0 && lastPhase !== 0) { press('Enter'); }
    lastPhase = phase;

    // Fire the ability and a consumable whenever they are ready.
    if (frame % 45 === 0) press('Space');
    if (frame % 200 === 0) press('F');
    if (frame % 140 === 0) press('LeftShift');

    // Every half second, not every three. The pilot has THREE hit points and a
    // one-second grace after each; on the old hundred-point scale a three-second
    // cadence was generous, and on this one it is a death sentence.
    if (assist && frame % 30 === 0) { pilotScript.invoke('heal', 9); }

    await g.step(1);

    const wave = director.invoke('getWave');
    const enemies = swarm.invoke('alive');
    const shots = bullets.invoke('count');
    const actors = scene.allActors.filter((a) => !a.isDestroyed).length;

    if (wave !== lastWave) {
        if (lastWave > 0) { waveSeconds[lastWave] = (frame - waveStartFrame) / 60; }
        if (wave > 0) { stats.wavesPlayed++; }
        lastWave = wave;
        waveStartFrame = frame;
    }
    if (wave > stats.maxWave) { stats.maxWave = wave; }
    if (actors > stats.maxActors) { stats.maxActors = actors; }
    if (enemies > stats.maxEnemies) { stats.maxEnemies = enemies; }
    if (shots > stats.maxBullets) { stats.maxBullets = shots; }
    if (director.invoke('getDarkness') > 0.5) { stats.darkSeen = 1; }
    if (director.invoke('isBossAlive')) { stats.bossWaves = 1; }
}

const wall = (Date.now() - t0) / 1000;

// Clear the field and let the pools take everything back, then count. An actor
// count taken mid-wave says nothing -- of course things are alive. What matters
// is whether the scene can return to where it started.
// Stop the tap BEFORE emptying the bucket. Clearing the field while the
// director is still spawning measures how fast the game refills, not whether
// anything leaked: four seconds of settling was enough for a live wave to put
// twenty enemies back, and the count swung by forty between identical runs.
director.invoke('forceBudget', 0);
swarm.invoke('clearAll');
bullets.invoke('clearAll');
const bossLeft = scene.allActors.find((a) => a.tag === 'Boss' && !a.isDestroyed);
if (bossLeft) { bossLeft.destroy(); }
await g.step(240);
const settledActors = scene.allActors.filter((a) => !a.isDestroyed).length;
// The leak measurement: what is still SWITCHED ON with the field cleared.
//
// Counting everything undestroyed counted the pools too, and a pool holding a
// hundred parked shots is a pool doing its job -- the number tracked the peak of
// the run rather than anything being lost. It moved between 233 and 495 across
// identical runs, so the check was really a slow coin toss with a 400-wide
// slack bolted on to hide it.
//
// A parked actor is inactive; a leaked one is not. Nothing should be alive on an
// empty field but the fixtures the scene started with.
const liveActors = scene.allActors.filter((a) => !a.isDestroyed && a.isActive).length;

g.restore();

// ---------------------------------------------------------------------------
// Report
// ---------------------------------------------------------------------------

// The log is the only place a boss says it arrived and died, so it is what the
// gate below reads: a boss that spawns and never dies is a wave that never ends.
const bossSpawns = logs.filter((l) => l.indexOf('>>>') >= 0).length;
const bossKills = logs.filter((l) => l.indexOf('BOSS DOWN') >= 0).length;
const waveLines = logs.filter((l) => l.indexOf('=== WAVE') >= 0).length;

console.log('--- UltraDark soak ---------------------------------------');
console.log(`  game time      ${minutes} min (${totalFrames} frames) in ${wall.toFixed(1)}s wall`);
console.log(`  reached wave   ${stats.maxWave}`);
console.log(`  drafts taken   ${stats.drafts}`);
console.log(`  bosses killed  ${bossKills}  (of ${bossSpawns} met)`);
console.log(`  runs / deaths  ${stats.deaths + 1} / ${stats.deaths}`);
console.log(`  peak enemies   ${stats.maxEnemies}`);
console.log(`  peak shots     ${stats.maxBullets}`);
console.log(`  peak actors    ${stats.maxActors}   (settled to ${settledActors}, of which ${liveActors} live; baseline ${baselineActors})`);

const timed = waveSeconds.map((sec, w) => (sec ? `${w}:${sec.toFixed(0)}s` : null)).filter(Boolean);
if (timed.length) { console.log(`  wave lengths   ${timed.join('  ')}`); }
console.log(`  score          ${director.invoke('getScore')}`);
console.log(`  the dark       ${stats.darkSeen ? 'yes' : 'not reached'}`);
console.log(`  assist         ${assist ? 'on (use --no-assist for an unaided run)' : 'off'}`);
console.log(`  script errors  ${errors.length}`);

for (const e of errors.slice(0, 12)) { console.log(`    ${e}`); }

// ---------------------------------------------------------------------------
// What makes this a gate rather than a printout
// ---------------------------------------------------------------------------
const failures = [];

if (errors.length > 0) { failures.push(`${errors.length} script error(s)`); }
if (stats.maxWave < 2) { failures.push(`only reached wave ${stats.maxWave} -- waves are not advancing`); }
if (stats.maxEnemies === 0) { failures.push('no enemy ever spawned'); }
if (stats.maxBullets === 0) { failures.push('no projectile ever fired'); }
if (stats.bossWaves && bossSpawns === 0) { failures.push('a boss wave started but no boss announced itself'); }
if (bossSpawns > 2 && bossKills === 0) { failures.push(`${bossSpawns} bosses spawned across the run and none ever died`); }
if (waveLines < stats.wavesPlayed) { failures.push(`${stats.wavesPlayed} waves were played and only ${waveLines} announced themselves`); }

// With the bot kept alive, a long run has to actually go somewhere: past the
// boss, into the late roster, and into the dark. Anything less means a wave is
// stalling rather than the pilot dying.
// A run that starts deep must actually meet the dark: that is the one feature a
// shallow run can never cover, so `--wave 14` is the gate for it.
if (assist && startWave >= 14) {
    if (!stats.darkSeen) { failures.push('a run starting in the dark never saw it'); }
    if (stats.maxWave < startWave + 2) { failures.push(`a deep run did not advance past wave ${stats.maxWave}`); }
}
if (assist && minutes >= 15 && startWave === 0) {
    if (stats.maxWave < 8) { failures.push(`an assisted ${minutes}-minute run only reached wave ${stats.maxWave}`); }
    if (bossKills === 0) { failures.push('an assisted run never killed a boss'); }
}

// Pools are what make the game affordable, so a pool that forgets to hand an
// actor back is a real defect -- but a pool HOLDING actors is not one, and the
// old form of this check could not tell the two apart. It counted every
// undestroyed actor, so it counted the pools, so it tracked the peak of the run
// and failed on the good runs: a fix that let bosses die pushed the game further
// and tripped it. Wrong thing measured, and generously enough to hide that.
//
// Alive-on-an-empty-field is the thing that cannot be explained away.
const liveHeadroom = 24;
if (liveActors > baselineActors + liveHeadroom) {
    failures.push(`${liveActors} actors are still live on a cleared field against a baseline of ${baselineActors} -- something is not being recycled`);
}

if (failures.length > 0) {
    console.log('');
    for (const f of failures) { console.log(`  FAIL: ${f}`); }
    process.exit(1);
}

console.log('  OK');
