import test from 'node:test';
import assert from 'node:assert/strict';

import { EditorState } from '../editor/EditorState.js';
import { Editor } from '../editor/Editor.js';
import { ViewportPanel } from '../editor/panels/Viewport.js';
import { Vector3 } from '../src/math/index.js';

const actor = (id, { parent = null } = {}) => ({
    id,
    isDestroyed: false,
    isDescendantOf(other) {
        for (let current = parent; current; current = current.parent) {
            if (current === other) return true;
        }
        return false;
    },
    parent,
});

test('editor selection is ordered, unique and carries its last actor as primary', () => {
    const state = new EditorState();
    const a = actor(1);
    const b = actor(2);
    const notifications = [];
    state.selectionChanged.add((primary) => notifications.push([
        primary?.id ?? null,
        state.selectedActors.map((item) => item.id),
    ]));

    state.selectActors([a, b, a, null]);
    assert.deepEqual(state.selectedActors, [a, b]);
    assert.equal(state.selectedActor, b);
    state.toggleActor(a);
    assert.deepEqual(state.selectedActors, [b]);
    state.toggleActor(a);
    assert.deepEqual(state.selectedActors, [b, a]);
    assert.equal(state.selectedActor, a);
    assert.deepEqual(notifications, [[2, [1, 2]], [2, [2]], [1, [2, 1]]]);
});

test('duplicate/delete selection use roots so a selected child is not applied twice', () => {
    const parent = actor(1);
    const child = actor(2, { parent });
    const sibling = actor(3);
    const editor = Object.create(Editor.prototype);
    editor.state = { selectedActors: [parent, child, sibling] };

    assert.deepEqual(editor._selectionRoots(), [parent, sibling]);
});

test('a multi-transform drag restores every selected transform as one history item', () => {
    const makeTransform = (x) => ({
        position: new Vector3(x, 0, 0),
        localScale: new Vector3(1, 1, 1),
        eulerAngles: new Vector3(0, 0, 0),
    });
    const first = makeTransform(0);
    const second = makeTransform(4);
    const start = (transform) => ({
        position: transform.position.clone(), scale: transform.localScale.clone(), euler: transform.eulerAngles.clone(),
    });
    const entries = [];
    const panel = Object.create(ViewportPanel.prototype);
    panel.editor = { history: { push: (...entry) => entries.push(entry) } };
    panel.inspectorRefresh = () => {};
    panel._gizmoDrag = {
        actor: { name: 'Primary' },
        transforms: [
            { actor: { name: 'Primary' }, transform: first, start: start(first) },
            { actor: { name: 'Secondary' }, transform: second, start: start(second) },
        ],
    };

    first.position = new Vector3(2, 0, 0);
    second.position = new Vector3(6, 0, 0);
    panel._finishGizmo();
    assert.equal(entries[0][0], 'Transform 2 actors');
    entries[0][1]();
    assert.deepEqual(first.position.toArray(), [0, 0, 0]);
    assert.deepEqual(second.position.toArray(), [4, 0, 0]);
    entries[0][2]();
    assert.deepEqual(first.position.toArray(), [2, 0, 0]);
    assert.deepEqual(second.position.toArray(), [6, 0, 0]);
});
