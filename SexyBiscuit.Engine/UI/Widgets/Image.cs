using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.UI.Widgets;

/// <summary>
/// Draws a <see cref="Texture2D"/> stretched to fill its bounds.
/// Supports 9-patch (slice) rendering for scalable UI panels.
/// </summary>
public class Image : Widget
{
    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------
    public Texture2D? Texture       { get; set; }
    public new Color  Tint          { get; set; } = Color.White;
    public bool       IsNinePatch   { get; set; } = false;
    /// <summary>Uniform border in pixels used for 9-patch slicing.</summary>
    public int        NinePatchBorder { get; set; } = 8;

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------
    public override void Draw(SpriteBatch sb, SpriteFont? font)
    {
        if (!Visible) return;

        if (Texture != null)
        {
            Color color = Tint * Opacity;
            var   dest  = Bounds;

            if (IsNinePatch)
                DrawNinePatch(sb, Texture, dest, NinePatchBorder, color);
            else
                sb.Draw(Texture, dest, color);
        }

        foreach (var child in Children)
            if (child.Visible)
                child.Draw(sb, font);
    }

    // -------------------------------------------------------------------------
    // 9-patch helper (static so other widgets can reuse it)
    // -------------------------------------------------------------------------
    /// <summary>
    /// Draws a texture as a 9-patch (9-slice) to fill <paramref name="dest"/>.
    /// The <paramref name="border"/> value is the uniform corner/edge size in
    /// source-texture pixels.
    /// </summary>
    internal static void DrawNinePatch(
        SpriteBatch sb,
        Texture2D   texture,
        Rectangle   dest,
        int         border,
        Color       color)
    {
        int tw = texture.Width;
        int th = texture.Height;
        int b  = border;

        // Clamp border so it never exceeds half the texture or destination size
        b = Math.Min(b, Math.Min(tw / 2, th / 2));
        b = Math.Min(b, Math.Min(dest.Width / 2, dest.Height / 2));

        if (b <= 0)
        {
            sb.Draw(texture, dest, color);
            return;
        }

        int midSrcW = tw - b * 2;
        int midSrcH = th - b * 2;
        int midDstW = dest.Width  - b * 2;
        int midDstH = dest.Height - b * 2;

        // Source rectangles (3x3 grid)
        Rectangle srcTL  = new(0,       0,       b,       b);
        Rectangle srcTM  = new(b,       0,       midSrcW, b);
        Rectangle srcTR  = new(tw - b,  0,       b,       b);
        Rectangle srcML  = new(0,       b,       b,       midSrcH);
        Rectangle srcMM  = new(b,       b,       midSrcW, midSrcH);
        Rectangle srcMR  = new(tw - b,  b,       b,       midSrcH);
        Rectangle srcBL  = new(0,       th - b,  b,       b);
        Rectangle srcBM  = new(b,       th - b,  midSrcW, b);
        Rectangle srcBR  = new(tw - b,  th - b,  b,       b);

        // Destination rectangles
        Rectangle dstTL  = new(dest.X,               dest.Y,               b,       b);
        Rectangle dstTM  = new(dest.X + b,            dest.Y,               midDstW, b);
        Rectangle dstTR  = new(dest.X + dest.Width - b, dest.Y,            b,       b);
        Rectangle dstML  = new(dest.X,               dest.Y + b,           b,       midDstH);
        Rectangle dstMM  = new(dest.X + b,            dest.Y + b,           midDstW, midDstH);
        Rectangle dstMR  = new(dest.X + dest.Width - b, dest.Y + b,        b,       midDstH);
        Rectangle dstBL  = new(dest.X,               dest.Y + dest.Height - b, b,   b);
        Rectangle dstBM  = new(dest.X + b,            dest.Y + dest.Height - b, midDstW, b);
        Rectangle dstBR  = new(dest.X + dest.Width - b, dest.Y + dest.Height - b, b, b);

        sb.Draw(texture, dstTL, srcTL, color);
        sb.Draw(texture, dstTM, srcTM, color);
        sb.Draw(texture, dstTR, srcTR, color);
        sb.Draw(texture, dstML, srcML, color);
        sb.Draw(texture, dstMM, srcMM, color);
        sb.Draw(texture, dstMR, srcMR, color);
        sb.Draw(texture, dstBL, srcBL, color);
        sb.Draw(texture, dstBM, srcBM, color);
        sb.Draw(texture, dstBR, srcBR, color);
    }
}
