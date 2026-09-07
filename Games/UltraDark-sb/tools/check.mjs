// check.mjs -- every gate this game has, in one command.
//
//   node tools/check.mjs
//
// In order, cheapest first, stopping at the first failure:
//
//   validate    the scripting contract     (from html5/)
//   checks      24 tests of real situations
//   draw-order  which side of the dark every layer is on
//   perf        what a frame costs at the loads the game can produce
//   soak        three minutes of play, then six more starting in the dark
//
// The last four exist because the first one passes on a game that renders as a
// grey rectangle, cannot shoot a boss, and overflows its own stack.

import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const gameDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.resolve(gameDir, '../..');

const steps = [
    ['validate',   'node', ['tools/validate.js', '../Games/UltraDark-sb', '--strict'], path.join(repoRoot, 'html5')],
    ['checks',     'node', ['--test', 'tools/checks.mjs'], gameDir],
    ['draw-order', 'node', ['tools/draw-order.mjs'], gameDir],
    ['perf',       'node', ['tools/perf.mjs'], gameDir],
    ['soak',       'node', ['tools/soak.mjs', '--minutes', '3'], gameDir],
    ['soak dark',  'node', ['tools/soak.mjs', '--wave', '14', '--minutes', '6'], gameDir],
];

let failed = 0;

for (const [name, cmd, args, cwd] of steps) {
    process.stdout.write(`\n=== ${name} ${'='.repeat(Math.max(0, 40 - name.length))}\n`);
    const r = spawnSync(cmd, args, { cwd, stdio: 'inherit' });
    if (r.status !== 0) {
        console.log(`\n  ${name} FAILED`);
        failed++;
        break;
    }
}

if (failed) { process.exit(1); }
console.log('\nAll gates green.');
