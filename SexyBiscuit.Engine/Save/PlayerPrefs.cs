using System.Text.Json;
using System.Text.Json.Serialization;

namespace SexyBiscuit.Engine.Save;

/// <summary>
/// Persistent, cross-session key-value store backed by a JSON file at
/// <c>Saves/prefs.json</c>.
///
/// Values are stored internally as their native types (float, int, string, bool).
/// All writes are in-memory until <see cref="Save"/> is called explicitly, or
/// automatically when the process exits (via <see cref="AppDomain.ProcessExit"/>).
///
/// <see cref="Load"/> is called automatically on the first access to any key.
/// </summary>
public static class PlayerPrefs
{
    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------

    private const string DefaultPrefsFileName = "prefs.json";

    // Exposed so callers can redirect the file (e.g. during testing)
    private static string _prefsPath = Path.Combine("Saves", DefaultPrefsFileName);
    public static string PrefsPath
    {
        get => _prefsPath;
        set
        {
            _prefsPath = value;
            // Reset loaded flag so the next access re-reads from the new location
            _loaded = false;
        }
    }

    // -------------------------------------------------------------------------
    // Storage
    // -------------------------------------------------------------------------

    // Raw storage — values are float, int, string, or bool
    private static Dictionary<string, object> _data = new();
    private static bool _loaded;
    private static bool _dirty;

    private static readonly object _lock = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented          = true,
        PropertyNameCaseInsensitive = true,
        // Preserve float/int distinction when round-tripping through JsonElement
        NumberHandling         = JsonNumberHandling.AllowReadingFromString,
    };

    // -------------------------------------------------------------------------
    // Auto-save on process exit
    // -------------------------------------------------------------------------

    static PlayerPrefs()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (_dirty) Save();
        };
    }

    // -------------------------------------------------------------------------
    // Setters
    // -------------------------------------------------------------------------

    public static void SetFloat(string key, float value)
    {
        EnsureLoaded();
        lock (_lock) { _data[key] = value; _dirty = true; }
    }

    public static void SetInt(string key, int value)
    {
        EnsureLoaded();
        lock (_lock) { _data[key] = value; _dirty = true; }
    }

    public static void SetString(string key, string value)
    {
        EnsureLoaded();
        lock (_lock) { _data[key] = value; _dirty = true; }
    }

    public static void SetBool(string key, bool value)
    {
        EnsureLoaded();
        lock (_lock) { _data[key] = value; _dirty = true; }
    }

    // -------------------------------------------------------------------------
    // Getters
    // -------------------------------------------------------------------------

    public static float GetFloat(string key, float defaultValue = 0f)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_data.TryGetValue(key, out object? raw)) return defaultValue;
            return raw switch
            {
                float  f => f,
                double d => (float)d,
                int    i => i,
                long   l => l,
                JsonElement je => je.ValueKind == JsonValueKind.Number ? je.GetSingle() : defaultValue,
                string s when float.TryParse(s, out float parsed) => parsed,
                _ => defaultValue,
            };
        }
    }

    public static int GetInt(string key, int defaultValue = 0)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_data.TryGetValue(key, out object? raw)) return defaultValue;
            return raw switch
            {
                int    i => i,
                long   l => (int)l,
                float  f => (int)f,
                double d => (int)d,
                JsonElement je => je.ValueKind == JsonValueKind.Number ? je.GetInt32() : defaultValue,
                string s when int.TryParse(s, out int parsed) => parsed,
                _ => defaultValue,
            };
        }
    }

    public static string GetString(string key, string defaultValue = "")
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_data.TryGetValue(key, out object? raw)) return defaultValue;
            return raw switch
            {
                string s => s,
                JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? defaultValue,
                _ => raw.ToString() ?? defaultValue,
            };
        }
    }

    public static bool GetBool(string key, bool defaultValue = false)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_data.TryGetValue(key, out object? raw)) return defaultValue;
            return raw switch
            {
                bool   b => b,
                int    i => i != 0,
                JsonElement je when je.ValueKind == JsonValueKind.True  => true,
                JsonElement je when je.ValueKind == JsonValueKind.False => false,
                string s when bool.TryParse(s, out bool parsed) => parsed,
                _ => defaultValue,
            };
        }
    }

    // -------------------------------------------------------------------------
    // Key utilities
    // -------------------------------------------------------------------------

    /// <summary>Returns <see langword="true"/> if <paramref name="key"/> has a stored value.</summary>
    public static bool HasKey(string key)
    {
        EnsureLoaded();
        lock (_lock) return _data.ContainsKey(key);
    }

    /// <summary>Removes the entry for <paramref name="key"/>. No-op if the key does not exist.</summary>
    public static void DeleteKey(string key)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.Remove(key)) _dirty = true;
        }
    }

    /// <summary>Removes all stored preferences from memory (does not write to disk until <see cref="Save"/> is called).</summary>
    public static void DeleteAll()
    {
        EnsureLoaded();
        lock (_lock)
        {
            _data.Clear();
            _dirty = true;
        }
    }

    // -------------------------------------------------------------------------
    // Persistence
    // -------------------------------------------------------------------------

    /// <summary>
    /// Flushes the current in-memory preferences to <see cref="PrefsPath"/>.
    /// Thread-safe. Called automatically on process exit.
    /// </summary>
    public static void Save()
    {
        string json;
        lock (_lock)
        {
            // Serialise a copy of the data as a plain string-keyed dictionary.
            // Values are stored as their original CLR types where possible.
            json   = JsonSerializer.Serialize(BuildSerializableDict(), _jsonOptions);
            _dirty = false;
        }

        try
        {
            string dir = Path.GetDirectoryName(_prefsPath)!;
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_prefsPath, json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PlayerPrefs.Save] Failed to write '{_prefsPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Reads preferences from <see cref="PrefsPath"/> into memory.
    /// If the file does not exist, the in-memory store is cleared and the call succeeds silently.
    /// Called automatically on the first access.
    /// </summary>
    public static void Load()
    {
        lock (_lock)
        {
            _loaded = true;
            _dirty  = false;

            if (!File.Exists(_prefsPath))
            {
                _data = new Dictionary<string, object>();
                return;
            }

            try
            {
                string json = File.ReadAllText(_prefsPath, System.Text.Encoding.UTF8);
                var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, _jsonOptions);
                _data = new Dictionary<string, object>();

                if (raw is not null)
                {
                    foreach (var (key, element) in raw)
                        _data[key] = UnwrapJsonElement(element);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PlayerPrefs.Load] Failed to read '{_prefsPath}': {ex.Message}");
                _data = new Dictionary<string, object>();
            }
        }
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static void EnsureLoaded()
    {
        if (!_loaded) Load();
    }

    /// <summary>
    /// Converts a <see cref="JsonElement"/> into the most specific native CLR type
    /// so round-tripping through <see cref="Load"/> produces sensible typed values.
    /// </summary>
    private static object UnwrapJsonElement(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.True    => (object)true,
            JsonValueKind.False   => false,
            JsonValueKind.String  => e.GetString() ?? "",
            JsonValueKind.Number  => TryUnwrapNumber(e),
            _                     => e.Clone(),   // keep as JsonElement for unsupported kinds
        };
    }

    private static object TryUnwrapNumber(JsonElement e)
    {
        // Prefer int → long → double to keep GetInt/GetFloat semantics predictable
        if (e.TryGetInt32(out int i))  return i;
        if (e.TryGetInt64(out long l)) return l;
        if (e.TryGetSingle(out float f) && !float.IsInfinity(f)) return f;
        return e.GetDouble();
    }

    /// <summary>
    /// Builds a plain <c>Dictionary&lt;string, object&gt;</c> suitable for
    /// <see cref="JsonSerializer.Serialize"/> from the internal store.
    /// JsonElement values are preserved as-is; other types are their native CLR values.
    /// </summary>
    private static Dictionary<string, object> BuildSerializableDict()
    {
        // Return a snapshot so the serialisation runs outside the lock if needed
        return new Dictionary<string, object>(_data);
    }
}
