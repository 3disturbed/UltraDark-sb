// -----------------------------------------------------------------------------
// Holds the UI document codec to the same golden cases the C# engine reads from
// the same file (SexyBiscuit.Tests/UiDocumentTests.cs).
//
// The strongest parity check available for the script-facing UI. The other bridge
// tests compare member *names*, which catches a missing property and cannot catch
// one that means something slightly different on each engine -- a colour that
// loses its alpha, a size shorthand that resolves differently, a loose enum
// spelling one side accepts and the other does not. Feeding one spec through both
// codecs and comparing what comes back out catches exactly that.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { fromObject, toObject, toJson, read, isWritable } from '../src/ui/UiDocument.js';
import { UiNode } from '../src/ui/UiNode.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const fixture = JSON.parse(fs.readFileSync(path.join(here, 'fixtures/ui-build-cases.json'), 'utf8'));

for (const testCase of fixture.cases) {
    test(`build case: ${testCase.name}`, () => {
        if (testCase.throws) {
            assert.throws(() => fromObject(testCase.spec), new RegExp(testCase.throws));
            return;
        }
        assert.deepEqual(toObject(fromObject(testCase.spec)), testCase.expect);
    });
}

test('writing a tree and reading it back gives the same tree', () => {
    // A tree a script built has to survive being written out and read back, or a UI
    // authored in a script could never be saved as a .ui document and reopened.
    const spec = {
        name: 'hud', layout: 'column', gap: 6, padding: 12,
        background: '#161920e6',
        children: [
            { name: 'hp', kind: 'bar', width: '*', height: 8, value: 0.4, tint: '#c63832' },
            { name: 'go', kind: 'button', text: 'Go', anchor: 'bottomright', x: -12, y: -12 },
        ],
    };

    const once = toJson(fromObject(spec));
    const twice = toJson(fromObject(JSON.parse(once)));

    assert.equal(once, twice);
    assert.equal(fromObject(JSON.parse(once)).children.length, 2);
});

test('every property the codec can be told, it can also be asked', () => {
    // The handles on both engines are built by wiring `get` to read() and `set` to
    // apply(). A property one knows and the other does not would give a script a
    // member it can write and never read back, so the two switches have to agree.
    const node = new UiNode();

    for (const key of Object.keys(toObject(fromObject({})))) {
        assert.doesNotThrow(() => read(node, key), `read() does not know "${key}"`);
    }

    // And the read-only results are readable but refused as writes.
    for (const key of ['rect', 'hovered', 'pressed', 'clicked', 'focused', 'expanded']) {
        assert.doesNotThrow(() => read(node, key), `read() does not know "${key}"`);
        assert.equal(isWritable(key), false, `"${key}" should not be writable`);
    }
});

test('an unknown property is refused by the reader too, not only the writer', () => {
    assert.throws(() => read(new UiNode(), 'widht'), /not a UI node property/);
});
