// -----------------------------------------------------------------------------
// UiDocument — reads and writes a UI tree as JSON.
//
// One codec for three authors: the object literal a script passes to UI.build, a
// .ui file on disk, and the block a scene file stores inside a UiCanvas. The
// mirror of SexyBiscuit.Engine/UI/UiDocument.cs.
//
// Keys are matched case-insensitively, so a scene file's "Anchor" and a script's
// `anchor` land on the same property -- the same courtesy the scene serialiser
// already extends to component properties.
//
// An unrecognised key is an error, not a shrug. The flat UI this replaces ended
// its options switch with a silent default, so a typo did nothing and said
// nothing; in a tree a mistyped "childern" would drop everything below it.
// -----------------------------------------------------------------------------

import { UiNode, toVec2, toVec4 } from './UiNode.js';
import {
    UiKind, SizeMode, LayoutMode, AlignMode, PositionMode, ScrollMode, UiAnchor, Focusability,
    canonical, parseEnum, UiDocumentError,
} from './UiEnums.js';

/** Builds a tree from a JSON string or an already-parsed object. */
export function fromJson(source) {
    const spec = typeof source === 'string' ? JSON.parse(source) : source;
    return fromObject(spec);
}

/** Builds a tree from a plain object. */
export function fromObject(spec) {
    if (spec === null || typeof spec !== 'object' || Array.isArray(spec)) {
        throw new UiDocumentError(`A UI node must be an object, not ${Array.isArray(spec) ? 'an array' : typeof spec}.`);
    }

    const node = new UiNode();

    for (const [key, value] of Object.entries(spec)) {
        if (canonical(key) === 'children') {
            if (!Array.isArray(value)) throw new UiDocumentError('"children" must be an array.');
            for (const child of value) node.add(fromObject(child));
            continue;
        }
        apply(node, key, value);
    }

    return node;
}

/**
 * Writes one property onto a node, expanding the shorthand forms.
 * Throws for a key a node does not have.
 */
export function apply(node, key, value) {
    switch (canonical(key)) {
        // --- identity ---
        case 'name':        node.name = String(value ?? ''); break;
        case 'kind':
        case 'type':        node.kind = parseEnum(UiKind, value, key); break;
        case 'visible':     node.visible = bool(value); break;
        case 'interactive': node.interactive = bool(value); break;
        case 'order':       node.order = num(value); break;
        case 'style':       node.style = String(value ?? ''); break;

        // --- size, with the "auto" / "50%" / "*" shorthand ---
        case 'width':  { const [m, v] = size(value); node.widthMode = m; node.width = v; break; }
        case 'height': { const [m, v] = size(value); node.heightMode = m; node.height = v; break; }
        case 'size': {
            const s = toVec2(value);
            node.widthMode = SizeMode.Fixed; node.width = s.x;
            node.heightMode = SizeMode.Fixed; node.height = s.y;
            break;
        }
        case 'widthmode':  node.widthMode = parseEnum(SizeMode, value, key); break;
        case 'heightmode': node.heightMode = parseEnum(SizeMode, value, key); break;

        case 'minwidth':  node.minWidth = num(value); break;
        case 'minheight': node.minHeight = num(value); break;
        case 'maxwidth':  node.maxWidth = num(value); break;
        case 'maxheight': node.maxHeight = num(value); break;
        case 'grow':      node.grow = num(value); break;
        case 'shrink':    node.shrink = num(value); break;

        case 'padding': node.padding = edges(value, key); break;
        case 'margin':  node.margin = edges(value, key); break;

        // --- container ---
        case 'layout':     node.layout = parseEnum(LayoutMode, value, key); break;
        case 'gap':        node.gap = pair(value); break;
        case 'wrap':       node.wrap = bool(value); break;
        case 'mainalign':  node.mainAlign = parseEnum(AlignMode, value, key); break;
        case 'crossalign': node.crossAlign = parseEnum(AlignMode, value, key); break;
        case 'columns':    node.columns = num(value); break;
        case 'cellsize':   node.cellSize = toVec2(value); break;

        // --- placement ---
        case 'positioning': node.positioning = parseEnum(PositionMode, value, key); break;
        case 'absolute':    node.positioning = bool(value) ? PositionMode.Absolute : PositionMode.Layout; break;
        case 'anchor':      node.anchor = parseEnum(UiAnchor, value, key); break;
        case 'anchormin':   node.anchorMin = toVec2(value); break;
        case 'anchormax':   node.anchorMax = toVec2(value); break;
        case 'pivot':       node.pivot = toVec2(value); break;
        case 'offset':      node.offset = toVec2(value); break;
        case 'offsetmax':   node.offsetMax = toVec2(value); break;

        // `x` and `y` are how every existing script positions an element.
        case 'x': node.offset = { x: num(value), y: node.offset.y }; node.positioning = PositionMode.Absolute; break;
        case 'y': node.offset = { x: node.offset.x, y: num(value) }; node.positioning = PositionMode.Absolute; break;

        // --- text ---
        case 'text':          node.text = String(value ?? ''); break;
        case 'scale':
        case 'textscale':     node.textScale = num(value); break;
        case 'align':
        case 'textalign':     node.textAlign = parseEnum(AlignMode, value, key); break;
        case 'verticalalign': node.verticalAlign = parseEnum(AlignMode, value, key); break;
        case 'wraptext':      node.wrapText = bool(value); break;
        case 'linespacing':   node.lineSpacing = num(value); break;

        // --- paint ---
        case 'background':  node.background = colour(value); break;
        case 'tint':        node.tint = colour(value) ?? '#ffffff'; break;
        case 'opacity':     node.opacity = num(value); break;
        case 'bordercolour':
        case 'bordercolor': node.borderColour = colour(value); break;
        case 'borderwidth': node.borderWidth = num(value); break;
        case 'texturepath': node.texturePath = String(value ?? ''); break;
        case 'sourcerect':  node.sourceRect = toVec4(value); break;
        case 'ninepatch':   node.ninePatch = toVec4(value); break;

        // --- clipping and scrolling ---
        case 'clip':           node.clip = bool(value); break;
        case 'scroll':         node.scroll = parseEnum(ScrollMode, value, key); break;
        case 'scrolloffset':   node.scrollOffset = toVec2(value); break;
        case 'ignoresafearea': node.ignoreSafeArea = bool(value); break;

        // --- focus and navigation ---
        case 'focusable': node.focusable = parseEnum(Focusability, value, key); break;
        case 'modal':     node.modal = bool(value); break;
        case 'navup':     node.navUp = String(value ?? ''); break;
        case 'navdown':   node.navDown = String(value ?? ''); break;
        case 'navleft':   node.navLeft = String(value ?? ''); break;
        case 'navright':  node.navRight = String(value ?? ''); break;
        case 'autofocus': node.autoFocus = bool(value); break;

        // --- payload ---
        case 'value':         node.value = num(value); break;
        case 'minvalue':      node.minValue = num(value); break;
        case 'maxvalue':      node.maxValue = num(value); break;
        case 'step':          node.step = num(value); break;
        case 'checked':       node.checked = bool(value); break;
        case 'selectedindex': node.selectedIndex = num(value); break;
        case 'options':
            node.options = (value ?? []).map((o) => String(o));
            break;

        default:
            throw new UiDocumentError(
                `"${key}" is not a UI node property. A typo here would otherwise be silent.`);
    }
}

// -----------------------------------------------------------------------------
// Value shapes
// -----------------------------------------------------------------------------

/**
 * A size is a number of pixels, or one of the three words: "auto" sizes to content,
 * "*" fills what the parent hands out, and "50%" takes a share.
 */
function size(value) {
    if (typeof value === 'number') return [SizeMode.Fixed, value];

    const text = String(value ?? '').trim();
    if (/^auto$/i.test(text)) return [SizeMode.Auto, 0];
    if (text === '*' || /^(stretch|fill)$/i.test(text)) return [SizeMode.Stretch, 0];

    const percent = /^(-?[\d.]+)%$/.exec(text);
    if (percent) return [SizeMode.Percent, parseFloat(percent[1]) / 100];

    const pixels = Number(text);
    if (Number.isFinite(pixels)) return [SizeMode.Fixed, pixels];

    throw new UiDocumentError(`"${text}" is not a size. Use a number, "auto", "*" or a percentage.`);
}

/** One number for all four edges, two for the axes, or four as left, top, right, bottom. */
function edges(value, key) {
    if (typeof value === 'number') return { x: value, y: value, z: value, w: value };
    if (!Array.isArray(value)) throw new UiDocumentError(`"${key}" must be a number or an array of 2 or 4 numbers.`);
    if (![1, 2, 4].includes(value.length)) {
        throw new UiDocumentError(`"${key}" needs 1, 2 or 4 numbers, not ${value.length}.`);
    }
    return toVec4(value);
}

/** One number meaning both axes, or two. */
function pair(value) {
    if (typeof value === 'number') return { x: value, y: value };
    return toVec2(value);
}

/**
 * The colour forms a scene file already accepts: hex with or without alpha, an
 * [r,g,b] or [r,g,b,a] array, or an object with R/G/B/A. Null means "draw nothing",
 * which is not the same as black.
 */
export function colour(value) {
    if (value === null || value === undefined) return null;

    if (typeof value === 'string') return normaliseHex(value);

    if (Array.isArray(value)) {
        if (value.length !== 3 && value.length !== 4) {
            throw new UiDocumentError('A colour array needs 3 or 4 channels.');
        }
        const [r, g, b, a = 255] = value.map(Number);
        return `#${hex2(r)}${hex2(g)}${hex2(b)}${a === 255 ? '' : hex2(a)}`;
    }

    if (typeof value === 'object') {
        const r = Number(value.R ?? value.r ?? 0);
        const g = Number(value.G ?? value.g ?? 0);
        const b = Number(value.B ?? value.b ?? 0);
        const a = Number(value.A ?? value.a ?? 255);
        return `#${hex2(r)}${hex2(g)}${hex2(b)}${a === 255 ? '' : hex2(a)}`;
    }

    throw new UiDocumentError(`${JSON.stringify(value)} is not a colour.`);
}

function normaliseHex(text) {
    const trimmed = text.trim();
    if (!trimmed) return null;

    const body = trimmed.replace(/^#/, '');
    if (!/^([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$/i.test(body)) {
        throw new UiDocumentError(`"${text}" is not a colour. Use #rgb, #rrggbb or #rrggbbaa.`);
    }

    if (body.length === 3) {
        return `#${body[0]}${body[0]}${body[1]}${body[1]}${body[2]}${body[2]}`.toLowerCase();
    }
    return `#${body.toLowerCase()}`;
}

function hex2(v) {
    return Math.max(0, Math.min(255, Math.round(Number(v) || 0))).toString(16).padStart(2, '0');
}

function bool(v) {
    if (typeof v === 'boolean') return v;
    if (typeof v === 'number') return v !== 0;
    throw new UiDocumentError(`Expected true or false, not ${JSON.stringify(v)}.`);
}

function num(v) {
    if (typeof v === 'number') return v;
    if (typeof v === 'boolean') return v ? 1 : 0;
    const parsed = Number(v);
    if (Number.isFinite(parsed)) return parsed;
    throw new UiDocumentError(`Expected a number, not ${JSON.stringify(v)}.`);
}
