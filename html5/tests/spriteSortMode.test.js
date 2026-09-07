// -----------------------------------------------------------------------------
// Which way layerDepth points.
//
// Nothing else in either suite pins this, and it is invisible to everything that
// guards a game: the validator reads the scripting contract, a headless run
// reports script errors, and neither can see a picture. So a project laid out
// against the wrong direction passes every gate and ships a screen filled by
// whatever it meant to put at the back.
//
// That happened. `SpriteSortMode.BackToFront` was commented "high layerDepth
// first" while the sort is ascending, so a game laid out by believing the
// comment drew its ground over its own city.
//
// The engines now agree, and this is where that is recorded. The browser sorts
// ascending and draws the highest depth last. The native side reaches the same
// answer from the opposite-sounding constant: MonoGame's BackToFront draws the
// HIGHEST depth first, which puts it at the back, so RenderSystem2D opens its
// batch with FrontToBack instead. Re-measured with Games/DepthProbe on a native
// build, 2026-09-07: a red sprite created FIRST at depth 0.90 fills the window
// over a blue one created after it at 0.10.
//
// Keeping them agreed is not cosmetic. A game that sorts by position — anything
// top-down where you walk in front of one wall and behind the next — cannot be
// laid out in creation order, because the actor is created once and the
// relationship changes every step. It needs the depth honoured on both sides.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { SpriteBatch, SpriteSortMode } from '../src/rendering/SpriteBatch.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '../..');

/** The order a batch actually executes its commands in, given a sort mode. */
function drawOrder(sortMode, depths) {
    const batch = new SpriteBatch();
    batch.ctx = {
        save() {}, restore() {}, setTransform() {}, translate() {}, scale() {}, rotate() {},
    };

    batch.begin({ sortMode });
    for (const depth of depths) batch.fillRect(0, 0, 1, 1, `d${depth}`, depth);

    const order = [];
    batch._execute = (ctx, command) => order.push(command.layerDepth);
    batch.end();
    return order;
}

test('BackToFront draws the highest layerDepth last, so high depth is the front', () => {
    // A ground plane, a wall and a player, submitted in an order that would give
    // the wrong answer on its own — the sort has to be what decides.
    const order = drawOrder(SpriteSortMode.BackToFront, [0.95, 0.3, 0.6]);

    assert.deepEqual(order, [0.3, 0.6, 0.95],
        'BackToFront must draw ascending, so the last one drawn is the one in front');
    assert.equal(order[order.length - 1], 0.95,
        'a sprite at depth 0.95 ends up on top of one at 0.3');
});

test('FrontToBack is the exact reverse', () => {
    assert.deepEqual(drawOrder(SpriteSortMode.FrontToBack, [0.95, 0.3, 0.6]), [0.95, 0.6, 0.3]);
});

test('Deferred keeps call order whatever the depths say', () => {
    assert.deepEqual(drawOrder(SpriteSortMode.Deferred, [0.95, 0.3, 0.6]), [0.95, 0.3, 0.6]);
});

test('both engines document the same direction as they implement', () => {
    // The comments are the only thing a game author reads before laying out a
    // scene, so a comment that disagrees with the sort is the actual defect.
    const js = fs.readFileSync(path.join(repoRoot, 'html5/src/rendering/SpriteBatch.js'), 'utf8');
    const sortLine = /BackToFront: 'BackToFront',\s*\/\/(.*)/.exec(js);
    assert.ok(sortLine, 'BackToFront should carry a comment saying which way it sorts');
    assert.match(sortLine[1], /low layerDepth first|high depth is the front/i,
        'the BackToFront comment must not claim high depth is drawn first');

    // The summary only, not the remarks: the remarks quote the old wrong wording
    // on purpose, to say why the direction is spelled out at all.
    const cs = fs.readFileSync(path.join(repoRoot, 'SexyBiscuit.Engine/Rendering/SpriteRenderer.cs'), 'utf8');
    const summary = /\/\/\/ <summary>((?:(?!<\/summary>)[\s\S])*?Draw order within the batch[\s\S]*?)\/\/\/ <\/summary>/.exec(cs);
    assert.ok(summary, 'LayerDepth should still carry its summary');
    assert.doesNotMatch(summary[1], /0 = front, 1 = back/,
        'the C# LayerDepth summary had the direction backwards; it must not come back');
    assert.match(summary[1], /0 = back, 1 = front|[Hh]igher is nearer/,
        'the summary must say which end of layerDepth is the front');
    // The summary must not still be telling readers the native side ignores it.
    // That claim was true, then was fixed, and then outlived the fix by a day —
    // during which it was advising games to lay themselves out in creation order.
    assert.doesNotMatch(summary[1], /this engine currently does not|does not sort/i,
        'the native caveat was removed when the sort was fixed; it must not come back untested');
});

test('the native batch sorts the way the browser does', () => {
    // The one line that decides it, read from the source rather than trusted.
    // MonoGame's names are counter-intuitive here: BackToFront draws the HIGHEST
    // depth FIRST, which lands it at the back — the exact inverse of this engine.
    // FrontToBack is the one that matches. Anyone changing it has to change this.
    const rs = fs.readFileSync(path.join(repoRoot, 'SexyBiscuit.Engine/Rendering/RenderSystem2D.cs'), 'utf8');
    const mode = /public SpriteSortMode SortMode \{ get; set; \} = SpriteSortMode\.(\w+);/.exec(rs);

    assert.ok(mode, 'RenderSystem2D should still declare a default SortMode');
    assert.equal(mode[1], 'FrontToBack',
        'the scene batch must sort ascending by depth, as the browser does; ' +
        'BackToFront would silently invert every layered scene in the repository');
});
