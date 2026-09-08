// -----------------------------------------------------------------------------
// UiPainter — paints a UiCanvas onto a 2D context.
//
// The mirror of SexyBiscuit.Engine/UI/UiPainter.cs.
//
// Only five of the twelve UiKinds have a painter of their own, which is the bargain
// UiEnums describes: everything after Image is drawn as a composition of a box, a
// bar and a run of text. Twelve painters written twice is twenty-four; five written
// twice is ten, and the seven compositions are the same arithmetic on both engines.
// -----------------------------------------------------------------------------

import { UiKind, AlignMode, ScrollMode } from './UiEnums.js';
import { paintOrder } from './UiLayout.js';
import { width as textWidth, height as textHeight, wrap, lineHeightOf } from './UiTextMeasure.js';
import { drawText } from './BitmapFont.js';
import { UiCanvas } from './UiCanvas.js';

/** How much lighter a hovered button is. Matches the C# `Lighten`. */
const HOVER_GAIN = 1.25;

/** How much darker a pressed control is, so a press reads as a press. */
const PRESS_GAIN = 0.82;

/** Seconds for a full caret blink cycle. */
const CARET_PERIOD = 1.0;

/** The thickness of the ring drawn round the focused node under a gamepad. */
const FOCUS_RING_WIDTH = 2;

/** How wide a scroll thumb is, in canvas units. */
export const SCROLL_BAR_THICKNESS = 6;

// -----------------------------------------------------------------------------
// Entry points
// -----------------------------------------------------------------------------

/** Paints every live canvas in `order`, lowest first. */
export function paintAll(ctx, viewportWidth, viewportHeight, options = {}) {
    const canvases = [...UiCanvas.all].sort((a, b) => a.order - b.order);
    for (const canvas of canvases) {
        canvas.setViewport(viewportWidth, viewportHeight);
        canvas.layout();
        paint(ctx, canvas, options);
    }
}

/** Paints one canvas. The tree must already have been laid out. */
export function paint(ctx, canvas, { showFocusRing = false, texture = null, time = 0 } = {}) {
    if (canvas.root.children.length === 0 && !canvas.root.background) return;

    const state = { ctx, canvas, showFocusRing, texture, time };

    ctx.save();
    ctx.setTransform(canvas.scale.x, 0, 0, canvas.scale.y, canvas.canvasOffset.x, canvas.canvasOffset.y);
    paintNode(state, canvas.root, 1);
    ctx.restore();
}

// -----------------------------------------------------------------------------
// The walk
// -----------------------------------------------------------------------------

function paintNode(state, node, inheritedOpacity) {
    if (!node.visible) return;

    const opacity = inheritedOpacity * Math.min(1, Math.max(0, node.opacity));
    if (opacity <= 0.004) return;

    // An empty clip rectangle means every descendant is scrolled or masked out of
    // view. Returning is not just an optimisation: a zero-area clip would otherwise
    // be pushed and everything inside would paint over the whole canvas.
    const clip = node.clipRect;
    if (clip.width <= 0 || clip.height <= 0) return;

    const { ctx } = state;
    ctx.save();
    ctx.beginPath();
    ctx.rect(clip.x, clip.y, clip.width, clip.height);
    ctx.clip();

    paintSelf(state, node, opacity);
    for (const child of paintOrder(node)) paintNode(state, child, opacity);

    ctx.restore();
}

function paintSelf(state, node, opacity) {
    switch (node.kind) {
        case UiKind.Panel:      paintPanel(state, node, opacity); break;
        case UiKind.Label:      paintLabel(state, node, opacity, null); break;
        case UiKind.Bar:        paintBar(state, node, opacity); break;
        case UiKind.Button:     paintButton(state, node, opacity); break;
        case UiKind.Image:      paintImage(state, node, opacity); break;

        case UiKind.Slider:     paintSlider(state, node, opacity); break;
        case UiKind.Toggle:     paintToggle(state, node, opacity); break;
        case UiKind.TextField:  paintTextField(state, node, opacity); break;
        case UiKind.Dropdown:   paintDropdown(state, node, opacity); break;
        case UiKind.ScrollView: paintScrollView(state, node, opacity); break;
        case UiKind.TabStrip:   paintTabStrip(state, node, opacity); break;
        case UiKind.Spacer:     break;
        default: break;
    }

    if (state.showFocusRing && node.focused) {
        drawRing(state, node.rect, node.tint, opacity, FOCUS_RING_WIDTH);
    }
}

// -----------------------------------------------------------------------------
// The five painters, and the seven compositions
// -----------------------------------------------------------------------------

function paintPanel(state, node, opacity) {
    fill(state, node.rect, node.background, opacity);
    drawBorder(state, node, opacity);
}

function paintLabel(state, node, opacity, forceAlign) {
    fill(state, node.rect, node.background, opacity);
    drawBorder(state, node, opacity);
    text(state, node, node.text, node.contentRect, forceAlign, opacity);
}

function paintBar(state, node, opacity) {
    fill(state, node.rect, node.background, opacity);

    const r = node.rect;
    const f = fraction(node);
    if (f > 0) fill(state, { x: r.x, y: r.y, width: r.width * f, height: r.height }, node.tint, opacity);

    drawBorder(state, node, opacity);
    if (node.text) text(state, node, node.text, node.rect, AlignMode.Center, opacity);
}

function paintButton(state, node, opacity) {
    fill(state, node.rect, shade(node.background, node), opacity);
    drawBorder(state, node, opacity);
    text(state, node, node.text, node.rect, AlignMode.Center, opacity);
}

function paintImage(state, node, opacity) {
    // A resolver rather than a map: the asset manager keys on a resolved path and
    // wraps the bitmap in a Texture2D, and the canvas can only draw the bitmap.
    const resolved = node.texturePath && state.texture ? state.texture(node.texturePath) : null;
    const texture = resolved?.source ?? resolved ?? null;
    if (!texture) {
        // No art: a tinted box, the same answer SpriteRenderer gives.
        fill(state, node.rect, node.background, opacity);
        drawBorder(state, node, opacity);
        return;
    }

    const r = node.rect;
    const s = node.sourceRect;
    const source = s.z > 0 && s.w > 0 ? s : null;

    state.ctx.save();
    state.ctx.globalAlpha *= opacity;

    if (node.ninePatch.x || node.ninePatch.y || node.ninePatch.z || node.ninePatch.w) {
        drawNinePatch(state, node, texture, source);
    } else if (source) {
        state.ctx.drawImage(texture, source.x, source.y, source.z, source.w, r.x, r.y, r.width, r.height);
    } else {
        state.ctx.drawImage(texture, r.x, r.y, r.width, r.height);
    }

    state.ctx.restore();
    drawBorder(state, node, opacity);
}

/** A track, the filled part of it, and a handle sitting at the value. */
function paintSlider(state, node, opacity) {
    const r = node.rect;
    const f = fraction(node);

    // The track is inset vertically so the handle is the tall thing, which is what
    // makes a slider read as a slider rather than as a progress bar.
    const trackHeight = Math.max(4, r.height * 0.3);
    const track = { x: r.x, y: r.y + (r.height - trackHeight) * 0.5, width: r.width, height: trackHeight };

    fill(state, track, node.background, opacity);
    if (f > 0) fill(state, { ...track, width: track.width * f }, node.tint, opacity);

    const handleWidth = Math.max(8, r.height * 0.5);
    const handle = { x: r.x + (r.width - handleWidth) * f, y: r.y, width: handleWidth, height: r.height };

    fill(state, handle, shade(node.tint, node), opacity);
    drawBorder(state, node, opacity);
}

/** A box, a tick when checked, and the label to the right of both. */
function paintToggle(state, node, opacity) {
    const r = node.rect;
    const side = Math.min(r.height, Math.max(12, r.height));
    const box = { x: r.x, y: r.y + (r.height - side) * 0.5, width: side, height: side };

    fill(state, box, shade(node.background, node), opacity);
    drawRing(state, box, node.tint, opacity * 0.7, 1);

    if (node.checked) {
        const inset = Math.max(2, side * 0.25);
        fill(state, {
            x: box.x + inset, y: box.y + inset,
            width: box.width - inset * 2, height: box.height - inset * 2,
        }, node.tint, opacity);
    }

    if (!node.text) return;

    const gap = Math.max(4, side * 0.4);
    const area = {
        x: box.x + box.width + gap, y: r.y,
        width: Math.max(0, r.x + r.width - box.x - box.width - gap), height: r.height,
    };
    text(state, node, node.text, area, AlignMode.Start, opacity);
}

/** A box, the text, and a caret that blinks only while the field has focus. */
function paintTextField(state, node, opacity) {
    fill(state, node.rect, node.background, opacity);
    drawBorder(state, node, opacity);
    text(state, node, node.text, node.contentRect, AlignMode.Start, opacity);

    if (!node.focused) return;
    if (state.time % CARET_PERIOD >= CARET_PERIOD * 0.5) return;

    const scale = Math.max(1, Math.round(node.textScale));
    const caretX = node.contentRect.x + textWidth(node.text, scale);
    const caretHeight = lineHeightOf(scale);
    const caretY = node.contentRect.y + (node.contentRect.height - caretHeight) * 0.5;

    fill(state, { x: caretX + 1, y: caretY, width: Math.max(1, scale), height: caretHeight }, node.tint, opacity);
}

/** The closed control, plus the list when the router has opened it. */
function paintDropdown(state, node, opacity) {
    fill(state, node.rect, shade(node.background, node), opacity);
    drawBorder(state, node, opacity);
    text(state, node, selectedLabel(node), node.contentRect, AlignMode.Start, opacity);

    // A chevron, drawn as a stack of narrowing rows: three fills instead of a glyph
    // the shared 5x7 table does not have.
    const r = node.rect;
    const size = Math.max(3, Math.min(8, r.height * 0.22));
    const cx = r.x + r.width - size * 2;
    const cy = r.y + (r.height - size) * 0.5;
    for (let i = 0; i < Math.floor(size); i++) {
        fill(state, { x: cx + i, y: cy + i, width: size * 2 - i * 2, height: 1 }, node.tint, opacity);
    }

    if (!node.expanded || node.options.length === 0) return;
    paintDropdownList(state, node, opacity);
}

function paintDropdownList(state, node, opacity) {
    const list = dropdownListRect(node);
    const { ctx } = state;

    // The list escapes the control's own clip: it is drawn over whatever is below
    // it, which is the whole point of a popup.
    ctx.save();
    ctx.beginPath();
    ctx.rect(0, 0, state.canvas.canvasSize.x, state.canvas.canvasSize.y);
    ctx.clip();

    fill(state, list, node.background ?? '#000000', opacity);
    drawRing(state, list, node.tint, opacity * 0.6, 1);

    const rowHeight = node.rect.height;
    for (let i = 0; i < node.options.length; i++) {
        const row = { x: list.x, y: list.y + i * rowHeight, width: list.width, height: rowHeight };
        if (i === node.selectedIndex) fill(state, row, node.tint, opacity * 0.25);

        text(state, node, node.options[i], {
            x: row.x + node.padding.x, y: row.y,
            width: Math.max(0, row.width - node.padding.x - node.padding.z), height: row.height,
        }, AlignMode.Start, opacity);
    }

    ctx.restore();
}

/** The viewport, plus a thumb on each axis that actually overflows. */
function paintScrollView(state, node, opacity) {
    fill(state, node.rect, node.background, opacity);
    drawBorder(state, node, opacity);

    const view = node.contentRect;
    const bar = SCROLL_BAR_THICKNESS;

    if ((node.scroll === ScrollMode.Vertical || node.scroll === ScrollMode.Both)
        && node.contentSize.y > view.height) {
        const travel = node.contentSize.y - view.height;
        const height = Math.max(bar * 2, view.height * (view.height / node.contentSize.y));
        const y = view.y + (view.height - height) * clamp01(node.scrollOffset.y / travel);
        fill(state, { x: node.rect.x + node.rect.width - bar, y, width: bar, height }, node.tint, opacity * 0.5);
    }

    if ((node.scroll === ScrollMode.Horizontal || node.scroll === ScrollMode.Both)
        && node.contentSize.x > view.width) {
        const travel = node.contentSize.x - view.width;
        const width = Math.max(bar * 2, view.width * (view.width / node.contentSize.x));
        const x = view.x + (view.width - width) * clamp01(node.scrollOffset.x / travel);
        fill(state, { x, y: node.rect.y + node.rect.height - bar, width, height: bar }, node.tint, opacity * 0.5);
    }
}

/** Equal segments across the box, the selected one lit. */
function paintTabStrip(state, node, opacity) {
    fill(state, node.rect, node.background, opacity);
    drawBorder(state, node, opacity);

    const count = node.options.length;
    if (count === 0) return;

    for (let i = 0; i < count; i++) {
        const tab = tabRect(node, i);
        const selected = i === node.selectedIndex;

        if (selected) {
            fill(state, tab, node.tint, opacity * 0.22);
            // An underline rather than a filled tab: it survives any background.
            fill(state, { x: tab.x, y: tab.y + tab.height - 2, width: tab.width, height: 2 }, node.tint, opacity);
        }

        text(state, node, node.options[i], tab, AlignMode.Center, opacity * (selected ? 1 : 0.65));
    }
}

// -----------------------------------------------------------------------------
// Geometry the router needs too
// -----------------------------------------------------------------------------

/** The rectangle of one tab, so painting and hit-testing agree. */
export function tabRect(node, index) {
    const count = Math.max(1, node.options.length);
    const width = node.rect.width / count;
    return { x: node.rect.x + width * index, y: node.rect.y, width, height: node.rect.height };
}

/** The open list's rectangle, which the router hit-tests against. */
export function dropdownListRect(node) {
    const rowHeight = node.rect.height;
    const height = rowHeight * node.options.length;
    let y = node.rect.y + node.rect.height;

    // Flip above the control when the list would run off the bottom of the canvas.
    if (node.canvas && y + height > node.canvas.canvasSize.y && node.rect.y - height >= 0) {
        y = node.rect.y - height;
    }

    return { x: node.rect.x, y, width: node.rect.width, height };
}

/** Where a node's value sits between its bounds, as 0..1. */
export function fraction(node) {
    const span = node.maxValue - node.minValue;
    return span <= 0 ? 0 : clamp01((node.value - node.minValue) / span);
}

/** The label a dropdown shows when it is closed. */
export function selectedLabel(node) {
    return node.selectedIndex >= 0 && node.selectedIndex < node.options.length
        ? node.options[node.selectedIndex]
        : node.text;
}

// -----------------------------------------------------------------------------
// Primitives
// -----------------------------------------------------------------------------

function fill(state, r, colour, opacity) {
    if (!colour || r.width <= 0 || r.height <= 0) return;

    const { ctx } = state;
    const previous = ctx.globalAlpha;
    ctx.globalAlpha = previous * Math.min(1, Math.max(0, opacity));
    ctx.fillStyle = colour;
    ctx.fillRect(r.x, r.y, r.width, r.height);
    ctx.globalAlpha = previous;
}

function drawBorder(state, node, opacity) {
    if (!node.borderColour || node.borderWidth <= 0) return;
    drawRing(state, node.rect, node.borderColour, opacity, node.borderWidth);
}

/** Four fills, not a stroked rectangle: the corners must not double up. */
function drawRing(state, r, colour, opacity, width) {
    if (width <= 0 || r.width <= 0 || r.height <= 0) return;

    const w = Math.min(width, Math.min(r.width, r.height) * 0.5);
    fill(state, { x: r.x, y: r.y, width: r.width, height: w }, colour, opacity);
    fill(state, { x: r.x, y: r.y + r.height - w, width: r.width, height: w }, colour, opacity);
    fill(state, { x: r.x, y: r.y + w, width: w, height: r.height - w * 2 }, colour, opacity);
    fill(state, { x: r.x + r.width - w, y: r.y + w, width: w, height: r.height - w * 2 }, colour, opacity);
}

function text(state, node, body, area, forceAlign, opacity) {
    if (!body) return;

    const scale = Math.max(1, Math.round(node.textScale));
    const content = node.wrapText ? wrap(body, scale, area.width) : body;

    const w = textWidth(content, scale);
    const h = textHeight(content, scale, node.lineSpacing);

    const align = forceAlign ?? node.textAlign;
    let x = area.x;
    if (align === AlignMode.Center) x = area.x + (area.width - w) * 0.5;
    else if (align === AlignMode.End) x = area.x + area.width - w;

    let y = area.y + (area.height - h) * 0.5;
    if (node.verticalAlign === AlignMode.Start) y = area.y;
    else if (node.verticalAlign === AlignMode.End) y = area.y + area.height - h;

    const { ctx } = state;
    const previous = ctx.globalAlpha;
    ctx.globalAlpha = previous * Math.min(1, Math.max(0, opacity));
    drawText(ctx, content, Math.round(x), Math.round(y), { scale, colour: node.tint });
    ctx.globalAlpha = previous;
}

/** Nine stretched quads: corners fixed, edges stretched on one axis, centre on both. */
function drawNinePatch(state, node, texture, source) {
    const src = source ?? { x: 0, y: 0, z: texture.width, w: texture.height };
    const n = node.ninePatch;
    const r = node.rect;

    const sx = [src.x, src.x + n.x, src.x + src.z - n.z, src.x + src.z];
    const sy = [src.y, src.y + n.y, src.y + src.w - n.w, src.y + src.w];
    const dx = [r.x, r.x + n.x, r.x + r.width - n.z, r.x + r.width];
    const dy = [r.y, r.y + n.y, r.y + r.height - n.w, r.y + r.height];

    for (let row = 0; row < 3; row++) {
        for (let col = 0; col < 3; col++) {
            const sw = sx[col + 1] - sx[col];
            const sh = sy[row + 1] - sy[row];
            const dw = dx[col + 1] - dx[col];
            const dh = dy[row + 1] - dy[row];
            if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) continue;
            state.ctx.drawImage(texture, sx[col], sy[row], sw, sh, dx[col], dy[row], dw, dh);
        }
    }
}

/** Hover lightens, press darkens; a press wins because it is the later state. */
function shade(colour, node) {
    if (!colour) return null;
    if (!node.interactive) return colour;
    if (node.pressed) return scaleColour(colour, PRESS_GAIN);
    if (node.hovered) return scaleColour(colour, HOVER_GAIN, 12);
    return colour;
}

/**
 * Multiplies a hex colour's channels, matching the C# `Scale` and `Lighten`.
 * Anything that is not a plain #rgb or #rrggbb is handed back untouched.
 */
function scaleColour(colour, gain, lift = 0) {
    const hex = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(String(colour));
    if (!hex) return colour;

    let body = hex[1];
    if (body.length === 3) body = body.split('').map((c) => c + c).join('');

    const channels = [0, 2, 4].map((i) => {
        const value = parseInt(body.slice(i, i + 2), 16);
        return Math.min(255, Math.max(0, Math.round(value * gain) + lift));
    });

    return `#${channels.map((c) => c.toString(16).padStart(2, '0')).join('')}`;
}

function clamp01(v) { return Math.min(1, Math.max(0, v)); }
