// -----------------------------------------------------------------------------
// Holds directional navigation to the same golden cases the C# engine reads from
// the same file (SexyBiscuit.Tests/UiNavigationTests.cs).
//
// Navigation is what makes a gamepad, a D-pad and a TV remote work at all, and it
// is decided entirely by numbers. A scoring constant that differs between the two
// engines would make the same menu feel right on one and arbitrary on the other,
// and none of the source-reading parity tests in this repository could see it.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { find, NavDirection } from '../src/ui/UiNavigation.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const fixture = JSON.parse(fs.readFileSync(path.join(here, 'fixtures/nav-cases.json'), 'utf8'));

const rect = ([x, y, width, height]) => ({ x, y, width, height });

for (const testCase of fixture.cases) {
    test(`navigation case: ${testCase.name}`, () => {
        const from = rect(testCase.from);
        const candidates = testCase.candidates.map(rect);

        for (const [direction, expected] of Object.entries(testCase.expect)) {
            const actual = find(from, candidates, direction);
            assert.equal(actual, expected,
                `${direction} expected candidate ${expected} but landed on ${actual}`);
        }
    });
}

test('stepping one way and back again returns to where it started', () => {
    // A player walking a menu with a D-pad must never end up somewhere they cannot
    // get back from.
    const cells = [];
    for (let row = 0; row < 3; row++) {
        for (let col = 0; col < 3; col++) {
            cells.push({ index: row * 3 + col, rect: { x: col * 110, y: row * 60, width: 100, height: 50 } });
        }
    }

    const pairs = [[NavDirection.Right, NavDirection.Left], [NavDirection.Down, NavDirection.Up]];

    for (const start of cells) {
        const others = cells.filter((c) => c.index !== start.index);

        for (const [go, back] of pairs) {
            const stepped = find(start.rect, others.map((c) => c.rect), go);
            if (stepped < 0) continue;

            const landed = others[stepped];
            const fromThere = cells.filter((c) => c.index !== landed.index);
            const returned = find(landed.rect, fromThere.map((c) => c.rect), back);

            assert.ok(returned >= 0 && fromThere[returned].index === start.index,
                `${go} from ${start.index} landed on ${landed.index}, but ${back} did not come back`);
        }
    }
});
