// -----------------------------------------------------------------------------
// UiLayout — measure bottom-up for desired sizes, arrange top-down to hand out the
// space there actually is. The mirror of SexyBiscuit.Engine/UI/Layout/UiLayout.cs.
//
// An unbounded axis is Infinity, never zero. Using zero to mean "as much as you
// like" is the classic way two independent layout engines drift apart while both
// look right in isolation, so the sentinel is spelled the same word on both sides
// and is a case in layout-cases.json.
//
// Sizes here are border boxes: padding is inside a node's own size, margin is
// outside it and is added by whichever parent is arranging the node. That is what
// lets a parent sum its children's desired sizes without knowing their padding.
// -----------------------------------------------------------------------------

import { UiKind, SizeMode, LayoutMode, AlignMode, PositionMode, ScrollMode } from './UiEnums.js';
import { measureText } from './UiTextMeasure.js';

const EPSILON = 0.0001;

// =============================================================================
// Measure
// =============================================================================

/**
 * Computes and stores a node's desired border-box size, given the space its parent
 * can offer. Recurses into children.
 */
export function measure(node, available) {
    if (!node.visible) {
        node.desiredSize = { x: 0, y: 0 };
        return node.desiredSize;
    }

    const padX = node.padding.x + node.padding.z;
    const padY = node.padding.y + node.padding.w;

    // An axis whose size is already decided constrains its content: a wrapping label
    // inside a 200px panel must wrap at 200, not at whatever the window happens to be.
    const knownW = knownSize(node.widthMode, node.width, available.x);
    const knownH = knownSize(node.heightMode, node.height, available.y);

    const inner = {
        x: shrinkBy(Number.isNaN(knownW) ? available.x : knownW, padX),
        y: shrinkBy(Number.isNaN(knownH) ? available.y : knownH, padY),
    };

    // A scroll axis measures its children unbounded; that overflow is the thing to scroll.
    const childSpace = {
        x: scrollsHorizontally(node.scroll) ? Infinity : inner.x,
        y: scrollsVertically(node.scroll) ? Infinity : inner.y,
    };

    const content = measureContent(node, childSpace);
    node.contentSize = content;

    const w = resolveAxis(node.widthMode, node.width, content.x + padX, available.x);
    const h = resolveAxis(node.heightMode, node.height, content.y + padY, available.y);

    node.desiredSize = {
        x: clampAxis(w, node.minWidth, node.maxWidth),
        y: clampAxis(h, node.minHeight, node.maxHeight),
    };
    return node.desiredSize;
}

function measureContent(node, inner) {
    let text = { x: 0, y: 0 };
    if (drawsText(node.kind) && node.text) {
        text = measureText(node.text, node.textScale, node.wrapText, inner.x, node.lineSpacing);
    }

    let children;
    switch (node.layout) {
        case LayoutMode.Row:
        case LayoutMode.Flow:   children = measureLinear(node, inner, true); break;
        case LayoutMode.Column: children = measureLinear(node, inner, false); break;
        case LayoutMode.Grid:   children = measureGrid(node, inner); break;
        default:                children = measureOverlay(node, inner); break;
    }

    return { x: Math.max(text.x, children.x), y: Math.max(text.y, children.y) };
}

/** Row and column, including the wrapped forms. */
function measureLinear(node, inner, horizontal) {
    const gapMain = horizontal ? node.gap.x : node.gap.y;
    const gapCross = horizontal ? node.gap.y : node.gap.x;
    const limit = horizontal ? inner.x : inner.y;
    const wraps = node.wrap || node.layout === LayoutMode.Flow;

    let lineMain = 0, lineCross = 0, totalMain = 0, totalCross = 0, onThisLine = 0;

    for (const child of node.children) {
        if (!child.visible || child.positioning === PositionMode.Absolute) continue;

        measure(child, inner);
        const main = mainOf(child, horizontal) + marginMain(child, horizontal);
        const cross = crossOf(child, horizontal) + marginCross(child, horizontal);
        const withChild = onThisLine === 0 ? main : lineMain + gapMain + main;

        if (wraps && onThisLine > 0 && Number.isFinite(limit) && withChild > limit + EPSILON) {
            totalMain = Math.max(totalMain, lineMain);
            totalCross += lineCross + gapCross;
            lineMain = main;
            lineCross = cross;
            onThisLine = 1;
            continue;
        }

        lineMain = withChild;
        lineCross = Math.max(lineCross, cross);
        onThisLine++;
    }

    totalMain = Math.max(totalMain, lineMain);
    totalCross += lineCross;

    return horizontal ? { x: totalMain, y: totalCross } : { x: totalCross, y: totalMain };
}

function measureGrid(node, inner) {
    const columns = Math.max(1, node.columns);
    const cellW = node.cellSize.x > 0
        ? node.cellSize.x
        : (!Number.isFinite(inner.x) ? 0 : Math.max(0, (inner.x - node.gap.x * (columns - 1)) / columns));

    const cellSpace = {
        x: cellW > 0 ? cellW : Infinity,
        y: node.cellSize.y > 0 ? node.cellSize.y : Infinity,
    };

    let widest = 0, totalHeight = 0, rowHeight = 0, index = 0;

    for (const child of node.children) {
        if (!child.visible || child.positioning === PositionMode.Absolute) continue;

        measure(child, cellSpace);
        widest = Math.max(widest, child.desiredSize.x);
        rowHeight = Math.max(rowHeight, child.desiredSize.y);

        if (++index % columns === 0) {
            totalHeight += rowHeight + node.gap.y;
            rowHeight = 0;
        }
    }

    if (rowHeight > 0) totalHeight += rowHeight + node.gap.y;
    if (totalHeight > 0) totalHeight -= node.gap.y;

    const rowW = node.cellSize.x > 0 ? node.cellSize.x : (cellW > 0 ? cellW : widest);
    return { x: rowW * columns + node.gap.x * (columns - 1), y: Math.max(0, totalHeight) };
}

/** No layout: children overlap, and the node is as big as the largest. */
function measureOverlay(node, inner) {
    let largest = { x: 0, y: 0 };

    for (const child of node.children) {
        if (!child.visible) continue;
        measure(child, inner);
        if (child.positioning === PositionMode.Absolute) continue;

        largest = {
            x: Math.max(largest.x, child.desiredSize.x + marginMain(child, true)),
            y: Math.max(largest.y, child.desiredSize.y + marginMain(child, false)),
        };
    }

    return largest;
}

// =============================================================================
// Arrange
// =============================================================================

/** Places a node in a final rectangle and distributes what is left to its children. */
export function arrange(node, final, parentClip) {
    node.rect = final;
    node.contentRect = deflate(final, node.padding);
    node.clipRect = clips(node) ? intersect(parentClip, final) : parentClip;

    if (!node.visible) {
        node.clearDirty();
        return;
    }

    let content = node.contentRect;

    // Scrolling shifts the children, and clamps so a short list cannot be scrolled away.
    if (node.scroll !== ScrollMode.None) {
        const maxX = Math.max(0, node.contentSize.x - content.width);
        const maxY = Math.max(0, node.contentSize.y - content.height);
        const clamped = {
            x: scrollsHorizontally(node.scroll) ? clamp(node.scrollOffset.x, 0, maxX) : 0,
            y: scrollsVertically(node.scroll) ? clamp(node.scrollOffset.y, 0, maxY) : 0,
        };

        node.scrollOffset = clamped;
        content = {
            x: content.x - clamped.x,
            y: content.y - clamped.y,
            width: scrollsHorizontally(node.scroll) ? Math.max(content.width, node.contentSize.x) : content.width,
            height: scrollsVertically(node.scroll) ? Math.max(content.height, node.contentSize.y) : content.height,
        };
    }

    switch (node.layout) {
        case LayoutMode.Row:
        case LayoutMode.Flow:   arrangeLinear(node, content, true); break;
        case LayoutMode.Column: arrangeLinear(node, content, false); break;
        case LayoutMode.Grid:   arrangeGrid(node, content); break;
        default:                arrangeOverlay(node, content); break;
    }

    // Absolute children never join the flex distribution; they hang off the content box.
    for (const child of node.children) {
        if (child.visible && child.positioning === PositionMode.Absolute) {
            arrange(child, anchorRect(child, node.contentRect), node.clipRect);
        }
    }

    node.clearDirty();
}

function arrangeLinear(node, content, horizontal) {
    const flow = flowChildren(node);
    if (flow.length === 0) return;

    const gapMain = horizontal ? node.gap.x : node.gap.y;
    const gapCross = horizontal ? node.gap.y : node.gap.x;
    const mainSize = horizontal ? content.width : content.height;
    const wraps = node.wrap || node.layout === LayoutMode.Flow;

    let crossCursor = horizontal ? content.y : content.x;

    for (const line of splitLines(flow, mainSize, gapMain, horizontal, wraps)) {
        let lineCross = 0;
        for (const c of line) lineCross = Math.max(lineCross, crossOf(c, horizontal) + marginCross(c, horizontal));

        // A single line that is allowed to stretch takes the whole cross extent, which is
        // what makes `crossAlign: stretch` on a column give every button the same width.
        if (!wraps) lineCross = horizontal ? content.height : content.width;

        placeLine(node, line, content, crossCursor, lineCross, gapMain, mainSize, horizontal);
        crossCursor += lineCross + gapCross;
    }
}

function placeLine(node, line, content, crossStart, lineCross, gapMain, mainSize, horizontal) {
    const count = line.length;
    const main = new Array(count);
    let used = gapMain * (count - 1);
    let totalGrow = 0, totalShrinkWeight = 0;

    for (let i = 0; i < count; i++) {
        main[i] = mainOf(line[i], horizontal) + marginMain(line[i], horizontal);
        used += main[i];
        totalGrow += Math.max(0, line[i].grow);
        totalShrinkWeight += Math.max(0, line[i].shrink) * main[i];
    }

    let free = mainSize - used;

    if (free > EPSILON && totalGrow > 0) {
        for (let i = 0; i < count; i++) main[i] += free * Math.max(0, line[i].grow) / totalGrow;
        free = 0;
    } else if (free < -EPSILON && totalShrinkWeight > 0) {
        const overflow = -free;
        for (let i = 0; i < count; i++) {
            const share = overflow * (Math.max(0, line[i].shrink) * main[i]) / totalShrinkWeight;
            const floor = minMainOf(line[i], horizontal) + marginMain(line[i], horizontal);
            main[i] = Math.max(floor, main[i] - share);
        }
        free = 0;
    }

    // Whatever is still spare is distributed by the main-axis alignment.
    let cursor = horizontal ? content.x : content.y;
    let between = gapMain;

    switch (node.mainAlign) {
        case AlignMode.Center: cursor += free * 0.5; break;
        case AlignMode.End:    cursor += free; break;
        case AlignMode.SpaceBetween:
            if (count > 1) between += free / (count - 1);
            break;
        case AlignMode.SpaceAround:
            if (count > 0) { between += free / count; cursor += free / count * 0.5; }
            break;
        case AlignMode.SpaceEvenly:
            if (count > 0) { between += free / (count + 1); cursor += free / (count + 1); }
            break;
        default: break;
    }

    for (let i = 0; i < count; i++) {
        const child = line[i];
        const m = child.margin;

        const mainStart = cursor + (horizontal ? m.x : m.y);
        const mainExtent = Math.max(0, main[i] - marginMain(child, horizontal));

        const available = Math.max(0, lineCross - marginCross(child, horizontal));
        const stretch = node.crossAlign === AlignMode.Stretch
            || (horizontal ? child.heightMode : child.widthMode) === SizeMode.Stretch;

        const crossExtent = stretch ? available : Math.min(crossOf(child, horizontal), available);
        let crossOffset = 0;
        if (node.crossAlign === AlignMode.Center) crossOffset = (available - crossExtent) * 0.5;
        else if (node.crossAlign === AlignMode.End) crossOffset = available - crossExtent;

        const crossPos = crossStart + (horizontal ? m.y : m.x) + crossOffset;

        const rect = horizontal
            ? { x: mainStart, y: crossPos, width: mainExtent, height: crossExtent }
            : { x: crossPos, y: mainStart, width: crossExtent, height: mainExtent };

        // Re-measure a child whose width was decided by the line, so its own wrapped text
        // and auto height are computed against the width it actually got.
        if (needsRemeasure(child, rect)) measure(child, { x: rect.width, y: rect.height });

        arrange(child, rect, node.clipRect);
        cursor += main[i] + between;
    }
}

function arrangeGrid(node, content) {
    const flow = flowChildren(node);
    if (flow.length === 0) return;

    const columns = Math.max(1, node.columns);
    const cellW = node.cellSize.x > 0
        ? node.cellSize.x
        : Math.max(0, (content.width - node.gap.x * (columns - 1)) / columns);

    let y = content.y;
    for (let start = 0; start < flow.length; start += columns) {
        const end = Math.min(start + columns, flow.length);

        let rowH = node.cellSize.y;
        if (rowH <= 0) for (let i = start; i < end; i++) rowH = Math.max(rowH, flow[i].desiredSize.y);

        for (let i = start; i < end; i++) {
            const child = flow[i];
            const x = content.x + (i - start) * (cellW + node.gap.x);
            const rect = {
                x: x + child.margin.x,
                y: y + child.margin.y,
                width: Math.max(0, cellW - child.margin.x - child.margin.z),
                height: Math.max(0, rowH - child.margin.y - child.margin.w),
            };

            if (needsRemeasure(child, rect)) measure(child, { x: rect.width, y: rect.height });
            arrange(child, rect, node.clipRect);
        }

        y += rowH + node.gap.y;
    }
}

function arrangeOverlay(node, content) {
    for (const child of flowChildren(node)) {
        const w = child.widthMode === SizeMode.Stretch ? content.width : child.desiredSize.x;
        const h = child.heightMode === SizeMode.Stretch ? content.height : child.desiredSize.y;

        const rect = {
            x: content.x + child.margin.x,
            y: content.y + child.margin.y,
            width: Math.max(0, w - child.margin.x - child.margin.z),
            height: Math.max(0, h - child.margin.y - child.margin.w),
        };

        if (needsRemeasure(child, rect)) measure(child, { x: rect.width, y: rect.height });
        arrange(child, rect, node.clipRect);
    }
}

// =============================================================================
// Anchoring
// =============================================================================

/**
 * An absolutely positioned node's rectangle within its parent's content box.
 *
 * With a named anchor, min, max and pivot are all the same fraction, and this reduces
 * exactly to what the flat script UI has always done: the anchor is both the point the
 * node hangs from and the corner of its own box that hangs there. When min and max
 * differ the node stretches between them instead, which is the behaviour the widget
 * tree this replaces declared and never implemented.
 */
export function anchorRect(child, parentContent) {
    const [x, w] = resolveAnchorAxis(
        child.anchorMin.x, child.anchorMax.x, child.pivot.x,
        child.offset.x, child.offsetMax.x,
        parentContent.x, parentContent.width, child.desiredSize.x);

    const [y, h] = resolveAnchorAxis(
        child.anchorMin.y, child.anchorMax.y, child.pivot.y,
        child.offset.y, child.offsetMax.y,
        parentContent.y, parentContent.height, child.desiredSize.y);

    return { x, y, width: w, height: h };
}

function resolveAnchorAxis(anchorMin, anchorMax, pivot, offset, offsetMax, parentPos, parentSize, desired) {
    const lead = parentPos + parentSize * anchorMin + offset;

    if (Math.abs(anchorMax - anchorMin) < EPSILON) return [lead - desired * pivot, desired];

    const trail = parentPos + parentSize * anchorMax + offsetMax;
    return [lead, Math.max(0, trail - lead)];
}

// =============================================================================
// Hit testing
// =============================================================================

/**
 * The top-most pickable node under a canvas-space point, or null.
 *
 * Children are searched last-painted first, so the node a player can see on top is the
 * node they hit. A node that is not interactive still blocks what is behind it -- a
 * disabled button is not a hole -- but a panel with nothing drawn in it does not,
 * because an invisible layout row that ate clicks would be impossible to debug.
 */
export function hitTest(node, point) {
    if (!node.visible || node.opacity <= 0.01) return null;
    if (clips(node) && !contains(node.clipRect, point)) return null;

    const painted = paintOrder(node);
    for (let i = painted.length - 1; i >= 0; i--) {
        const hit = hitTest(painted[i], point);
        if (hit) return hit;
    }

    return pickable(node) && contains(node.rect, point) ? node : null;
}

function pickable(node) {
    switch (node.kind) {
        case UiKind.Button:
        case UiKind.Slider:
        case UiKind.Toggle:
        case UiKind.TextField:
        case UiKind.Dropdown:
        case UiKind.TabStrip:
            return true;
        case UiKind.Spacer:
            return false;
        default:
            // Anything that paints something can be hit; a bare layout row cannot.
            return node.background !== null || Boolean(node.texturePath);
    }
}

// =============================================================================
// Helpers
// =============================================================================

/** Children in paint order: by order, then by the order they were added. */
export function paintOrder(node) {
    // A stable insertion sort, so equal order keeps tree order and paint order never flickers.
    const ordered = node.children.slice();
    for (let i = 1; i < ordered.length; i++) {
        const item = ordered[i];
        let j = i - 1;
        while (j >= 0 && ordered[j].order > item.order) { ordered[j + 1] = ordered[j]; j--; }
        ordered[j + 1] = item;
    }
    return ordered;
}

function flowChildren(node) {
    return paintOrder(node).filter((c) => c.visible && c.positioning !== PositionMode.Absolute);
}

function* splitLines(flow, mainSize, gap, horizontal, wraps) {
    if (!wraps || !Number.isFinite(mainSize)) {
        yield flow;
        return;
    }

    let line = [];
    let used = 0;

    for (const child of flow) {
        const main = mainOf(child, horizontal) + marginMain(child, horizontal);
        const next = line.length === 0 ? main : used + gap + main;

        if (line.length > 0 && next > mainSize + EPSILON) {
            yield line;
            line = [child];
            used = main;
            continue;
        }

        line.push(child);
        used = next;
    }

    if (line.length > 0) yield line;
}

/**
 * True when arrange gave a child a different width than measure assumed, so its wrapped
 * text and content-driven height have to be worked out again.
 */
function needsRemeasure(child, rect) {
    return child.children.length > 0
        || (child.wrapText && Math.abs(rect.width - child.desiredSize.x) > EPSILON);
}

function clips(node) { return node.clip || node.scroll !== ScrollMode.None; }

function drawsText(kind) {
    return kind === UiKind.Label || kind === UiKind.Button || kind === UiKind.Bar
        || kind === UiKind.TextField || kind === UiKind.Dropdown;
}

function scrollsHorizontally(m) { return m === ScrollMode.Horizontal || m === ScrollMode.Both; }
function scrollsVertically(m) { return m === ScrollMode.Vertical || m === ScrollMode.Both; }

function mainOf(n, horizontal) { return horizontal ? n.desiredSize.x : n.desiredSize.y; }
function crossOf(n, horizontal) { return horizontal ? n.desiredSize.y : n.desiredSize.x; }
function minMainOf(n, horizontal) { return horizontal ? n.minWidth : n.minHeight; }

function marginMain(n, horizontal) {
    return horizontal ? n.margin.x + n.margin.z : n.margin.y + n.margin.w;
}

function marginCross(n, horizontal) {
    return horizontal ? n.margin.y + n.margin.w : n.margin.x + n.margin.z;
}

/** The size an axis already knows, or NaN when it depends on the content. */
function knownSize(mode, value, available) {
    if (mode === SizeMode.Fixed) return value;
    if (mode === SizeMode.Percent && Number.isFinite(available)) return available * value;
    if (mode === SizeMode.Stretch && Number.isFinite(available)) return available;
    return NaN;
}

/**
 * A Percent or Stretch child of an Auto parent measures as Auto. Without that rule the
 * two would define each other and the pass would never terminate.
 */
function resolveAxis(mode, value, content, available) {
    if (mode === SizeMode.Fixed) return value;
    if (mode === SizeMode.Percent && Number.isFinite(available)) return available * value;
    return content;
}

function clampAxis(v, min, max) {
    if (min > 0 && v < min) v = min;
    if (max > 0 && v > max) v = max;
    return Math.max(0, v);
}

/** Subtracts an inset while leaving an unbounded axis unbounded. */
function shrinkBy(available, by) {
    return Number.isFinite(available) ? Math.max(0, available - by) : available;
}

function clamp(v, lo, hi) { return v < lo ? lo : v > hi ? hi : v; }

export function deflate(r, inset) {
    return {
        x: r.x + inset.x,
        y: r.y + inset.y,
        width: Math.max(0, r.width - inset.x - inset.z),
        height: Math.max(0, r.height - inset.y - inset.w),
    };
}

export function intersect(a, b) {
    const x0 = Math.max(a.x, b.x);
    const y0 = Math.max(a.y, b.y);
    const x1 = Math.min(a.x + a.width, b.x + b.width);
    const y1 = Math.min(a.y + a.height, b.y + b.height);
    return x1 <= x0 || y1 <= y0
        ? { x: 0, y: 0, width: 0, height: 0 }
        : { x: x0, y: y0, width: x1 - x0, height: y1 - y0 };
}

/**
 * Half-open containment: left and top edges are inside, right and bottom are not, so two
 * rectangles sharing an edge never both claim a point and a pointer on a seam cannot hit
 * two adjacent buttons at once.
 */
export function contains(r, p) {
    return p.x >= r.x && p.x < r.x + r.width && p.y >= r.y && p.y < r.y + r.height;
}
