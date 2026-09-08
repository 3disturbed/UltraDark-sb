// -----------------------------------------------------------------------------
// EditorHistory — reversible authoring operations for the browser workbench.
//
// Scene snapshots are deliberately reserved for structural changes. Continuous
// tools such as the viewport gizmo record a single value command instead, so a
// drag is one meaningful undo step rather than hundreds of mouse samples.
// -----------------------------------------------------------------------------

export class EditorHistory {
    constructor(editor, { limit = 100 } = {}) {
        this.editor = editor;
        this.limit = limit;
        this._undo = [];
        this._redo = [];
    }

    get canUndo() { return this._undo.length > 0; }
    get canRedo() { return this._redo.length > 0; }
    get undoLabel() { return this._undo.at(-1)?.label ?? null; }
    get redoLabel() { return this._redo.at(-1)?.label ?? null; }

    clear() {
        this._undo.length = 0;
        this._redo.length = 0;
    }

    /** Records a pair of already-defined reversible operations. */
    push(label, undo, redo) {
        this._undo.push({ label, undo, redo });
        if (this._undo.length > this.limit) this._undo.shift();
        this._redo.length = 0;
    }

    /** Runs a structural change and stores the scene before and after it. */
    snapshot(label, action) {
        const before = this.editor.captureHistorySnapshot();
        const result = action();
        const after = this.editor.captureHistorySnapshot();
        if (before.scene !== after.scene) {
            this.push(label,
                () => this.editor.restoreHistorySnapshot(before),
                () => this.editor.restoreHistorySnapshot(after));
        }
        return result;
    }

    undo() {
        const entry = this._undo.pop();
        if (!entry) return false;
        entry.undo();
        this._redo.push(entry);
        this.editor.state.info(`Undid ${entry.label}.`);
        return true;
    }

    redo() {
        const entry = this._redo.pop();
        if (!entry) return false;
        entry.redo();
        this._undo.push(entry);
        this.editor.state.info(`Redid ${entry.label}.`);
        return true;
    }
}
