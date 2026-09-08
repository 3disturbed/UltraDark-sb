// -----------------------------------------------------------------------------
// Holds the browser layout engine to the same golden cases the C# engine reads
// from the same file (SexyBiscuit.Tests/UiLayoutTests.cs).
//
// Every other parity test in this repository reads the opposite engine's source
// with a regular expression, which can see a missing member and cannot see a
// number that is quietly wrong -- and a layout engine is almost entirely numbers.
// So both sides run this fixture and compare rectangles, which is the only check
// that would actually catch the drift.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { fromObject } from '../src/ui/UiDocument.js';
import { measure, arrange, hitTest } from '../src/ui/UiLayout.js';
import { UiNode } from '../src/ui/UiNode.js';
import { UiCanvas } from '../src/ui/UiCanvas.js';
import { UiKind, LayoutMode, SizeMode, PositionMode, ScrollMode, UiAnchor } from '../src/ui/UiEnums.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const cases = JSON.parse(fs.readFileSync(path.join(here, '../src/ui/layout-cases.json'), 'utf8'));

const TOLERANCE = 0.01;

/** What UiCanvas.layout() does, without needing a canvas: measure, then arrange. */
function layout(root, viewportWidth, viewportHeight) {
    const area = { x: 0, y: 0, width: viewportWidth, height: viewportHeight };
    measure(root, { x: area.width, y: area.height });
    arrange(root, area, area);
}

function assertRect(expected, actual, who) {
    const near = (a, b) => Math.abs(a - b) < TOLERANCE;
    const ok = near(expected[0], actual.x) && near(expected[1], actual.y)
        && near(expected[2], actual.width) && near(expected[3], actual.height);

    assert.ok(ok, `"${who}" expected [${expected}] but was `
        + `[${actual.x}, ${actual.y}, ${actual.width}, ${actual.height}]`);
}

// -----------------------------------------------------------------------------
// The shared fixture
// -----------------------------------------------------------------------------

for (const testCase of cases.cases) {
    test(`layout case: ${testCase.name}`, () => {
        const root = fromObject(testCase.tree);
        layout(root, testCase.viewport[0], testCase.viewport[1]);

        for (const [name, expected] of Object.entries(testCase.expect)) {
            const node = name === 'root' ? root : root.find(name);
            assert.ok(node, `the case names "${name}", which the tree does not contain`);
            assertRect(expected, node.rect, name);
        }
    });
}

// -----------------------------------------------------------------------------
// Invalidation
// -----------------------------------------------------------------------------

test('changing a size marks every ancestor for measuring again', () => {
    // The whole reason a node caches its rectangle is that the widget tree this
    // replaces recomputed bounds by walking to the root on every single read. A
    // setter that forgets to mark the tree dirty gives a layout that is stale for
    // one frame and then correct, which is the worst kind of bug to reproduce.
    const root = new UiNode({ layout: LayoutMode.Row });
    const row = root.add(new UiNode({ layout: LayoutMode.Row }));
    const child = row.add(new UiNode({ widthMode: SizeMode.Fixed, width: 100, heightMode: SizeMode.Fixed, height: 20 }));

    layout(root, 800, 600);
    assert.equal(root.measureDirty, false);

    child.width = 250;

    assert.equal(child.measureDirty, true);
    assert.equal(row.measureDirty, true);
    assert.equal(root.measureDirty, true);

    layout(root, 800, 600);
    assert.equal(child.rect.width, 250);
});

test('an anchored node moves when the viewport does', () => {
    // A rotated phone must not leave the HUD hanging off the edge.
    const root = new UiNode();
    const pin = root.add(new UiNode({
        positioning: PositionMode.Absolute,
        anchor: UiAnchor.BottomRight,
        offset: { x: -12, y: -12 },
        widthMode: SizeMode.Fixed, width: 100,
        heightMode: SizeMode.Fixed, height: 30,
    }));

    layout(root, 800, 600);
    assert.equal(pin.rect.x, 688);

    root.invalidateMeasure();
    layout(root, 1024, 768);
    assert.equal(pin.rect.x, 912);
    assert.equal(pin.rect.y, 726);
});

// -----------------------------------------------------------------------------
// Hit testing
// -----------------------------------------------------------------------------

function box(name, x, y, w, h) {
    return new UiNode({
        name,
        kind: UiKind.Panel,
        background: '#ffffff',
        positioning: PositionMode.Absolute,
        offset: { x, y },
        widthMode: SizeMode.Fixed, width: w,
        heightMode: SizeMode.Fixed, height: h,
    });
}

test('an empty layout row does not eat the click meant for its button', () => {
    // If a container swallowed clicks, every row in a UI would be an invisible
    // obstacle in front of its own children.
    const root = new UiNode({ layout: LayoutMode.Row });
    const row = root.add(new UiNode({
        layout: LayoutMode.Row,
        widthMode: SizeMode.Fixed, width: 400,
        heightMode: SizeMode.Fixed, height: 100,
    }));
    const button = row.add(new UiNode({
        kind: UiKind.Button,
        widthMode: SizeMode.Fixed, width: 200,
        heightMode: SizeMode.Fixed, height: 50,
    }));

    layout(root, 800, 600);

    assert.equal(hitTest(root, { x: 100, y: 25 }), button);
    // Past the button but still inside the row: nothing, because the row paints nothing.
    assert.equal(hitTest(root, { x: 300, y: 25 }), null);
});

test('the top-most node takes the hit rather than every node under the pointer', () => {
    const root = new UiNode();
    const under = root.add(box('under', 0, 0, 200, 200));
    const over = root.add(box('over', 50, 50, 100, 100));

    layout(root, 800, 600);

    assert.equal(hitTest(root, { x: 100, y: 100 }), over);
    assert.equal(hitTest(root, { x: 180, y: 180 }), under);
});

test('a node scrolled out of sight is not clickable', () => {
    const root = new UiNode({ layout: LayoutMode.Column, scroll: ScrollMode.Vertical });
    const first = root.add(new UiNode({
        name: 'first', kind: UiKind.Panel, background: '#ffffff',
        widthMode: SizeMode.Fixed, width: 200, heightMode: SizeMode.Fixed, height: 80,
    }));
    for (let i = 1; i < 5; i++) {
        root.add(new UiNode({
            name: `row${i}`, kind: UiKind.Panel, background: '#ffffff',
            widthMode: SizeMode.Fixed, width: 200, heightMode: SizeMode.Fixed, height: 80,
        }));
    }

    layout(root, 400, 100);
    assert.equal(hitTest(root, { x: 10, y: 10 }), first);

    // Five 80px rows in a 100px window scroll 300px, so 200 is well past the first row.
    root.scrollOffset = { x: 0, y: 200 };
    layout(root, 400, 100);
    assert.notEqual(hitTest(root, { x: 10, y: 10 }), first);
});

// -----------------------------------------------------------------------------
// The codec
// -----------------------------------------------------------------------------

test('an unknown property is refused rather than silently ignored', () => {
    // The flat UI this replaces ended its options switch with a silent default, so a
    // typo did nothing and said nothing. In a tree, a mistyped "childern" would drop
    // every node below it and leave no trace.
    assert.throws(() => fromObject({ childern: [] }), /not a UI node property/);
    assert.throws(() => fromObject({ widht: 100 }), /not a UI node property/);
});

test('a size can be a number, auto, a percentage or a star', () => {
    assert.equal(fromObject({ width: 240 }).widthMode, SizeMode.Fixed);
    assert.equal(fromObject({ width: 'auto' }).widthMode, SizeMode.Auto);
    assert.equal(fromObject({ width: '*' }).widthMode, SizeMode.Stretch);

    const half = fromObject({ width: '50%' });
    assert.equal(half.widthMode, SizeMode.Percent);
    assert.equal(half.width, 0.5);
});

test('an anchor name may be written any of the ways this repository writes them', () => {
    for (const spelling of ['bottomright', 'bottom-right', 'BottomRight', 'bottom_right']) {
        assert.equal(fromObject({ anchor: spelling }).anchor, UiAnchor.BottomRight);
    }
    // "centre" and "center" are the same place, and this codebase writes both.
    assert.equal(fromObject({ anchor: 'centre' }).anchor, UiAnchor.Center);
});

test('no background is not the same as a black background', () => {
    // These round-trip differently if either engine coerces null to a colour, and the
    // difference is a solid black box over the game rather than nothing at all.
    assert.equal(fromObject({}).background, null);
    assert.equal(fromObject({ background: null }).background, null);
    assert.equal(fromObject({ background: '#000' }).background, '#000000');
});

test('a panel built hidden lays out when it is shown', () => {
    // `invalidateMeasure` stops at the first node already fully dirty, which is only
    // sound while a dirty node implies dirty ancestors. Nothing cleared a subtree the
    // layout pass never reached, so a node under a hidden one stayed dirty for ever;
    // the next change inside it broke out of that walk at once, the root was never
    // marked, and UiCanvas.layout() skipped the pass. A panel built hidden and shown
    // later therefore never laid out at all -- zero rect, invisible, un-clickable --
    // which is what a pause menu, a dialog and a card picker all are.
    UiCanvas.clearAll();
    const canvas = new UiCanvas();
    canvas.setViewport(1280, 720);
    canvas.adopt(fromObject({
        name: 'root', layout: 'column',
        children: [{
            name: 'card', visible: false, width: 200, height: 100, layout: 'column',
            children: [{ name: 'title', kind: 'label', text: 'x' }],
        }],
    }));

    canvas.layout();

    const card = canvas.find('card');
    const title = canvas.find('title');
    assert.equal(title.measureDirty, false,
        'a node under a hidden one was left dirty, so nothing below it can ever mark the root');

    // What opening a menu does: fill the labels in, then show it.
    title.text = 'Dash Nova';
    card.visible = true;
    assert.equal(canvas.root.measureDirty, true, 'showing a panel did not reach the root');

    canvas.layout();
    assert.equal(card.rect.width, 200, 'the panel was shown and still has no size');
    assert.ok(title.rect.width > 0, 'the label inside it was never measured');
});
