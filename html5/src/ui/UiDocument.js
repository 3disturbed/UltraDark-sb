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

// =============================================================================
// Reading — the mirror of apply()
// =============================================================================

/**
 * Reads one property back off a node, in the same spelling `apply` accepts.
 *
 * The other half of the codec, and the reason a script handle does not need sixty-odd
 * hand-written getters on each engine: a handle wires `get` here and `set` to `apply`,
 * so a property added to one switch reaches both engines' script API at once instead
 * of needing five hand-mirrored edits.
 *
 * Structural members -- `parent`, `children` and the functions -- are not here. They
 * hand back node handles rather than values, which only a bridge can build.
 */
export function read(node, key) {
    switch (canonical(key)) {
        // --- identity ---
        case 'name':        return node.name;
        case 'kind':
        case 'type':        return node.kind;
        case 'visible':     return node.visible;
        case 'interactive': return node.interactive;
        case 'order':       return node.order;
        case 'style':       return node.style;

        // --- size ---
        case 'width':      return readSize(node.widthMode, node.width);
        case 'height':     return readSize(node.heightMode, node.height);
        case 'widthmode':  return node.widthMode;
        case 'heightmode': return node.heightMode;
        case 'minwidth':   return node.minWidth;
        case 'minheight':  return node.minHeight;
        case 'maxwidth':   return node.maxWidth;
        case 'maxheight':  return node.maxHeight;
        case 'grow':       return node.grow;
        case 'shrink':     return node.shrink;
        case 'padding':    return fromEdges(node.padding);
        case 'margin':     return fromEdges(node.margin);

        // --- container ---
        case 'layout':     return node.layout;
        case 'gap':        return fromVec2(node.gap);
        case 'wrap':       return node.wrap;
        case 'mainalign':  return node.mainAlign;
        case 'crossalign': return node.crossAlign;
        case 'columns':    return node.columns;
        case 'cellsize':   return fromVec2(node.cellSize);

        // --- placement ---
        case 'positioning': return node.positioning;
        case 'absolute':    return node.positioning === PositionMode.Absolute;
        case 'anchor':      return node.anchor;
        case 'anchormin':   return fromVec2(node.anchorMin);
        case 'anchormax':   return fromVec2(node.anchorMax);
        case 'pivot':       return fromVec2(node.pivot);
        case 'offset':      return fromVec2(node.offset);
        case 'offsetmax':   return fromVec2(node.offsetMax);
        case 'x':           return node.offset.x;
        case 'y':           return node.offset.y;

        // --- text ---
        case 'text':          return node.text;
        case 'scale':
        case 'textscale':     return node.textScale;
        case 'align':
        case 'textalign':     return node.textAlign;
        case 'verticalalign': return node.verticalAlign;
        case 'wraptext':      return node.wrapText;
        case 'linespacing':   return node.lineSpacing;

        // --- paint ---
        case 'background':  return node.background ?? null;
        case 'tint':        return node.tint;
        case 'opacity':     return node.opacity;
        case 'bordercolour':
        case 'bordercolor': return node.borderColour ?? null;
        case 'borderwidth': return node.borderWidth;
        case 'texturepath': return node.texturePath;
        case 'sourcerect':  return fromEdges(node.sourceRect);
        case 'ninepatch':   return fromEdges(node.ninePatch);

        // --- clipping and scrolling ---
        case 'clip':           return node.clip;
        case 'scroll':         return node.scroll;
        case 'scrolloffset':   return fromVec2(node.scrollOffset);
        case 'ignoresafearea': return node.ignoreSafeArea;

        // --- focus and navigation ---
        case 'focusable': return node.focusable;
        case 'modal':     return node.modal;
        case 'navup':     return node.navUp;
        case 'navdown':   return node.navDown;
        case 'navleft':   return node.navLeft;
        case 'navright':  return node.navRight;
        case 'autofocus': return node.autoFocus;

        // --- payload ---
        case 'value':         return node.value;
        case 'minvalue':      return node.minValue;
        case 'maxvalue':      return node.maxValue;
        case 'step':          return node.step;
        case 'checked':       return node.checked;
        case 'selectedindex': return node.selectedIndex;
        case 'options':       return node.options.slice();

        // --- resolved by layout, read-only ---
        case 'rect': return {
            x: node.rect.x, y: node.rect.y,
            width: node.rect.width, height: node.rect.height,
        };

        // --- written by the input router, read-only ---
        case 'hovered':  return node.hovered;
        case 'pressed':  return node.pressed;
        case 'clicked':  return node.clicked;
        case 'focused':  return node.focused;
        case 'expanded': return node.expanded;

        default:
            throw new UiDocumentError(`"${key}" is not a UI node property.`);
    }
}

/** True when a key names something a node can be told, rather than only asked. */
export function isWritable(key) {
    return !['rect', 'hovered', 'pressed', 'clicked', 'focused', 'expanded']
        .includes(canonical(key));
}

// =============================================================================
// Writing
// =============================================================================

/**
 * The properties a document round-trip writes, in a fixed order.
 *
 * Only the ones that survive a reload: layout results and interaction flags are
 * recomputed every frame, and the aliases (`x`, `y`, `size`, `type`, `scale`, `align`,
 * `absolute`) would each write a second copy of a property already listed.
 */
const WRITABLE_KEYS = Object.freeze([
    'name', 'kind', 'visible', 'interactive', 'order', 'style',
    'width', 'height', 'minWidth', 'minHeight', 'maxWidth', 'maxHeight', 'grow', 'shrink',
    'padding', 'margin',
    'layout', 'gap', 'wrap', 'mainAlign', 'crossAlign', 'columns', 'cellSize',
    'positioning', 'anchor', 'anchorMin', 'anchorMax', 'pivot', 'offset', 'offsetMax',
    'text', 'textScale', 'textAlign', 'verticalAlign', 'wrapText', 'lineSpacing',
    'background', 'tint', 'opacity', 'borderColour', 'borderWidth', 'texturePath',
    'sourceRect', 'ninePatch',
    'clip', 'scroll', 'scrollOffset', 'ignoreSafeArea',
    'focusable', 'modal', 'navUp', 'navDown', 'navLeft', 'navRight', 'autoFocus',
    'value', 'minValue', 'maxValue', 'step', 'checked', 'selectedIndex', 'options',
]);

/**
 * Convenience spellings a handle carries as well as the property they set.
 *
 * Every existing script positions with `x`/`y` and sizes text with `scale`, so the tree
 * keeps those rather than making a migration rewrite arithmetic it did not need to change.
 * `size`, `type` and `absolute` are spec-only shorthand and are deliberately not handle
 * members: each writes a property the handle already exposes.
 */
const ALIAS_KEYS = Object.freeze(['x', 'y', 'scale', 'align']);

/** What layout and the input router work out, which a script may read but not set. */
const RESULT_KEYS = Object.freeze(['rect', 'hovered', 'pressed', 'clicked', 'focused', 'expanded']);

/**
 * Every value member a script handle exposes, in order.
 *
 * Both bridges build their handle by looping this, wiring `get` to read() and `set` to
 * apply(). A property added to the codec therefore reaches both engines' script API by
 * being named here once, instead of the five hand-mirrored edits the flat UI needed.
 * A test pins this list against the contract in both directions.
 */
export const SCRIPT_PROPERTIES = Object.freeze([...WRITABLE_KEYS, ...ALIAS_KEYS, ...RESULT_KEYS]);

/**
 * Writes a tree back out as the same JSON `fromObject` reads.
 *
 * Properties still at their default are omitted, so a document says only what it changed
 * and a round-trip does not bloat. The C# engine writes the identical shape, which is what
 * lets one fixture build a tree on both engines and compare the two serialisations.
 */
export function toObject(node) {
    const reference = new UiNode();
    const result = {};

    for (const key of WRITABLE_KEYS) {
        // A named anchor already implies its min, max and pivot, so writing all three
        // would put three redundant arrays into every anchored node of a .ui file.
        if (skipDerivedAnchor(node, key)) continue;

        const mine = read(node, key);
        const theirs = read(reference, key);
        if (same(mine, theirs)) continue;
        result[key] = mine;
    }

    if (node.children.length > 0) result.children = node.children.map(toObject);
    return result;
}

/** The tree as JSON text. */
export function toJson(node) { return JSON.stringify(toObject(node)); }

function same(a, b) { return JSON.stringify(a ?? null) === JSON.stringify(b ?? null); }

function skipDerivedAnchor(node, key) {
    return node.anchor !== UiAnchor.Custom
        && (key === 'anchorMin' || key === 'anchorMax' || key === 'pivot');
}

// -----------------------------------------------------------------------------
// Reading a value back out
// -----------------------------------------------------------------------------

/**
 * A size reads back in the spelling it was written in, so a round-trip is lossless:
 * a number for a fixed size, and the word for the three modes that have one.
 */
function readSize(mode, size) {
    if (mode === SizeMode.Auto) return 'auto';
    if (mode === SizeMode.Stretch) return '*';
    if (mode === SizeMode.Percent) return `${trimZeros(size * 100)}%`;
    return size;
}

function trimZeros(n) { return String(Math.round(n * 10000) / 10000); }

function fromVec2(v) { return [v.x, v.y]; }
function fromEdges(v) { return [v.x, v.y, v.z, v.w]; }
