// -----------------------------------------------------------------------------
// UiCanvas — one screen-space surface holding a tree of UiNode.
//
// The mirror of SexyBiscuit.Engine/UI/UiCanvas.cs, member for member. This is the
// retained tree that UiLayout, UiFocus and UiDocument work on, and what the `UI`
// script global builds into.
//
// The scale and offset computed here are used by *both* painting and hit-testing.
// The canvas this replaces had a scale matrix that nothing ever called, so pointer
// coordinates stayed in raw pixels and hit-testing quietly disagreed with what was
// on screen at every window size but one.
// -----------------------------------------------------------------------------

import { Component } from '../core/Component.js';
import { PropertyType as P } from '../core/PropertyTypes.js';
import { registerComponent } from '../core/TypeRegistry.js';
import { UiNode } from './UiNode.js';
import { UiKind, UiScaleMode, SafeAreaMode, UiSpace, UiFacing, PositionMode, canonical } from './UiEnums.js';
import { Vector3 } from '../math/Vector3.js';
import { Transform3D } from '../core/Transform3D.js';
import { Camera3D } from '../rendering/Camera3D.js';
import { build as buildBasis, rayToCanvas } from './UiWorld.js';
import { measure as measureLayout, arrange, hitTest as hitTestLayout, deflate } from './UiLayout.js';
import { UiInput } from './UiInput.js';
import { fromJson, fromObject, apply as uiApply, read as uiRead, WRITABLE_KEYS } from './UiDocument.js';

/** Every canvas that currently exists, for the host's screen-space pass. */
const all = [];

/**
 * Properties of the root that belong to the canvas, not to the document it adopts.
 *
 * A document's root is a box like any other and may well have been authored with a size and a
 * position; the canvas root is neither, it is the whole surface. Copying a width onto it would
 * shrink the UI to whatever the author happened to be looking at when they saved. Everything
 * else is copied -- by walking the codec's own key list rather than a hand-written one, so a
 * property added to the format is not quietly lost by a list nobody remembered to extend.
 */
const ROOT_OWNED_KEYS = new Set([
    'width', 'height', 'minwidth', 'minheight', 'maxwidth', 'maxheight',
    'grow', 'shrink', 'margin', 'positioning', 'anchor', 'anchormin', 'anchormax',
    'pivot', 'offset', 'offsetmax', 'worldfollow', 'worldanchor', 'worldfollowdistance',
]);

export class UiCanvas extends Component {
    /**
     * What a scene file stores: the canvas's settings and the path to its document,
     * never the tree. Mirrors the public properties of the C# UiCanvas, and
     * ComponentSchemaParityTests holds the two lists together.
     */
    static schema = {
        scaleMode:           { type: P.Enum, values: ['ConstantPixel', 'ScaleToFit', 'ScaleToFill', 'Match'], default: 'ConstantPixel' },
        referenceResolution: { type: P.Vector2, default: [1920, 1080] },
        matchWidthOrHeight:  { type: P.Number, default: 0.5, min: 0, max: 1 },
        order:               { type: P.Int, default: 0 },
        document:            { type: P.Asset, assetKind: 'ui', default: '' },
        safeArea:            { type: P.Vector4, default: [0, 0, 0, 0] },
        safeAreaMode:        { type: P.Enum, values: ['Ignore', 'Inset', 'InsetX', 'InsetY'], default: 'Ignore' },
        interactive:         { type: P.Bool, default: true },
        playerIndex:         { type: P.Int, default: -1 },
        space:               { type: P.Enum, values: ['Screen', 'World'], default: 'Screen' },
        facing:              { type: P.Enum, values: ['Billboard', 'VerticalBillboard', 'Plane'], default: 'Billboard' },
        worldPosition:       { type: P.Vector3, default: [0, 0, 0] },
        worldOffset:         { type: P.Vector3, default: [0, 0, 0] },
        pixelsPerUnit:       { type: P.Number, default: 100 },
        maxDrawDistance:     { type: P.Number, default: 0 },
        doubleSided:         { type: P.Bool, default: false },
        depthTest:           { type: P.Bool, default: true },
    };

    constructor() {
        super();
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

        // --- world space ---

        /** Whether this canvas is a surface on the screen or a plane in the world. */
        this.space = UiSpace.Screen;

        /** How a world-space canvas is turned to face the player. */
        this.facing = UiFacing.Billboard;

        /** Where a world canvas stands when its actor has no Transform3D -- or no actor. */
        this.worldPosition = { x: 0, y: 0, z: 0 };

        /** Shifts a world canvas off its anchor, in world units. */
        this.worldOffset = { x: 0, y: 0, z: 0 };

        /** Canvas units per world unit: the size dial for a world canvas. */
        this.pixelsPerUnit = 100;

        /** Stop drawing a world canvas past this distance. Zero never stops. */
        this.maxDrawDistance = 0;

        /** Whether a world canvas is legible from behind as well as in front. */
        this.doubleSided = false;

        /** Whether geometry in front of a world canvas hides it. */
        this.depthTest = true;

        /** The last canvas point a pointer ray found, held across a drag that leaves the plane. */
        this._lastWorldPoint = null;

        /**
         * Whether any node in this tree has ever asked to follow a world point.
         *
         * Latched rather than counted. A count would have to be kept correct across every
         * add, remove, reparent and property write, and getting that wrong shows up as a
         * marker that silently stops moving; a latch can only cost a walk that finds nothing.
         */
        this._hasWorldFollowers = false;

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

    /**
     * A point far enough outside any canvas that every rectangle test rejects it.
     *
     * Finite on purpose. An infinity or a NaN would propagate through the slider arithmetic
     * in UiInput instead of simply missing, and "missing" is exactly what a pointer ray that
     * does not meet a canvas's plane should mean.
     */
    static NOWHERE = Object.freeze({ x: -1000000, y: -1000000 });

    /** Every live canvas, in creation order. */
    static get all() { return all; }

    /** Drops every canvas. A test that leaks one poisons the next. */
    static clearAll() { all.length = 0; }

    /** Removes this canvas from the paint list. */
    /**
     * Deregisters the canvas, so the host stops laying it out and painting it.
     *
     * Named for the Component hook the scene calls, which is what the C# side
     * overrides too; `destroy()` stays as the spelling direct owners already use.
     */
    onDestroy() {
        const i = all.indexOf(this);
        if (i >= 0) all.splice(i, 1);
    }

    destroy() { this.onDestroy(); }

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
        // A world canvas is not fitted to anything: it is a fixed sheet standing in the scene,
        // so its size is what it was authored at and its transform is the identity. That one
        // branch is why every other thing in the UI -- layout, painting, clipping, focus,
        // navigation, hit testing -- keeps working in world space without being told about it.
        if (this.space === UiSpace.World) {
            this.scale = { x: 1, y: 1 };
            this.canvasOffset = { x: 0, y: 0 };
            this.canvasSize = {
                x: Math.max(1, this.referenceResolution.x),
                y: Math.max(1, this.referenceResolution.y),
            };
            return;
        }

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

    /**
     * Device pixels to canvas units. Every hit test starts here.
     *
     * The single seam between the two spaces, which is why world canvases cost the input
     * router nothing: it still asks one question and still gets canvas units back.
     */
    screenToCanvas(point) {
        if (this.space === UiSpace.World) return this._worldScreenToCanvas(point);

        return {
            x: (point.x - this.canvasOffset.x) / this.scale.x,
            y: (point.y - this.canvasOffset.y) / this.scale.y,
        };
    }

    _worldScreenToCanvas(point) {
        const basis = this.worldBasis();
        const camera = Camera3D.main;

        if (basis && camera) {
            const ray = camera.screenToWorldRay(point, this._viewport.x, this._viewport.y);
            const hit = rayToCanvas(basis, ray.origin, ray.direction, this.canvasSize);
            if (hit) {
                this._lastWorldPoint = hit;
                return hit;
            }
        }

        // A drag holds its last good point rather than reporting a miss. Swinging the camera
        // until the ray leaves the plane would otherwise snap the slider you are dragging to
        // its minimum, which is a worse answer than "wherever you last had it".
        if (this._input?.isDragging && this._lastWorldPoint) return this._lastWorldPoint;

        return { ...UiCanvas.NOWHERE };
    }

    // -------------------------------------------------------------------------
    // World space
    // -------------------------------------------------------------------------

    /** The actor's 3D transform when it has one; a script's canvas has no actor at all. */
    get transform3D() { return this.actor?.getComponent(Transform3D) ?? null; }

    /** The centre of a world canvas: its actor's position plus the offset. */
    get worldAnchor() {
        const t = this.transform3D;
        const origin = t ? t.position : this.worldPosition;
        return Vector3.add(Vector3.from(origin), Vector3.from(this.worldOffset));
    }

    /** The canvas's extent in world units: its size in canvas units, scaled down. */
    get worldSize() {
        const ppu = Math.max(0.0001, this.pixelsPerUnit);
        return { x: this.canvasSize.x / ppu, y: this.canvasSize.y / ppu };
    }

    /** The plane this canvas occupies, or null when it is not in the world or has no camera. */
    worldBasis() {
        if (this.space !== UiSpace.World) return null;

        const camera = Camera3D.main;
        if (!camera) return null;

        const t = this.transform3D;

        return buildBasis(
            this.worldAnchor,
            this.facing,
            t ? t.rotation : { x: 0, y: 0, z: 0, w: 1 },
            camera.getViewMatrix(),
            camera.getTransform3D().position,
            this.worldSize);
    }

    /** Whether a world canvas is close enough to the camera to be worth drawing. */
    get isWithinDrawDistance() {
        if (this.maxDrawDistance <= 0) return true;

        const camera = Camera3D.main;
        if (!camera) return true;

        const away = Vector3.subtract(camera.getTransform3D().position, this.worldAnchor);
        return away.lengthSquared <= this.maxDrawDistance * this.maxDrawDistance;
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
        this._resolveWorldFollowers();
        if (!this.root.measureDirty && !this.root.arrangeDirty) return;

        const area = this.safeRect;
        measureLayout(this.root, { x: area.width, y: area.height });
        arrange(this.root, area, { x: 0, y: 0, width: this.canvasSize.x, height: this.canvasSize.y });
    }

    /** Tells this canvas one of its nodes wants to follow a world point. */
    noteWorldFollower() { this._hasWorldFollowers = true; }

    /**
     * Moves every following node to wherever its world anchor is on screen this frame.
     *
     * Runs before the dirty check rather than after it, because a node that follows a moving
     * actor is dirty by definition and would otherwise be laid out once and left. A canvas
     * with no followers pays one boolean.
     *
     * Nodes behind the camera are hidden rather than placed: the perspective divide flips a
     * point behind the viewer to the opposite side of the screen, which is why worldToScreen
     * returns null there instead of a coordinate.
     */
    _resolveWorldFollowers() {
        if (!this._hasWorldFollowers) return;

        const camera = Camera3D.main;
        const eye = camera ? camera.getTransform3D().position : null;

        for (const node of this.root.descendants()) {
            if (!node.worldFollow) continue;

            node.positioning = PositionMode.Absolute;

            if (!camera) {
                node.visible = false;
                continue;
            }

            if (node.worldFollowDistance > 0) {
                const away = Vector3.subtract(Vector3.from(eye), Vector3.from(node.worldAnchor));
                if (away.lengthSquared > node.worldFollowDistance * node.worldFollowDistance) {
                    node.visible = false;
                    continue;
                }
            }

            const screen = camera.worldToScreen(node.worldAnchor, this._viewport.x, this._viewport.y);
            if (!screen) {
                node.visible = false;
                continue;
            }

            node.visible = true;
            node.offset = this.screenToCanvas(screen);
        }
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

    /**
     * Loads the `.ui` document named by `document`, if there is one.
     *
     * Asynchronous, and the one real asymmetry with the C# side, which reads the file inside
     * a synchronous Awake. A browser cannot; so the canvas paints empty for the frame or two
     * the fetch takes and then fills in. Before this existed the browser never loaded the
     * property at all -- it round-tripped through a `.scene` file and did nothing -- so a UI
     * saved in the editor appeared natively and nowhere else.
     */
    awake() {
        super.awake?.();
        if (!this.document) return;

        // The same route SpriteRenderer takes to its texture, rather than a second one.
        const assets = this.actor?.scene?.engine?.assets;
        if (!assets) return;

        assets.loadJson(this.document)
            .then((spec) => { if (spec) this.adopt(fromObject(spec)); })
            .catch((error) => {
                console.warn(`[UiCanvas] could not load document '${this.document}': ${error.message}`);
            });
    }

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

        for (const key of WRITABLE_KEYS) {
            if (ROOT_OWNED_KEYS.has(canonical(key))) continue;
            uiApply(this.root, key, uiRead(document, key));
        }

        for (const child of [...document.children]) this.root.add(child);
        this.invalidateLayout();
    }
}

registerComponent(UiCanvas, {
    category: 'UI',
    fullName: 'SexyBiscuit.Engine.UI.UiCanvas',
    summary: 'A screen-space surface holding a tree of UI nodes.',
});
