// ui-shot.mjs -- where every UI element actually lands, without a browser.
//
// Every other gate here reads text, and a layout is a picture. This boots the
// real scene through the real host and asks the real ScriptUi for the rectangle
// of each element, then fails if any of them is off the viewport.
//
// It exists because a HUD that renders in the corner and hangs off the edge is
// invisible to the validator, to 32 game tests and to the engine's own suite --
// and because when that was reported, nothing here could answer it. It is also
// a size sweep: the layout is anchored, so it has to survive a viewport it was
// not built at.
//
//   node tools/ui-shot.mjs [state] [width] [height]
//   state: hangar | wave | boss | draft | dead
import fs from 'node:fs';
import { boot } from './harness.mjs';

const state = process.argv[2] ?? 'draft';
const W = Number(process.argv[3] ?? 1280);
const H = Number(process.argv[4] ?? 720);

const g = boot();
g.ui.setViewport(W, H);

try {
    if (state === 'hangar') {
        await g.step(20);
    } else if (state === 'wave') {
        g.director().invoke('forceLaunch');
        await g.step(60);
    } else if (state === 'boss') {
        // The marquee bar is the widest thing the HUD ever draws and the only
        // one sized from the viewport rather than fixed, so it is the one most
        // able to hang off the edge of a window it was not built in.
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceWave', 5);
        await g.step(90);
    } else if (state === 'dead') {
        g.director().invoke('forceLaunch');
        await g.step(20);
        for (let i = 0; i < 30; i++) { g.pilot().invoke('hurt', 999); await g.step(20); }
    } else {
        g.director().invoke('forceLaunch');
        await g.step(10);
        g.director().invoke('forceWave', 1);
        g.director().invoke('forceBudget', 0);
        g.swarm().invoke('clearAll');
        await g.step(30);
    }
    g.ui.setViewport(W, H);
    await g.step(2);
} finally {
    g.restore();
}

const out = g.ui.elements
    .filter((e) => e.visible)
    .map((e) => ({ kind: e.kind, text: String(e.text ?? ''), anchor: e.anchor,
                   scale: e.scale, ...g.ui.rectOf(e) }));

fs.writeFileSync('/tmp/ui-rects.json', JSON.stringify({ state, W, H, elements: out }, null, 1));

// A little slack: a one-pixel rounding overhang is not a layout fault.
const SLACK = 2;
const outside = out.filter((e) => e.x < -SLACK || e.y < -SLACK
    || e.x + e.width > W + SLACK || e.y + e.height > H + SLACK);

console.log(`  ${state.padEnd(7)} ${String(W).padStart(5)}x${String(H).padEnd(5)} ${String(out.length).padStart(3)} visible, ${outside.length} outside`);
for (const e of outside.slice(0, 12)) {
    console.log(`    OUTSIDE ${e.kind.padEnd(6)} "${e.text.slice(0, 18)}" x=${Math.round(e.x)} y=${Math.round(e.y)} w=${Math.round(e.width)} h=${Math.round(e.height)}`);
}

if (out.length === 0) {
    console.log('    FAIL: nothing was drawn at all');
    process.exit(1);
}
if (outside.length > 0) { process.exit(1); }
