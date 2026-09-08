// -----------------------------------------------------------------------------
// The contract describes every member, not just names it: a fn has params and a
// return, a prop has a type, every type an expression names is declared, and the
// checked-in .d.ts is what the generator writes from it. bridge.test.js holds
// the browser bridge to the names; this holds the descriptions to the members.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

import { generateDts, typeNames, declaredTypes, contractPath, outputPath, HEADER, BUILTIN_TYPES } from '../tools/gen-dts.js';

const contract = JSON.parse(fs.readFileSync(contractPath, 'utf8'));

function* everyMember() {
    for (const [g, members] of Object.entries(contract.globals)) for (const [m, info] of Object.entries(members)) yield [`${g}.${m}`, info];
    for (const section of ['bare', 'actorProxy', 'actorProxyTransform', 'collisionData', 'uiNode']) {
        for (const [m, info] of Object.entries(contract[section])) yield [`${section}.${m}`, info];
    }
    for (const [name, type] of Object.entries(contract.types ?? {})) for (const [m, info] of Object.entries(type.members ?? {})) yield [`types.${name}.${m}`, info];
}

test('every fn has params and a return, every prop a type', () => {
    for (const [path, info] of everyMember()) {
        if (info.kind === 'fn' || info.params !== undefined) {
            assert.ok(Array.isArray(info.params), `${path}: a fn needs params (an array, empty for none)`);
            assert.ok(typeof info.returns === 'string' && info.returns, `${path}: a fn needs returns`);
            for (const p of info.params) assert.ok(p.name && p.type, `${path}: parameter ${JSON.stringify(p)} needs name and type`);
        } else {
            assert.ok(typeof info.type === 'string' && info.type, `${path}: a prop needs type`);
        }
    }
    for (const [name, hook] of Object.entries(contract.hooks)) {
        assert.ok(Array.isArray(hook.params), `hooks.${name} needs params`);
    }
});

test('every type a member names is declared or a builtin', () => {
    const declared = declaredTypes(contract);
    const expressions = [];
    for (const [path, info] of everyMember()) {
        if (info.type) expressions.push([path, info.type]);
        if (info.returns) expressions.push([path, info.returns]);
        for (const p of info.params ?? []) expressions.push([path, p.type]);
    }
    for (const hook of Object.values(contract.hooks)) for (const p of hook.params ?? []) expressions.push(['hook', p.type]);
    for (const [path, expression] of expressions) {
        for (const name of typeNames(expression)) {
            assert.ok(declared.has(name) || BUILTIN_TYPES.has(name), `${path} names the type ${name}, which nothing declares`);
        }
    }
});

test('the checked-in sb-engine.d.ts is what the generator writes', () => {
    // CI runs `npm run lint`, which includes gen-dts --check; this is the same fact as a test.
    const generated = generateDts(contract);
    assert.equal(fs.readFileSync(outputPath, 'utf8'), generated, 'sb-engine.d.ts is stale: run npm run gen');
    assert.ok(generated.includes(HEADER));
});

test('the generated declarations mention every global, member, hook and node key', () => {
    const text = generateDts(contract);
    for (const [g, members] of Object.entries(contract.globals)) {
        assert.ok(text.includes(`declare const ${g}:`), `no declaration for ${g}`);
        for (const m of Object.keys(members)) assert.ok(new RegExp(`\\b${m}\\b`).test(text), `${g}.${m} is not declared`);
    }
    for (const b of Object.keys(contract.bare)) assert.ok(text.includes(`declare function ${b}(`));
    for (const h of Object.keys(contract.hooks)) assert.ok(text.includes(`declare function ${h}(`), `hook ${h} is not declared`);
    for (const k of Object.keys(contract.uiNode)) assert.ok(new RegExp(`\\b${k}\\b`).test(text), `uiNode.${k} is not declared`);
});
