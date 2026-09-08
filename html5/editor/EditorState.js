// -----------------------------------------------------------------------------
// EditorState — what the editor is looking at, and who wants to know.
//
// A single observable bag, as in EditorState.cs: every panel reads and writes it
// and subscribes to the changes it cares about, rather than panels holding
// references to each other.
// -----------------------------------------------------------------------------

import { SBEvent } from '../src/core/SBEvent.js';

/** The editor's shared state. */
export class EditorState {
    constructor() {
        /** Ordered selection: the last item is the primary actor shown in Details. */
        this._selectedActors = [];
        /** @type {?import('../src/core/Layer.js').Layer} */
        this.selectedLayer = null;
        /** @type {?string} */
        this.selectedAssetPath = null;

        this.isPlaying = false;
        this.isPaused = false;

        /** 'translate' | 'rotate' | 'scale' */
        this.gizmoMode = 'translate';
        /** Whether transform axes follow the world or the selected actor. */
        this.transformSpace = 'world';
        this.snapEnabled = false;
        this.translateSnap = 0.25;
        this.rotateSnap = 15;
        this.scaleSnap = 0.1;

        /** Draw the scene in 3D. False shows the flat 2D view. */
        this.viewport3D = true;

        /** Look through the scene's own camera rather than the editor's. */
        this.useGameCamera = false;

        this.projectRoot = '';
        this.projectName = 'Untitled';
        this.currentScenePath = null;
        this.sceneDirty = false;

        // ---- Signals ----
        this.selectionChanged = new SBEvent();
        this.hierarchyChanged = new SBEvent();
        this.sceneChanged = new SBEvent();
        this.playStateChanged = new SBEvent();
        this.projectOpened = new SBEvent();
        this.logged = new SBEvent();
    }

    /** The primary selected actor, retained for existing single-selection panels. */
    get selectedActor() { return this._selectedActors.at(-1) ?? null; }

    /** A read-only copy so panels cannot accidentally reorder shared selection. */
    get selectedActors() { return [...this._selectedActors]; }

    /** Replaces the selection with one actor, or clears it for null. */
    selectActor(actor) {
        this.selectActors(actor ? [actor] : []);
    }

    /** Replaces the ordered selection, ignoring null, destroyed and duplicate actors. */
    selectActors(actors) {
        const next = [];
        for (const actor of actors ?? []) {
            if (!actor || actor.isDestroyed || next.includes(actor)) continue;
            next.push(actor);
        }
        if (sameActors(next, this._selectedActors)) return;
        this._selectedActors = next;
        // SBEvent intentionally carries one payload. Existing panels receive the
        // primary actor as before and can read selectedActors when they need the
        // full ordered set.
        this.selectionChanged.broadcast(this.selectedActor);
    }

    /** Adds/removes an actor while retaining deterministic insertion order. */
    toggleActor(actor) {
        if (!actor || actor.isDestroyed) return;
        const next = this.selectedActors;
        const index = next.indexOf(actor);
        if (index >= 0) next.splice(index, 1);
        else next.push(actor);
        this.selectActors(next);
    }

    /** Marks the scene as edited, so the title bar and the save button update. */
    markDirty() {
        if (this.sceneDirty) return;
        this.sceneDirty = true;
        this.sceneChanged.broadcast(this);
    }

    markClean() {
        this.sceneDirty = false;
        this.sceneChanged.broadcast(this);
    }

    /** Tells the hierarchy that actors were added, removed or renamed. */
    notifyHierarchy() { this.hierarchyChanged.broadcast(this); }

    /** Writes a line to the console panel. */
    log(level, ...parts) {
        const message = parts.map(stringify).join(' ');
        this.logged.broadcast({ level, message, time: new Date() });
    }

    info(...parts) { this.log('info', ...parts); }
    warn(...parts) { this.log('warn', ...parts); }
    error(...parts) { this.log('error', ...parts); }
}

function sameActors(left, right) {
    return left.length === right.length && left.every((actor, index) => actor === right[index]);
}

function stringify(value) {
    if (typeof value === 'string') return value;
    if (value instanceof Error) return value.message;
    if (value && typeof value === 'object') {
        try { return JSON.stringify(value); } catch { return String(value); }
    }
    return String(value);
}
