// -----------------------------------------------------------------------------
// Interaction, from the shared fixture.
//
// The mirror of SexyBiscuit.Tests/UiInteractionTests.cs. Both suites read
// tests/fixtures/ui-interaction-cases.json, so a judgement call made on one engine
// and not the other fails here rather than in somebody's game six weeks later.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { UiCanvas } from '../src/ui/UiCanvas.js';
import { fromObject } from '../src/ui/UiDocument.js';

const here = dirname(fileURLToPath(import.meta.url));
const fixture = JSON.parse(readFileSync(join(here, 'fixtures', 'ui-interaction-cases.json'), 'utf8'));

/** The tolerance the layout cases use, for the same reason: these are floats. */
const EPSILON = 0.01;

for (const testCase of fixture.cases) {
    test(`interaction case: ${testCase.name}`, () => {
        UiCanvas.clearAll();

        const canvas = new UiCanvas();
        canvas.interactive = testCase.interactive ?? true;
        canvas.setViewport(testCase.viewport[0], testCase.viewport[1]);
        canvas.adopt(fromObject(testCase.tree));

        let previous = null;
        for (const spec of testCase.frames) {
            const pointer = spec.pointer ? { x: spec.pointer[0], y: spec.pointer[1] } : { x: -1, y: -1 };
            const delta = previous
                ? { x: pointer.x - previous.x, y: pointer.y - previous.y }
                : { x: 0, y: 0 };
            previous = pointer;

            // Layout every frame: scrolling and a changed value both move rectangles,
            // and the hit test has to read this frame's.
            canvas.layout();
            canvas.input.update({ ...spec, pointer, pointerDelta: delta });
        }

        for (const [name, expected] of Object.entries(testCase.expect)) {
            const node = canvas.find(name);
            assert.ok(node, `${testCase.name}: no node named "${name}"`);

            for (const [key, want] of Object.entries(expected)) {
                const got = readState(node, key);
                if (typeof want === 'number') {
                    assert.ok(Math.abs(got - want) <= EPSILON,
                        `${testCase.name}: ${name}.${key} was ${got}, expected ${want}`);
                } else {
                    assert.equal(got, want, `${testCase.name}: ${name}.${key}`);
                }
            }
        }

        UiCanvas.clearAll();
    });
}

/** The state keys a case may assert on, named the same on both engines. */
function readState(node, key) {
    switch (key) {
        case 'hovered':       return node.hovered;
        case 'pressed':       return node.pressed;
        case 'clicked':       return node.clicked;
        case 'focused':       return node.focused;
        case 'expanded':      return node.expanded;
        case 'checked':       return node.checked;
        case 'value':         return node.value;
        case 'selectedIndex': return node.selectedIndex;
        case 'text':          return node.text;
        case 'scrollOffsetX': return node.scrollOffset.x;
        case 'scrollOffsetY': return node.scrollOffset.y;
        default: throw new Error(`Unknown state key "${key}" in the interaction fixture.`);
    }
}
