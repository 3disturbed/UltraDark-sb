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
/// is constructed directly. Nothing here may dereference <see cref="Component.Actor"/>
/// unguarded — a world canvas reads a <c>Transform3D</c> through <c>Actor?.</c> and falls
/// back to <see cref="WorldPosition"/>, so a canvas a script made still has a place to stand.
/// </para>
/// <para>
/// <see cref="Space"/> is the whole of the difference between a HUD and a screen bolted to a
/// wall. The tree, the layout, the painter, the focus ring and the hit test are the same in
/// both; only the mapping from canvas units to pixels changes. That is what lets one
/// <c>.ui</c> document be adopted by either without editing a line of it.
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
        WorldTarget?.Dispose();
        WorldTarget = null;
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

    /// <summary>Whether this canvas is a surface on the screen or a plane in the world.</summary>
    public UiSpace Space { get; set; } = UiSpace.Screen;

    /// <summary>How a world-space canvas is turned to face the player.</summary>
    public UiFacing Facing { get; set; } = UiFacing.Billboard;

    /// <summary>
    /// Where a world canvas stands when its actor has no <c>Transform3D</c> — or has no actor.
    /// </summary>
    public Vector3 WorldPosition { get; set; }

    /// <summary>Shifts a world canvas off its anchor, in world units.</summary>
    /// <remarks>The usual value is a little way up, so a nameplate clears the head it belongs to.</remarks>
    public Vector3 WorldOffset { get; set; }

    /// <summary>
    /// Canvas units per world unit, which is the size dial for a world canvas.
    /// </summary>
    /// <remarks>
    /// A canvas authored at 400x200 with a hundred pixels to the unit is four units wide. Raising
    /// this shrinks the canvas in the world without re-laying anything out, which is the point:
    /// the tree is authored in pixels once and scaled here, rather than being re-authored small.
    /// </remarks>
    public float PixelsPerUnit { get; set; } = 100f;

    /// <summary>Stop drawing a world canvas past this distance. Zero never stops.</summary>
    public float MaxDrawDistance { get; set; }

    /// <summary>Whether a world canvas is legible from behind as well as in front.</summary>
    public bool DoubleSided { get; set; }

    /// <summary>Whether geometry in front of a world canvas hides it.</summary>
    /// <remarks>
    /// On for a screen on a wall, which should be behind the pillar in front of it. Off for a
    /// nameplate, which is wanted through the scenery.
    /// </remarks>
    public bool DepthTest { get; set; } = true;

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
        // A world canvas is not fitted to anything: it is a fixed sheet standing in the scene,
        // so its size is what it was authored at and its transform is the identity. That one
        // branch is why every other thing in the UI -- layout, painting, clipping, focus,
        // navigation, hit testing -- keeps working in world space without being told about it.
        if (Space == UiSpace.World)
        {
            Scale        = Vector2.One;
            CanvasOffset = Vector2.Zero;
            CanvasSize   = new Vector2(MathF.Max(1f, ReferenceResolution.X),
                                       MathF.Max(1f, ReferenceResolution.Y));
            return;
        }

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

    /// <summary>
    /// A point far enough outside any canvas that every rectangle test rejects it.
    /// </summary>
    /// <remarks>
    /// Finite on purpose. An infinity or a NaN would propagate through the slider arithmetic
    /// in <see cref="UiInput"/> instead of simply missing, and "missing" is exactly what a
    /// pointer ray that does not meet a canvas's plane should mean.
    /// </remarks>
    public static readonly Vector2 Nowhere = new(-1_000_000f, -1_000_000f);

    /// <summary>Device pixels to canvas units. Every hit test starts here.</summary>
    /// <remarks>
    /// The single seam between the two spaces, which is why world canvases cost the input
    /// router nothing: it still asks one question and still gets canvas units back.
    /// </remarks>
    public Vector2 ScreenToCanvas(Vector2 screen)
    {
        if (Space == UiSpace.World) return WorldScreenToCanvas(screen);

        return new Vector2(
            (screen.X - CanvasOffset.X) / Scale.X,
            (screen.Y - CanvasOffset.Y) / Scale.Y);
    }

    private Vector2 WorldScreenToCanvas(Vector2 screen)
    {
        if (WorldBasis() is { } basis && Rendering.Camera3D.Main is { } camera)
        {
            (Vector3 origin, Vector3 direction) =
                camera.ScreenToWorldRay(screen, _viewport.X, _viewport.Y);

            if (UiWorld.RayToCanvas(basis, origin, direction, CanvasSize) is { } point)
            {
                _lastWorldPoint = point;
                return point;
            }
        }

        // A drag holds its last good point rather than reporting a miss. Swinging the camera
        // until the ray leaves the plane would otherwise snap the slider you are dragging to
        // its minimum, which is a worse answer than "wherever you last had it".
        if (_input is { IsDragging: true } && _lastWorldPoint is { } held) return held;

        return Nowhere;
    }

    private Vector2? _lastWorldPoint;

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
    // World space
    // -------------------------------------------------------------------------

    /// <summary>The centre of a world canvas, being its actor's position plus the offset.</summary>
    public Vector3 WorldAnchor
    {
        get
        {
            Vector3 origin = Transform3D is { } t ? t.Position : WorldPosition;
            return origin + WorldOffset;
        }
    }

    /// <summary>The canvas's extent in world units: its size in canvas units, scaled down.</summary>
    public Vector2 WorldSize
    {
        get
        {
            float ppu = MathF.Max(0.0001f, PixelsPerUnit);
            return new Vector2(CanvasSize.X / ppu, CanvasSize.Y / ppu);
        }
    }

    /// <summary>
    /// The plane this canvas occupies, or null when it is not in the world or there is no
    /// camera to face.
    /// </summary>
    public UiWorld.Basis? WorldBasis()
    {
        if (Space != UiSpace.World) return null;
        if (Rendering.Camera3D.Main is not { } camera) return null;

        return UiWorld.Build(
            WorldAnchor,
            Facing,
            Transform3D is { } t ? t.Rotation : Quaternion.Identity,
            camera.GetViewMatrix(),
            camera.GetTransform3D().Position,
            WorldSize);
    }

    /// <summary>Whether a world canvas is close enough to the camera to be worth drawing.</summary>
    public bool IsWithinDrawDistance
    {
        get
        {
            if (MaxDrawDistance <= 0f) return true;
            if (Rendering.Camera3D.Main is not { } camera) return true;

            return Vector3.DistanceSquared(camera.GetTransform3D().Position, WorldAnchor)
                 <= MaxDrawDistance * MaxDrawDistance;
        }
    }

    /// <summary>
    /// The actor's 3D transform when it has one. Read through a null guard, because the
    /// canvas behind the script <c>UI</c> global has no actor at all.
    /// </summary>
    private Transform3D? Transform3D => Actor?.GetComponent<Transform3D>();

    /// <summary>The texture a world canvas is painted into, once it has been painted.</summary>
    public RenderTarget2D? WorldTarget { get; private set; }

    /// <summary>
    /// Makes sure the world texture exists and is the size the canvas was authored at.
    /// </summary>
    /// <remarks>
    /// Colour only: the UI paints with depth testing off and always has, so a depth buffer
    /// here would be a megabyte a canvas bought for nothing. Occlusion against the scene is
    /// the quad's job, not the texture's.
    /// </remarks>
    internal RenderTarget2D? EnsureWorldTarget(GraphicsDevice gd)
    {
        int width  = (int)MathF.Round(CanvasSize.X);
        int height = (int)MathF.Round(CanvasSize.Y);

        if (width <= 0 || height <= 0) return null;

        if (WorldTarget is { } existing && existing.Width == width && existing.Height == height)
            return existing;

        WorldTarget?.Dispose();
        WorldTarget = new RenderTarget2D(gd, width, height, false, SurfaceFormat.Color, DepthFormat.None);
        return WorldTarget;
    }

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
        ResolveWorldFollowers();
        if (!Root.MeasureDirty && !Root.ArrangeDirty) return;

        RectangleF area = SafeRect;
        UiLayout.Measure(Root, new Vector2(area.Width, area.Height));
        UiLayout.Arrange(Root, area, new RectangleF(0f, 0f, CanvasSize.X, CanvasSize.Y));
    }

    /// <summary>
    /// Moves every following node to wherever its world anchor is on screen this frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs before the dirty check rather than after it, because a node that follows a
    /// moving actor is dirty by definition and would otherwise be laid out once and left.
    /// A canvas with no followers pays one boolean.
    /// </para>
    /// <para>
    /// Nodes behind the camera are hidden rather than placed. The perspective divide flips
    /// a point behind the viewer to the opposite side of the screen, so projecting without
    /// the check would draw a marker for the thing standing behind you — which is why
    /// <c>Camera3D.WorldToScreen</c> returns null there instead of a coordinate.
    /// </para>
    /// </remarks>
    private void ResolveWorldFollowers()
    {
        if (!_hasWorldFollowers) return;

        Rendering.Camera3D? camera = Rendering.Camera3D.Main;
        Vector3 eye = camera?.GetTransform3D().Position ?? Vector3.Zero;

        foreach (UiNode node in Root.Descendants())
        {
            if (!node.WorldFollow) continue;

            node.Positioning = PositionMode.Absolute;

            if (camera == null)
            {
                node.Visible = false;
                continue;
            }

            if (node.WorldFollowDistance > 0f &&
                Vector3.DistanceSquared(eye, node.WorldAnchor) > node.WorldFollowDistance * node.WorldFollowDistance)
            {
                node.Visible = false;
                continue;
            }

            if (camera.WorldToScreen(node.WorldAnchor, _viewport.X, _viewport.Y) is not { } screen)
            {
                node.Visible = false;
                continue;
            }

            node.Visible = true;
            node.Offset  = ScreenToCanvas(screen);
        }
    }

    /// <summary>
    /// Whether any node in this tree has ever asked to follow a world point.
    /// </summary>
    /// <remarks>
    /// Latched rather than counted. A count would have to be kept correct across every add,
    /// remove, reparent and property write in the tree, and getting that wrong shows up as a
    /// marker that silently stops moving; a latch can only ever cost a walk that finds
    /// nothing, and only on a canvas that used the feature at least once.
    /// </remarks>
    private bool _hasWorldFollowers;

    /// <summary>Tells this canvas one of its nodes wants to follow a world point.</summary>
    internal void NoteWorldFollower() => _hasWorldFollowers = true;

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

        if (!File.Exists(path))
        {
            // Said out loud. A canvas that silently stays empty because a path is stale is
            // indistinguishable from a canvas whose layout is wrong, and the two are fixed in
            // completely different places.
            Console.Error.WriteLine($"[UiCanvas] document not found: {Document}");
            return;
        }

        try
        {
            Adopt(UiDocument.FromJson(File.ReadAllText(path)));
        }
        catch (UiDocumentException error)
        {
            Console.Error.WriteLine($"[UiCanvas] '{Document}' is not a valid UI document: {error.Message}");
        }
    }

    /// <summary>Moves a loaded document's children onto <see cref="Root"/>, replacing what was there.</summary>
    /// <summary>
    /// Properties of the root that belong to the canvas, not to the document it adopts.
    /// </summary>
    /// <remarks>
    /// A document's root is a box like any other and may well have been authored with a size
    /// and a position; the canvas root is neither, it is the whole surface. Copying a width
    /// onto it would shrink the UI to the size of whatever the author happened to be looking
    /// at when they saved. Everything else is copied — by walking the codec's own key list
    /// rather than a hand-written one, so a property added to the format is not quietly lost
    /// by a list nobody remembered to extend.
    /// </remarks>
    private static readonly HashSet<string> RootOwnedKeys = new()
    {
        "width", "height", "minwidth", "minheight", "maxwidth", "maxheight",
        "grow", "shrink", "margin", "positioning", "anchor", "anchormin", "anchormax",
        "pivot", "offset", "offsetmax", "worldfollow", "worldanchor", "worldfollowdistance",
    };

    public void Adopt(UiNode document)
    {
        ArgumentNullException.ThrowIfNull(document);

        for (int i = Root.Children.Count - 1; i >= 0; i--) Root.Remove(Root.Children[i]);

        foreach (string key in UiDocument.WritableKeys)
        {
            if (RootOwnedKeys.Contains(UiDocument.Canonical(key))) continue;
            UiDocument.Apply(Root, key, UiDocument.Read(document, key));
        }

        foreach (UiNode child in new List<UiNode>(document.Children)) Root.Add(child);
        InvalidateLayout();
    }
}
