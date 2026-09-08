// -----------------------------------------------------------------------------
// Editor workbench contract — browser commands and reversible edit behaviour.
// The JSON is the shared source of truth for browser and native workbench work;
// keeping this small fixture here makes a command rename fail before it reaches
// a user.
// -----------------------------------------------------------------------------

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { EditorHistory } from '../editor/EditorHistory.js';
import { Editor } from '../editor/Editor.js';
import { InspectorPanel } from '../editor/panels/Inspector.js';
import { PropertyType } from '../src/core/PropertyTypes.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const workbench = JSON.parse(fs.readFileSync(path.join(here, '../../editor-workbench.json'), 'utf8'));

test('the workbench reserves one UE-style transform vocabulary for both editors', () => {
    assert.equal(workbench.version, 1);
    assert.deepEqual(workbench.layout.regions,
        ['menu', 'modes', 'viewport', 'outliner', 'details', 'content', 'output', 'status']);
    assert.equal(workbench.commands['editor.transform.translate'].shortcut, 'W');
    assert.equal(workbench.commands['editor.transform.rotate'].shortcut, 'E');
    assert.equal(workbench.commands['editor.transform.scale'].shortcut, 'R');
    assert.deepEqual(workbench.transform.spaces, ['world', 'local']);
    assert.equal(workbench.selection.primary, 'last');
    assert.deepEqual(workbench.selection.multiSelect, ['toggle', 'range']);
});

test('every browser schema property type has an explicit cross-client capability status', () => {
    const coverage = workbench.propertyTypeCoverage;
    assert.deepEqual(Object.keys(coverage).sort(), Object.values(PropertyType).sort());
    for (const statuses of Object.values(coverage)) {
        assert.ok(workbench.componentStatus.includes(statuses.native));
        assert.ok(workbench.componentStatus.includes(statuses.browser));
    }
    assert.equal(coverage.list.browser, 'editable');
    assert.equal(coverage.material.browser, 'read-only-with-reason');
});

test('browser command dispatch exposes every contracted command identifier', () => {
    const editor = Object.create(Editor.prototype);
    editor.state = { selectedActor: null, selectedActors: [], isPlaying: false, gizmoMode: 'translate' };
    editor.history = { canUndo: false, canRedo: false };
    const ids = editor.commandDefinitions().map((command) => command.id);

    for (const id of Object.keys(workbench.commands)) assert.ok(ids.includes(id), id);
    assert.equal(editor.executeCommand('editor.transform.rotate'), true);
    assert.equal(editor.state.gizmoMode, 'rotate');
    assert.equal(editor.executeCommand('editor.delete'), false);
});

test('a structural browser edit is restored as one named history operation', () => {
    let scene = 'before';
    const messages = [];
    const editor = {
        state: { info: (message) => messages.push(message) },
        captureHistorySnapshot: () => ({ scene }),
        restoreHistorySnapshot: (snapshot) => { scene = snapshot.scene; },
    };
    const history = new EditorHistory(editor);

    history.snapshot('Add Cube', () => { scene = 'after'; });
    assert.equal(history.undoLabel, 'Add Cube');
    assert.equal(history.undo(), true);
    assert.equal(scene, 'before');
    assert.equal(history.redo(), true);
    assert.equal(scene, 'after');
    assert.deepEqual(messages, ['Undid Add Cube.', 'Redid Add Cube.']);
});

test('one focused inspector edit creates one transaction and Escape restores it', () => {
    let scene = 'before';
    const entries = [];
    const panel = Object.create(InspectorPanel.prototype);
    panel._fieldTransaction = null;
    panel.editor = {
        scene: {},
        history: { push: (...entry) => entries.push(entry) },
        captureHistorySnapshot: () => ({ scene }),
        restoreHistorySnapshot: (snapshot) => { scene = snapshot.scene; },
    };
    panel.state = {
        sceneDirty: false,
        markDirty() { this.sceneDirty = true; },
        markClean() { this.sceneDirty = false; },
    };

    panel._beginFieldTransaction('Set speed');
    scene = 'after';                 // many input events may happen before blur
    panel._commitFieldTransaction();
    assert.equal(entries.length, 1);
    assert.equal(entries[0][0], 'Set speed');
    entries[0][1]();
    assert.equal(scene, 'before');

    panel.state.markClean();
    panel._beginFieldTransaction('Set speed');
    scene = 'cancelled value';
    panel._cancelFieldTransaction();
    assert.equal(scene, 'before');
    assert.equal(panel.state.sceneDirty, false);
});
