// -----------------------------------------------------------------------------
// The benchmark's arithmetic, from the shared fixture.
//
// The mirror of SexyBiscuit.Tests/GraphicsBenchmarkTests.cs. The benchmark decides
// which preset a machine is offered, so the two engines agreeing on the average is
// not enough -- they have to agree on the percentile maths as well, or the same
// laptop is told "high" by the desktop build and "medium" by the web one.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { GraphicsBenchmark, summarise, describe, WARM_UP_FRAMES } from '../src/debug/GraphicsBenchmark.js';

const here = dirname(fileURLToPath(import.meta.url));
const fixture = JSON.parse(readFileSync(join(here, 'fixtures', 'benchmark-cases.json'), 'utf8'));

const EPSILON = 0.01;

for (const testCase of fixture.cases) {
    test(`benchmark case: ${testCase.name}`, () => {
        const result = summarise(testCase.frameTimes);

        for (const [key, want] of Object.entries(testCase.expect)) {
            assert.ok(Math.abs(result[key] - want) <= EPSILON,
                `${key} was ${result[key]}, expected ${want}`);
        }

        assert.equal(result.suggestedPreset, testCase.expectSuggested);
    });
}

test('the warm-up frames are discarded, because they time a preset switch', () => {
    // Starting a run means changing preset, which reallocates buffers and drops the
    // primitive cache. Timing that would measure the switch, not the hardware.
    const benchmark = new GraphicsBenchmark();
    benchmark.duration = 1;
    benchmark.start();

    for (let i = 0; i < WARM_UP_FRAMES; i++) {
        assert.equal(benchmark.tick(0.5), null, 'a warm-up frame produced a result');
    }

    let result = null;
    while (!result) result = benchmark.tick(1 / 60);

    // Half-second frames would have dragged the average to about 2 fps.
    assert.ok(result.averageFps > 55, `the warm-up leaked in: ${result.averageFps} fps`);
});

test('a stall is not this machine\'s frame rate and is left out', () => {
    const benchmark = new GraphicsBenchmark();
    benchmark.duration = 0.5;
    benchmark.start();
    for (let i = 0; i < WARM_UP_FRAMES; i++) benchmark.tick(1 / 60);

    benchmark.tick(4);        // An alt-tab, or a garbage collection the size of a level load.
    let result = null;
    while (!result) result = benchmark.tick(1 / 60);

    assert.ok(result.averageFps > 55, `the stall was averaged in: ${result.averageFps} fps`);
});

test('a run reports its progress and stops exactly once', () => {
    const benchmark = new GraphicsBenchmark();
    benchmark.duration = 1;
    benchmark.start();
    for (let i = 0; i < WARM_UP_FRAMES; i++) benchmark.tick(1 / 60);

    assert.ok(benchmark.progress >= 0 && benchmark.progress < 1);

    let results = 0;
    for (let i = 0; i < 200; i++) if (benchmark.tick(1 / 60)) results++;

    assert.equal(results, 1, 'the run produced a result more than once');
    assert.equal(benchmark.isRunning, false);
    assert.equal(benchmark.progress, 0, 'a finished run still reports progress');
});

test('no frames at all is a printable result rather than a crash', () => {
    const result = summarise([]);
    assert.equal(result.averageFps, 0);
    assert.equal(result.frames, 0);
    assert.match(describe(result), /suggests/);
});

test('the one-line summary names every number the menu shows', () => {
    const line = describe(summarise([1 / 60, 1 / 60, 1 / 30]));
    assert.match(line, /fps average/);
    assert.match(line, /1% low/);
    assert.match(line, /0\.1% low/);
    assert.match(line, /suggests/);
});
