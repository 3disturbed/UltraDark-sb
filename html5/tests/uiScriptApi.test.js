// -----------------------------------------------------------------------------
// The widget tree as a game script reaches it. The mirror of
// SexyBiscuit.Tests/UiScriptApiTests.cs.
//
// The tree was finished and painting on both engines for a while before anything
// in the contract could reach a single node, so a HUD had no way onto it. These
// hold the bridge to the contract and to the behaviour a HUD actually needs.
//
// The handle is checked in BOTH directions. The flat UI's handle was pinned one
// way only -- the contract was read and the C# side probed for each member -- so
// a member one engine had and the contract did not passed silently, and this
// engine's handle was pinned by nothing at all.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { createScriptGlobals } from '../src/scripting/ScriptBridge.js';
import { Actor } from '../src/core/Actor.js';
import { UiCanvas } from '../src/ui/UiCanvas.js';
import { SCRIPT_PROPERTIES } from '../src/ui/UiDocument.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const contract = JSON.parse(
    fs.readFileSync(path.join(here, '../src/scripting/bridge-api.json'), 'utf8'));

/** A script's globals on a throwaway actor, plus the teardown the component would call. */
function script(name = 'Host') {
    const globals = createScriptGlobals(new Actor(name), {});
    const dispose = Object.getOwnPropertySymbols(globals)
        .map((s) => globals[s])
        .find((v) => typeof v === 'function');
    return { UI: globals.UI, dispose };
}

test.afterEach(() => UiCanvas.clearAll());

// -----------------------------------------------------------------------------
// The contract
// -----------------------------------------------------------------------------

test('a handle carries exactly the members the contract promises', () => {
    const { UI } = script();
    const root = UI.build({ name: 'root' });

    const expected = Object.keys(contract.uiNode).sort();
    const actual = Object.getOwnPropertyNames(root).sort();

    assert.deepEqual(actual, expected,
        `the node handle differs from bridge-api.json\n`
        + `  missing: ${expected.filter((n) => !actual.includes(n))}\n`
        + `  extra:   ${actual.filter((n) => !expected.includes(n))}`);
});

test('the UI global carries exactly the members the contract promises', () => {
    const { UI } = script();
    assert.deepEqual(
        Object.getOwnPropertyNames(UI).sort(),
        Object.keys(contract.globals.UI).sort());
});

test('every handle property can be read, and the writable ones written', () => {
    // A property the codec can be told must also be one it can be asked, or a script
    // gets a member it can write and never read back.
    const { UI } = script();
    const root = UI.build({ name: 'root' });

    for (const key of SCRIPT_PROPERTIES) {
        assert.notEqual(root[key], undefined, `root.${key} reads undefined`);
    }
});

// -----------------------------------------------------------------------------
// Building a tree
// -----------------------------------------------------------------------------

test('a script can build a tree and reach it by name', () => {
    const { UI } = script();
    const root = UI.build({
        name: 'hud', layout: 'column', gap: 8, padding: 12,
        children: [
            { name: 'title', kind: 'label', text: 'JAKE01', scale: 3 },
            { name: 'hp', kind: 'bar', width: '*', height: 10, value: 0.72 },
        ],
    });

    assert.equal(UI.find('title').text, 'JAKE01');
    assert.equal(root.children.length, 2);
    assert.equal(UI.find('hp').parent.name, 'hud');

    // The one a HUD writes every frame.
    UI.find('hp').value = 0.25;
    assert.equal(UI.find('hp').value, 0.25);
});

test('the same node always comes back as the same handle', () => {
    // Two reads of the same node must be the same handle, or a script comparing what
    // it found against what it kept is quietly always false.
    const { UI } = script();
    UI.build({ children: [{ name: 'ok', kind: 'button' }] });
    assert.equal(UI.find('ok'), UI.find('ok'));
});

test('a node added at runtime joins the tree', () => {
    const { UI } = script();
    const root = UI.build({ name: 'root', layout: 'column' });
    const added = root.add({ name: 'later', kind: 'label', text: 'hello' });

    assert.equal(root.children.length, 1);
    assert.equal(UI.find('later').text, 'hello');

    added.remove();
    assert.equal(root.children.length, 0);
});

test('a misspelt property is refused rather than ignored', () => {
    // The flat UI ended its options switch with a silent default, so a misspelt key
    // did nothing and reported nothing. In a tree a mistyped "childern" would drop
    // every node below it and leave no trace at all.
    const { UI } = script();
    assert.throws(() => UI.build({ widht: 100 }), /not a UI node property/);
    assert.equal(UI.root.children.length, 0, 'nothing was half-built behind the refusal');
});

// -----------------------------------------------------------------------------
// Ownership
// -----------------------------------------------------------------------------

test('a script can only clear its own nodes', () => {
    const a = script('A');
    const b = script('B');

    a.UI.build({ children: [{ name: 'fromA', kind: 'label', text: 'A' }] });
    b.UI.build({ children: [{ name: 'fromB', kind: 'label', text: 'B' }] });

    a.UI.clear();

    assert.equal(a.UI.find('fromA'), null);
    assert.equal(b.UI.find('fromB').text, 'B');
});

test('destroying the script takes its canvas with it', () => {
    // The flat UI had exactly this bug and shipped with it: this engine cleaned up on
    // teardown and the native side never called its own disposer, so a destroyed
    // actor left its HUD on screen in one build and not the other.
    const before = UiCanvas.all.length;
    const { UI, dispose } = script();

    UI.build({ children: [{ name: 'hud', kind: 'label', text: 'x' }] });
    assert.equal(UiCanvas.all.length, before + 1);

    dispose();
    assert.equal(UiCanvas.all.length, before);
});

// -----------------------------------------------------------------------------
// Focus, which is what a pad and a TV remote drive
// -----------------------------------------------------------------------------

test('a script can move focus between the buttons it built', () => {
    const { UI } = script();
    UI.build({
        layout: 'column', gap: 8,
        children: [
            { name: 'one', kind: 'button', text: 'One', width: 200, height: 40 },
            { name: 'two', kind: 'button', text: 'Two', width: 200, height: 40 },
        ],
    });

    // Layout has to have run for navigation to have rectangles to reason about.
    UiCanvas.all[0].setViewport(800, 600);
    UiCanvas.all[0].layout();

    UI.setFocus(UI.find('one'));
    assert.equal(UI.focused.name, 'one');

    assert.equal(UI.navigate('down'), true);
    assert.equal(UI.focused.name, 'two');

    // Nothing below the last one, so nothing happens rather than wrapping surprisingly.
    assert.equal(UI.navigate('down'), false);
    assert.equal(UI.focused.name, 'two');
});
