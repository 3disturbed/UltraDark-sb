using System.Text.Json;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Localization;

/// <summary>
/// String table lookup with runtime language switching and named argument substitution.
/// </summary>
/// <remarks>
/// <para>
/// Tables are flat JSON objects of key to translated string, one file per language:
/// <c>Assets/Locales/en.json</c>, <c>fr.json</c>, and so on. Keys are stable identifiers
/// (<c>ui.menu.start</c>), never English text — so changing the English wording does not
/// invalidate every other language.
/// </para>
/// <para>
/// A missing key returns the key itself rather than an empty string. That makes an
/// untranslated string obvious on screen instead of silently blank, and keeps layout
/// roughly right while translation is in progress.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// Loc.LoadDirectory("Assets/Locales");
/// Loc.SetLanguage("fr");
///
/// label.Text = Loc.Get("ui.menu.start");
/// hud.Text   = Loc.Format("hud.score", ("score", 1200));   // "Score: {score}"
/// </code>
/// </example>
public static class Loc
{
    private static readonly Dictionary<string, Dictionary<string, string>> _tables =
        new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> _current = new(StringComparer.Ordinal);

    /// <summary>Language code currently in use, for example "en" or "pt-BR".</summary>
    public static string CurrentLanguage { get; private set; } = "en";

    /// <summary>
    /// Language consulted when a key is missing from <see cref="CurrentLanguage"/>.
    /// Keeps partially translated builds usable.
    /// </summary>
    public static string FallbackLanguage { get; set; } = "en";

    /// <summary>Every language code that has been loaded.</summary>
    public static IEnumerable<string> AvailableLanguages => _tables.Keys;

    /// <summary>Raised after <see cref="SetLanguage"/> succeeds, with the new code. Rebuild UI text here.</summary>
    public static SBEvent<string> LanguageChanged { get; } = new();

    // -------------------------------------------------------------------------
    // Loading
    // -------------------------------------------------------------------------

    /// <summary>
    /// Loads one language table from a flat JSON object. Replaces any table already
    /// registered under <paramref name="languageCode"/>.
    /// </summary>
    public static void LoadTable(string languageCode, string json)
    {
        try
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                        ?? new Dictionary<string, string>();

            _tables[languageCode] = new Dictionary<string, string>(table, StringComparer.Ordinal);

            if (string.Equals(languageCode, CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                _current = _tables[languageCode];
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[Loc] '{languageCode}' table is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>Loads a language table from a JSON file, using the filename as the language code.</summary>
    public static void LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"[Loc] No locale file at '{path}'.");
            return;
        }

        LoadTable(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path));
    }

    /// <summary>
    /// Loads every <c>*.json</c> in a directory as a language table.
    /// Returns the number of tables loaded.
    /// </summary>
    public static int LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"[Loc] No locale directory at '{directory}'.");
            return 0;
        }

        int count = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            LoadFile(file);
            count++;
        }

        return count;
    }

    // -------------------------------------------------------------------------
    // Language selection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Switches language. Returns false when no table is loaded for that code, leaving the
    /// current language in place.
    /// </summary>
    public static bool SetLanguage(string languageCode)
    {
        if (!_tables.TryGetValue(languageCode, out var table)) return false;

        CurrentLanguage = languageCode;
        _current        = table;
        LanguageChanged.Broadcast(languageCode);
        return true;
    }

    /// <summary>
    /// Picks the closest loaded language to the operating system's setting — an exact match
    /// first, then the base language of a regional code, then <see cref="FallbackLanguage"/>.
    /// </summary>
    public static void SetLanguageFromSystem()
    {
        string culture = System.Globalization.CultureInfo.CurrentUICulture.Name;

        if (SetLanguage(culture)) return;

        int dash = culture.IndexOf('-');
        if (dash > 0 && SetLanguage(culture[..dash])) return;

        SetLanguage(FallbackLanguage);
    }

    // -------------------------------------------------------------------------
    // Lookup
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the translation for <paramref name="key"/>, falling back to
    /// <see cref="FallbackLanguage"/> and then to the key itself.
    /// </summary>
    public static string Get(string key)
    {
        if (_current.TryGetValue(key, out var value)) return value;

        if (_tables.TryGetValue(FallbackLanguage, out var fallback)
            && fallback.TryGetValue(key, out var fallbackValue))
            return fallbackValue;

        return key;
    }

    /// <summary>True when the key exists in the current or fallback language.</summary>
    public static bool Has(string key)
        => _current.ContainsKey(key)
        || (_tables.TryGetValue(FallbackLanguage, out var f) && f.ContainsKey(key));

    /// <summary>
    /// Looks up a key and substitutes <c>{name}</c> placeholders from the supplied pairs.
    /// </summary>
    /// <remarks>
    /// Named rather than positional placeholders, because translators routinely need to
    /// reorder the values in a sentence and <c>{0}</c> gives them no way to know what a
    /// slot contains.
    /// </remarks>
    public static string Format(string key, params (string name, object? value)[] arguments)
    {
        string text = Get(key);

        foreach (var (name, value) in arguments)
            text = text.Replace($"{{{name}}}", value?.ToString() ?? string.Empty, StringComparison.Ordinal);

        return text;
    }

    /// <summary>Removes every loaded table. Mainly for tests.</summary>
    public static void Clear()
    {
        _tables.Clear();
        _current = new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
