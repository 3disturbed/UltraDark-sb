import test from 'node:test';
import assert from 'node:assert/strict';

import { PropertyType } from '../src/core/PropertyTypes.js';
import { Vector3 } from '../src/math/index.js';
import { appendListValue, formatListValue, parseListValue } from '../editor/listValue.js';

test('list inspector values validate array JSON and restore their schema type', () => {
    const descriptor = { type: PropertyType.List, of: { type: PropertyType.Vector3 } };
    const next = parseListValue('[[1, 2, 3]]', descriptor);

    assert.equal(next.length, 1);
    assert.ok(next[0] instanceof Vector3);
    assert.deepEqual(next[0].toArray(), [1, 2, 3]);
    assert.equal(formatListValue(next, descriptor), '[\n  [\n    1,\n    2,\n    3\n  ]\n]');
});

test('list inspector values reject invalid and non-array JSON without mutating a component', () => {
    const descriptor = { type: PropertyType.List, of: { type: PropertyType.Int } };
    assert.throws(() => parseListValue('{"not":"a list"}', descriptor), /JSON array/);
    assert.throws(() => parseListValue('[', descriptor), /valid JSON array/);
});

test('adding a collection item uses a fresh schema default', () => {
    const descriptor = { type: PropertyType.List, of: { type: PropertyType.Vector3, default: [2, 3, 4] } };
    const next = appendListValue([], descriptor);
    assert.equal(next.length, 1);
    assert.ok(next[0] instanceof Vector3);
    assert.deepEqual(next[0].toArray(), [2, 3, 4]);
});
