using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// Abstract base for all UI widgets. Handles layout anchoring, hit-testing,
/// input propagation, and a child hierarchy.
/// </summary>
public abstract class Widget
{
    // -------------------------------------------------------------------------
    // Identity / Visibility
    // -------------------------------------------------------------------------
    public string Name         { get; set; } = "";
    public bool   Visible      { get; set; } = true;
    public bool   Interactable { get; set; } = true;

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------
    /// <summary>Position relative to parent widget origin (or screen origin if root).</summary>
    public Vector2 Position     { get; set; }
    public Vector2 Size         { get; set; } = new(100, 30);
    public Color   Tint         { get; set; } = Color.White;
    public float   Opacity      { get; set; } = 1f;

    /// <summary>Normalised anchor corner within parent rect (0–1).</summary>
    public Vector2 AnchorMin    { get; set; } = new(0, 0);
    public Vector2 AnchorMax    { get; set; } = new(0, 0);
    /// <summary>Additional pixel offset applied after the anchor point is resolved.</summary>
    public Vector2 AnchorOffset { get; set; }

    // -------------------------------------------------------------------------
    // Hierarchy
    // -------------------------------------------------------------------------
    public Canvas? Canvas   { get; internal set; }
    public Widget? Parent   { get; internal set; }
    public List<Widget> Children { get; } = new();

    // -------------------------------------------------------------------------
    // Computed bounds
    // -------------------------------------------------------------------------
    /// <summary>
    /// Screen-space rectangle resolved from Position, Size, anchor relative to parent.
    /// When there is no parent the position is treated as screen-space directly.
    /// </summary>
    public Rectangle Bounds
    {
        get
        {
            Vector2 origin;
            if (Parent != null)
            {
                var pb = Parent.Bounds;
                // Resolve the anchor pivot inside the parent rectangle
                float anchorX = pb.X + pb.Width  * AnchorMin.X;
                float anchorY = pb.Y + pb.Height * AnchorMin.Y;
                origin = new Vector2(anchorX, anchorY) + AnchorOffset;
            }
            else
            {
                origin = AnchorOffset;
            }

            return new Rectangle(
                (int)(origin.X + Position.X),
                (int)(origin.Y + Position.Y),
                (int)Size.X,
                (int)Size.Y);
        }
    }

    // -------------------------------------------------------------------------
    // Input events
    // -------------------------------------------------------------------------
    public event Action? OnClick;
    public event Action? OnHoverEnter;
    public event Action? OnHoverExit;

    protected void RaiseClick() => OnClick?.Invoke();

    protected bool IsHovered { get; private set; }

    // -------------------------------------------------------------------------
    // Abstract / virtual lifecycle
    // -------------------------------------------------------------------------
    public abstract void Draw(SpriteBatch sb, SpriteFont? font);

    public virtual void Update(float dt)
    {
        foreach (var child in Children)
            child.Update(dt);
    }

    // -------------------------------------------------------------------------
    // Child management
    // -------------------------------------------------------------------------
    public void AddChild(Widget child)
    {
        if (child.Parent != null)
            child.Parent.RemoveChild(child);

        child.Parent = this;
        child.Canvas = Canvas;
        Children.Add(child);
    }

    public void RemoveChild(Widget child)
    {
        if (Children.Remove(child))
        {
            child.Parent = null;
            child.Canvas = null;
        }
    }

    // -------------------------------------------------------------------------
    // Hit testing
    // -------------------------------------------------------------------------
    public bool ContainsPoint(Vector2 screenPoint)
        => Bounds.Contains((int)screenPoint.X, (int)screenPoint.Y);

    // -------------------------------------------------------------------------
    // Input handling
    // -------------------------------------------------------------------------
    /// <summary>
    /// Default input handling: hover detection and click raising.
    /// Subclasses override to add drag, press-state, etc.
    /// </summary>
    public virtual void HandleInput(Vector2 mousePos, bool mouseDown, bool mouseJustPressed)
    {
        if (!Visible || !Interactable) return;

        bool over = ContainsPoint(mousePos);

        if (over && !IsHovered)
        {
            IsHovered = true;
            OnHoverEnter?.Invoke();
        }
        else if (!over && IsHovered)
        {
            IsHovered = false;
            OnHoverExit?.Invoke();
        }

        if (over && mouseJustPressed)
            RaiseClick();

        // Propagate to children (deepest first so top-most child gets priority)
        for (int i = Children.Count - 1; i >= 0; i--)
            Children[i].HandleInput(mousePos, mouseDown, mouseJustPressed);
    }

    // -------------------------------------------------------------------------
    // Drawing helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// A shared 1x1 white texture, created on first use from the batch's device.
    /// </summary>
    /// <remarks>
    /// Lets a widget draw a solid fill without the caller supplying a texture, so a
    /// freshly constructed widget is visible rather than invisible. Cached per device
    /// because the device can change when the window is recreated.
    /// </remarks>
    public static Texture2D GetPixel(SpriteBatch sb)
    {
        if (_pixel is { IsDisposed: false } && ReferenceEquals(_pixelDevice, sb.GraphicsDevice))
            return _pixel;

        _pixel = new Texture2D(sb.GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _pixelDevice = sb.GraphicsDevice;
        return _pixel;
    }

    private static Texture2D?     _pixel;
    private static GraphicsDevice? _pixelDevice;

    /// <summary>Fills a rectangle with a solid colour.</summary>
    protected static void FillRect(SpriteBatch sb, Rectangle rect, Color color)
        => sb.Draw(GetPixel(sb), rect, color);

    /// <summary>Draws a rectangle outline of the given thickness, inset within the rectangle.</summary>
    protected static void StrokeRect(SpriteBatch sb, Rectangle rect, Color color, int thickness = 1)
    {
        if (thickness <= 0) return;
        var px = GetPixel(sb);

        sb.Draw(px, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
        sb.Draw(px, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
        sb.Draw(px, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
        sb.Draw(px, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
    }

    /// <summary>Draws text left-aligned and vertically centred within a rectangle.</summary>
    protected static void DrawTextInRect(SpriteBatch sb, SpriteFont? font, string text,
                                         Rectangle rect, Color color, float padding = 4f)
    {
        if (font == null || string.IsNullOrEmpty(text)) return;

        var size = font.MeasureString(text);
        var position = new Vector2(rect.X + padding, rect.Y + (rect.Height - size.Y) * 0.5f);
        sb.DrawString(font, text, position, color);
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------
    /// <summary>Effective tint multiplied by opacity alpha.</summary>
    protected Color EffectiveColor => Tint * Opacity;

    public override string ToString() => $"{GetType().Name}[{Name}]";
}
