// -----------------------------------------------------------------------------
// The screen-space UI, and the contract it shares with the C# side.
//
// Two things here are worth more than the rest: that the shared glyph table
// actually loads, and that the element handle exposes every member the contract
// promises. Both have a silent failure mode — text that renders as nothing, and
// a property that reads undefined — which is the shape of every UI bug this
// engine has had.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { UiCanvas, ANCHORS, widestLine } from '../src/ui/UiCanvas.js';
import { measure, measureHeight, GLYPH_WIDTH, GLYPH_HEIGHT } from '../src/ui/BitmapFont.js';
import font from '../src/ui/font5x7.json' with { type: 'json' };

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '../..');

test('the shared glyph table covers every printable ASCII character', () => {
    for (let code = 32; code <= 126; code++) {
        const glyph = font.glyphs[String.fromCharCode(code)];
        assert.ok(glyph, `no glyph for ${JSON.stringify(String.fromCharCode(code))}`);
        assert.equal(glyph.length, GLYPH_WIDTH * GLYPH_HEIGHT,
            'every glyph is a 5x7 mask, because both engines index it that way');
    }
    assert.equal(Object.keys(font.glyphs).length, 95);
});

test('the C# engine embeds the same font file rather than a copy of it', () => {
    // A copy would drift. The csproj must point at this very path.
    const csproj = fs.readFileSync(path.join(repoRoot, 'SexyBiscuit.Engine/SexyBiscuit.Engine.csproj'), 'utf8');
    assert.match(csproj, /html5\/src\/ui\/font5x7\.json/,
        'SexyBiscuit.Engine must embed html5/src/ui/font5x7.json, not its own glyph table');
    assert.match(csproj, /LogicalName="SexyBiscuit\.Engine\.font5x7\.json"/,
        'BitmapFont looks the resource up by this exact logical name');
});

test('an anchored element stays put when the window changes size', () => {
    const ui = new UiCanvas({ width: 800, height: 600 });
    const badge = ui.add('panel', { anchor: 'bottomright', x: -12, y: -12, width: 80, height: 28 });

    const small = ui.rectOf(badge);
    assert.equal(small.x + small.width, 800 - 12);
    assert.equal(small.y + small.height, 600 - 12);

    ui.setViewport(1920, 1080);
    const large = ui.rectOf(badge);
    assert.equal(large.x + large.width, 1920 - 12, 'still twelve pixels in from the right');
    assert.equal(large.y + large.height, 1080 - 12, 'still twelve pixels up from the bottom');
});

test('every anchor name resolves, and centre really is the centre', () => {
    const ui = new UiCanvas({ width: 1000, height: 500 });
    for (const name of Object.keys(ANCHORS)) {
        const el = ui.add('panel', { anchor: name, width: 100, height: 50 });
        const r = ui.rectOf(el);
        assert.ok(Number.isFinite(r.x) && Number.isFinite(r.y), `${name} produced no rectangle`);
    }
    const middle = ui.add('panel', { anchor: 'center', width: 100, height: 50 });
    const r = ui.rectOf(middle);
    assert.equal(r.x + r.width / 2, 500);
    assert.equal(r.y + r.height / 2, 250);
});

test('a click is a release inside the element, and only for one frame', () => {
    const ui = new UiCanvas({ width: 400, height: 300 });
    const button = ui.add('button', { x: 100, y: 100, width: 80, height: 30, text: 'GO' });

    ui.setPointer(120, 110, true); ui.update();
    assert.equal(button.clicked, false, 'pressing is not yet clicking');
    assert.equal(button.hovered, true);

    ui.setPointer(120, 110, false); ui.update();
    assert.equal(button.clicked, true, 'the release inside is the click');

    ui.update();
    assert.equal(button.clicked, false, 'and it lasts exactly one frame');
});

test('a release outside the element is not a click', () => {
    const ui = new UiCanvas({ width: 400, height: 300 });
    const button = ui.add('button', { x: 100, y: 100, width: 80, height: 30 });

    ui.setPointer(10, 10, true); ui.update();
    ui.setPointer(10, 10, false); ui.update();
    assert.equal(button.clicked, false);
});

test('only buttons are interactive, so a label never eats a click', () => {
    const ui = new UiCanvas({ width: 400, height: 300 });
    const label = ui.add('label', { x: 0, y: 0, width: 400, height: 300, text: 'hello' });

    ui.setPointer(200, 150, true); ui.update();
    ui.setPointer(200, 150, false); ui.update();
    assert.equal(label.clicked, false);
    assert.equal(label.hovered, false);
});

test('measuring matches what drawing will lay down', () => {
    assert.equal(measure('', 1), 0);
    assert.equal(measure('A', 1), GLYPH_WIDTH);                    // no trailing gap
    assert.equal(measure('AB', 1), GLYPH_WIDTH * 2 + 1);
    assert.equal(measure('AB', 2), (GLYPH_WIDTH * 2 + 1) * 2);
    assert.equal(widestLine('a\nlonger', 1), measure('longer', 1));
    assert.equal(measureHeight('one\ntwo', 1), GLYPH_HEIGHT * 2 + 2);
});

test('clear takes everything away', () => {
    const ui = new UiCanvas({});
    const a = ui.add('panel', {});
    ui.add('label', { text: 'x' });
    ui.clear();
    assert.equal(ui.elements.length, 0);
    assert.equal(a.destroyed, true, 'a handle a script still holds must report that it is gone');
});

test('an unknown element kind is refused rather than silently ignored', () => {
    const ui = new UiCanvas({});
    assert.throws(() => ui.add('dropdown', {}), /unknown element kind/);
});

test('a label with no size measures itself, so anchoring works without hand-measuring', () => {
    const ui = new UiCanvas({ width: 1000, height: 500 });

    const right = ui.add('label', { anchor: 'bottomright', x: -12, y: -12, text: 'bottom right', scale: 2 });
    const r = ui.rectOf(right);
    assert.ok(r.width > 0, 'the label took its width from its text');
    assert.equal(r.x + r.width, 1000 - 12, 'its RIGHT edge is on the right margin, not its left');

    const middle = ui.add('label', { anchor: 'center', text: 'centred', scale: 2 });
    const c = ui.rectOf(middle);
    assert.equal(c.x + c.width / 2, 500, 'a centred label straddles the centre');

    // An explicit width still wins: a caller laying out a column wants its number.
    const fixed = ui.add('label', { anchor: 'topleft', text: 'x', width: 200, height: 40 });
    assert.equal(ui.rectOf(fixed).width, 200);
});
