import test from 'node:test';
import assert from 'node:assert/strict';

import { EditorState } from '../editor/EditorState.js';
import { Editor } from '../editor/Editor.js';

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
