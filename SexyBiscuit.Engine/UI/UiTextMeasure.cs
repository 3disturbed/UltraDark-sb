using System.Text;
using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// The one place the layout engine asks how big a piece of text is.
/// </summary>
/// <remarks>
/// A seam, on purpose. Today every answer comes from the shared 5x7 bitmap font, whose
/// glyph table both engines read from the same file so the two cannot disagree. When
/// scalable fonts land they land here, and the layout engine does not change.
/// A widget that measures text by asking a rasteriser instead is a parity bug.
/// </remarks>
public static class UiTextMeasure
{
    /// <summary>The widest line of a block, which is what centring has to measure.</summary>
    public static float Width(string? text, float scale)
        => BitmapFont.MeasureWidest(text, EffectiveScale(scale));

    /// <summary>The height of a block, counting its line breaks.</summary>
    public static float Height(string? text, float scale, float lineSpacing = 0f)
    {
        if (string.IsNullOrEmpty(text)) return 0f;

        float s = EffectiveScale(scale);
        float h = BitmapFont.MeasureHeight(text, s);
        int breaks = CountLines(text) - 1;
        return h + MathF.Max(0f, lineSpacing) * breaks;
    }

    /// <summary>
    /// Measures a block, wrapping it to a width first when asked.
    /// </summary>
    public static Vector2 Measure(string? text, float scale, bool wrap, float maxWidth, float lineSpacing = 0f)
    {
        if (string.IsNullOrEmpty(text)) return Vector2.Zero;

        string body = wrap && !float.IsPositiveInfinity(maxWidth) && maxWidth > 0f
            ? Wrap(text, scale, maxWidth)
            : text;

        return new Vector2(Width(body, scale), Height(body, scale, lineSpacing));
    }

    /// <summary>
    /// Greedily breaks a string to a maximum width, at spaces and after hyphens.
    /// </summary>
    /// <remarks>
    /// Deliberately not the Unicode line-breaking algorithm. A word longer than the
    /// line is left to overflow rather than being cut mid-word, because a truncated
    /// word reads as a bug and an overflowing one reads as a layout to fix.
    /// </remarks>
    public static string Wrap(string? text, float scale, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0f) return text ?? "";

        var output = new StringBuilder();
        string[] paragraphs = text.Split('\n');

        for (int p = 0; p < paragraphs.Length; p++)
        {
            if (p > 0) output.Append('\n');
            WrapParagraph(output, paragraphs[p], scale, maxWidth);
        }

        return output.ToString();
    }

    private static void WrapParagraph(StringBuilder output, string paragraph, float scale, float maxWidth)
    {
        int lineStart = 0;
        int lastBreak = -1;

        for (int i = 0; i < paragraph.Length; i++)
        {
            // A hyphen may be broken *after*; a space is consumed by the break.
            if (paragraph[i] == ' ' || paragraph[i] == '-') lastBreak = i;

            float width = Width(paragraph[lineStart..(i + 1)], scale);
            if (width <= maxWidth || lastBreak <= lineStart) continue;

            int cut = lastBreak;
            output.Append(paragraph[lineStart..cut].TrimEnd());
            output.Append('\n');

            lineStart = paragraph[cut] == ' ' ? cut + 1 : cut + 1;
            lastBreak = -1;
            i = lineStart - 1;
        }

        if (lineStart < paragraph.Length) output.Append(paragraph[lineStart..]);
    }

    /// <summary>The height of one line, which is what an empty text field still reserves.</summary>
    public static float LineHeight(float scale) => BitmapFont.MeasureHeight("A", EffectiveScale(scale));

    /// <summary>
    /// Bitmap glyphs only look right at whole multiples of their cell, so the scale is
    /// rounded and never allowed below one.
    /// </summary>
    private static float EffectiveScale(float scale) => MathF.Max(1f, MathF.Round(scale));

    private static int CountLines(string text)
    {
        int lines = 1;
        foreach (char c in text) if (c == '\n') lines++;
        return lines;
    }
}
