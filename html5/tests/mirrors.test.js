// -----------------------------------------------------------------------------
// The mirror map — `/mirrors.json` names each C# file's JavaScript twin, and this
// suite is the JavaScript half of the checks that walk it: the pure diff rule
// behind tools/mirror-check.js, every listed file existing, every component's
// schema having a C# property per key, every mirrored enum agreeing member for
// member, and every registered component being mapped or listed as one-sided.
// SexyBiscuit.Tests/ComponentSchemaParityTests.cs is the other half.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { findUnmirrored, parseAllowances, loadMirrors, repoRoot } from '../tools/mirror-check.js';
import { listComponents } from '../src/core/TypeRegistry.js';
import '../src/registerBuiltins.js';

const mirrors = loadMirrors();
const read = (rel) => fs.readFileSync(path.join(repoRoot, rel), 'utf8');

// ---- the rule -----------------------------------------------------------------

const pairs = [
    { name: 'UiNode', strict: true,  cs: 'E/UiNode.cs', js: 'h/UiNode.js' },
    { name: 'Scene',  strict: false, cs: 'E/Scene.cs',  js: 'h/Scene.js' },
];

test('a strict pair changed on one side fails; both sides, or neither, pass', () => {
    assert.deepEqual(findUnmirrored(['E/UiNode.cs'], pairs).failures, ['UiNode: E/UiNode.cs changed, twin h/UiNode.js did not']);
    assert.deepEqual(findUnmirrored(['h/UiNode.js', 'E/UiNode.cs'], pairs).failures, []);
    assert.deepEqual(findUnmirrored(['README.md'], pairs).failures, []);
});

test('an advisory pair only warns, and an allowance excuses a strict one', () => {
    const { failures, warnings } = findUnmirrored(['E/Scene.cs'], pairs);
    assert.deepEqual(failures, []);
    assert.equal(warnings.length, 1);
    assert.deepEqual(findUnmirrored(['E/UiNode.cs'], pairs, { names: new Set(['UiNode']) }).failures, []);
    assert.deepEqual(findUnmirrored(['E/UiNode.cs'], pairs, { all: true }).failures, []);
});

test('commit trailers excuse a one-sided change by name or altogether', () => {
    const a = parseAllowances('Fix the painter\n\nMirror-only: cs UiPainter UiNode\n');
    assert.deepEqual([...a.names].sort(), ['UiNode', 'UiPainter']);
    assert.equal(a.all, false);
    assert.equal(parseAllowances('mirror-only: ALL').all, true);
    assert.equal(parseAllowances('nothing here').names.size, 0);
});

// ---- the map ------------------------------------------------------------------

test('every file the map names exists', () => {
    for (const pair of mirrors.pairs) {
        assert.ok(fs.existsSync(path.join(repoRoot, pair.cs)), `${pair.name}: ${pair.cs} is missing`);
        assert.ok(fs.existsSync(path.join(repoRoot, pair.js)), `${pair.name}: ${pair.js} is missing`);
    }
    for (const table of mirrors.tables) {
        for (const file of [table.path, table.cs, table.js]) {
            assert.ok(fs.existsSync(path.join(repoRoot, file)), `table ${table.name}: ${file} is missing`);
        }
    }
});

/** Public property names on a C# component, the way the C# parity test reads them. */
function csharpProperties(source) {
    return new Set([...source.matchAll(/public\s+[\w<>?\[\]\.]+\s+([A-Z]\w*)\s*(?:\{\s*get|=>)/g)].map((m) => m[1].toLowerCase()));
}

test('every property a browser component serialises exists on its C# twin', async () => {
    // A name that exists on one engine and not the other is applied on one and dropped with a
    // warning on the other, at runtime, in a build nobody has opened yet.
    const schemaPairs = mirrors.pairs.filter((p) => p.check === 'schema');
    assert.ok(schemaPairs.length >= 15, 'the map lists too few component pairs');

    for (const pair of schemaPairs) {
        const module = await import(pathToFileURL(path.join(repoRoot, pair.js)).href);
        const ctor = module[pair.name];
        assert.ok(ctor?.schema, `${pair.name}: ${pair.js} exports no class with a static schema`);
        // Enabled and the rest of the base class are every component's, and live in Component.cs.
        const properties = new Set([...csharpProperties(read(pair.cs)), ...csharpProperties(read('SexyBiscuit.Engine/Core/Component.cs'))]);
        const known = new Set((mirrors.components.knownGaps?.[pair.name] ?? []).map((k) => k.toLowerCase()));
        const missing = Object.keys(ctor.schema).filter((key) => !properties.has(key.toLowerCase()) && !known.has(key.toLowerCase()));
        assert.deepEqual(missing, [], `${pair.name}: the browser serialises ${missing.join(', ')}, which ${pair.cs} has no property for`);
    }
});

/** The members of a C# enum, whether it is declared on one line or many. */
function csharpEnumMembers(source, symbol) {
    const start = source.search(new RegExp(`enum ${symbol}\\b[^{]*\\{`));
    if (start < 0) return [];
    const open = source.indexOf('{', start);
    const close = source.indexOf('}', open);
    return source.slice(open + 1, close)
        .replace(/\/\/[^\n]*|\/\*[\s\S]*?\*\/|\[[^\]]*\]/g, '')
        .split(',')
        .map((entry) => entry.trim().split('=')[0].trim())
        .filter((name) => /^\w+$/.test(name))
        .sort();
}

test('every mirrored enum has the same members on both engines', async () => {
    const enums = mirrors.pairs.filter((p) => p.check === 'names');
    assert.ok(enums.length >= 2, 'the map lists too few enums');

    for (const pair of enums) {
        const allowed = new Set([...(pair.allowCsOnly ?? [])]);
        const theirs = csharpEnumMembers(read(pair.cs), pair.symbol).filter((n) => !allowed.has(n));
        assert.ok(theirs.length > 0, `${pair.cs} has no enum ${pair.symbol}`);
        const module = await import(pathToFileURL(path.join(repoRoot, pair.js)).href);
        const ours = Object.keys(module[pair.symbol] ?? {}).sort();
        assert.deepEqual(ours, theirs, `${pair.symbol} differs between the engines: C# ${pair.cs} has [${theirs.join(', ')}], JS ${pair.js} has [${ours.join(', ')}]`);
    }
});

test('every registered browser component is mapped, or listed as browser-only', () => {
    // A component one engine has and the other does not is a scene that half-loads there.
    const known = new Set([
        ...mirrors.pairs.filter((p) => p.kind === 'component').map((p) => p.name),
        ...mirrors.components.ignore,
        ...mirrors.components.jsOnly,
    ]);
    const unmapped = listComponents({ includeHidden: true }).map((e) => e.name).filter((n) => !known.has(n));
    assert.deepEqual(unmapped, [], `add a pair or a components.jsOnly entry in mirrors.json for: ${unmapped.join(', ')}`);
});
