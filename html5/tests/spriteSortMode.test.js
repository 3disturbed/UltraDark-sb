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
// The engines do NOT currently agree. This side sorts; the native side, measured
// with Games/DepthProbe, draws in creation order whatever the depths say. These
// tests pin the half that is settled — the browser's direction, and that both
// comments describe what their code does, native caveat included — so that when
// the native sort is fixed the target is written down rather than guessed at.
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
    // And it must keep saying that the native path does not sort on it yet, so
    // nobody reads the first line alone and lays a scene out against it.
    const cs2 = fs.readFileSync(path.join(repoRoot, 'SexyBiscuit.Engine/Rendering/SpriteRenderer.cs'), 'utf8');
    assert.match(cs2, /this engine currently does not|DepthProbe/i,
        'the LayerDepth remarks must keep the native caveat until the sort is fixed');
});
