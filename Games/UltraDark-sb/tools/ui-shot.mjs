// ui-shot.mjs -- where every UI element actually lands, without a browser.
//
// Every other gate here reads text, and a layout is a picture. This boots the
// real scene through the real host, lays out every UiCanvas the way the host
// does, and asks each node where it landed -- then fails if any of them is off
// the viewport.
//
// It exists because a HUD that renders in the corner and hangs off the edge is
// invisible to the validator, to 32 game tests and to the engine's own suite --
// and because when that was reported, nothing here could answer it. It is also
// a size sweep: the layout is anchored, so it has to survive a viewport it was
// not built at.
//
//   node tools/ui-shot.mjs [state] [width] [height]
//   state: hangar | select | options | wave | boss | draft | dead
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
    } else if (state === 'select' || state === 'options') {
        // The two biggest screens in the game: eight cards in a grid, and a
        // panel of rows that each share their width. Both are laid out rather
        // than placed, so both are worth checking at a viewport they were not
        // designed at.
        await g.step(10);
        g.scriptOn('Menus').invoke(state === 'select' ? 'openSelect' : 'openMain');
        if (state === 'options') {
            const node = g.ui.nodes().find((n) => n.name === 'mOptions');
            g.pointAt(node.screen.x + node.screen.width / 2, node.screen.y + node.screen.height / 2);
            g.pointerDown(true); await g.step(2);
            g.pointerDown(false); await g.step(4);
        }
        await g.step(6);
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

// Screen rectangles, not canvas ones. A canvas scales and offsets its tree, and
// the two agree only at ConstantPixel on a viewport that matches -- which is the
// one case a sweep like this exists to look past. What overflows is what the
// player would see off the edge.
//
const all = g.ui.nodes().map((n) => ({ kind: n.kind, name: n.name, text: n.text, ...n.screen }));

// A container at 0x0 is not a fault -- an empty layout row is legitimately
// nothing. A BAR at 0x0 is, and so is a label with words in it: both are things
// the player is meant to read, and a zero rect is the exact shape of "grow: 1
// with nothing to grow into" or "a percentage of a parent sized by its content".
//
// Skipping them, which is what this did first, is how the HUD shipped with five
// bars of no width and a boss bar pinned to its minimum at every window size --
// visible only in a native frame, and only because somebody looked.
const collapsed = all.filter((n) => (n.kind === 'bar' || n.text) && (n.width <= 0 || n.height <= 0));

const out = all.filter((n) => n.width > 0 && n.height > 0);

fs.writeFileSync('/tmp/ui-rects.json', JSON.stringify({ state, W, H, elements: out }, null, 1));

// A little slack: a one-pixel rounding overhang is not a layout fault.
const SLACK = 2;
const outside = out.filter((e) => e.x < -SLACK || e.y < -SLACK
    || e.x + e.width > W + SLACK || e.y + e.height > H + SLACK);

console.log(`  ${state.padEnd(7)} ${String(W).padStart(5)}x${String(H).padEnd(5)} `
    + `${String(out.length).padStart(3)} visible, ${outside.length} outside, ${collapsed.length} collapsed`);
for (const n of collapsed.slice(0, 8)) {
    console.log(`    COLLAPSED ${n.kind} "${n.name}" ${JSON.stringify(n.text).slice(0, 30)} `
        + `is ${n.width}x${n.height}`);
}
for (const e of outside.slice(0, 12)) {
    console.log(`    OUTSIDE ${e.kind.padEnd(6)} "${e.text.slice(0, 18)}" x=${Math.round(e.x)} y=${Math.round(e.y)} w=${Math.round(e.width)} h=${Math.round(e.height)}`);
}

if (out.length === 0) {
    console.log('    FAIL: nothing was drawn at all');
    process.exit(1);
}
if (outside.length > 0 || collapsed.length > 0) { process.exit(1); }
