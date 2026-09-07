using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// Text without an asset pipeline: a 5x7 font stored as data and drawn as quads.
/// </summary>
/// <remarks>
/// <para>
/// The UI widgets have always been able to draw text and have never had a font to
/// draw it with — <c>Label.Draw</c> opens with <c>if (font == null) return;</c> and
/// there is no font asset anywhere in the repository, so every label rendered as
/// nothing, silently. Building a script-facing UI on that would have shipped the
/// same defect one layer up.
/// </para>
/// <para>
/// The glyphs come from <c>html5/src/ui/font5x7.json</c>, embedded in this
/// assembly. The browser engine imports that same file, so neither engine can
/// render text the other cannot — the drift this repository keeps catching between
/// its two halves is impossible here by construction.
/// </para>
/// </remarks>
public static class BitmapFont
{
    /// <summary>Glyph cell width, in unscaled pixels.</summary>
    public const int GlyphWidth = 5;

    /// <summary>Glyph cell height, in unscaled pixels.</summary>
    public const int GlyphHeight = 7;

    /// <summary>Gap between glyphs, in unscaled pixels. Matches the browser's LETTER_SPACING.</summary>
    public const int LetterSpacing = 1;

    /// <summary>Gap between lines, in unscaled pixels. Matches the browser's LINE_SPACING.</summary>
    public const int LineSpacing = 2;

    private static readonly Dictionary<char, string> Glyphs = LoadGlyphs();
    private static Texture2D? _pixel;

    // -------------------------------------------------------------------------
    // The shared table
    // -------------------------------------------------------------------------

    private static Dictionary<char, string> LoadGlyphs()
    {
        var table = new Dictionary<char, string>();
        try
        {
            using Stream? stream = typeof(BitmapFont).Assembly
                .GetManifestResourceStream("SexyBiscuit.Engine.font5x7.json");
            if (stream == null) return table;

            using var document = JsonDocument.Parse(stream);
            foreach (JsonProperty glyph in document.RootElement.GetProperty("glyphs").EnumerateObject())
            {
                if (glyph.Name.Length == 1)
                    table[glyph.Name[0]] = glyph.Value.GetString() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            // A missing or malformed font must not take the game down; text simply
            // does not draw, and the reason is on the console rather than nowhere.
            Console.Error.WriteLine($"[BitmapFont] could not load the shared glyph table: {ex.Message}");
        }
        return table;
    }

    /// <summary>True once the shared glyph table is loaded. False means text will not draw.</summary>
    public static bool IsLoaded => Glyphs.Count > 0;

    /// <summary>How many glyphs the shared table holds. The browser must agree.</summary>
    public static int GlyphCount => Glyphs.Count;

    // -------------------------------------------------------------------------
    // Measuring
    // -------------------------------------------------------------------------

    /// <summary>Width of a single line at <paramref name="scale"/>.</summary>
    public static float MeasureLine(string? text, float scale = 1f)
    {
        int length = text?.Length ?? 0;
        if (length == 0) return 0f;
        return (length * (GlyphWidth + LetterSpacing) - LetterSpacing) * scale;
    }

    /// <summary>Width of the widest line in a block, which is what centring needs.</summary>
    public static float MeasureWidest(string? text, float scale = 1f)
    {
        if (string.IsNullOrEmpty(text)) return 0f;

        float widest = 0f;
        foreach (string line in text.Split('\n')) widest = MathF.Max(widest, MeasureLine(line, scale));
        return widest;
    }

    /// <summary>Height of a block, counting newlines.</summary>
    public static float MeasureHeight(string? text, float scale = 1f)
    {
        int lines = string.IsNullOrEmpty(text) ? 1 : text.Split('\n').Length;
        return (lines * (GlyphHeight + LineSpacing) - LineSpacing) * scale;
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Draws text as one quad per lit pixel.
    /// </summary>
    /// <remarks>
    /// A quad per pixel sounds extravagant and is not: a HUD is a few hundred
    /// glyphs, they batch into one draw call, and it buys identical output on two
    /// engines with no font file, no atlas and no measuring to reconcile.
    /// </remarks>
    public static void Draw(SpriteBatch sb, string? text, Vector2 position, Color colour,
                            float scale = 1f, float layerDepth = 0f)
    {
        if (string.IsNullOrEmpty(text) || Glyphs.Count == 0) return;

        Texture2D pixel = Pixel(sb.GraphicsDevice);
        string[] lines = text.Split('\n');

        for (int row = 0; row < lines.Length; row++)
        {
            float lineY = position.Y + row * (GlyphHeight + LineSpacing) * scale;
            float penX = position.X;

            foreach (char character in lines[row])
            {
                if (Glyphs.TryGetValue(character, out string? mask))
                {
                    for (int gy = 0; gy < GlyphHeight; gy++)
                    {
                        for (int gx = 0; gx < GlyphWidth; gx++)
                        {
                            if (mask[gy * GlyphWidth + gx] != '#') continue;

                            sb.Draw(pixel,
                                new Vector2(penX + gx * scale, lineY + gy * scale),
                                null, colour, 0f, Vector2.Zero, scale, SpriteEffects.None, layerDepth);
                        }
                    }
                }
                penX += (GlyphWidth + LetterSpacing) * scale;
            }
        }
    }

    private static Texture2D Pixel(GraphicsDevice gd)
    {
        if (_pixel is null || _pixel.IsDisposed || _pixel.GraphicsDevice != gd)
        {
            _pixel = new Texture2D(gd, 1, 1);
            _pixel.SetData(new[] { Color.White });
        }
        return _pixel;
    }
}
