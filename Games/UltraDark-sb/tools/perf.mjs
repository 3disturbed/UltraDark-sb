// perf.mjs -- what a frame costs, and at what point the game stops being 60fps.
//
// This exists because a soak that took twelve seconds started taking minutes
// after one tuning change (`trickle` 0.42 -> 0.30, which is just "spawn a bit
// faster"), and nothing in the repository could say why. The answer was the
// collision broadphase: Physics.overlapCircle is a linear scan of every
// collider in the scene, so the cost of a frame is projectiles x enemies, and a
// denser wave moves both numbers at once.
//
// A twin-stick shooter that cannot hold 60 fps in a browser is broken in the
// way that matters most, and it is the one property no other gate here measures.
//
//   node tools/perf.mjs

import { boot, DT } from './harness.mjs';

const BUDGET_MS = 16.6;        // one frame at 60 fps
// The top row is what the game can actually produce: the Director holds the
// budget at `concurrentCap` and the Swarm refuses past `maxEnemies`, so a load
// above that cannot happen in play. Measuring it anyway is how the cap was
// chosen -- see the note on maxEnemies in Swarm.js.
const LOADS = [
    { enemies: 20,  shots: 20,  what: 'an early wave' },
    { enemies: 60,  shots: 60,  what: 'a middle wave' },
    { enemies: 105, shots: 120, what: 'a late wave at the concurrent cap' },
    { enemies: 130, shots: 200, what: 'the hard ceiling, everything firing' },
];

const g = boot();
const report = [];
const problems = [];

try {
    g.director().invoke('forceLaunch');
    await g.step(30);

    for (const load of LOADS) {
        g.director().invoke('forceWave', 20);
        g.director().invoke('forceBudget', 0);
        g.swarm().invoke('clearAll');
        g.bullets().invoke('clearAll');
        await g.step(4);

        // A spread of kinds, so the cost includes the ones that think hardest.
        for (let i = 0; i < load.enemies; i++) {
            const kind = i % 12;
            const a = (i / load.enemies) * Math.PI * 2;
            g.swarm().invoke('spawnKind', kind, Math.cos(a) * 600, Math.sin(a) * 600, 999, 1);
        }
        // Projectiles have to be topped up rather than fired once: a piercing
        // shot through a dense ring spends its pierce in a few frames, and a
        // measurement taken afterwards is a measurement of no projectiles at
        // all. Refilling each frame is also what a player holding fire does.
        const topUp = () => {
            const short = load.shots - g.bullets().invoke('count');
            for (let i = 0; i < short; i++) {
                g.bullets().invoke('fire', 0, 0, Math.random() * Math.PI * 2,
                                   700, 0.001, 0, 5, 30, 999999, 0);
            }
        };

        topUp();
        await g.step(10);
        topUp();
        await g.step(20);

        // Warm, then measure.
        let ms = 0;
        const frames = 120;
        topUp();
        const t0 = process.hrtime.bigint();
        for (let f = 0; f < frames; f++) {
            g.scene.update(DT);
            g.scene.physics2D.fixedStep(DT);
            g.scene.lateUpdate(DT);
            g.scene.flushPendingActors();
            if (f % 10 === 0) { topUp(); }
        }
        ms = Number(process.hrtime.bigint() - t0) / 1e6 / frames;

        const live = g.swarm().invoke('alive');
        const shots = g.bullets().invoke('count');
        report.push(`  ${String(live).padStart(3)} enemies + ${String(shots).padStart(3)} shots   ${ms.toFixed(2)} ms/frame   ${(BUDGET_MS / ms).toFixed(1)}x budget   ${load.what}`);

        // Node here is faster than a phone and slower than nothing; a frame that
        // already costs the whole budget on this box has no headroom left at all.
        if (ms > BUDGET_MS) {
            problems.push(`${load.what} (${live} enemies, ${shots} shots) costs ${ms.toFixed(1)} ms, over a ${BUDGET_MS} ms frame`);
        }
    }
} finally {
    g.restore();
}

console.log('--- UltraDark frame cost ---------------------------------');
for (const line of report) { console.log(line); }

if (problems.length > 0) {
    console.log('');
    for (const p of problems) { console.log(`  FAIL: ${p}`); }
    process.exit(1);
}
console.log('  OK -- every load fits in a 60 fps frame');
