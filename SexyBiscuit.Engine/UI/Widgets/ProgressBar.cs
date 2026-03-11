using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.UI.Widgets;

/// <summary>Direction in which the fill grows.</summary>
public enum FillDirection
{
    LeftToRight,
    RightToLeft,
    TopToBottom,
    BottomToTop
}

/// <summary>
/// Read-only progress bar. Draws a background and a fill rectangle
/// clipped to the current progress fraction.
/// </summary>
public class ProgressBar : Widget
{
    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------
    private float _value = 0f;

    public float Value
    {
        get => _value;
        set => _value = Math.Clamp(value, 0f, MaxValue);
    }

    public float         MaxValue         { get; set; } = 1f;
    public FillDirection Direction        { get; set; } = FillDirection.LeftToRight;
    public Color         BackgroundColor  { get; set; } = Color.DarkGray;
    public Color         FillColor        { get; set; } = Color.Green;
    public Texture2D?    BackgroundTexture { get; set; }
    public Texture2D?    FillTexture      { get; set; }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------
    public override void Draw(SpriteBatch sb, SpriteFont? font)
    {
        if (!Visible) return;

        var bounds   = Bounds;
        float frac   = MaxValue > 0f ? Math.Clamp(_value / MaxValue, 0f, 1f) : 0f;
        Color bgColor = BackgroundColor * Opacity;
        Color fgColor = FillColor       * Opacity;

        // Background
        if (BackgroundTexture != null)
            sb.Draw(BackgroundTexture, bounds, bgColor);

        // Fill rectangle
        Rectangle fillRect = ComputeFillRect(bounds, frac);

        if (fillRect.Width > 0 && fillRect.Height > 0)
        {
            if (FillTexture != null)
            {
                // Clip the source UV so the fill texture doesn't stretch
                Rectangle srcRect = ComputeFillSourceRect(FillTexture, bounds, frac);
                sb.Draw(FillTexture, fillRect, srcRect, fgColor);
            }
        }

        foreach (var child in Children)
            if (child.Visible)
                child.Draw(sb, font);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private Rectangle ComputeFillRect(Rectangle bounds, float frac)
    {
        int w, h;
        switch (Direction)
        {
            case FillDirection.LeftToRight:
                return new Rectangle(bounds.X, bounds.Y, (int)(bounds.Width * frac), bounds.Height);
            case FillDirection.RightToLeft:
                w = (int)(bounds.Width * frac);
                return new Rectangle(bounds.Right - w, bounds.Y, w, bounds.Height);
            case FillDirection.TopToBottom:
                return new Rectangle(bounds.X, bounds.Y, bounds.Width, (int)(bounds.Height * frac));
            case FillDirection.BottomToTop:
                h = (int)(bounds.Height * frac);
                return new Rectangle(bounds.X, bounds.Bottom - h, bounds.Width, h);
            default:
                return Rectangle.Empty;
        }
    }

    private static Rectangle ComputeFillSourceRect(Texture2D tex, Rectangle bounds, float frac)
    {
        // Map the visible portion of the fill rect back into UV space of the texture
        return new Rectangle(0, 0, tex.Width, tex.Height);
    }
}
