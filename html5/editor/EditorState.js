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
        /** @type {?import('../src/core/Actor.js').Actor} */
        this._selectedActor = null;
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

    get selectedActor() { return this._selectedActor; }

    /** Selects an actor, or null to clear. Broadcasts only on a real change. */
    selectActor(actor) {
        if (this._selectedActor === actor) return;
        this._selectedActor = actor && !actor.isDestroyed ? actor : null;
        this.selectionChanged.broadcast(this._selectedActor);
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

function stringify(value) {
    if (typeof value === 'string') return value;
    if (value instanceof Error) return value.message;
    if (value && typeof value === 'object') {
        try { return JSON.stringify(value); } catch { return String(value); }
    }
    return String(value);
}
