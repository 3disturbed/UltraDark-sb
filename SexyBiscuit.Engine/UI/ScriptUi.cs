using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.UI;

/// <summary>What an element is. The browser engine knows the same five kinds.</summary>
public enum UiKind { Panel, Label, Bar, Button, Image }

/// <summary>Where an element hangs from, and which of its own corners hangs there.</summary>
public enum UiAnchor
{
    TopLeft, Top, TopRight,
    Left, Center, Right,
    BottomLeft, Bottom, BottomRight,
}

/// <summary>One screen-space element. Plain data; the bridge hands a script a proxy over it.</summary>
public sealed class UiElement
{
    public UiKind   Kind        { get; init; }
    public float    X           { get; set; }
    public float    Y           { get; set; }
    public float    Width       { get; set; } = 100f;
    public float    Height      { get; set; } = 24f;
    public string   Text        { get; set; } = string.Empty;
    public float    Value       { get; set; } = 1f;
    public float    Scale       { get; set; } = 1f;
    public bool     Visible     { get; set; } = true;
    public UiAnchor Anchor      { get; set; } = UiAnchor.TopLeft;
    public Color    Tint        { get; set; } = Color.White;
    public Color?   Background  { get; set; }
    public string   TexturePath { get; set; } = string.Empty;
    public string   Align       { get; set; } = "left";
    public float    Padding     { get; set; } = 6f;

    public bool Hovered   { get; internal set; }
    public bool Clicked   { get; internal set; }
    public bool Destroyed { get; internal set; }
}

/// <summary>
/// Screen-space UI a game script can build, mirroring <c>html5/src/ui/UiCanvas.js</c>.
/// </summary>
/// <remarks>
/// <para>
/// The shared scripting contract had no UI and, worse, no viewport: a script could
/// not ask how wide the window was, so it could not put anything in a corner. Every
/// prototype grew its HUD out of world-space sprites floating over the player with
/// no text anywhere, because there was no other option.
/// </para>
/// <para>
/// Deliberately five kinds and no more. Every member here has to exist in the
/// browser too, and a member that exists on one engine only is precisely the defect
/// this repository keeps finding. The editor's richer widget set under
/// <c>UI/Widgets</c> is a separate thing and is not reachable from a script.
/// </para>
/// </remarks>
public sealed class ScriptUi
{
    /// <summary>The canvas the running game draws. One per process, like the 2D physics world.</summary>
    public static ScriptUi Instance { get; } = new();

    private readonly List<UiElement> _elements = new();

    private Vector2 _pointer = new(-1, -1);
    private bool _pointerDown;
    private bool _pointerWasDown;

    /// <summary>Viewport width in pixels. Kept in step with the back buffer by the host.</summary>
    public int Width { get; private set; } = 1280;

    /// <summary>Viewport height in pixels.</summary>
    public int Height { get; private set; } = 720;

    /// <summary>Every live element, in the order they were added.</summary>
    public IReadOnlyList<UiElement> Elements => _elements;

    public void SetViewport(int width, int height)
    {
        Width  = Math.Max(1, width);
        Height = Math.Max(1, height);
    }

    public UiElement Add(UiElement element)
    {
        _elements.Add(element);
        return element;
    }

    public void Remove(UiElement? element)
    {
        if (element == null) return;
        element.Destroyed = true;
        _elements.Remove(element);
    }

    /// <summary>Drops everything. A scene change must not leave the last scene's HUD behind.</summary>
    public void Clear()
    {
        foreach (UiElement element in _elements) element.Destroyed = true;
        _elements.Clear();
    }

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------

    /// <summary>The fraction of the viewport an anchor sits at.</summary>
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

    /// <summary>
    /// The element's screen rectangle. The anchor is both where on the screen it
    /// hangs from and which of its own corners hangs there, so
    /// <c>anchor "bottomright", x -12, y -12</c> sits twelve pixels in from the
    /// bottom-right at any window size.
    /// </summary>
    public RectangleF RectOf(UiElement element)
    {
        Vector2 a = AnchorFraction(element.Anchor);

        // A label with no size measures itself. Without this a right-anchored label
        // hangs its LEFT edge on the right border and runs off the screen, and a
        // centred one starts at the centre instead of straddling it — the caller
        // would have to measure the text by hand every time it changed.
        float scale = MathF.Max(1f, MathF.Round(element.Scale));
        float width = element.Width > 0f ? element.Width
            : element.Kind == UiKind.Label ? BitmapFont.MeasureWidest(element.Text, scale) : 0f;
        float height = element.Height > 0f ? element.Height
            : element.Kind == UiKind.Label ? BitmapFont.MeasureHeight(element.Text, scale) : 0f;

        return new RectangleF(
            Width  * a.X + element.X - width  * a.X,
            Height * a.Y + element.Y - height * a.Y,
            width,
            height);
    }

    // -------------------------------------------------------------------------
    // Frame
    // -------------------------------------------------------------------------

    /// <summary>Feeds this frame's pointer in. <paramref name="down"/> is held, not pressed.</summary>
    public void SetPointer(float x, float y, bool down)
    {
        _pointerWasDown = _pointerDown;
        _pointer = new Vector2(x, y);
        _pointerDown = down;
    }

    /// <summary>
    /// Resolves hover and click. A click is a release inside the element, which is
    /// what every other toolkit means by one.
    /// </summary>
    public void Update()
    {
        bool released = _pointerWasDown && !_pointerDown;

        foreach (UiElement element in _elements)
        {
            element.Clicked = false;
            if (!element.Visible || element.Kind != UiKind.Button) { element.Hovered = false; continue; }

            RectangleF r = RectOf(element);
            bool inside = _pointer.X >= r.X && _pointer.X <= r.X + r.Width
                       && _pointer.Y >= r.Y && _pointer.Y <= r.Y + r.Height;

            element.Hovered = inside;
            if (inside && released) element.Clicked = true;
        }

        // Consume the release. Without this a second Update() in the same frame —
        // or a frame the host does not feed a pointer into — sees the same release
        // again and fires the button twice.
        _pointerWasDown = _pointerDown;
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------

    /// <summary>Paints every visible element. Call inside a batch with no camera transform.</summary>
    public void Draw(SpriteBatch sb)
    {
        foreach (UiElement element in _elements)
        {
            if (!element.Visible) continue;
            RectangleF r = RectOf(element);

            switch (element.Kind)
            {
                case UiKind.Panel:
                    FillBox(sb, r, element.Background);
                    break;

                case UiKind.Button:
                    FillBox(sb, r, element.Hovered ? Lighten(element.Background) : element.Background);
                    DrawText(sb, element, r, "center");
                    break;

                case UiKind.Bar:
                    FillBox(sb, r, element.Background);
                    float fraction = Math.Clamp(element.Value, 0f, 1f);
                    FillBox(sb, new RectangleF(r.X, r.Y, r.Width * fraction, r.Height), element.Tint);
                    if (!string.IsNullOrEmpty(element.Text)) DrawText(sb, element, r, "center");
                    break;

                case UiKind.Label:
                    DrawText(sb, element, r, null);
                    break;

                case UiKind.Image:
                    // No art yet: a tinted box, the same answer SpriteRenderer gives.
                    FillBox(sb, r, element.Background);
                    break;
            }
        }
    }

    private static void FillBox(SpriteBatch sb, RectangleF r, Color? colour)
    {
        if (colour is not Color fill || r.Width <= 0f || r.Height <= 0f) return;

        sb.Draw(WhitePixel(sb.GraphicsDevice), new Vector2(r.X, r.Y), null, fill, 0f,
            Vector2.Zero, new Vector2(r.Width, r.Height), SpriteEffects.None, 0f);
    }

    private static void DrawText(SpriteBatch sb, UiElement element, RectangleF r, string? forceAlign)
    {
        if (string.IsNullOrEmpty(element.Text)) return;

        string align = forceAlign ?? element.Align;
        float scale = MathF.Max(1f, MathF.Round(element.Scale));

        float textWidth  = BitmapFont.MeasureWidest(element.Text, scale);
        float textHeight = BitmapFont.MeasureHeight(element.Text, scale);

        float x = r.X + element.Padding;
        if (align == "center") x = r.X + (r.Width - textWidth) / 2f;
        else if (align == "right") x = r.X + r.Width - textWidth - element.Padding;

        float y = element.Kind == UiKind.Label && r.Height <= textHeight
            ? r.Y
            : r.Y + (r.Height - textHeight) / 2f;

        BitmapFont.Draw(sb, element.Text, new Vector2(MathF.Round(x), MathF.Round(y)), element.Tint, scale);
    }

    /// <summary>The hover shade, matching the browser's `lighten`.</summary>
    private static Color? Lighten(Color? colour)
    {
        if (colour is not Color c) return null;
        static byte Up(byte v) => (byte)Math.Min(255, (int)Math.Round(v * 1.25) + 12);
        return new Color(Up(c.R), Up(c.G), Up(c.B), c.A);
    }

    private static Texture2D? _whitePixel;

    private static Texture2D WhitePixel(GraphicsDevice gd)
    {
        if (_whitePixel is null || _whitePixel.IsDisposed || _whitePixel.GraphicsDevice != gd)
        {
            _whitePixel = new Texture2D(gd, 1, 1);
            _whitePixel.SetData(new[] { Color.White });
        }
        return _whitePixel;
    }
}

/// <summary>A rectangle with float edges. XNA's Rectangle is integral and UI is not.</summary>
public readonly record struct RectangleF(float X, float Y, float Width, float Height);
