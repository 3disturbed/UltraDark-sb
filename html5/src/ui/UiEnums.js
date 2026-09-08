// -----------------------------------------------------------------------------
// UiEnums — the node vocabulary, spelled exactly as the C# enums are.
//
// The strings here are what `Enum.ToString()` produces on the other engine, so a
// scene file or a .ui document written by either one reads identically on the
// other. Comparisons go through `canonical()`, so a hand-written "bottom-right"
// or "bottomRight" still means BottomRight -- what is pinned is what gets
// *written*, not what has to be typed.
// -----------------------------------------------------------------------------

/** What a node is. Only the first five paint themselves; the rest are compositions. */
export const UiKind = Object.freeze({
    Panel: 'Panel',
    Label: 'Label',
    Bar: 'Bar',
    Button: 'Button',
    Image: 'Image',

    Slider: 'Slider',
    Toggle: 'Toggle',
    TextField: 'TextField',
    Dropdown: 'Dropdown',
    ScrollView: 'ScrollView',
    TabStrip: 'TabStrip',
    Spacer: 'Spacer',
});

/** How one axis of a node decides its size. */
export const SizeMode = Object.freeze({
    Auto: 'Auto',
    Fixed: 'Fixed',
    Percent: 'Percent',
    Stretch: 'Stretch',
});

/** How a container arranges its children. */
export const LayoutMode = Object.freeze({
    None: 'None',
    Row: 'Row',
    Column: 'Column',
    Grid: 'Grid',
    Flow: 'Flow',
});

/** Where content sits along an axis. */
export const AlignMode = Object.freeze({
    Start: 'Start',
    Center: 'Center',
    End: 'End',
    Stretch: 'Stretch',
    SpaceBetween: 'SpaceBetween',
    SpaceAround: 'SpaceAround',
    SpaceEvenly: 'SpaceEvenly',
});

/** Whether a node joins its parent's layout or hangs from an anchor. */
export const PositionMode = Object.freeze({
    Layout: 'Layout',
    Absolute: 'Absolute',
});

/** Which axes a node scrolls on. */
export const ScrollMode = Object.freeze({
    None: 'None',
    Vertical: 'Vertical',
    Horizontal: 'Horizontal',
    Both: 'Both',
});

/**
 * Where a node hangs from, and which of its own corners hangs there.
 *
 * Deliberately both at once: `BottomRight` with an offset of (-12, -12) sits
 * twelve pixels in from the corner at any window size, with nothing measured by
 * hand. `Custom` hands control to anchorMin/anchorMax/pivot, which is how a node
 * stretches between two anchors.
 */
export const UiAnchor = Object.freeze({
    TopLeft: 'TopLeft', Top: 'Top', TopRight: 'TopRight',
    Left: 'Left', Center: 'Center', Right: 'Right',
    BottomLeft: 'BottomLeft', Bottom: 'Bottom', BottomRight: 'BottomRight',
    Custom: 'Custom',
});

/** Whether a node can take focus, and whether it says so itself. */
export const Focusability = Object.freeze({
    /** Let the kind decide: a button is focusable, a label is not. */
    Auto: 'Auto',
    Yes: 'Yes',
    No: 'No',
});

/**
 * Which class of device the player is currently driving the UI with.
 *
 * The reason one UI can feel native on four input classes at once. The focus ring is
 * drawn only in Directional, hover only in Pointer, and neither under Touch -- where a
 * finger has no hover state to show and a ring left over from a gamepad is just clutter.
 */
export const UiInputMode = Object.freeze({
    Pointer: 'Pointer',
    Directional: 'Directional',
    Touch: 'Touch',
});

/** How a canvas maps its reference resolution onto the real viewport. */
export const UiScaleMode = Object.freeze({
    ConstantPixel: 'ConstantPixel',
    ScaleToFit: 'ScaleToFit',
    ScaleToFill: 'ScaleToFill',
    Match: 'Match',
});

/** Which axes the safe-area insets apply to. */
export const SafeAreaMode = Object.freeze({
    Ignore: 'Ignore',
    Inset: 'Inset',
    InsetX: 'InsetX',
    InsetY: 'InsetY',
});

/** Whether a canvas is a surface on the screen or a plane standing in the world. */
export const UiSpace = Object.freeze({
    Screen: 'Screen',
    World: 'World',
});

/** How a world-space canvas is turned to face the player. */
export const UiFacing = Object.freeze({
    Billboard: 'Billboard',
    VerticalBillboard: 'VerticalBillboard',
    Plane: 'Plane',
});

/** The fraction of a parent's box that a named anchor sits at. */
export function anchorFraction(anchor) {
    switch (canonical(anchor)) {
        case 'topleft':     return { x: 0,   y: 0   };
        case 'top':         return { x: 0.5, y: 0   };
        case 'topright':    return { x: 1,   y: 0   };
        case 'left':        return { x: 0,   y: 0.5 };
        case 'center':
        case 'centre':      return { x: 0.5, y: 0.5 };
        case 'right':       return { x: 1,   y: 0.5 };
        case 'bottomleft':  return { x: 0,   y: 1   };
        case 'bottom':      return { x: 0.5, y: 1   };
        case 'bottomright': return { x: 1,   y: 1   };
        default:            return { x: 0,   y: 0   };
    }
}

/** Lower-cased with hyphens, underscores and spaces removed, for forgiving comparison. */
export function canonical(name) {
    return String(name ?? '').replace(/[-_ ]/g, '').toLowerCase();
}

/**
 * Resolves a loosely written enum value to the exact spelling the other engine writes.
 * Throws rather than falling back, because a silently ignored value is the defect this
 * whole subsystem exists to stop repeating.
 */
export function parseEnum(table, value, key) {
    const wanted = canonical(value);
    for (const name of Object.values(table)) {
        if (canonical(name) === wanted) return name;
    }
    // "centre" and "center" are the same place, and this codebase writes both.
    if (wanted === 'centre' && table.Center) return table.Center;

    throw new UiDocumentError(
        `"${value}" is not a valid value for "${key}". Expected one of: ${Object.values(table).join(', ')}.`);
}

/** Raised when a UI document says something a node cannot mean. */
export class UiDocumentError extends Error {}
