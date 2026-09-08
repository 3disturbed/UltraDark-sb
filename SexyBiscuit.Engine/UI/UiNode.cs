using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// One node in a UI tree: plain, serialisable data with a resolved rectangle.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately one class with a <see cref="UiKind"/> rather than a class per widget.
/// A hierarchy of twelve types means twelve rows in the component-schema parity list,
/// twelve JSON cases, twelve inspector drawers and twelve browser mirrors, all
/// hand-maintained; one class means one of each. The cost is that a <c>Value</c> on a
/// label means nothing, which the inspector greys out and the codec omits.
/// </para>
/// <para>
/// A node is not an Actor and not a Component. <see cref="Actor.AttachTo"/> rebases the
/// local transform when it reparents, which is right for a turret and wrong for a button
/// dragged between two panels; and an Actor per node would put a Transform, a component
/// list and six allocating lifecycle walks behind every row of a scrolling list.
/// </para>
/// <para>
/// Every property that can change a layout has a hand-written setter that marks the tree
/// dirty. That is the whole reason <see cref="Rect"/> is a cached field read in O(1)
/// rather than a walk up the parent chain on every access.
/// </para>
/// </remarks>
public sealed class UiNode
{
    // -------------------------------------------------------------------------
    // Identity and state
    // -------------------------------------------------------------------------

    /// <summary>Lookup key for <c>find(name)</c>. First match in tree order wins.</summary>
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }

    public UiKind Kind { get => _kind; set => SetMeasure(ref _kind, value); }

    /// <summary>An invisible node is skipped by measure, arrange, paint and hit-testing alike.</summary>
    public bool Visible { get => _visible; set => SetMeasure(ref _visible, value); }

    /// <summary>Whether this node and its children can be hovered, pressed or focused.</summary>
    public bool Interactive { get => _interactive; set => Set(ref _interactive, value); }

    /// <summary>Sibling paint and hit order. Stable; tree order breaks ties.</summary>
    public int Order { get => _order; set => SetArrange(ref _order, value); }

    // -------------------------------------------------------------------------
    // Box model
    // -------------------------------------------------------------------------

    public float    Width      { get => _width;      set => SetMeasure(ref _width, value); }
    public float    Height     { get => _height;     set => SetMeasure(ref _height, value); }
    public SizeMode WidthMode  { get => _widthMode;  set => SetMeasure(ref _widthMode, value); }
    public SizeMode HeightMode { get => _heightMode; set => SetMeasure(ref _heightMode, value); }

    public float MinWidth  { get => _minWidth;  set => SetMeasure(ref _minWidth, value); }
    public float MinHeight { get => _minHeight; set => SetMeasure(ref _minHeight, value); }

    /// <summary>Upper bound in pixels. Zero or less means unbounded.</summary>
    public float MaxWidth  { get => _maxWidth;  set => SetMeasure(ref _maxWidth, value); }
    public float MaxHeight { get => _maxHeight; set => SetMeasure(ref _maxHeight, value); }

    /// <summary>Share of leftover space along the parent's main axis.</summary>
    public float Grow { get => _grow; set => SetMeasure(ref _grow, value); }

    /// <summary>Share of the overflow to give up when children do not fit.</summary>
    public float Shrink { get => _shrink; set => SetMeasure(ref _shrink, value); }

    /// <summary>Inner inset, as left, top, right, bottom.</summary>
    public Vector4 Padding { get => _padding; set => SetMeasure(ref _padding, value); }

    /// <summary>Outer inset, as left, top, right, bottom.</summary>
    public Vector4 Margin { get => _margin; set => SetMeasure(ref _margin, value); }

    // -------------------------------------------------------------------------
    // Container
    // -------------------------------------------------------------------------

    public LayoutMode Layout     { get => _layout;     set => SetMeasure(ref _layout, value); }
    public Vector2    Gap        { get => _gap;        set => SetMeasure(ref _gap, value); }
    public bool       Wrap       { get => _wrap;       set => SetMeasure(ref _wrap, value); }
    public AlignMode  MainAlign  { get => _mainAlign;  set => SetArrange(ref _mainAlign, value); }
    public AlignMode  CrossAlign { get => _crossAlign; set => SetArrange(ref _crossAlign, value); }
    public int        Columns    { get => _columns;    set => SetMeasure(ref _columns, value); }

    /// <summary>Grid cell size. Zero on an axis derives that axis from the content.</summary>
    public Vector2 CellSize { get => _cellSize; set => SetMeasure(ref _cellSize, value); }

    // -------------------------------------------------------------------------
    // Placement
    // -------------------------------------------------------------------------

    public PositionMode Positioning { get => _positioning; set => SetMeasure(ref _positioning, value); }

    /// <summary>
    /// The named anchor. Anything but <see cref="UiAnchor.Custom"/> drives AnchorMin,
    /// AnchorMax and Pivot together, so the anchor is both the point the node hangs
    /// from and the corner of its own box that hangs there.
    /// </summary>
    public UiAnchor Anchor
    {
        get => _anchor;
        set
        {
            if (_anchor == value) return;
            _anchor = value;
            if (value != UiAnchor.Custom)
            {
                Vector2 f = AnchorFraction(value);
                _anchorMin = _anchorMax = _pivot = f;
            }
            InvalidateArrange();
        }
    }

    public Vector2 AnchorMin { get => _anchorMin; set => SetCustomAnchor(ref _anchorMin, value); }
    public Vector2 AnchorMax { get => _anchorMax; set => SetCustomAnchor(ref _anchorMax, value); }
    public Vector2 Pivot     { get => _pivot;     set => SetCustomAnchor(ref _pivot, value); }

    public Vector2 Offset    { get => _offset;    set => SetArrange(ref _offset, value); }
    public Vector2 OffsetMax { get => _offsetMax; set => SetArrange(ref _offsetMax, value); }

    // -------------------------------------------------------------------------
    // Text
    // -------------------------------------------------------------------------

    public string    Text          { get => _text;          set => SetMeasure(ref _text, value ?? ""); }
    public float     TextScale     { get => _textScale;     set => SetMeasure(ref _textScale, value); }
    public AlignMode TextAlign     { get => _textAlign;     set => SetArrange(ref _textAlign, value); }
    public AlignMode VerticalAlign { get => _verticalAlign; set => SetArrange(ref _verticalAlign, value); }
    public bool      WrapText      { get => _wrapText;      set => SetMeasure(ref _wrapText, value); }
    public float     LineSpacing   { get => _lineSpacing;   set => SetMeasure(ref _lineSpacing, value); }

    // -------------------------------------------------------------------------
    // Paint
    // -------------------------------------------------------------------------

    /// <summary>Fill colour. Null draws nothing at all, which is not the same as black.</summary>
    public Color? Background { get => _background; set => Set(ref _background, value); }

    public Color  Tint    { get => _tint;    set => Set(ref _tint, value); }
    public float  Opacity { get => _opacity; set => Set(ref _opacity, value); }

    public Color? BorderColour { get => _borderColour; set => Set(ref _borderColour, value); }
    public float  BorderWidth  { get => _borderWidth;  set => Set(ref _borderWidth, value); }

    public string  TexturePath { get => _texturePath; set => SetMeasure(ref _texturePath, value ?? ""); }
    public Vector4 SourceRect  { get => _sourceRect;  set => Set(ref _sourceRect, value); }

    /// <summary>Nine-patch borders as left, top, right, bottom. All zero means no slicing.</summary>
    public Vector4 NinePatch { get => _ninePatch; set => Set(ref _ninePatch, value); }

    /// <summary>Named theme block. Empty falls back to the style for this node's kind.</summary>
    public string Style { get => _style; set => Set(ref _style, value ?? ""); }

    // -------------------------------------------------------------------------
    // Clipping and scrolling
    // -------------------------------------------------------------------------

    public bool       Clip         { get => _clip;         set => SetArrange(ref _clip, value); }
    public ScrollMode Scroll       { get => _scroll;       set => SetMeasure(ref _scroll, value); }
    public Vector2    ScrollOffset { get => _scrollOffset; set => SetArrange(ref _scrollOffset, value); }

    /// <summary>Lets a full-bleed background cover the notch the rest of the UI avoids.</summary>
    public bool IgnoreSafeArea { get => _ignoreSafeArea; set => SetMeasure(ref _ignoreSafeArea, value); }

    // -------------------------------------------------------------------------
    // Focus and navigation
    // -------------------------------------------------------------------------

    /// <summary>Whether this node can take focus. Auto lets the kind decide.</summary>
    public Focusability Focusable { get; set; } = Focusability.Auto;

    /// <summary>
    /// Traps focus inside this subtree while it is visible.
    /// </summary>
    /// <remarks>
    /// One flag serves both input classes: it stops directional navigation escaping to the
    /// HUD behind a dialog, and it makes a pointer click outside the dialog miss everything
    /// rather than pressing whatever happens to be under it.
    /// </remarks>
    public bool Modal { get => _modal; set => Set(ref _modal, value); }

    /// <summary>
    /// Overrides for when the automatic answer is wrong, each naming a node.
    /// </summary>
    /// <remarks>
    /// A name rather than a reference so an override survives serialisation and can be
    /// written in a <c>.ui</c> file. An override naming a node that is not currently
    /// focusable is followed onwards in the same direction rather than being a dead end,
    /// so a chain still works when one item in it is disabled.
    /// </remarks>
    public string NavUp    { get; set; } = "";
    public string NavDown  { get; set; } = "";
    public string NavLeft  { get; set; } = "";
    public string NavRight { get; set; } = "";

    /// <summary>Focused first when a scope opens, whatever reading order would have said.</summary>
    public bool AutoFocus { get; set; }

    // -------------------------------------------------------------------------
    // Payload — meaningful only for some kinds
    // -------------------------------------------------------------------------

    public float Value    { get => _value;    set => Set(ref _value, value); }
    public float MinValue { get => _minValue; set => Set(ref _minValue, value); }
    public float MaxValue { get => _maxValue; set => Set(ref _maxValue, value); }
    public float Step     { get => _step;     set => Set(ref _step, value); }
    public bool  Checked  { get => _checked;  set => Set(ref _checked, value); }

    public int SelectedIndex { get => _selectedIndex; set => Set(ref _selectedIndex, value); }

    /// <summary>Dropdown and tab labels. Filled in place, so the serialiser can read it.</summary>
    public List<string> Options { get; } = new();

    // -------------------------------------------------------------------------
    // Resolved by the layout pass — never serialised
    // -------------------------------------------------------------------------

    /// <summary>The node's rectangle in canvas space, valid after the frame's layout pass.</summary>
    [SceneIgnore, JsonIgnore] public RectangleF Rect { get; internal set; }

    /// <summary>The rectangle inside the padding, which children are arranged into.</summary>
    [SceneIgnore, JsonIgnore] public RectangleF ContentRect { get; internal set; }

    /// <summary>The visible region, being every clipping ancestor intersected together.</summary>
    [SceneIgnore, JsonIgnore] public RectangleF ClipRect { get; internal set; }

    /// <summary>What measure asked for, before arrange handed out what there was.</summary>
    [SceneIgnore, JsonIgnore] public Vector2 DesiredSize { get; internal set; }

    /// <summary>Total size of the children, which is what a scroll region scrolls over.</summary>
    [SceneIgnore, JsonIgnore] public Vector2 ContentSize { get; internal set; }

    [SceneIgnore, JsonIgnore] public UiNode?   Parent   { get; internal set; }
    [SceneIgnore, JsonIgnore] public UiCanvas? Canvas   { get; internal set; }
    [SceneIgnore, JsonIgnore] public List<UiNode> Children { get; } = new();

    [SceneIgnore, JsonIgnore] public bool MeasureDirty { get; private set; } = true;
    [SceneIgnore, JsonIgnore] public bool ArrangeDirty { get; private set; } = true;

    // -------------------------------------------------------------------------
    // Interaction — written by the input router each frame, never serialised
    // -------------------------------------------------------------------------

    /// <summary>The pointer is over this node and nothing in front of it took the hit.</summary>
    [SceneIgnore, JsonIgnore] public bool Hovered { get; internal set; }

    /// <summary>The pointer went down on this node and has not been released yet.</summary>
    [SceneIgnore, JsonIgnore] public bool Pressed { get; internal set; }

    /// <summary>True for the one frame a press completed on this node.</summary>
    /// <remarks>
    /// A click is a release inside the node the press started on, which is what every
    /// other toolkit means by one. Dragging off a button and letting go must not fire it.
    /// </remarks>
    [SceneIgnore, JsonIgnore] public bool Clicked { get; internal set; }

    /// <summary>Whether <see cref="UiFocus"/> currently holds this node.</summary>
    [SceneIgnore, JsonIgnore] public bool Focused { get; internal set; }

    /// <summary>Whether a dropdown is showing its list.</summary>
    /// <remarks>
    /// Transient rather than a real property because an open list is a thing the player
    /// is doing, not a thing the document says. It lives here rather than in the router
    /// so the painter can read it without either side knowing about the other.
    /// </remarks>
    [SceneIgnore, JsonIgnore] public bool Expanded { get; internal set; }

    // -------------------------------------------------------------------------
    // Tree
    // -------------------------------------------------------------------------

    /// <summary>Appends a child, detaching it from any previous parent first.</summary>
    public UiNode Add(UiNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child == this) throw new InvalidOperationException("A node cannot be its own child.");

        child.Parent?.Remove(child);
        child.Parent = this;
        child.SetCanvasRecursive(Canvas);
        Children.Add(child);
        InvalidateMeasure();
        return child;
    }

    public bool Remove(UiNode child)
    {
        if (!Children.Remove(child)) return false;
        child.Parent = null;
        child.SetCanvasRecursive(null);
        InvalidateMeasure();
        return true;
    }

    /// <summary>Detaches this node from its parent. Safe to call when it has none.</summary>
    public void Detach() => Parent?.Remove(this);

    /// <summary>First descendant with this name, depth-first, or null.</summary>
    public UiNode? Find(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (Name == name) return this;

        foreach (UiNode child in Children)
            if (child.Find(name) is { } hit) return hit;

        return null;
    }

    /// <summary>Every node beneath this one, parents before children.</summary>
    public IEnumerable<UiNode> Descendants()
    {
        foreach (UiNode child in Children)
        {
            yield return child;
            foreach (UiNode deeper in child.Descendants()) yield return deeper;
        }
    }

    private void SetCanvasRecursive(UiCanvas? canvas)
    {
        Canvas = canvas;
        foreach (UiNode child in Children) child.SetCanvasRecursive(canvas);
    }

    // -------------------------------------------------------------------------
    // Invalidation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Marks this node and every ancestor as needing measuring again. A size change
    /// travels up, because a parent sized to its content is now the wrong size too.
    /// </summary>
    public void InvalidateMeasure()
    {
        for (UiNode? n = this; n != null && !(n.MeasureDirty && n.ArrangeDirty); n = n.Parent)
        {
            n.MeasureDirty = true;
            n.ArrangeDirty = true;
        }
    }

    /// <summary>Marks the node as needing re-placing, without re-measuring it.</summary>
    public void InvalidateArrange()
    {
        for (UiNode? n = this; n != null && !n.ArrangeDirty; n = n.Parent)
            n.ArrangeDirty = true;
    }

    internal void ClearDirty()
    {
        MeasureDirty = false;
        ArrangeDirty = false;
    }

    /// <summary>Clears this node and everything under it.</summary>
    /// <remarks>
    /// <see cref="InvalidateMeasure"/> stops at the first node already fully dirty, which is
    /// only sound while a dirty node implies dirty ancestors. A subtree the layout pass never
    /// reaches — one under a hidden node — would otherwise keep the flags it was born with for
    /// ever, and the next change inside it would break out of that walk immediately and never
    /// mark the root. The canvas would then skip the pass, so a panel built hidden and shown
    /// later never lays out at all: it stays at a zero rect, invisible and un-clickable, which
    /// is what a pause menu, a dialog and a card picker all are.
    /// </remarks>
    internal void ClearDirtyTree()
    {
        ClearDirty();
        foreach (UiNode child in Children) child.ClearDirtyTree();
    }

    // -------------------------------------------------------------------------
    // Anchors
    // -------------------------------------------------------------------------

    /// <summary>The fraction of a parent's box that a named anchor sits at.</summary>
    public static Vector2 AnchorFraction(UiAnchor anchor) => anchor switch
    {
        UiAnchor.TopLeft     => new Vector2(0f,   0f),
        UiAnchor.Top         => new Vector2(0.5f, 0f),
        UiAnchor.TopRight    => new Vector2(1f,   0f),
        UiAnchor.Left        => new Vector2(0f,   0.5f),
        UiAnchor.Center      => new Vector2(0.5f, 0.5f),
        UiAnchor.Right       => new Vector2(1f,   0.5f),
        UiAnchor.BottomLeft  => new Vector2(0f,   1f),
        UiAnchor.Bottom      => new Vector2(0.5f, 1f),
        UiAnchor.BottomRight => new Vector2(1f,   1f),
        _                    => new Vector2(0f,   0f),
    };

    // -------------------------------------------------------------------------
    // Setter plumbing
    // -------------------------------------------------------------------------

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
    }

    private void SetMeasure<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        InvalidateMeasure();
    }

    private void SetArrange<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        InvalidateArrange();
    }

    /// <summary>
    /// Writing any of the three anchor vectors by hand means the author wants the
    /// custom form; silently keeping a named preset would make the write a no-op the
    /// next time the preset was applied.
    /// </summary>
    private void SetCustomAnchor(ref Vector2 field, Vector2 value)
    {
        if (field == value) return;
        field = value;
        _anchor = UiAnchor.Custom;
        InvalidateArrange();
    }

    public override string ToString()
        => string.IsNullOrEmpty(Name) ? $"{Kind}" : $"{Kind}[{Name}]";

    // -------------------------------------------------------------------------
    // Backing fields
    // -------------------------------------------------------------------------

    private string   _name = "";
    private UiKind   _kind;
    private bool     _visible = true;
    private bool     _interactive = true;
    private int      _order;

    private float    _width, _height;
    private SizeMode _widthMode = SizeMode.Auto, _heightMode = SizeMode.Auto;
    private float    _minWidth, _minHeight, _maxWidth, _maxHeight;
    private float    _grow, _shrink = 1f;
    private Vector4  _padding, _margin;

    private LayoutMode _layout = LayoutMode.None;
    private Vector2    _gap;
    private bool       _wrap;
    private AlignMode  _mainAlign = AlignMode.Start, _crossAlign = AlignMode.Start;
    private int        _columns = 2;
    private Vector2    _cellSize;

    private PositionMode _positioning = PositionMode.Layout;
    private UiAnchor     _anchor = UiAnchor.TopLeft;
    private Vector2      _anchorMin, _anchorMax, _pivot;
    private Vector2      _offset, _offsetMax;

    private string    _text = "";
    private float     _textScale = 1f;
    private AlignMode _textAlign = AlignMode.Start, _verticalAlign = AlignMode.Center;
    private bool      _wrapText;
    private float     _lineSpacing;

    private Color?  _background;
    private Color   _tint = Color.White;
    private float   _opacity = 1f;
    private Color?  _borderColour;
    private float   _borderWidth;
    private string  _texturePath = "";
    private Vector4 _sourceRect;
    private Vector4 _ninePatch;
    private string  _style = "";

    private bool       _modal;
    private bool       _clip;
    private ScrollMode _scroll = ScrollMode.None;
    private Vector2    _scrollOffset;
    private bool       _ignoreSafeArea;

    private float _value = 1f, _minValue, _maxValue = 1f, _step;
    private bool  _checked;
    private int   _selectedIndex;
}
