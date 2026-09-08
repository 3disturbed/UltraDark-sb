using System.Globalization;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// Reads and writes a UI tree as JSON. One codec for three authors: the object literal a
/// script passes to <c>UI.build</c>, a <c>.ui</c> file on disk, and the block a scene file
/// stores inside a <see cref="UiCanvas"/>.
/// </summary>
/// <remarks>
/// <para>
/// Keys are matched case-insensitively, so a scene file's <c>"Anchor"</c> and a script's
/// <c>anchor</c> land on the same property. That is the same courtesy the scene serialiser
/// already extends to component properties.
/// </para>
/// <para>
/// An unrecognised key is an error, not a shrug. The flat UI this replaces ended its options
/// switch with <c>default: break;</c>, so a typo did nothing and said nothing; in a tree a
/// mistyped <c>"childern"</c> would silently drop everything below it.
/// </para>
/// </remarks>
public static class UiDocument
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // =========================================================================
    // Reading
    // =========================================================================

    /// <summary>Builds a tree from a JSON document.</summary>
    public static UiNode FromJson(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        return FromElement(doc.RootElement);
    }

    /// <summary>Builds a tree from an already-parsed object.</summary>
    public static UiNode FromElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new UiDocumentException($"A UI node must be an object, not {element.ValueKind}.");

        var node = new UiNode();

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (Matches(property.Name, "children"))
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                    throw new UiDocumentException("\"children\" must be an array.");

                foreach (JsonElement child in property.Value.EnumerateArray())
                    node.Add(FromElement(child));

                continue;
            }

            Apply(node, property.Name, property.Value);
        }

        return node;
    }

    /// <summary>
    /// Writes one property onto a node, expanding the shorthand forms.
    /// </summary>
    /// <exception cref="UiDocumentException">The key is not one a node has.</exception>
    public static void Apply(UiNode node, string key, JsonElement value)
    {
        switch (Canonical(key))
        {
            // --- identity ---
            case "name":        node.Name = Str(value); break;
            case "kind":
            case "type":        node.Kind = ParseEnum<UiKind>(value, key); break;
            case "visible":     node.Visible = Bool(value); break;
            case "interactive": node.Interactive = Bool(value); break;
            case "order":       node.Order = (int)Num(value); break;
            case "style":       node.Style = Str(value); break;

            // --- size, with the "auto" / "50%" / "*" shorthand ---
            case "width":  ApplySize(value, out SizeMode wm, out float w); node.WidthMode = wm; node.Width = w; break;
            case "height": ApplySize(value, out SizeMode hm, out float h); node.HeightMode = hm; node.Height = h; break;
            case "size":
            {
                Vector2 s = Vec2(value, key);
                node.WidthMode = SizeMode.Fixed;  node.Width  = s.X;
                node.HeightMode = SizeMode.Fixed; node.Height = s.Y;
                break;
            }
            case "widthmode":  node.WidthMode  = ParseEnum<SizeMode>(value, key); break;
            case "heightmode": node.HeightMode = ParseEnum<SizeMode>(value, key); break;

            case "minwidth":  node.MinWidth  = Num(value); break;
            case "minheight": node.MinHeight = Num(value); break;
            case "maxwidth":  node.MaxWidth  = Num(value); break;
            case "maxheight": node.MaxHeight = Num(value); break;
            case "grow":      node.Grow      = Num(value); break;
            case "shrink":    node.Shrink    = Num(value); break;

            case "padding": node.Padding = Edges(value, key); break;
            case "margin":  node.Margin  = Edges(value, key); break;

            // --- container ---
            case "layout":     node.Layout     = ParseEnum<LayoutMode>(value, key); break;
            case "gap":        node.Gap        = Pair(value, key); break;
            case "wrap":       node.Wrap       = Bool(value); break;
            case "mainalign":  node.MainAlign  = ParseEnum<AlignMode>(value, key); break;
            case "crossalign": node.CrossAlign = ParseEnum<AlignMode>(value, key); break;
            case "columns":    node.Columns    = (int)Num(value); break;
            case "cellsize":   node.CellSize   = Vec2(value, key); break;

            // --- placement ---
            case "positioning": node.Positioning = ParseEnum<PositionMode>(value, key); break;
            case "absolute":    node.Positioning = Bool(value) ? PositionMode.Absolute : PositionMode.Layout; break;
            case "anchor":      node.Anchor      = ParseEnum<UiAnchor>(value, key); break;
            case "anchormin":   node.AnchorMin   = Vec2(value, key); break;
            case "anchormax":   node.AnchorMax   = Vec2(value, key); break;
            case "pivot":       node.Pivot       = Vec2(value, key); break;
            case "offset":      node.Offset      = Vec2(value, key); break;
            case "offsetmax":   node.OffsetMax   = Vec2(value, key); break;

            // `x` and `y` are how every existing script positions an element.
            case "x": node.Offset = new Vector2(Num(value), node.Offset.Y); node.Positioning = PositionMode.Absolute; break;
            case "y": node.Offset = new Vector2(node.Offset.X, Num(value)); node.Positioning = PositionMode.Absolute; break;

            // --- text ---
            case "text":          node.Text          = Str(value); break;
            case "scale":
            case "textscale":     node.TextScale     = Num(value); break;
            case "align":
            case "textalign":     node.TextAlign     = ParseEnum<AlignMode>(value, key); break;
            case "verticalalign": node.VerticalAlign = ParseEnum<AlignMode>(value, key); break;
            case "wraptext":      node.WrapText      = Bool(value); break;
            case "linespacing":   node.LineSpacing   = Num(value); break;

            // --- paint ---
            case "background":  node.Background   = Colour(value, key); break;
            case "tint":        node.Tint         = Colour(value, key) ?? Color.White; break;
            case "opacity":     node.Opacity      = Num(value); break;
            case "bordercolour":
            case "bordercolor": node.BorderColour = Colour(value, key); break;
            case "borderwidth": node.BorderWidth  = Num(value); break;
            case "texturepath": node.TexturePath  = Str(value); break;
            case "sourcerect":  node.SourceRect   = Edges(value, key); break;
            case "ninepatch":   node.NinePatch    = Edges(value, key); break;

            // --- clipping and scrolling ---
            case "clip":           node.Clip           = Bool(value); break;
            case "scroll":         node.Scroll         = ParseEnum<ScrollMode>(value, key); break;
            case "scrolloffset":   node.ScrollOffset   = Vec2(value, key); break;
            case "ignoresafearea": node.IgnoreSafeArea = Bool(value); break;

            // --- focus and navigation ---
            case "focusable": node.Focusable = ParseEnum<Focusability>(value, key); break;
            case "modal":     node.Modal     = Bool(value); break;
            case "navup":     node.NavUp     = Str(value); break;
            case "navdown":   node.NavDown   = Str(value); break;
            case "navleft":   node.NavLeft   = Str(value); break;
            case "navright":  node.NavRight  = Str(value); break;
            case "autofocus": node.AutoFocus = Bool(value); break;

            // --- payload ---
            case "value":         node.Value         = Num(value); break;
            case "minvalue":      node.MinValue      = Num(value); break;
            case "maxvalue":      node.MaxValue      = Num(value); break;
            case "step":          node.Step          = Num(value); break;
            case "checked":       node.Checked       = Bool(value); break;
            case "selectedindex": node.SelectedIndex = (int)Num(value); break;
            case "options":
                node.Options.Clear();
                foreach (JsonElement option in value.EnumerateArray()) node.Options.Add(Str(option));
                break;

            default:
                throw new UiDocumentException(
                    $"\"{key}\" is not a UI node property. A typo here would otherwise be silent.");
        }
    }

    // =========================================================================
    // Value shapes
    // =========================================================================

    /// <summary>
    /// A size is a number of pixels, or one of the three words: <c>"auto"</c> sizes to
    /// content, <c>"*"</c> fills what the parent hands out, and <c>"50%"</c> takes a share.
    /// </summary>
    private static void ApplySize(JsonElement value, out SizeMode mode, out float size)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            mode = SizeMode.Fixed;
            size = (float)value.GetDouble();
            return;
        }

        string text = Str(value).Trim();

        if (text.Equals("auto", StringComparison.OrdinalIgnoreCase)) { mode = SizeMode.Auto;    size = 0f; return; }
        if (text is "*" or "stretch" or "fill")                      { mode = SizeMode.Stretch; size = 0f; return; }

        if (text.EndsWith('%') &&
            float.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out float percent))
        {
            mode = SizeMode.Percent;
            size = percent / 100f;
            return;
        }

        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float pixels))
        {
            mode = SizeMode.Fixed;
            size = pixels;
            return;
        }

        throw new UiDocumentException($"\"{text}\" is not a size. Use a number, \"auto\", \"*\" or a percentage.");
    }

    /// <summary>One number for all four edges, two for the axes, or four as left, top, right, bottom.</summary>
    private static Vector4 Edges(JsonElement value, string key)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            float all = (float)value.GetDouble();
            return new Vector4(all, all, all, all);
        }

        if (value.ValueKind != JsonValueKind.Array)
            throw new UiDocumentException($"\"{key}\" must be a number or an array of 2 or 4 numbers.");

        float[] parts = Numbers(value);
        return parts.Length switch
        {
            1 => new Vector4(parts[0], parts[0], parts[0], parts[0]),
            2 => new Vector4(parts[0], parts[1], parts[0], parts[1]),
            4 => new Vector4(parts[0], parts[1], parts[2], parts[3]),
            _ => throw new UiDocumentException($"\"{key}\" needs 1, 2 or 4 numbers, not {parts.Length}."),
        };
    }

    /// <summary>One number meaning both axes, or two.</summary>
    private static Vector2 Pair(JsonElement value, string key)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            float both = (float)value.GetDouble();
            return new Vector2(both, both);
        }
        return Vec2(value, key);
    }

    private static Vector2 Vec2(JsonElement value, string key)
    {
        float[] parts = Numbers(value);
        if (parts.Length != 2) throw new UiDocumentException($"\"{key}\" needs two numbers.");
        return new Vector2(parts[0], parts[1]);
    }

    private static float[] Numbers(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new UiDocumentException("Expected an array of numbers.");

        var list = new List<float>();
        foreach (JsonElement item in value.EnumerateArray()) list.Add((float)item.GetDouble());
        return list.ToArray();
    }

    /// <summary>
    /// The colour forms a scene file already accepts: hex with or without alpha, an
    /// [r,g,b] or [r,g,b,a] array, or an object with R/G/B/A. Null means "draw nothing",
    /// which is not the same as black.
    /// </summary>
    public static Color? Colour(JsonElement value, string key)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;

            case JsonValueKind.String:
                return ParseHex(value.GetString(), key);

            case JsonValueKind.Array:
            {
                float[] c = Numbers(value);
                if (c.Length is not (3 or 4)) throw new UiDocumentException($"\"{key}\" needs 3 or 4 channels.");
                return new Color((int)c[0], (int)c[1], (int)c[2], c.Length == 4 ? (int)c[3] : 255);
            }

            case JsonValueKind.Object:
            {
                int r = Channel(value, "R"), g = Channel(value, "G"), b = Channel(value, "B");
                int a = value.TryGetProperty("A", out JsonElement av) || value.TryGetProperty("a", out av)
                    ? (int)av.GetDouble() : 255;
                return new Color(r, g, b, a);
            }

            default:
                throw new UiDocumentException($"\"{key}\" is not a colour.");
        }
    }

    private static int Channel(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out JsonElement v) ||
            obj.TryGetProperty(name.ToLowerInvariant(), out v))
            return (int)v.GetDouble();
        return 0;
    }

    /// <summary>
    /// Parses a hex colour written the way a document writes one, or null.
    /// </summary>
    /// <remarks>
    /// The same parser the codec uses, exposed for callers that already hold a string
    /// rather than a <c>JsonElement</c> — the graphics menu's theme, for one. Two hex
    /// parsers would be two places for "#rgb" to mean slightly different things.
    /// </remarks>
    public static Color? ParseColour(string? hex) => ParseHex(hex, "colour");

    private static Color? ParseHex(string? hex, string key)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;

        string body = hex.TrimStart('#');
        if (body.Length == 3)
            body = string.Concat(body[0], body[0], body[1], body[1], body[2], body[2]);

        if (body.Length is not (6 or 8) ||
            !int.TryParse(body[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r) ||
            !int.TryParse(body[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g) ||
            !int.TryParse(body[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b))
            throw new UiDocumentException($"\"{hex}\" is not a colour for \"{key}\". Use #rgb, #rrggbb or #rrggbbaa.");

        int a = 255;
        if (body.Length == 8 &&
            !int.TryParse(body[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a))
            throw new UiDocumentException($"\"{hex}\" has an alpha that is not hex.");

        return new Color(r, g, b, a);
    }

    private static string Str(JsonElement v)
        => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();

    private static bool Bool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Number => v.GetDouble() != 0d,
        _                    => throw new UiDocumentException($"Expected true or false, not {v.ValueKind}."),
    };

    private static float Num(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => (float)v.GetDouble(),
        JsonValueKind.True   => 1f,
        JsonValueKind.False  => 0f,
        JsonValueKind.String when float.TryParse(v.GetString(), NumberStyles.Float,
                                                 CultureInfo.InvariantCulture, out float parsed) => parsed,
        _ => throw new UiDocumentException($"Expected a number, not {v.ValueKind}."),
    };

    /// <summary>
    /// Enum names are matched without case, hyphens or underscores, so "bottom-right",
    /// "bottomRight" and "BottomRight" are the same anchor.
    /// </summary>
    private static T ParseEnum<T>(JsonElement value, string key) where T : struct, Enum
    {
        string raw = Str(value);
        string wanted = Canonical(raw);

        foreach (T candidate in Enum.GetValues<T>())
            if (Canonical(candidate.ToString()) == wanted) return candidate;

        // "center" and "centre" are the same place, and the codebase writes both.
        if (wanted == "centre" && Enum.TryParse("Center", true, out T centre)) return centre;

        throw new UiDocumentException(
            $"\"{raw}\" is not a valid {typeof(T).Name} for \"{key}\". " +
            $"Expected one of: {string.Join(", ", Enum.GetNames<T>())}.");
    }

    private static bool Matches(string key, string name) => Canonical(key) == name;

    private static string Canonical(string key)
    {
        Span<char> buffer = stackalloc char[key.Length];
        int n = 0;
        foreach (char c in key)
        {
            if (c is '-' or '_' or ' ') continue;
            buffer[n++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..n]);
    }
}

/// <summary>Raised when a UI document says something a node cannot mean.</summary>
public sealed class UiDocumentException : Exception
{
    public UiDocumentException(string message) : base(message) { }
}
