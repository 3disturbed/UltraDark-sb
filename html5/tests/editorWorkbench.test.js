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
