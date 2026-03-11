using System.Text.Json;
using System.Text.Json.Serialization;

namespace SexyBiscuit.Engine.Input;

// ---------------------------------------------------------------------------
// Binding data model
// ---------------------------------------------------------------------------

/// <summary>
/// A single binding within an <see cref="InputAction"/>.
/// All fields are optional; only the ones relevant to the device are set.
/// </summary>
public sealed record InputBinding
{
    /// <summary>"keyboard" | "mouse" | "gamepad" | "touch"</summary>
    [JsonPropertyName("device")]
    public string Device { get; init; } = "keyboard";

    /// <summary>Keyboard key name (e.g. "Space", "W"). Matches <see cref="Microsoft.Xna.Framework.Input.Keys"/> enum names.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>Negative key for an axis (e.g. "A" for left on the X axis).</summary>
    [JsonPropertyName("negKey")]
    public string? NegKey { get; init; }

    /// <summary>Positive key for an axis (e.g. "D" for right on the X axis).</summary>
    [JsonPropertyName("posKey")]
    public string? PosKey { get; init; }

    /// <summary>Mouse button name ("Left", "Right", "Middle", "XButton1", "XButton2").</summary>
    [JsonPropertyName("button")]
    public string? Button { get; init; }

    /// <summary>Gamepad axis name ("LeftX", "LeftY", "RightX", "RightY", "LeftTrigger", "RightTrigger").</summary>
    [JsonPropertyName("axis")]
    public string? Axis { get; init; }

    /// <summary>Whether to invert the axis value.</summary>
    [JsonPropertyName("invert")]
    public bool Invert { get; init; }

    /// <summary>Scale factor applied to the raw value before returning. Default 1.</summary>
    [JsonPropertyName("scale")]
    public float Scale { get; init; } = 1f;
}

// ---------------------------------------------------------------------------
// Action
// ---------------------------------------------------------------------------

/// <summary>
/// Named input action with one or more <see cref="InputBinding"/> alternatives.
/// The first binding that reports a non-zero value wins.
/// </summary>
public sealed class InputAction
{
    public string Name { get; set; } = string.Empty;
    public List<InputBinding> Bindings { get; set; } = new();

    public InputAction() { }

    public InputAction(string name, params InputBinding[] bindings)
    {
        Name     = name;
        Bindings = new List<InputBinding>(bindings);
    }
}

// ---------------------------------------------------------------------------
// ActionMap
// ---------------------------------------------------------------------------

/// <summary>
/// Collection of named <see cref="InputAction"/> objects. Can be loaded from a
/// JSON file or populated at runtime.
/// </summary>
public sealed class ActionMap
{
    public Dictionary<string, InputAction> Actions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // -----------------------------------------------------------------------
    // Factory — from JSON
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses an action map from a JSON file. Expected format:
    /// <code>
    /// {
    ///   "Jump": [{"device":"keyboard","key":"Space"},{"device":"gamepad","button":"A"}],
    ///   "MoveX": [{"device":"keyboard","negKey":"A","posKey":"D"},{"device":"gamepad","axis":"LeftX"}]
    /// }
    /// </code>
    /// </summary>
    public static ActionMap LoadFromJson(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"ActionMap: file not found: '{path}'");

        var json    = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas         = true,
            ReadCommentHandling         = JsonCommentHandling.Skip
        };

        // JSON root: Dictionary<string, List<InputBinding>>
        var raw = JsonSerializer.Deserialize<Dictionary<string, List<InputBinding>>>(json, options)
                  ?? throw new InvalidDataException($"ActionMap: failed to parse '{path}'");

        var map = new ActionMap();
        foreach (var (name, bindings) in raw)
        {
            map.Actions[name] = new InputAction
            {
                Name     = name,
                Bindings = bindings
            };
        }
        return map;
    }

    // -----------------------------------------------------------------------
    // Factory — default map
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a sensible default action map covering the most common game actions.
    /// Gamepad bindings use an XInput / Xbox layout.
    /// </summary>
    public static ActionMap Default() => new()
    {
        Actions = new Dictionary<string, InputAction>(StringComparer.OrdinalIgnoreCase)
        {
            ["MoveX"] = new("MoveX",
                new InputBinding { Device = "keyboard", NegKey = "A",    PosKey = "D"          },
                new InputBinding { Device = "keyboard", NegKey = "Left", PosKey = "Right"       },
                new InputBinding { Device = "gamepad",  Axis   = "LeftX"                       }),

            ["MoveY"] = new("MoveY",
                new InputBinding { Device = "keyboard", NegKey = "S",    PosKey = "W",  Invert = true },
                new InputBinding { Device = "keyboard", NegKey = "Down", PosKey = "Up", Invert = true },
                new InputBinding { Device = "gamepad",  Axis   = "LeftY", Invert = true              }),

            ["Jump"] = new("Jump",
                new InputBinding { Device = "keyboard", Key    = "Space"                       },
                new InputBinding { Device = "gamepad",  Button = "A"                           }),

            ["Attack"] = new("Attack",
                new InputBinding { Device = "mouse",    Button = "Left"                        },
                new InputBinding { Device = "keyboard", Key    = "Z"                           },
                new InputBinding { Device = "gamepad",  Button = "X"                           }),

            ["Dodge"] = new("Dodge",
                new InputBinding { Device = "keyboard", Key    = "LeftShift"                   },
                new InputBinding { Device = "gamepad",  Button = "B"                           }),

            ["Interact"] = new("Interact",
                new InputBinding { Device = "keyboard", Key    = "E"                           },
                new InputBinding { Device = "gamepad",  Button = "X"                           }),

            ["Pause"] = new("Pause",
                new InputBinding { Device = "keyboard", Key    = "Escape"                      },
                new InputBinding { Device = "gamepad",  Button = "Start"                       }),

            ["CameraX"] = new("CameraX",
                new InputBinding { Device = "mouse",    Axis   = "MouseX"                      },
                new InputBinding { Device = "gamepad",  Axis   = "RightX"                      }),

            ["CameraY"] = new("CameraY",
                new InputBinding { Device = "mouse",    Axis   = "MouseY"                      },
                new InputBinding { Device = "gamepad",  Axis   = "RightY"                      }),
        }
    };

    // -----------------------------------------------------------------------
    // Serialisation helpers (used by InputManager.SaveBindings / LoadBindings)
    // -----------------------------------------------------------------------

    public string ToJson()
    {
        var raw     = Actions.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Bindings);
        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(raw, options);
    }

    public void SaveToFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }

    /// <summary>
    /// Returns a deep clone of this action map by round-tripping through JSON.
    /// </summary>
    public ActionMap Clone()
    {
        var json    = ToJson();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var raw     = JsonSerializer.Deserialize<Dictionary<string, List<InputBinding>>>(json, options)
                      ?? new Dictionary<string, List<InputBinding>>();
        var clone   = new ActionMap();
        foreach (var (name, bindings) in raw)
            clone.Actions[name] = new InputAction { Name = name, Bindings = bindings };
        return clone;
    }
}
