using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.UI.Widgets;

namespace SexyBiscuit.Engine.UI;

// =============================================================================
// WidgetStyle — per-widget-type style data
// =============================================================================

/// <summary>
/// Style definition for one widget type. All texture fields are content-pipeline
/// paths; load them via your AssetManager and assign to the widget manually, or
/// use the Theme.Apply helper after loading assets.
/// </summary>
public class WidgetStyle
{
    [JsonPropertyName("normalTexture")]
    public string? NormalTexture   { get; set; }

    [JsonPropertyName("hoverTexture")]
    public string? HoverTexture    { get; set; }

    [JsonPropertyName("pressedTexture")]
    public string? PressedTexture  { get; set; }

    [JsonPropertyName("disabledTexture")]
    public string? DisabledTexture { get; set; }

    [JsonPropertyName("textColor")]
    public string TextColor  { get; set; } = "#FFFFFF";

    [JsonPropertyName("fontPath")]
    public string? FontPath  { get; set; }

    [JsonPropertyName("fontSize")]
    public float   FontSize  { get; set; } = 16f;

    [JsonPropertyName("backgroundColor")]
    public string? BackgroundColor { get; set; }

    // -------------------------------------------------------------------------
    // Parsed helpers
    // -------------------------------------------------------------------------

    /// <summary>Parses the hex <see cref="TextColor"/> string into an XNA Color.</summary>
    public Color GetTextColor()       => ParseColor(TextColor,       Color.White);
    public Color GetBackgroundColor() => ParseColor(BackgroundColor, Color.Transparent);

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;

        hex = hex.TrimStart('#');

        try
        {
            switch (hex.Length)
            {
                case 6:
                {
                    byte r = byte.Parse(hex[0..2], NumberStyles.HexNumber);
                    byte g = byte.Parse(hex[2..4], NumberStyles.HexNumber);
                    byte b = byte.Parse(hex[4..6], NumberStyles.HexNumber);
                    return new Color(r, g, b);
                }
                case 8:
                {
                    byte r = byte.Parse(hex[0..2], NumberStyles.HexNumber);
                    byte g = byte.Parse(hex[2..4], NumberStyles.HexNumber);
                    byte b = byte.Parse(hex[4..6], NumberStyles.HexNumber);
                    byte a = byte.Parse(hex[6..8], NumberStyles.HexNumber);
                    return new Color(r, g, b, a);
                }
            }
        }
        catch
        {
            // Fall through to fallback
        }

        return fallback;
    }
}

// =============================================================================
// Theme
// =============================================================================

/// <summary>
/// JSON-driven skin / theme system.
/// Styles are keyed by widget type name (e.g. "Button", "Label", "Panel").
/// </summary>
public class Theme
{
    // -------------------------------------------------------------------------
    // Singleton current theme
    // -------------------------------------------------------------------------
    public static Theme Current { get; private set; } = new Theme();

    // -------------------------------------------------------------------------
    // Style dictionary
    // -------------------------------------------------------------------------
    /// <summary>Maps widget type names to their style definitions.</summary>
    public Dictionary<string, WidgetStyle> Styles { get; } = new(StringComparer.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // JSON loading
    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true
    };

    /// <summary>
    /// Loads a theme from a JSON file and sets it as <see cref="Current"/>.
    /// <para>
    /// Expected JSON format:
    /// <code>
    /// {
    ///   "Button": { "normalTexture": "UI/btn.png", "textColor": "#FF0000" },
    ///   "Label":  { "textColor": "#CCCCCC", "fontSize": 18 }
    /// }
    /// </code>
    /// </para>
    /// </summary>
    public static void Load(string jsonPath)
    {
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException($"Theme file not found: {jsonPath}", jsonPath);

        string json = File.ReadAllText(jsonPath);
        var dict = JsonSerializer.Deserialize<Dictionary<string, WidgetStyle>>(json, _jsonOptions)
                   ?? new Dictionary<string, WidgetStyle>();

        var theme = new Theme();
        foreach (var (key, style) in dict)
            theme.Styles[key] = style;

        Current = theme;
    }

    /// <summary>
    /// Loads a theme from a JSON string directly (useful for embedded resources).
    /// </summary>
    public static void LoadFromJson(string json)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, WidgetStyle>>(json, _jsonOptions)
                   ?? new Dictionary<string, WidgetStyle>();

        var theme = new Theme();
        foreach (var (key, style) in dict)
            theme.Styles[key] = style;

        Current = theme;
    }

    // -------------------------------------------------------------------------
    // Application
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies the current theme's style to a widget.
    /// Texture loading is intentionally NOT performed here — call your
    /// AssetManager separately and assign textures to widgets directly.
    /// This method sets colours, font sizes, and background colours only.
    /// </summary>
    public static void Apply(Widget widget)
        => Current.ApplyTo(widget);

    /// <summary>Applies this theme to a widget using its concrete type name as the key.</summary>
    public void ApplyTo(Widget widget)
    {
        // Walk up the type hierarchy to find the most specific style
        Type? t = widget.GetType();
        while (t != null && t != typeof(object))
        {
            if (Styles.TryGetValue(t.Name, out var style))
            {
                ApplyStyle(widget, style);
                return;
            }
            t = t.BaseType;
        }
    }

    private static void ApplyStyle(Widget widget, WidgetStyle style)
    {
        switch (widget)
        {
            case Button btn:
                btn.TextColor = style.GetTextColor();
                break;

            case Label lbl:
                lbl.TextColor = style.GetTextColor();
                lbl.FontSize  = style.FontSize;
                break;

            case Panel panel:
                if (style.BackgroundColor != null)
                    panel.BackgroundColor = style.GetBackgroundColor();
                break;

            case TextInput ti:
                ti.TextColor = style.GetTextColor();
                break;

            default:
                // Generic: try to set tint from textColor if nothing else applies
                widget.Tint = style.GetTextColor();
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------

    /// <summary>Returns the style for a given widget type name, or null if not found.</summary>
    public WidgetStyle? GetStyle(string typeName)
        => Styles.TryGetValue(typeName, out var s) ? s : null;

    /// <summary>Returns the style for a widget's type, walking up the hierarchy.</summary>
    public WidgetStyle? GetStyle(Widget widget)
    {
        Type? t = widget.GetType();
        while (t != null && t != typeof(object))
        {
            if (Styles.TryGetValue(t.Name, out var s)) return s;
            t = t.BaseType;
        }
        return null;
    }
}
