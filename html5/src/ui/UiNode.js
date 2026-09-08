// -----------------------------------------------------------------------------
// UiNode — one node in a UI tree: plain data with a resolved rectangle.
//
// The mirror of SexyBiscuit.Engine/UI/UiNode.cs, property for property. One class
// with a `kind`, not a class per widget: a hierarchy of twelve types would mean
// twelve schema rows, twelve inspector drawers and twelve mirrors, all maintained
// by hand, and one class means one of each.
//
// Every property that can change a layout marks the tree dirty on write. That is
// the whole reason `rect` is a cached field read in O(1) rather than a walk up the
// parent chain on every access, which is what the widget tree this replaces did.
// -----------------------------------------------------------------------------

import {
    UiKind, SizeMode, LayoutMode, AlignMode, PositionMode, ScrollMode, UiAnchor,
    Focusability, anchorFraction,
} from './UiEnums.js';

export class UiNode {
    constructor(properties = null) {
        // --- identity ---
        this._name = '';
        this._kind = UiKind.Panel;
        this._visible = true;
        this._interactive = true;
        this._order = 0;
        this._style = '';

        // --- box model ---
        this._width = 0;
        this._height = 0;
        this._widthMode = SizeMode.Auto;
        this._heightMode = SizeMode.Auto;
        this._minWidth = 0;
        this._minHeight = 0;
        this._maxWidth = 0;          // <= 0 means unbounded
        this._maxHeight = 0;
        this._grow = 0;
        this._shrink = 1;
        this._padding = { x: 0, y: 0, z: 0, w: 0 };   // left, top, right, bottom
        this._margin = { x: 0, y: 0, z: 0, w: 0 };

        // --- container ---
        this._layout = LayoutMode.None;
        this._gap = { x: 0, y: 0 };
        this._wrap = false;
        this._mainAlign = AlignMode.Start;
        this._crossAlign = AlignMode.Start;
        this._columns = 2;
        this._cellSize = { x: 0, y: 0 };

        // --- placement ---
        this._positioning = PositionMode.Layout;
        this._anchor = UiAnchor.TopLeft;
        this._anchorMin = { x: 0, y: 0 };
        this._anchorMax = { x: 0, y: 0 };
        this._pivot = { x: 0, y: 0 };
        this._offset = { x: 0, y: 0 };
        this._offsetMax = { x: 0, y: 0 };

        // --- text ---
        this._text = '';
        this._textScale = 1;
        this._textAlign = AlignMode.Start;
        this._verticalAlign = AlignMode.Center;
        this._wrapText = false;
        this._lineSpacing = 0;

        // --- paint ---
        this._background = null;     // null draws nothing, which is not the same as black
        this._tint = '#ffffff';
        this._opacity = 1;
        this._borderColour = null;
        this._borderWidth = 0;
        this._texturePath = '';
        this._sourceRect = { x: 0, y: 0, z: 0, w: 0 };
        this._ninePatch = { x: 0, y: 0, z: 0, w: 0 };

        // --- clipping and scrolling ---
        this._clip = false;
        this._scroll = ScrollMode.None;
        this._scrollOffset = { x: 0, y: 0 };
        this._ignoreSafeArea = false;

        // --- focus and navigation ---
        this.focusable = Focusability.Auto;
        this._modal = false;
        // Overrides name a node rather than referencing one, so they survive
        // serialisation and can be written in a .ui file.
        this.navUp = '';
        this.navDown = '';
        this.navLeft = '';
        this.navRight = '';
        this.autoFocus = false;

        // --- payload ---
        this._value = 1;
        this._minValue = 0;
        this._maxValue = 1;
        this._step = 0;
        this._checked = false;
        this._selectedIndex = 0;
        this.options = [];

        // --- resolved by the layout pass, never serialised ---
        this.rect = { x: 0, y: 0, width: 0, height: 0 };
        this.contentRect = { x: 0, y: 0, width: 0, height: 0 };
        this.clipRect = { x: 0, y: 0, width: 0, height: 0 };
        this.desiredSize = { x: 0, y: 0 };
        this.contentSize = { x: 0, y: 0 };

        this.parent = null;
        this.canvas = null;
        this.children = [];

        this.measureDirty = true;
        this.arrangeDirty = true;

        // --- transient interaction state, written by the input router ---
        this.hovered = false;
        this.pressed = false;
        this.clicked = false;
        this.focused = false;

        // Whether a dropdown is showing its list. Transient rather than a real
        // property because an open list is a thing the player is doing, not a
        // thing the document says.
        this.expanded = false;

        if (properties) Object.assign(this, properties);
    }

    // -------------------------------------------------------------------------
    // Identity
    // -------------------------------------------------------------------------

    get name() { return this._name; }
    set name(v) { this._name = String(v ?? ''); }

    get kind() { return this._kind; }
    set kind(v) { this._setMeasure('_kind', v); }

    get visible() { return this._visible; }
    set visible(v) { this._setMeasure('_visible', Boolean(v)); }

    get interactive() { return this._interactive; }
    set interactive(v) { this._interactive = Boolean(v); }

    get order() { return this._order; }
    set order(v) { this._setArrange('_order', Number(v) || 0); }

    get style() { return this._style; }
    set style(v) { this._style = String(v ?? ''); }

    // -------------------------------------------------------------------------
    // Box model
    // -------------------------------------------------------------------------

    get width() { return this._width; }
    set width(v) { this._setMeasure('_width', Number(v) || 0); }

    get height() { return this._height; }
    set height(v) { this._setMeasure('_height', Number(v) || 0); }

    get widthMode() { return this._widthMode; }
    set widthMode(v) { this._setMeasure('_widthMode', v); }

    get heightMode() { return this._heightMode; }
    set heightMode(v) { this._setMeasure('_heightMode', v); }

    get minWidth() { return this._minWidth; }
    set minWidth(v) { this._setMeasure('_minWidth', Number(v) || 0); }

    get minHeight() { return this._minHeight; }
    set minHeight(v) { this._setMeasure('_minHeight', Number(v) || 0); }

    get maxWidth() { return this._maxWidth; }
    set maxWidth(v) { this._setMeasure('_maxWidth', Number(v) || 0); }

    get maxHeight() { return this._maxHeight; }
    set maxHeight(v) { this._setMeasure('_maxHeight', Number(v) || 0); }

    get grow() { return this._grow; }
    set grow(v) { this._setMeasure('_grow', Number(v) || 0); }

    get shrink() { return this._shrink; }
    set shrink(v) { this._setMeasure('_shrink', Number(v) || 0); }

    get padding() { return this._padding; }
    set padding(v) { this._setMeasureVec('_padding', v, 4); }

    get margin() { return this._margin; }
    set margin(v) { this._setMeasureVec('_margin', v, 4); }

    // -------------------------------------------------------------------------
    // Container
    // -------------------------------------------------------------------------

    get layout() { return this._layout; }
    set layout(v) { this._setMeasure('_layout', v); }

    get gap() { return this._gap; }
    set gap(v) { this._setMeasureVec('_gap', v, 2); }

    get wrap() { return this._wrap; }
    set wrap(v) { this._setMeasure('_wrap', Boolean(v)); }

    get mainAlign() { return this._mainAlign; }
    set mainAlign(v) { this._setArrange('_mainAlign', v); }

    get crossAlign() { return this._crossAlign; }
    set crossAlign(v) { this._setArrange('_crossAlign', v); }

    get columns() { return this._columns; }
    set columns(v) { this._setMeasure('_columns', Math.trunc(Number(v) || 0)); }

    get cellSize() { return this._cellSize; }
    set cellSize(v) { this._setMeasureVec('_cellSize', v, 2); }

    // -------------------------------------------------------------------------
    // Placement
    // -------------------------------------------------------------------------

    get positioning() { return this._positioning; }
    set positioning(v) { this._setMeasure('_positioning', v); }

    get anchor() { return this._anchor; }
    set anchor(v) {
        if (this._anchor === v) return;
        this._anchor = v;
        if (v !== UiAnchor.Custom) {
            const f = anchorFraction(v);
            this._anchorMin = { ...f };
            this._anchorMax = { ...f };
            this._pivot = { ...f };
        }
        this.invalidateArrange();
    }

    get anchorMin() { return this._anchorMin; }
    set anchorMin(v) { this._setCustomAnchor('_anchorMin', v); }

    get anchorMax() { return this._anchorMax; }
    set anchorMax(v) { this._setCustomAnchor('_anchorMax', v); }

    get pivot() { return this._pivot; }
    set pivot(v) { this._setCustomAnchor('_pivot', v); }

    get offset() { return this._offset; }
    set offset(v) { this._setArrangeVec('_offset', v, 2); }

    get offsetMax() { return this._offsetMax; }
    set offsetMax(v) { this._setArrangeVec('_offsetMax', v, 2); }

    // -------------------------------------------------------------------------
    // Text
    // -------------------------------------------------------------------------

    get text() { return this._text; }
    set text(v) { this._setMeasure('_text', String(v ?? '')); }

    get textScale() { return this._textScale; }
    set textScale(v) { this._setMeasure('_textScale', Number(v) || 0); }

    get textAlign() { return this._textAlign; }
    set textAlign(v) { this._setArrange('_textAlign', v); }

    get verticalAlign() { return this._verticalAlign; }
    set verticalAlign(v) { this._setArrange('_verticalAlign', v); }

    get wrapText() { return this._wrapText; }
    set wrapText(v) { this._setMeasure('_wrapText', Boolean(v)); }

    get lineSpacing() { return this._lineSpacing; }
    set lineSpacing(v) { this._setMeasure('_lineSpacing', Number(v) || 0); }

    // -------------------------------------------------------------------------
    // Paint
    // -------------------------------------------------------------------------

    get background() { return this._background; }
    set background(v) { this._background = v ?? null; }

    get tint() { return this._tint; }
    set tint(v) { this._tint = v ?? '#ffffff'; }

    get opacity() { return this._opacity; }
    set opacity(v) { this._opacity = Number(v); }

    get borderColour() { return this._borderColour; }
    set borderColour(v) { this._borderColour = v ?? null; }

    get borderWidth() { return this._borderWidth; }
    set borderWidth(v) { this._borderWidth = Number(v) || 0; }

    get texturePath() { return this._texturePath; }
    set texturePath(v) { this._setMeasure('_texturePath', String(v ?? '')); }

    get sourceRect() { return this._sourceRect; }
    set sourceRect(v) { this._sourceRect = toVec4(v); }

    get ninePatch() { return this._ninePatch; }
    set ninePatch(v) { this._ninePatch = toVec4(v); }

    // -------------------------------------------------------------------------
    // Clipping and scrolling
    // -------------------------------------------------------------------------

    /** Traps focus inside this subtree while it is visible. */
    get modal() { return this._modal; }
    set modal(v) { this._modal = Boolean(v); }

    get clip() { return this._clip; }
    set clip(v) { this._setArrange('_clip', Boolean(v)); }

    get scroll() { return this._scroll; }
    set scroll(v) { this._setMeasure('_scroll', v); }

    get scrollOffset() { return this._scrollOffset; }
    set scrollOffset(v) { this._setArrangeVec('_scrollOffset', v, 2); }

    get ignoreSafeArea() { return this._ignoreSafeArea; }
    set ignoreSafeArea(v) { this._setMeasure('_ignoreSafeArea', Boolean(v)); }

    // -------------------------------------------------------------------------
    // Payload
    // -------------------------------------------------------------------------

    get value() { return this._value; }
    set value(v) { this._value = Number(v) || 0; }

    get minValue() { return this._minValue; }
    set minValue(v) { this._minValue = Number(v) || 0; }

    get maxValue() { return this._maxValue; }
    set maxValue(v) { this._maxValue = Number(v) || 0; }

    get step() { return this._step; }
    set step(v) { this._step = Number(v) || 0; }

    get checked() { return this._checked; }
    set checked(v) { this._checked = Boolean(v); }

    get selectedIndex() { return this._selectedIndex; }
    set selectedIndex(v) { this._selectedIndex = Math.trunc(Number(v) || 0); }

    // -------------------------------------------------------------------------
    // Tree
    // -------------------------------------------------------------------------

    /** Appends a child, detaching it from any previous parent first. */
    add(child) {
        if (!child) throw new Error('UiNode.add: nothing to add.');
        if (child === this) throw new Error('UiNode.add: a node cannot be its own child.');

        if (child.parent) child.parent.remove(child);
        child.parent = this;
        child._setCanvasRecursive(this.canvas);
        this.children.push(child);
        this.invalidateMeasure();
        return child;
    }

    remove(child) {
        const at = this.children.indexOf(child);
        if (at < 0) return false;

        this.children.splice(at, 1);
        child.parent = null;
        child._setCanvasRecursive(null);
        this.invalidateMeasure();
        return true;
    }

    /** Detaches this node from its parent. Safe to call when it has none. */
    detach() { if (this.parent) this.parent.remove(this); }

    /** First descendant with this name, depth-first, or null. */
    find(name) {
        if (!name) return null;
        if (this._name === name) return this;

        for (const child of this.children) {
            const hit = child.find(name);
            if (hit) return hit;
        }
        return null;
    }

    /** Every node beneath this one, parents before children. */
    *descendants() {
        for (const child of this.children) {
            yield child;
            yield* child.descendants();
        }
    }

    _setCanvasRecursive(canvas) {
        this.canvas = canvas;
        for (const child of this.children) child._setCanvasRecursive(canvas);
    }

    // -------------------------------------------------------------------------
    // Invalidation
    // -------------------------------------------------------------------------

    /**
     * Marks this node and every ancestor as needing measuring again. A size change
     * travels up, because a parent sized to its content is now the wrong size too.
     */
    invalidateMeasure() {
        for (let n = this; n; n = n.parent) {
            if (n.measureDirty && n.arrangeDirty) break;
            n.measureDirty = true;
            n.arrangeDirty = true;
        }
    }

    /** Marks the node as needing re-placing, without re-measuring it. */
    invalidateArrange() {
        for (let n = this; n; n = n.parent) {
            if (n.arrangeDirty) break;
            n.arrangeDirty = true;
        }
    }

    clearDirty() {
        this.measureDirty = false;
        this.arrangeDirty = false;
    }

    toString() { return this._name ? `${this._kind}[${this._name}]` : String(this._kind); }

    // -------------------------------------------------------------------------
    // Setter plumbing
    // -------------------------------------------------------------------------

    _setMeasure(field, value) {
        if (this[field] === value) return;
        this[field] = value;
        this.invalidateMeasure();
    }

    _setArrange(field, value) {
        if (this[field] === value) return;
        this[field] = value;
        this.invalidateArrange();
    }

    _setMeasureVec(field, value, size) {
        const v = size === 4 ? toVec4(value) : toVec2(value);
        if (sameVec(this[field], v)) return;
        this[field] = v;
        this.invalidateMeasure();
    }

    _setArrangeVec(field, value, size) {
        const v = size === 4 ? toVec4(value) : toVec2(value);
        if (sameVec(this[field], v)) return;
        this[field] = v;
        this.invalidateArrange();
    }

    /**
     * Writing any of the three anchor vectors by hand means the author wants the custom
     * form; silently keeping a named preset would make the write a no-op the next time
     * the preset was applied.
     */
    _setCustomAnchor(field, value) {
        const v = toVec2(value);
        if (sameVec(this[field], v)) return;
        this[field] = v;
        this._anchor = UiAnchor.Custom;
        this.invalidateArrange();
    }
}

// -----------------------------------------------------------------------------
// Vector shapes
// -----------------------------------------------------------------------------

export function toVec2(v) {
    if (Array.isArray(v)) return { x: Number(v[0]) || 0, y: Number(v[1]) || 0 };
    if (typeof v === 'number') return { x: v, y: v };
    return { x: Number(v?.x) || 0, y: Number(v?.y) || 0 };
}

export function toVec4(v) {
    if (typeof v === 'number') return { x: v, y: v, z: v, w: v };
    if (Array.isArray(v)) {
        if (v.length === 1) return { x: +v[0], y: +v[0], z: +v[0], w: +v[0] };
        if (v.length === 2) return { x: +v[0], y: +v[1], z: +v[0], w: +v[1] };
        return { x: Number(v[0]) || 0, y: Number(v[1]) || 0, z: Number(v[2]) || 0, w: Number(v[3]) || 0 };
    }
    return { x: Number(v?.x) || 0, y: Number(v?.y) || 0, z: Number(v?.z) || 0, w: Number(v?.w) || 0 };
}

function sameVec(a, b) {
    return a.x === b.x && a.y === b.y && (a.z === undefined || (a.z === b.z && a.w === b.w));
}
