using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.UI.Widgets;

/// <summary>Horizontal text alignment for <see cref="Label"/>.</summary>
public enum TextAlignment { Left, Center, Right }

/// <summary>
/// Displays a string of text. Supports alignment, optional word-wrap,
/// and auto-sizing when <see cref="Widget.Size"/> is <see cref="Vector2.Zero"/>.
/// </summary>
public class Label : Widget
{
    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------
    public string        Text      { get; set; } = "";
    public Color         TextColor { get; set; } = Color.White;
    public float         FontSize  { get; set; } = 16f;   // informational; SpriteFont is pre-baked
    public TextAlignment Alignment { get; set; } = TextAlignment.Left;
    public bool          WordWrap  { get; set; } = false;

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------
    public override void Draw(SpriteBatch sb, SpriteFont? font)
    {
        if (!Visible || string.IsNullOrEmpty(Text)) return;
        if (font == null) return; // No font available — skip silently

        var bounds  = Bounds;
        var color   = TextColor * Opacity;
        string displayText = WordWrap ? WrapText(font, Text, bounds.Width) : Text;

        Vector2 textSize = font.MeasureString(displayText);

        // Auto-size: if Size is zero, expand to fit the measured text
        if (Size == Vector2.Zero)
        {
            Size = textSize;
            bounds = Bounds; // recompute after size change
        }

        Vector2 drawPos = Alignment switch
        {
            TextAlignment.Center => new Vector2(
                bounds.X + (bounds.Width  - textSize.X) * 0.5f,
                bounds.Y + (bounds.Height - textSize.Y) * 0.5f),
            TextAlignment.Right  => new Vector2(
                bounds.X + bounds.Width  - textSize.X,
                bounds.Y + (bounds.Height - textSize.Y) * 0.5f),
            _                    => new Vector2(
                bounds.X,
                bounds.Y + (bounds.Height - textSize.Y) * 0.5f)
        };

        sb.DrawString(font, displayText, drawPos, color);

        // Draw children
        foreach (var child in Children)
            if (child.Visible)
                child.Draw(sb, font);
    }

    // -------------------------------------------------------------------------
    // Word-wrap helper
    // -------------------------------------------------------------------------
    private static string WrapText(SpriteFont font, string text, float maxWidth)
    {
        if (maxWidth <= 0) return text;

        var words  = text.Split(' ');
        var result = new System.Text.StringBuilder();
        float lineWidth = 0f;
        float spaceWidth = font.MeasureString(" ").X;

        foreach (var word in words)
        {
            float wordWidth = font.MeasureString(word).X;

            if (lineWidth > 0 && lineWidth + spaceWidth + wordWidth > maxWidth)
            {
                result.Append('\n');
                lineWidth = 0;
            }
            else if (lineWidth > 0)
            {
                result.Append(' ');
                lineWidth += spaceWidth;
            }

            result.Append(word);
            lineWidth += wordWidth;
        }

        return result.ToString();
    }
}
