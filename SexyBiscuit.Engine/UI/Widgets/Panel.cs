using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.UI.Widgets;

/// <summary>Flow-layout mode for <see cref="Panel"/>.</summary>
public enum PanelLayoutMode
{
    /// <summary>Children are positioned manually; the Panel does not reflow them.</summary>
    None,
    /// <summary>Children are stacked vertically with <see cref="Panel.Padding"/> gap.</summary>
    Vertical,
    /// <summary>Children are stacked horizontally with <see cref="Panel.Padding"/> gap.</summary>
    Horizontal
}

/// <summary>
/// Container widget that draws a (optionally 9-patch) background and optionally
/// flows its children in a vertical or horizontal layout.
/// </summary>
public class Panel : Widget
{
    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------
    public Color      BackgroundColor   { get; set; } = new Color(0, 0, 0, 180);
    public Texture2D? BackgroundTexture { get; set; }
    public bool       IsNinePatch       { get; set; } = false;
    public int        NinePatchBorder   { get; set; } = 8;
    /// <summary>Padding around contents and gap between flow-layout children.</summary>
    public float      Padding           { get; set; } = 4f;
    public PanelLayoutMode LayoutMode   { get; set; } = PanelLayoutMode.None;

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------
    /// <summary>
    /// Repositions children according to <see cref="LayoutMode"/>.
    /// Called automatically before drawing. Safe to call manually after
    /// adding or resizing children.
    /// </summary>
    public void ApplyLayout()
    {
        if (LayoutMode == PanelLayoutMode.None) return;

        float cursor = Padding;

        foreach (var child in Children)
        {
            if (!child.Visible) continue;

            if (LayoutMode == PanelLayoutMode.Vertical)
            {
                child.Position = new Vector2(Padding, cursor);
                cursor += child.Size.Y + Padding;
            }
            else // Horizontal
            {
                child.Position = new Vector2(cursor, Padding);
                cursor += child.Size.X + Padding;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------
    public override void Draw(SpriteBatch sb, SpriteFont? font)
    {
        if (!Visible) return;

        ApplyLayout();

        var   bounds = Bounds;
        Color color  = BackgroundColor * Opacity;

        if (BackgroundTexture != null)
        {
            if (IsNinePatch)
                Image.DrawNinePatch(sb, BackgroundTexture, bounds, NinePatchBorder, color);
            else
                sb.Draw(BackgroundTexture, bounds, color);
        }

        foreach (var child in Children)
            if (child.Visible)
                child.Draw(sb, font);
    }
}
