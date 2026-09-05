using SexyBiscuit.Engine.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.UI.Widgets;

/// <summary>
/// A clipping container that scrolls its children, with drag-to-scroll momentum.
/// </summary>
/// <remarks>
/// Clipping uses the graphics device's scissor rectangle, which means the view has to
/// close and reopen the sprite batch around its contents. That costs a draw-call break
/// per scroll view — cheap for a handful of panels, worth knowing about if you plan on
/// dozens.
/// </remarks>
/// <example>
/// <code>
/// var list = new ScrollView { Size = new Vector2(320, 400) };
/// foreach (var item in inventory)
///     list.AddChild(new Label { Text = item.Name, Size = new Vector2(300, 24) });
/// list.LayoutVertically(gap: 4f);
/// </code>
/// </example>
public class ScrollView : Widget
{
    /// <summary>Current scroll offset in pixels. Positive scrolls content upward.</summary>
    public Vector2 ScrollOffset { get; set; }

    /// <summary>Total size of the content, computed from children by <see cref="MeasureContent"/>.</summary>
    public Vector2 ContentSize { get; private set; }

    /// <summary>Allows vertical scrolling.</summary>
    public bool ScrollVertical { get; set; } = true;

    /// <summary>Allows horizontal scrolling.</summary>
    public bool ScrollHorizontal { get; set; }

    /// <summary>Pixels scrolled per mouse-wheel notch.</summary>
    public float WheelStep { get; set; } = 48f;

    /// <summary>
    /// Fraction of velocity retained per second after a drag ends. 0 stops instantly,
    /// values near 1 glide for a long time.
    /// </summary>
    public float MomentumDamping { get; set; } = 0.06f;

    /// <summary>Draws a scrollbar track and thumb on the right edge.</summary>
    public bool ShowScrollbar { get; set; } = true;

    public Color BackgroundColor { get; set; } = new(24, 26, 32, 200);
    public Color ScrollbarColor  { get; set; } = new(120, 124, 138, 180);

    private Vector2 _velocity;
    private Vector2 _lastDragPosition;
    private bool    _dragging;

    public ScrollView() => Size = new Vector2(300, 240);

    /// <summary>Stacks children vertically and recomputes the content size.</summary>
    public void LayoutVertically(float gap = 4f, float padding = 4f)
    {
        float cursor = padding;
        foreach (var child in Children)
        {
            if (!child.Visible) continue;
            child.Position = new Vector2(padding, cursor);
            cursor += child.Size.Y + gap;
        }
        MeasureContent();
    }

    /// <summary>Recomputes <see cref="ContentSize"/> from the children's extents.</summary>
    public void MeasureContent()
    {
        float w = 0f, h = 0f;
        foreach (var child in Children)
        {
            if (!child.Visible) continue;
            w = MathF.Max(w, child.Position.X + child.Size.X);
            h = MathF.Max(h, child.Position.Y + child.Size.Y);
        }
        ContentSize = new Vector2(w, h);
    }

    /// <summary>Scrolls to the top.</summary>
    public void ScrollToTop() => ScrollOffset = Vector2.Zero;

    /// <summary>Scrolls to the bottom of the content.</summary>
    public void ScrollToBottom() => ScrollOffset = new Vector2(ScrollOffset.X, MaxScroll.Y);

    private Vector2 MaxScroll => new(
        MathF.Max(0f, ContentSize.X - Size.X),
        MathF.Max(0f, ContentSize.Y - Size.Y));

    // -------------------------------------------------------------------------
    // Input
    // -------------------------------------------------------------------------

    public override void HandleInput(Vector2 mousePos, bool mouseDown, bool mouseJustPressed)
    {
        if (!Visible || !Interactable) return;

        bool over = ContainsPoint(mousePos);

        if (mouseJustPressed && over)
        {
            _dragging = true;
            _lastDragPosition = mousePos;
            _velocity = Vector2.Zero;
        }

        if (_dragging)
        {
            if (!mouseDown)
            {
                _dragging = false;
            }
            else
            {
                var delta = mousePos - _lastDragPosition;
                _lastDragPosition = mousePos;

                // Dragging down moves content down, which means scrolling up.
                ApplyScroll(-delta);
                _velocity = -delta;
            }
        }

        var wheel = EngineHost.Current?.Input.ScrollDelta ?? 0f;
        if (over && wheel != 0f) ApplyScroll(new Vector2(0f, -wheel * WheelStep));

        // Children are hit-tested in the scrolled coordinate space.
        var adjusted = mousePos + ScrollOffset;
        for (int i = Children.Count - 1; i >= 0; i--)
            Children[i].HandleInput(adjusted, mouseDown, mouseJustPressed);
    }

    private void ApplyScroll(Vector2 delta)
    {
        if (!ScrollHorizontal) delta.X = 0f;
        if (!ScrollVertical)   delta.Y = 0f;

        ScrollOffset = Vector2.Clamp(ScrollOffset + delta, Vector2.Zero, MaxScroll);
    }

    public override void Update(float dt)
    {
        if (!_dragging && _velocity.LengthSquared() > 0.01f)
        {
            ApplyScroll(_velocity * dt * 60f);

            // Framerate-independent decay, so momentum feels the same at 30 and 144 fps.
            _velocity *= MathF.Pow(MomentumDamping, dt);
        }

        base.Update(dt);
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------

    public override void Draw(SpriteBatch sb, SpriteFont? font)
    {
        if (!Visible) return;

        MeasureContent();
        var bounds = Bounds;

        FillRect(sb, bounds, BackgroundColor * Opacity);

        // Restart the batch with a scissor rectangle so children are clipped to the view.
        var gd = sb.GraphicsDevice;
        var previousScissor = gd.ScissorRectangle;
        var previousRaster  = gd.RasterizerState;

        sb.End();
        gd.ScissorRectangle = Rectangle.Intersect(bounds, gd.Viewport.Bounds);
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            null, new RasterizerState { ScissorTestEnable = true, CullMode = CullMode.None });

        foreach (var child in Children)
        {
            if (!child.Visible) continue;

            // Shift the child into view space for the duration of the draw.
            var original = child.Position;
            child.Position = original - ScrollOffset;
            child.Draw(sb, font);
            child.Position = original;
        }

        sb.End();
        gd.ScissorRectangle = previousScissor;
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            null, previousRaster);

        if (ShowScrollbar) DrawScrollbar(sb, bounds);
    }

    private void DrawScrollbar(SpriteBatch sb, Rectangle bounds)
    {
        float max = MaxScroll.Y;
        if (max <= 0f) return;

        const int width = 6;
        var track = new Rectangle(bounds.Right - width - 2, bounds.Y + 2, width, bounds.Height - 4);

        float visibleFraction = Size.Y / MathF.Max(ContentSize.Y, 1f);
        int thumbHeight = Math.Max(24, (int)(track.Height * visibleFraction));
        int thumbY = track.Y + (int)((track.Height - thumbHeight) * (ScrollOffset.Y / max));

        FillRect(sb, track, (ScrollbarColor * 0.3f) * Opacity);
        FillRect(sb, new Rectangle(track.X, thumbY, width, thumbHeight), ScrollbarColor * Opacity);
    }
}
