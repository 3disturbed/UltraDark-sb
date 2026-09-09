// chibi-cost.mjs -- what a chibi costs, before anything is designed around one.
//
//   node tools/chibi-cost.mjs [count]
//
// The wiki says 35-55 actors each and "for a crowd, measure it". UltraDark can
// have 130 enemies on the field at once, so whether an enemy can be a character
// or has to stay a box is a measurement, not a preference -- and it decides the
// shape of the whole 3D conversion.
import { boot, DT } from './harness.mjs';

const counts = process.argv[2] ? [Number(process.argv[2])] : [1, 8, 32, 64, 130];
const report = [];

for (const count of counts) {
    const g = boot();
    try {
        const before = g.scene.allActors.length;

        for (let i = 0; i < count; i++) {
            const spawned = g.director().invoke('spawnChibiProbe', 1000 + i, i * 2, 0, 0);
            if (spawned === 0 && i === 0) { report.push('  the game has no probe hook'); break; }
        }
        // The body is built from a recipe read asynchronously, so it is not there
        // on the frame the actor is.
        await g.step(20);

        const after = g.scene.allActors.length;
        const per = count > 0 ? (after - before) / count : 0;

        // Time only the steady state, after everything is built.
        const start = process.hrtime.bigint();
        const frames = 120;
        await g.step(frames);
        const ms = Number(process.hrtime.bigint() - start) / 1e6 / frames;

        report.push(`  ${String(count).padStart(3)} chibis  ${String(after - before).padStart(5)} actors `
            + `(${per.toFixed(1)} each)  ${ms.toFixed(2)} ms/frame  `
            + `${(ms / (DT * 1000) * 100).toFixed(0)}% of a 16.7ms frame`);
    } finally { g.restore(); }
}

console.log('--- what a chibi costs ---------------------------------');
for (const line of report) console.log(line);
