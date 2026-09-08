import test from 'node:test';
import assert from 'node:assert/strict';

import { normaliseEditorLayout } from '../editor/Editor.js';

test('browser workbench layout restores only safe persisted dock sizes', () => {
    assert.deepEqual(normaliseEditorLayout({ left: 280, right: 360, bottom: 180 }),
        { left: 280, right: 360, bottom: 180 });
    assert.deepEqual(normaliseEditorLayout({ left: -5, right: 'wide', bottom: 900 }),
        { left: 240, right: 320, bottom: 210 });
    assert.deepEqual(normaliseEditorLayout(null), { left: 240, right: 320, bottom: 210 });
});
