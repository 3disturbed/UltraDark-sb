// -----------------------------------------------------------------------------
// The shared glyph table, and the silent failure it exists to prevent.
//
// Text that draws as nothing is not hypothetical here: the old native widgets
// opened with `if (font == null) return;` and there has never been a font asset,
// so every label they drew was invisible and no test noticed. Both engines draw
// from this one glyph file, and the C# side embeds it by path rather than keeping
// a copy -- a copy would drift.
//
// The layout, click and anchoring behaviour that used to live here moved with the
// flat UI it tested: layout is uiLayout.test.js against the shared fixture,
// clicks are uiInteraction.test.js, and the script-facing API is
// uiScriptApi.test.js.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { measure, measureHeight, GLYPH_WIDTH, GLYPH_HEIGHT } from '../src/ui/BitmapFont.js';
import { width as measureWidest, height as measureBlockHeight } from '../src/ui/UiTextMeasure.js';
import font from '../src/ui/font5x7.json' with { type: 'json' };

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');

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

test('measuring matches what drawing will lay down', () => {
    assert.equal(measure('', 1), 0);
    assert.equal(measure('A', 1), GLYPH_WIDTH);                    // no trailing gap
    assert.equal(measure('AB', 1), GLYPH_WIDTH * 2 + 1);
    assert.equal(measure('AB', 2), (GLYPH_WIDTH * 2 + 1) * 2);
    assert.equal(measureHeight('one\ntwo', 1), GLYPH_HEIGHT * 2 + 2);
});

test('the layout engine measures through the shared seam', () => {
    // One seam is the whole reason a wrapped label breaks in the same place on both
    // engines. A widget that measures text any other way is a parity bug that no
    // member-name check could see.
    assert.equal(measureWidest('a\nlonger', 1), measure('longer', 1));
    assert.equal(measureBlockHeight('one\ntwo', 1), measureHeight('one\ntwo', 1));
});

test('the host keeps every UI canvas in step with the drawing buffer', () => {
    // A canvas lays out against ITS OWN width and height, so if the host never tells
    // it about a resize, every anchor is measured against whatever size the drawing
    // buffer happened to be at construction. A bare <canvas> with no width/height
    // attributes is 300x150 -- so `center` resolved to (150, 75), which is the
    // top-left corner, and a panel sized UI.width x UI.height covered a fraction of
    // the screen.
    //
    // Nothing about that throws, and nothing is visible until a game puts something
    // on screen, which is why it is pinned here rather than left to a playtest.
    const source = fs.readFileSync(path.join(repoRoot, 'html5/src/EngineHost.js'), 'utf8');

    const update = source.slice(source.indexOf('    _updateUiCanvases('));
    const body = update.slice(0, update.indexOf('\n    }'));

    assert.match(body, /canvas\.setViewport\(/,
        'EngineHost._updateUiCanvases() does not send the current size to each canvas');
    assert.match(body, /this\.canvas2D\?\.width/,
        'the size should come from the drawing buffer, not from a remembered config');
});
