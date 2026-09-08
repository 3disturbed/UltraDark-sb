using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// One screen-space surface holding a tree of <see cref="UiNode"/>. Attach it to an
/// Actor to put a UI in a scene.
/// </summary>
/// <remarks>
/// <para>
/// A canvas does not paint through <see cref="Component.Draw"/>, and that is deliberate.
/// Component drawing happens inside the camera-transformed world batch, which is why the
/// UI this replaces panned, zoomed and shook with the camera and landed at the back of a
/// front-to-back sort. The host collects canvases and paints them in the screen-space pass
/// that runs after the world, in <see cref="Order"/>.
/// </para>
/// <para>
/// The scale and offset computed here are used by <em>both</em> painting and hit-testing.
/// The canvas this replaces had a public, documented scale matrix that nothing ever called,
/// so pointer coordinates stayed in raw screen pixels and hit-testing quietly disagreed with
/// what was on screen at any window size but one.
/// </para>
/// <para>
/// The canvas may have no Actor: the process-wide canvas behind the script <c>UI</c> global
/// is constructed directly. Nothing here may dereference <see cref="Component.Actor"/>.
/// </para>
/// </remarks>
public class UiCanvas : Component
{
    // -------------------------------------------------------------------------
    // Every live canvas, in paint order
    // -------------------------------------------------------------------------

    private static readonly List<UiCanvas> _all = new();

    /// <summary>Every canvas that currently exists, for the host's screen-space pass.</summary>
    public static IReadOnlyList<UiCanvas> All => _all;

    public UiCanvas()
    {
        _all.Add(this);
        Root.Canvas = this;
    }

    public override void OnDestroy()
    {
        _all.Remove(this);
        base.OnDestroy();
    }

    /// <summary>Drops every canvas. A test that leaks one poisons the next.</summary>
    internal static void ClearAll() => _all.Clear();

    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------

    /// <summary>How the reference resolution maps onto the viewport.</summary>
    public UiScaleMode ScaleMode { get; set; } = UiScaleMode.ConstantPixel;

    /// <summary>The resolution the tree is authored at, for every mode but ConstantPixel.</summary>
    public Vector2 ReferenceResolution { get; set; } = new(1920f, 1080f);

    /// <summary>Blends the width and height ratios under <see cref="UiScaleMode.Match"/>.</summary>
    public float MatchWidthOrHeight { get; set; } = 0.5f;

    /// <summary>Paint order between canvases. Lower paints first, so higher is in front.</summary>
    public int Order { get; set; }

    /// <summary>Path to a <c>.ui</c> document. When set it replaces <see cref="Root"/> at start.</summary>
    public string Document { get; set; } = "";

    /// <summary>Insets kept clear of notches and TV overscan, as left, top, right, bottom.</summary>
    public Vector4 SafeArea { get; set; }

    /// <summary>Which axes <see cref="SafeArea"/> applies to.</summary>
    public SafeAreaMode SafeAreaMode { get; set; } = SafeAreaMode.Ignore;

    /// <summary>Whether this canvas takes pointer and navigation input at all.</summary>
    public bool Interactive { get; set; } = true;

    /// <summary>Which player drives this canvas, or -1 for anybody's input.</summary>
    public int PlayerIndex { get; set; } = -1;

    /// <summary>The tree. Always present; an empty root paints nothing.</summary>
    public UiNode Root { get; } = new() { Name = "Root", Kind = UiKind.Panel };

    // -------------------------------------------------------------------------
    // Resolved each frame
    // -------------------------------------------------------------------------

    /// <summary>Canvas units to device pixels.</summary>
    public Vector2 Scale { get; private set; } = Vector2.One;

    /// <summary>Device-pixel offset of the canvas origin, which centres a letterbox.</summary>
    public Vector2 CanvasOffset { get; private set; }

    /// <summary>The viewport in canvas units, which is what a full-screen node fills.</summary>
    public Vector2 CanvasSize { get; private set; } = new(1280f, 720f);

    private Vector2 _viewport = new(1280f, 720f);

    /// <summary>
    /// Recomputes the scale and offset for a viewport size. Call before laying out.
    /// </summary>
    public void SetViewport(float width, float height)
    {
        width  = MathF.Max(1f, width);
        height = MathF.Max(1f, height);

        if (_viewport.X == width && _viewport.Y == height) return;
        _viewport = new Vector2(width, height);
        Root.InvalidateMeasure();
    }

    private void ResolveScale()
    {
        float vw = _viewport.X, vh = _viewport.Y;
        float rw = MathF.Max(1f, ReferenceResolution.X);
        float rh = MathF.Max(1f, ReferenceResolution.Y);

        switch (ScaleMode)
        {
            case UiScaleMode.ScaleToFit:
            {
                float s = MathF.Min(vw / rw, vh / rh);
                Scale = new Vector2(s, s);
                CanvasSize = new Vector2(rw, rh);
                break;
            }
            case UiScaleMode.ScaleToFill:
            {
                float s = MathF.Max(vw / rw, vh / rh);
                Scale = new Vector2(s, s);
                CanvasSize = new Vector2(rw, rh);
                break;
            }
            case UiScaleMode.Match:
            {
                float m = Math.Clamp(MatchWidthOrHeight, 0f, 1f);
                float s = MathF.Pow(vw / rw, 1f - m) * MathF.Pow(vh / rh, m);
                Scale = new Vector2(s, s);
                CanvasSize = new Vector2(vw / s, vh / s);
                break;
            }
            default:
                Scale = Vector2.One;
                CanvasSize = new Vector2(vw, vh);
                break;
        }

        // Centre whatever the scaled canvas does not cover, so a letterbox is even.
        CanvasOffset = new Vector2(
            (vw - CanvasSize.X * Scale.X) * 0.5f,
            (vh - CanvasSize.Y * Scale.Y) * 0.5f);
    }

    // -------------------------------------------------------------------------
    // Coordinates
    // -------------------------------------------------------------------------

    /// <summary>Device pixels to canvas units. Every hit test starts here.</summary>
    public Vector2 ScreenToCanvas(Vector2 screen) => new(
        (screen.X - CanvasOffset.X) / Scale.X,
        (screen.Y - CanvasOffset.Y) / Scale.Y);

    /// <summary>Canvas units to device pixels. Scissor rectangles must go through this.</summary>
    public Vector2 CanvasToScreen(Vector2 canvas) => new(
        canvas.X * Scale.X + CanvasOffset.X,
        canvas.Y * Scale.Y + CanvasOffset.Y);

    /// <summary>
    /// A canvas rectangle in device pixels, for the scissor test.
    /// </summary>
    /// <remarks>
    /// <c>GraphicsDevice.ScissorRectangle</c> is in device pixels and is not affected by
    /// the batch transform, so a clip rectangle passed straight through would be wrong by
    /// exactly the canvas scale.
    /// </remarks>
    public RectangleF CanvasToScreen(RectangleF r)
    {
        Vector2 topLeft     = CanvasToScreen(new Vector2(r.X, r.Y));
        Vector2 bottomRight = CanvasToScreen(new Vector2(r.Right, r.Bottom));
        return new RectangleF(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    /// <summary>The transform to hand <c>SpriteBatch.Begin</c> so canvas units paint correctly.</summary>
    public Matrix GetTransform()
        => Matrix.CreateScale(Scale.X, Scale.Y, 1f)
         * Matrix.CreateTranslation(CanvasOffset.X, CanvasOffset.Y, 0f);

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------

    /// <summary>
    /// The rectangle the root is laid out into: the canvas, less the safe-area insets
    /// on whichever axes they apply to.
    /// </summary>
    public RectangleF SafeRect
    {
        get
        {
            var full = new RectangleF(0f, 0f, CanvasSize.X, CanvasSize.Y);
            return SafeAreaMode switch
            {
                SafeAreaMode.Inset  => full.Deflate(SafeArea),
                SafeAreaMode.InsetX => full.Deflate(new Vector4(SafeArea.X, 0f, SafeArea.Z, 0f)),
                SafeAreaMode.InsetY => full.Deflate(new Vector4(0f, SafeArea.Y, 0f, SafeArea.W)),
                _                   => full,
            };
        }
    }

    /// <summary>
    /// Measures and arranges the tree if anything has changed since the last pass.
    /// Cheap to call every frame; that is the point of the dirty flags.
    /// </summary>
    public void Layout()
    {
        ResolveScale();
        if (!Root.MeasureDirty && !Root.ArrangeDirty) return;

        RectangleF area = SafeRect;
        UiLayout.Measure(Root, new Vector2(area.Width, area.Height));
        UiLayout.Arrange(Root, area, new RectangleF(0f, 0f, CanvasSize.X, CanvasSize.Y));
    }

    /// <summary>Forces a full pass next frame, whatever the dirty flags say.</summary>
    public void InvalidateLayout() => Root.InvalidateMeasure();

    // -------------------------------------------------------------------------
    // Hit testing
    // -------------------------------------------------------------------------

    /// <summary>
    /// The top-most interactive node under a device-pixel point, or null.
    /// </summary>
    public UiNode? HitTest(Vector2 screenPoint)
    {
        if (!Interactive) return null;
        return UiLayout.HitTest(Root, ScreenToCanvas(screenPoint));
    }

    /// <summary>Finds a node by name anywhere in this canvas.</summary>
    public UiNode? Find(string name) => Root.Find(name);

    // -------------------------------------------------------------------------
    // Input
    // -------------------------------------------------------------------------

    private UiInput? _input;

    /// <summary>
    /// This canvas's pointer, focus and control behaviour. Created on first use.
    /// </summary>
    /// <remarks>
    /// Lazy because a canvas that is only ever painted — a pure HUD — should not pay for
    /// a focus model it never consults, and because the router holds the focus state that
    /// has to survive between frames, so it cannot be created per frame by the host.
    /// </remarks>
    public UiInput Input => _input ??= new UiInput(this);

    // -------------------------------------------------------------------------
    // Documents
    // -------------------------------------------------------------------------

    /// <summary>Loads <see cref="Document"/>, if one is named, into <see cref="Root"/>.</summary>
    public override void Awake()
    {
        base.Awake();
        LoadDocument();
    }

    /// <summary>
    /// Replaces the tree with the contents of a <c>.ui</c> file.
    /// </summary>
    /// <remarks>
    /// The document's own root is not adopted as this canvas's root — its layout properties
    /// are copied onto <see cref="Root"/> and its children are moved across. A canvas whose
    /// root could be swapped would break every reference the host and the router hold, and
    /// <see cref="Root"/> is deliberately get-only for that reason.
    /// </remarks>
    public void LoadDocument()
    {
        if (string.IsNullOrWhiteSpace(Document)) return;

        string path = Core.ProjectPaths.Resolve(Document);
        if (!File.Exists(path)) return;

        Adopt(UiDocument.FromJson(File.ReadAllText(path)));
    }

    /// <summary>Moves a loaded document's children onto <see cref="Root"/>, replacing what was there.</summary>
    public void Adopt(UiNode document)
    {
        ArgumentNullException.ThrowIfNull(document);

        for (int i = Root.Children.Count - 1; i >= 0; i--) Root.Remove(Root.Children[i]);

        Root.Layout       = document.Layout;
        Root.Gap          = document.Gap;
        Root.Wrap         = document.Wrap;
        Root.Padding      = document.Padding;
        Root.MainAlign    = document.MainAlign;
        Root.CrossAlign   = document.CrossAlign;
        Root.Columns      = document.Columns;
        Root.CellSize     = document.CellSize;
        Root.Background   = document.Background;
        Root.Clip         = document.Clip;
        Root.Scroll       = document.Scroll;
        Root.ScrollOffset = document.ScrollOffset;

        foreach (UiNode child in new List<UiNode>(document.Children)) Root.Add(child);
        InvalidateLayout();
    }
}
