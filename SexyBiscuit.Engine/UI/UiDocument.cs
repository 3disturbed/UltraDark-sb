using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <summary>
    /// Writes one property from a JSON node, which is the shape a script's value arrives in.
    /// </summary>
    /// <remarks>
    /// An overload rather than a second implementation: a bridge holds a <c>JsValue</c>, which
    /// <see cref="Scripting.JsValueConverter.ToJsonNode"/> turns into a node, and everything
    /// then goes through the one switch below. A parallel setter for scripts is exactly how the
    /// two engines would come to disagree about what a property means.
    /// </remarks>
    public static void Apply(UiNode node, string key, JsonNode? value)
        => Apply(node, key, JsonSerializer.SerializeToElement(value));

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
    // Reading — the mirror of Apply
    // =========================================================================

    /// <summary>
    /// Reads one property back off a node, in the same spelling <see cref="Apply(UiNode, string, JsonElement)"/> accepts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of the codec, and the reason a script handle does not need sixty-odd
    /// hand-written getters on each engine: a handle wires <c>get</c> here and <c>set</c> to
    /// <see cref="Apply(UiNode, string, JsonElement)"/>, so a property added to one switch reaches both engines' script API
    /// at once instead of needing five hand-mirrored edits.
    /// </para>
    /// <para>
    /// Structural members — <c>parent</c>, <c>children</c> and the functions — are not here.
    /// They hand back node handles rather than values, which only a bridge can build.
    /// </para>
    /// </remarks>
    /// <exception cref="UiDocumentException">The key is not one a node has.</exception>
    public static JsonNode? Read(UiNode node, string key)
    {
        switch (Canonical(key))
        {
            // --- identity ---
            case "name":        return node.Name;
            case "kind":
            case "type":        return node.Kind.ToString();
            case "visible":     return node.Visible;
            case "interactive": return node.Interactive;
            case "order":       return node.Order;
            case "style":       return node.Style;

            // --- size ---
            case "width":      return ReadSize(node.WidthMode, node.Width);
            case "height":     return ReadSize(node.HeightMode, node.Height);
            case "widthmode":  return node.WidthMode.ToString();
            case "heightmode": return node.HeightMode.ToString();
            case "minwidth":   return node.MinWidth;
            case "minheight":  return node.MinHeight;
            case "maxwidth":   return node.MaxWidth;
            case "maxheight":  return node.MaxHeight;
            case "grow":       return node.Grow;
            case "shrink":     return node.Shrink;
            case "padding":    return FromEdges(node.Padding);
            case "margin":     return FromEdges(node.Margin);

            // --- container ---
            case "layout":     return node.Layout.ToString();
            case "gap":        return FromVec2(node.Gap);
            case "wrap":       return node.Wrap;
            case "mainalign":  return node.MainAlign.ToString();
            case "crossalign": return node.CrossAlign.ToString();
            case "columns":    return node.Columns;
            case "cellsize":   return FromVec2(node.CellSize);

            // --- placement ---
            case "positioning": return node.Positioning.ToString();
            case "absolute":    return node.Positioning == PositionMode.Absolute;
            case "anchor":      return node.Anchor.ToString();
            case "anchormin":   return FromVec2(node.AnchorMin);
            case "anchormax":   return FromVec2(node.AnchorMax);
            case "pivot":       return FromVec2(node.Pivot);
            case "offset":      return FromVec2(node.Offset);
            case "offsetmax":   return FromVec2(node.OffsetMax);
            case "x":           return node.Offset.X;
            case "y":           return node.Offset.Y;

            // --- text ---
            case "text":          return node.Text;
            case "scale":
            case "textscale":     return node.TextScale;
            case "align":
            case "textalign":     return node.TextAlign.ToString();
            case "verticalalign": return node.VerticalAlign.ToString();
            case "wraptext":      return node.WrapText;
            case "linespacing":   return node.LineSpacing;

            // --- paint ---
            case "background":  return FromColour(node.Background);
            case "tint":        return FromColour(node.Tint);
            case "opacity":     return node.Opacity;
            case "bordercolour":
            case "bordercolor": return FromColour(node.BorderColour);
            case "borderwidth": return node.BorderWidth;
            case "texturepath": return node.TexturePath;
            case "sourcerect":  return FromEdges(node.SourceRect);
            case "ninepatch":   return FromEdges(node.NinePatch);

            // --- clipping and scrolling ---
            case "clip":           return node.Clip;
            case "scroll":         return node.Scroll.ToString();
            case "scrolloffset":   return FromVec2(node.ScrollOffset);
            case "ignoresafearea": return node.IgnoreSafeArea;

            // --- focus and navigation ---
            case "focusable": return node.Focusable.ToString();
            case "modal":     return node.Modal;
            case "navup":     return node.NavUp;
            case "navdown":   return node.NavDown;
            case "navleft":   return node.NavLeft;
            case "navright":  return node.NavRight;
            case "autofocus": return node.AutoFocus;

            // --- payload ---
            case "value":         return node.Value;
            case "minvalue":      return node.MinValue;
            case "maxvalue":      return node.MaxValue;
            case "step":          return node.Step;
            case "checked":       return node.Checked;
            case "selectedindex": return node.SelectedIndex;
            case "options":
            {
                var options = new JsonArray();
                foreach (string option in node.Options) options.Add(option);
                return options;
            }

            // --- resolved by layout, read-only ---
            case "rect": return new JsonObject
            {
                ["x"]      = node.Rect.X,
                ["y"]      = node.Rect.Y,
                ["width"]  = node.Rect.Width,
                ["height"] = node.Rect.Height,
            };

            // --- written by the input router, read-only ---
            case "hovered":  return node.Hovered;
            case "pressed":  return node.Pressed;
            case "clicked":  return node.Clicked;
            case "focused":  return node.Focused;
            case "expanded": return node.Expanded;

            default:
                throw new UiDocumentException($"\"{key}\" is not a UI node property.");
        }
    }

    /// <summary>True when a key names something a node can be told, rather than only asked.</summary>
    public static bool IsWritable(string key) => Canonical(key) is not
        ("rect" or "hovered" or "pressed" or "clicked" or "focused" or "expanded");

    // =========================================================================
    // Writing
    // =========================================================================

    /// <summary>
    /// The properties a document round-trip writes, in a fixed order.
    /// </summary>
    /// <remarks>
    /// Only the ones that survive a reload: layout results and interaction flags are recomputed
    /// every frame, and the aliases (<c>x</c>, <c>y</c>, <c>size</c>, <c>type</c>, <c>scale</c>,
    /// <c>align</c>, <c>absolute</c>) would each write a second copy of a property already listed.
    /// </remarks>
    private static readonly string[] WritableKeys =
    {
        "name", "kind", "visible", "interactive", "order", "style",
        "width", "height", "minWidth", "minHeight", "maxWidth", "maxHeight", "grow", "shrink",
        "padding", "margin",
        "layout", "gap", "wrap", "mainAlign", "crossAlign", "columns", "cellSize",
        "positioning", "anchor", "anchorMin", "anchorMax", "pivot", "offset", "offsetMax",
        "text", "textScale", "textAlign", "verticalAlign", "wrapText", "lineSpacing",
        "background", "tint", "opacity", "borderColour", "borderWidth", "texturePath",
        "sourceRect", "ninePatch",
        "clip", "scroll", "scrollOffset", "ignoreSafeArea",
        "focusable", "modal", "navUp", "navDown", "navLeft", "navRight", "autoFocus",
        "value", "minValue", "maxValue", "step", "checked", "selectedIndex", "options",
    };

    /// <summary>Convenience spellings a handle carries as well as the property they set.</summary>
    /// <remarks>
    /// Every existing script positions with <c>x</c>/<c>y</c> and sizes text with <c>scale</c>,
    /// so the tree keeps those rather than making a migration rewrite arithmetic it did not need
    /// to change. <c>size</c>, <c>type</c> and <c>absolute</c> are spec-only shorthand and are
    /// deliberately not handle members: each writes a property the handle already exposes.
    /// </remarks>
    private static readonly string[] AliasKeys = { "x", "y", "scale", "align" };

    /// <summary>What layout and the input router work out, which a script may read but not set.</summary>
    private static readonly string[] ResultKeys =
        { "rect", "hovered", "pressed", "clicked", "focused", "expanded" };

    /// <summary>
    /// Every value member a script handle exposes, in order.
    /// </summary>
    /// <remarks>
    /// Both bridges build their handle by looping this, wiring <c>get</c> to <see cref="Read"/>
    /// and <c>set</c> to <see cref="Apply(UiNode, string, JsonElement)"/>. A property added to the codec therefore reaches
    /// both engines' script API by being named here once, instead of the five hand-mirrored
    /// edits the flat UI needed. A test pins this list against the contract in both directions.
    /// </remarks>
    public static IReadOnlyList<string> ScriptProperties { get; } =
        WritableKeys.Concat(AliasKeys).Concat(ResultKeys).ToArray();

    /// <summary>
    /// Writes a tree back out as the same JSON <see cref="FromJson"/> reads.
    /// </summary>
    /// <remarks>
    /// Properties still at their default are omitted, so a document says only what it changed
    /// and a round-trip does not bloat. The browser writes the identical shape, which is what
    /// lets one fixture build a tree on both engines and compare the two serialisations.
    /// </remarks>
    public static JsonObject ToNode(UiNode node)
    {
        var reference = new UiNode();
        var result = new JsonObject();

        foreach (string key in WritableKeys)
        {
            // A named anchor already implies its min, max and pivot, so writing all three
            // would put three redundant arrays into every anchored node of a .ui file.
            if (SkipDerivedAnchor(node, key)) continue;

            JsonNode? mine = Read(node, key);
            JsonNode? theirs = Read(reference, key);

            if (Same(mine, theirs)) continue;
            result[key] = mine;
        }

        if (node.Children.Count > 0)
        {
            var children = new JsonArray();
            foreach (UiNode child in node.Children) children.Add(ToNode(child));
            result["children"] = children;
        }

        return result;
    }

    /// <summary>The tree as JSON text.</summary>
    public static string ToJson(UiNode node) => ToNode(node).ToJsonString();

    private static bool SkipDerivedAnchor(UiNode node, string key)
        => node.Anchor != UiAnchor.Custom
        && key is "anchorMin" or "anchorMax" or "pivot";

    private static bool Same(JsonNode? a, JsonNode? b)
        => a is null ? b is null : b is not null && a.ToJsonString() == b.ToJsonString();

    // =========================================================================
    // Reading a value a script wrote
    // =========================================================================

    /// <summary>
    /// Builds a tree from a spec a script passed in.
    /// </summary>
    /// <remarks>
    /// A tree spec nests objects inside <c>children</c> arrays, and nothing else in the bridge
    /// reads a JS array. Rather than write a second walker that could drift from the JSON one,
    /// this reuses <see cref="Scripting.JsValueConverter.ToJsonNode"/> — which already handles
    /// arrays, and already checks <c>ArrayInstance</c> before <c>IsObject</c>, the ordering trap
    /// that makes an array look like an empty object.
    /// </remarks>
    public static UiNode FromJsValue(Jint.Native.JsValue value)
    {
        JsonNode? node = Scripting.JsValueConverter.ToJsonNode(value);
        if (node is not JsonObject)
            throw new UiDocumentException("A UI node must be an object.");

        return FromJson(node.ToJsonString());
    }

    // =========================================================================
    // Value shapes
    // =========================================================================

    // ---- reading a value back out ----

    /// <summary>
    /// A size reads back in the spelling it was written in, so a round-trip is lossless:
    /// a number for a fixed size, and the word for the three modes that have one.
    /// </summary>
    private static JsonNode ReadSize(SizeMode mode, float size) => mode switch
    {
        SizeMode.Auto    => "auto",
        SizeMode.Stretch => "*",
        SizeMode.Percent => $"{(size * 100f).ToString("0.####", CultureInfo.InvariantCulture)}%",
        _                => size,
    };

    private static JsonArray FromVec2(Vector2 v) => new() { v.X, v.Y };

    private static JsonArray FromEdges(Vector4 v) => new() { v.X, v.Y, v.Z, v.W };

    /// <summary>
    /// Eight digits when the colour is translucent, six when it is not.
    /// </summary>
    /// <remarks>
    /// The flat UI's hex writer dropped alpha unconditionally, so reading a background written
    /// as <c>#161920e6</c> and writing it back turned it opaque.
    /// </remarks>
    private static JsonNode? FromColour(Color? colour)
    {
        if (colour is not Color c) return null;
        return c.A == 255 ? $"#{c.R:x2}{c.G:x2}{c.B:x2}" : $"#{c.R:x2}{c.G:x2}{c.B:x2}{c.A:x2}";
    }

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
