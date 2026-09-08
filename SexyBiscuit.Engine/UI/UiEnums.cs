namespace SexyBiscuit.Engine.UI;

// =============================================================================
// Node vocabulary
// =============================================================================

/// <summary>
/// What a node is. The browser engine knows the same list, in the same order.
/// </summary>
/// <remarks>
/// The first five are the kinds the flat script API has always had, kept first and
/// kept named the same so the shim over <c>UI.panel/label/bar/button/image</c> is a
/// lookup rather than a translation. Only those five paint themselves; everything
/// after them is a composition of them plus a kind for behaviour and hit-testing,
/// which is what makes twelve kinds affordable on two engines instead of twelve
/// painters written twice.
/// </remarks>
public enum UiKind
{
    Panel,
    Label,
    Bar,
    Button,
    Image,

    Slider,
    Toggle,
    TextField,
    Dropdown,
    ScrollView,
    TabStrip,
    Spacer,
}

// =============================================================================
// Box model
// =============================================================================

/// <summary>How one axis of a node decides its size.</summary>
public enum SizeMode
{
    /// <summary>As large as the content needs.</summary>
    Auto,
    /// <summary>Exactly the stated number of pixels.</summary>
    Fixed,
    /// <summary>A fraction of the parent's content box, where 1 is all of it.</summary>
    Percent,
    /// <summary>Fill whatever the parent hands out.</summary>
    Stretch,
}

/// <summary>How a container arranges its children.</summary>
public enum LayoutMode
{
    /// <summary>Children are not arranged; each resolves its own anchor.</summary>
    None,
    Row,
    Column,
    Grid,
    /// <summary>A row that wraps. An alias, not a second code path.</summary>
    Flow,
}

/// <summary>
/// Where content sits along an axis. Used for both the main and cross axis of a
/// container, and for text within its own box.
/// </summary>
public enum AlignMode
{
    Start,
    Center,
    End,
    /// <summary>Fill the line. Meaningless on the main axis, where Grow does this.</summary>
    Stretch,
    SpaceBetween,
    SpaceAround,
    SpaceEvenly,
}

/// <summary>Whether a node joins its parent's layout or hangs from an anchor.</summary>
public enum PositionMode
{
    Layout,
    Absolute,
}

/// <summary>Which axes a node scrolls on.</summary>
public enum ScrollMode
{
    None,
    Vertical,
    Horizontal,
    Both,
}

// =============================================================================
// Anchoring
// =============================================================================

/// <summary>
/// Where a node hangs from, and which of its own corners hangs there.
/// </summary>
/// <remarks>
/// The anchor is deliberately both things at once, which is the one genuinely good
/// idea in the UI the engine had before this one: <c>BottomRight</c> with an offset
/// of (-12, -12) sits twelve pixels in from the corner at any window size, without
/// the caller measuring anything. <see cref="Custom"/> hands control to AnchorMin,
/// AnchorMax and Pivot, which is how a node stretches between two anchors.
/// </remarks>
public enum UiAnchor
{
    TopLeft, Top, TopRight,
    Left, Center, Right,
    BottomLeft, Bottom, BottomRight,
    Custom,
}

/// <summary>Whether a node can take focus, and whether it says so itself.</summary>
public enum Focusability
{
    /// <summary>Let the kind decide: a button is focusable, a label is not.</summary>
    Auto,
    Yes,
    No,
}

/// <summary>
/// Which class of device the player is currently driving the UI with.
/// </summary>
/// <remarks>
/// The reason one UI can feel native on four input classes at once. The focus ring is
/// drawn only in <see cref="Directional"/>, hover only in <see cref="Pointer"/>, and
/// neither under <see cref="Touch"/> — where a finger has no hover state to show and a
/// ring left over from a gamepad is just clutter.
/// </remarks>
public enum UiInputMode
{
    Pointer,
    Directional,
    Touch,
}

// =============================================================================
// Canvas
// =============================================================================

/// <summary>How a canvas maps its reference resolution onto the real viewport.</summary>
public enum UiScaleMode
{
    /// <summary>One canvas unit is one device pixel. The default, and what the shim uses.</summary>
    ConstantPixel,
    /// <summary>Uniform scale, letterboxed, so nothing is ever cropped.</summary>
    ScaleToFit,
    /// <summary>Uniform scale, cropped, so there is never a letterbox.</summary>
    ScaleToFill,
    /// <summary>Blend the width and height ratios by <c>MatchWidthOrHeight</c>.</summary>
    Match,
}

/// <summary>Which axes the safe-area insets apply to.</summary>
public enum SafeAreaMode
{
    Ignore,
    Inset,
    InsetX,
    InsetY,
}
