// soak.mjs -- play UltraDark headlessly for minutes of game time and report.
//
// The bundled template smoke test runs sixty frames with no physics host and no
// asset loader. Neither is enough here:
//
//   * Bullets.js finds what it hit with Physics.overlapCircle, which returns
//     nothing at all without a PhysicsSystem2D on the scene. With no physics the
//     game runs happily and nothing can ever be shot.
//   * The Boss is attached at RUNTIME with Scene.addComponent(..., "ScriptComponent"),
//     which loads its source through engine.assets.loadText. With no asset loader
//     every boss is an actor with no behaviour, and wave 5 never ends.
//   * Sixty frames is one second. The dark arrives on wave 16.
//
// So this gives the scene a real physics world, an asset loader backed by the
// filesystem, an input device the bot drives, and then plays.
//
//   node tools/soak.mjs                 # ~6 minutes of game time
//   node tools/soak.mjs --minutes 20    # long enough to reach the dark
//   node tools/soak.mjs --wave 16       # start there instead
//   node tools/soak.mjs --pilot 3       # fly DAVE

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const gameDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.resolve(gameDir, '../..');
const engine = await import(path.join(repoRoot, 'html5/src/index.js'));
const { deserialize, ScriptComponent, PhysicsSystem2D, Vector2 } = engine;

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

const DT = 1 / 60;
const totalFrames = Math.round(minutes * 60 * 60);

// ---------------------------------------------------------------------------
// The input device the bot drives.
//
// Shaped exactly like the members ScriptBridge reads, including the touch
// object -- Input.joystickX reads input().touch.leftJoystick.value.x and only
// guards the first hop, so a stub without it throws.
// ---------------------------------------------------------------------------
const held = new Set();
const pressed = new Set();

const input = {
    mousePosition: { x: 640, y: 360 },
    mouseDelta: { x: 0, y: 0 },
    scrollDelta: 0,
    touch: { touchCount: 0, touches: [], leftJoystick: { value: { x: 0, y: 0 } } },
    _mouseHeld: new Set(),

    isPressed: () => false,
    isHeld: () => false,
    isReleased: () => false,
    getAxis: () => 0,

    isKeyDown: (k) => held.has(String(k)),
    isKeyPressed: (k) => pressed.has(String(k)),
    isKeyReleased: () => false,

    isMouseButtonDown: (b) => input._mouseHeld.has(Number(b)),
    isMouseButtonPressed: (b) => input._mouseHeld.has(Number(b)),
    isMouseButtonReleased: () => false,
};

const press = (key) => pressed.add(key);
const hold = (key, on) => { if (on) held.add(key); else held.delete(key); };

// ---------------------------------------------------------------------------
// The scene
// ---------------------------------------------------------------------------
const errors = [];
const logs = [];
const originalError = console.error;
const originalLog = console.log;
console.error = (...a) => errors.push(a.join(' '));
console.log = (...a) => { const line = a.join(' '); logs.push(line); if (verbose) originalLog(line); };

const sceneFile = path.join(gameDir, 'Scenes/Ultradark.scene');
const scene = deserialize(fs.readFileSync(sceneFile, 'utf8'), { onWarning: () => {} });

// The asset loader a runtime-attached script needs. Paths are project-relative,
// the same rule the engine uses when it reads them off disk.
const assets = {
    loadText: async (p) => fs.readFileSync(path.join(gameDir, p), 'utf8'),
};

scene.engine = { input, assets, audio: null };
scene.physics2D = new PhysicsSystem2D({ scene, gravity: new Vector2(0, 0) });

scene.flushPendingActors();

// Scripts placed in the scene file are fed in directly; anything attached later
// goes through the loader above.
for (const actor of scene.allActors) {
    for (const script of actor.getComponents(ScriptComponent)) {
        const file = path.join(gameDir, script.scriptPath);
        if (!fs.existsSync(file)) { errors.push(`missing script ${script.scriptPath}`); continue; }
        script.setSource(fs.readFileSync(file, 'utf8'));
    }
}
scene.flushPendingActors();

const find = (name) => scene.allActors.find((a) => a.name === name && !a.isDestroyed);
const scriptOn = (name) => {
    const a = find(name);
    return a ? a.getComponents(ScriptComponent)[0] : null;
};

const director = scriptOn('Director');
const swarm = scriptOn('Swarm');
const bullets = scriptOn('Bullets');
const pilotScript = scriptOn('Player');
if (!director || !swarm || !bullets || !pilotScript) {
    originalError('soak: a manager script is missing from the scene');
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
input._mouseHeld.add(0);          // hold fire for the whole run

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
const baselineActors = scene.allActors.filter((a) => !a.isDestroyed).length;

for (frame = 0; frame < totalFrames; frame++) {
    pressed.clear();
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

    if (assist && frame % 180 === 0) { pilotScript.invoke('heal', 9999); }

    scene.update(DT);
    scene.physics2D.fixedStep(DT);
    scene.fixedUpdate(DT);
    scene.lateUpdate(DT);
    scene.flushPendingActors();

    // Let a runtime-attached script's loadText settle; without this the boss
    // never gets its source and wave 5 never ends.
    if (frame % 10 === 0) await new Promise((r) => setImmediate(r));

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
swarm.invoke('clearAll');
bullets.invoke('clearAll');
const bossLeft = scene.allActors.find((a) => a.tag === 'Boss' && !a.isDestroyed);
if (bossLeft) { bossLeft.destroy(); }
for (let i = 0; i < 240; i++) {
    scene.update(DT); scene.lateUpdate(DT); scene.flushPendingActors();
}
const settledActors = scene.allActors.filter((a) => !a.isDestroyed).length;

console.error = originalError;
console.log = originalLog;

// ---------------------------------------------------------------------------
// Report
// ---------------------------------------------------------------------------
const liveActors = scene.allActors.filter((a) => !a.isDestroyed).length;

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
console.log(`  peak actors    ${stats.maxActors}   (settled to ${settledActors}, baseline ${baselineActors})`);

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
// actor back is a real defect. The measurement is taken with the field cleared:
// the settled count is allowed to exceed the baseline by the pools themselves,
// and by nothing like the peak.
const poolHeadroom = 400;
if (settledActors > baselineActors + poolHeadroom) {
    failures.push(`the scene settled at ${settledActors} actors against a baseline of ${baselineActors} -- something is not being recycled`);
}

if (failures.length > 0) {
    console.log('');
    for (const f of failures) { console.log(`  FAIL: ${f}`); }
    process.exit(1);
}

console.log('  OK');
