// -----------------------------------------------------------------------------
// UiCanvas — one screen-space surface holding a tree of UiNode.
//
// The mirror of SexyBiscuit.Engine/UI/UiCanvas.cs, member for member. The flat
// five-kind surface a script builds through the `UI` global is ScriptUi.js next
// door; this is the retained tree that UiLayout, UiFocus and UiDocument work on.
//
// The scale and offset computed here are used by *both* painting and hit-testing.
// The canvas this replaces had a scale matrix that nothing ever called, so pointer
// coordinates stayed in raw pixels and hit-testing quietly disagreed with what was
// on screen at every window size but one.
// -----------------------------------------------------------------------------

import { UiNode } from './UiNode.js';
import { UiKind, UiScaleMode, SafeAreaMode } from './UiEnums.js';
import { measure as measureLayout, arrange, hitTest as hitTestLayout, deflate } from './UiLayout.js';
import { UiInput } from './UiInput.js';
import { fromJson } from './UiDocument.js';

/** Every canvas that currently exists, for the host's screen-space pass. */
const all = [];

export class UiCanvas {
    constructor() {
        // --- configuration ---
        this.scaleMode = UiScaleMode.ConstantPixel;
        this.referenceResolution = { x: 1920, y: 1080 };
        this.matchWidthOrHeight = 0.5;

        /** Paint order between canvases. Lower paints first, so higher is in front. */
        this.order = 0;

        /** Path to a `.ui` document. When set it replaces the tree at start. */
        this.document = '';

        /** Insets kept clear of notches and TV overscan, as left, top, right, bottom. */
        this.safeArea = { x: 0, y: 0, z: 0, w: 0 };
        this.safeAreaMode = SafeAreaMode.Ignore;

        /** Whether this canvas takes pointer and navigation input at all. */
        this.interactive = true;

        /** Which player drives this canvas, or -1 for anybody's input. */
        this.playerIndex = -1;

        /** The tree. Always present; an empty root paints nothing. */
        this.root = new UiNode({ name: 'Root', kind: UiKind.Panel });
        this.root.canvas = this;

        // --- resolved each frame ---
        this.scale = { x: 1, y: 1 };
        this.canvasOffset = { x: 0, y: 0 };
        this.canvasSize = { x: 1280, y: 720 };

        this._viewport = { x: 1280, y: 720 };
        this._input = null;

        all.push(this);
    }

    /** Every live canvas, in creation order. */
    static get all() { return all; }

    /** Drops every canvas. A test that leaks one poisons the next. */
    static clearAll() { all.length = 0; }

    /** Removes this canvas from the paint list. */
    destroy() {
        const i = all.indexOf(this);
        if (i >= 0) all.splice(i, 1);
    }

    // -------------------------------------------------------------------------
    // Input
    // -------------------------------------------------------------------------

    /**
     * This canvas's pointer, focus and control behaviour. Created on first use,
     * because the router holds focus state that has to survive between frames.
     */
    get input() {
        if (!this._input) this._input = new UiInput(this);
        return this._input;
    }

    // -------------------------------------------------------------------------
    // Scale
    // -------------------------------------------------------------------------

    /** Recomputes the scale and offset for a viewport size. Call before laying out. */
    setViewport(width, height) {
        const w = Math.max(1, width);
        const h = Math.max(1, height);
        if (this._viewport.x === w && this._viewport.y === h) return;

        this._viewport = { x: w, y: h };
        this.root.invalidateMeasure();
    }

    _resolveScale() {
        const vw = this._viewport.x;
        const vh = this._viewport.y;
        const rw = Math.max(1, this.referenceResolution.x);
        const rh = Math.max(1, this.referenceResolution.y);

        switch (this.scaleMode) {
            case UiScaleMode.ScaleToFit: {
                const s = Math.min(vw / rw, vh / rh);
                this.scale = { x: s, y: s };
                this.canvasSize = { x: rw, y: rh };
                break;
            }
            case UiScaleMode.ScaleToFill: {
                const s = Math.max(vw / rw, vh / rh);
                this.scale = { x: s, y: s };
                this.canvasSize = { x: rw, y: rh };
                break;
            }
            case UiScaleMode.Match: {
                const m = Math.min(1, Math.max(0, this.matchWidthOrHeight));
                const s = Math.pow(vw / rw, 1 - m) * Math.pow(vh / rh, m);
                this.scale = { x: s, y: s };
                this.canvasSize = { x: vw / s, y: vh / s };
                break;
            }
            default:
                this.scale = { x: 1, y: 1 };
                this.canvasSize = { x: vw, y: vh };
                break;
        }

        // Centre whatever the scaled canvas does not cover, so a letterbox is even.
        this.canvasOffset = {
            x: (vw - this.canvasSize.x * this.scale.x) * 0.5,
            y: (vh - this.canvasSize.y * this.scale.y) * 0.5,
        };
    }

    // -------------------------------------------------------------------------
    // Coordinates
    // -------------------------------------------------------------------------

    /** Device pixels to canvas units. Every hit test starts here. */
    screenToCanvas(point) {
        return {
            x: (point.x - this.canvasOffset.x) / this.scale.x,
            y: (point.y - this.canvasOffset.y) / this.scale.y,
        };
    }

    /** Canvas units to device pixels. Clip rectangles must go through this. */
    canvasToScreen(point) {
        return {
            x: point.x * this.scale.x + this.canvasOffset.x,
            y: point.y * this.scale.y + this.canvasOffset.y,
        };
    }

    /** A canvas rectangle in device pixels, for the clip region. */
    canvasRectToScreen(r) {
        const topLeft = this.canvasToScreen({ x: r.x, y: r.y });
        const bottomRight = this.canvasToScreen({ x: r.x + r.width, y: r.y + r.height });
        return {
            x: topLeft.x,
            y: topLeft.y,
            width: bottomRight.x - topLeft.x,
            height: bottomRight.y - topLeft.y,
        };
    }

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------

    /**
     * The rectangle the root is laid out into: the canvas, less the safe-area
     * insets on whichever axes they apply to.
     */
    get safeRect() {
        const full = { x: 0, y: 0, width: this.canvasSize.x, height: this.canvasSize.y };
        const s = this.safeArea;

        switch (this.safeAreaMode) {
            case SafeAreaMode.Inset:  return deflate(full, s);
            case SafeAreaMode.InsetX: return deflate(full, { x: s.x, y: 0, z: s.z, w: 0 });
            case SafeAreaMode.InsetY: return deflate(full, { x: 0, y: s.y, z: 0, w: s.w });
            default:                  return full;
        }
    }

    /**
     * Measures and arranges the tree if anything has changed since the last pass.
     * Cheap to call every frame; that is the point of the dirty flags.
     */
    layout() {
        this._resolveScale();
        if (!this.root.measureDirty && !this.root.arrangeDirty) return;

        const area = this.safeRect;
        measureLayout(this.root, { x: area.width, y: area.height });
        arrange(this.root, area, { x: 0, y: 0, width: this.canvasSize.x, height: this.canvasSize.y });
    }

    /** Forces a full pass next frame, whatever the dirty flags say. */
    invalidateLayout() { this.root.invalidateMeasure(); }

    // -------------------------------------------------------------------------
    // Hit testing
    // -------------------------------------------------------------------------

    /** The top-most interactive node under a device-pixel point, or null. */
    hitTest(screenPoint) {
        if (!this.interactive) return null;
        return hitTestLayout(this.root, this.screenToCanvas(screenPoint));
    }

    /** Finds a node by name anywhere in this canvas. */
    find(name) { return this.root.find(name); }

    // -------------------------------------------------------------------------
    // Documents
    // -------------------------------------------------------------------------

    /** Parses a `.ui` document and adopts it. */
    loadDocument(source) { this.adopt(fromJson(source)); }

    /**
     * Moves a loaded document's children onto the root, replacing what was there.
     *
     * The document's own root is not adopted as this canvas's root — its layout
     * properties are copied across and its children are moved. A canvas whose root
     * could be swapped would break every reference the host and the router hold.
     */
    adopt(document) {
        for (let i = this.root.children.length - 1; i >= 0; i--) {
            this.root.remove(this.root.children[i]);
        }

        this.root.layout = document.layout;
        this.root.gap = document.gap;
        this.root.wrap = document.wrap;
        this.root.padding = document.padding;
        this.root.mainAlign = document.mainAlign;
        this.root.crossAlign = document.crossAlign;
        this.root.columns = document.columns;
        this.root.cellSize = document.cellSize;
        this.root.background = document.background;
        this.root.clip = document.clip;
        this.root.scroll = document.scroll;
        this.root.scrollOffset = document.scrollOffset;

        for (const child of [...document.children]) this.root.add(child);
        this.invalidateLayout();
    }
}
