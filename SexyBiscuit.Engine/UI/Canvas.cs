using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.UI;

/// <summary>Determines how the Canvas maps its reference resolution to the physical viewport.</summary>
public enum CanvasScaleMode
{
    /// <summary>No scaling — one canvas pixel equals one screen pixel.</summary>
    PixelPerfect,
    /// <summary>Scale each axis independently to fill the viewport.</summary>
    ScaleWithScreen,
    /// <summary>Uniform scale using the smaller of the two axes so content is never clipped.</summary>
    ConstantSize
}

/// <summary>
/// Root UI container. Attach as a Component to an Actor.
/// Drives widget layout, input dispatch, and drawing.
/// </summary>
public class Canvas : Component
{
    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------
    public CanvasScaleMode ScaleMode          { get; set; } = CanvasScaleMode.ScaleWithScreen;
    public Vector2         ReferenceResolution { get; set; } = new(1920, 1080);

    /// <summary>Optional font used by child Label / Button widgets.</summary>
    public SpriteFont? Font { get; set; }

    // -------------------------------------------------------------------------
    // Children
    // -------------------------------------------------------------------------
    public List<Widget> Children { get; } = new();

    // -------------------------------------------------------------------------
    // Input state (tracked across frames)
    // -------------------------------------------------------------------------
    private MouseState _prevMouseState;

    // -------------------------------------------------------------------------
    // Child management
    // -------------------------------------------------------------------------
    public void AddWidget(Widget w)
    {
        w.Canvas = this;
        w.Parent = null;
        Children.Add(w);
    }

    public void RemoveWidget(Widget w)
    {
        if (Children.Remove(w))
            w.Canvas = null;
    }

    /// <summary>Creates, registers, and returns a new widget of type T.</summary>
    public T AddWidget<T>() where T : Widget, new()
    {
        var w = new T();
        AddWidget(w);
        return w;
    }

    // -------------------------------------------------------------------------
    // Scale matrix
    // -------------------------------------------------------------------------
    /// <summary>
    /// Returns the transform matrix to pass to <c>SpriteBatch.Begin</c> so that
    /// widgets authored at <see cref="ReferenceResolution"/> render correctly on
    /// the actual viewport.
    /// </summary>
    public Matrix GetScaleMatrix(GraphicsDevice gd)
    {
        var vp = gd.Viewport;

        switch (ScaleMode)
        {
            case CanvasScaleMode.PixelPerfect:
                return Matrix.Identity;

            case CanvasScaleMode.ScaleWithScreen:
            {
                float sx = vp.Width  / ReferenceResolution.X;
                float sy = vp.Height / ReferenceResolution.Y;
                return Matrix.CreateScale(sx, sy, 1f);
            }

            case CanvasScaleMode.ConstantSize:
            {
                float s = MathF.Min(
                    vp.Width  / ReferenceResolution.X,
                    vp.Height / ReferenceResolution.Y);
                return Matrix.CreateScale(s, s, 1f);
            }

            default:
                return Matrix.Identity;
        }
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------
    public override void Update(float dt)
    {
        if (!Enabled) return;

        // Gather input
        var mouse        = Mouse.GetState();
        var mousePos     = new Vector2(mouse.X, mouse.Y);
        bool mouseDown   = mouse.LeftButton == ButtonState.Pressed;
        bool mouseJust   = mouse.LeftButton  == ButtonState.Pressed
                        && _prevMouseState.LeftButton == ButtonState.Released;

        // Propagate to widgets (top-most / last drawn = highest priority)
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            var w = Children[i];
            if (w.Visible) w.HandleInput(mousePos, mouseDown, mouseJust);
            w.Update(dt);
        }

        _prevMouseState = mouse;
    }

    public override void Draw(SpriteBatch sb)
    {
        if (!Enabled) return;

        foreach (var w in Children)
            if (w.Visible)
                w.Draw(sb, Font);
    }
}
