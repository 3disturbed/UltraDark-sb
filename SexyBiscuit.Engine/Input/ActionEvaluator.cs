using System.Collections.Concurrent;
using Microsoft.Xna.Framework.Input;

namespace SexyBiscuit.Engine.Input;

/// <summary>
/// Turns a binding and a set of devices into an answer. Split out of <see cref="InputManager"/> so
/// the same map can be asked about different devices: the manager asks about every device at once,
/// a <see cref="PlayerInput"/> asks about the ones its player owns.
/// </summary>
public static class ActionEvaluator
{
    /// <summary>A stick has to be pushed this far before it counts as a button being held.</summary>
    public const float AxisAsButtonThreshold = 0.3f;

    // -------------------------------------------------------------------------
    // Actions
    // -------------------------------------------------------------------------

    /// <summary>True while any binding for the action is held.</summary>
    public static bool IsHeld(ActionMap map, IInputSource source, string action)
        => Any(map, action, binding => Held(binding, source));

    /// <summary>True on the frame any binding for the action goes down.</summary>
    public static bool IsPressed(ActionMap map, IInputSource source, string action)
        => Any(map, action, binding => Pressed(binding, source));

    /// <summary>True on the frame any binding for the action comes up.</summary>
    public static bool IsReleased(ActionMap map, IInputSource source, string action)
        => Any(map, action, binding => Released(binding, source));

    /// <summary>
    /// The action's value from -1 to 1. The first binding with a non-zero value wins, so a stick
    /// at rest falls through to the keys.
    /// </summary>
    public static float GetAxis(ActionMap map, IInputSource source, string action)
    {
        if (!map.Actions.TryGetValue(action, out var act)) return 0f;

        foreach (var binding in act.Bindings)
        {
            float value = Axis(binding, source);
            if (value != 0f) return value;
        }

        return 0f;
    }

    private static bool Any(ActionMap map, string action, Func<InputBinding, bool> test)
        => map.Actions.TryGetValue(action, out var act) && act.Bindings.Any(test);

    // -------------------------------------------------------------------------
    // One binding
    // -------------------------------------------------------------------------

    public static bool Held(InputBinding binding, IInputSource source) => binding.Device switch
    {
        "keyboard" => KeyboardHeld(binding, source),
        "mouse"    => binding.Button != null && TryParseMouseButton(binding.Button, out var mb) && source.IsMouseButtonDown(mb),
        "gamepad"  => GamepadHeld(binding, source),
        "touch"    => TouchHeld(binding, source),
        _          => false,
    };

    public static bool Pressed(InputBinding binding, IInputSource source) => binding.Device switch
    {
        "keyboard" => KeyboardEach(binding, source.IsKeyPressed),
        "mouse"    => binding.Button != null && TryParseMouseButton(binding.Button, out var mb) && source.IsMouseButtonPressed(mb),
        "gamepad"  => GamepadButton(binding, source, (pad, button) => pad.IsButtonPressed(button)),
        "touch"    => binding.Axis == null && source.ActiveTouch is { } touch
                      && touch.Touches.Any(t => t.Phase == TouchPhase.Began),
        _          => false,
    };

    public static bool Released(InputBinding binding, IInputSource source) => binding.Device switch
    {
        "keyboard" => KeyboardEach(binding, source.IsKeyReleased),
        "mouse"    => binding.Button != null && TryParseMouseButton(binding.Button, out var mb) && source.IsMouseButtonReleased(mb),
        "gamepad"  => GamepadButton(binding, source, (pad, button) => pad.IsButtonReleased(button)),
        "touch"    => binding.Axis == null && source.ActiveTouch is { } touch
                      && touch.Touches.Any(t => t.Phase == TouchPhase.Ended),
        _          => false,
    };

    /// <summary>The binding's value, with its scale and inversion applied.</summary>
    public static float Axis(InputBinding binding, IInputSource source)
    {
        float raw = binding.Device switch
        {
            "keyboard" => KeyboardAxis(binding, source),
            "mouse"    => MouseAxis(binding, source),
            "gamepad"  => binding.Axis != null && source.ActiveGamepad is { IsConnected: true } pad ? pad.GetAxis(binding.Axis) : 0f,
            "touch"    => TouchAxis(binding, source),
            _          => 0f,
        };

        raw *= binding.Scale;
        return binding.Invert ? -raw : raw;
    }

    // -------------------------------------------------------------------------
    // Per device
    // -------------------------------------------------------------------------

    private static bool KeyboardHeld(InputBinding binding, IInputSource source)
        => KeyboardEach(binding, source.IsKeyDown);

    private static bool KeyboardEach(InputBinding binding, Func<Keys, bool> test)
    {
        if (binding.Key    != null && TryParseKey(binding.Key,    out var key) && test(key))    return true;
        if (binding.PosKey != null && TryParseKey(binding.PosKey, out var pos) && test(pos))    return true;
        if (binding.NegKey != null && TryParseKey(binding.NegKey, out var neg) && test(neg))    return true;
        return false;
    }

    private static float KeyboardAxis(InputBinding binding, IInputSource source)
    {
        float value = 0f;
        if (binding.PosKey != null && TryParseKey(binding.PosKey, out var pos) && source.IsKeyDown(pos)) value += 1f;
        if (binding.NegKey != null && TryParseKey(binding.NegKey, out var neg) && source.IsKeyDown(neg)) value -= 1f;
        if (value == 0f && binding.Key != null && TryParseKey(binding.Key, out var key) && source.IsKeyDown(key)) value = 1f;
        return value;
    }

    private static float MouseAxis(InputBinding binding, IInputSource source) => binding.Axis switch
    {
        "MouseX" => source.MouseDelta.X,
        "MouseY" => source.MouseDelta.Y,
        "Scroll" => source.ScrollDelta,
        _        => 0f,
    };

    private static bool GamepadHeld(InputBinding binding, IInputSource source)
    {
        if (source.ActiveGamepad is not { IsConnected: true } pad) return false;

        if (binding.Button != null && TryParseGamepadButton(binding.Button, out var button)) return pad.IsButtonDown(button);
        if (binding.Axis   != null) return MathF.Abs(pad.GetAxis(binding.Axis)) > AxisAsButtonThreshold;

        return false;
    }

    private static bool GamepadButton(InputBinding binding, IInputSource source, Func<GamepadState, Buttons, bool> test)
    {
        if (source.ActiveGamepad is not { IsConnected: true } pad) return false;
        return binding.Button != null && TryParseGamepadButton(binding.Button, out var button) && test(pad, button);
    }

    private static bool TouchHeld(InputBinding binding, IInputSource source)
    {
        if (source.ActiveTouch is not { } touch) return false;
        if (binding.Axis != null) return MathF.Abs(Axis(binding, source)) > AxisAsButtonThreshold;

        // No axis: any finger on the screen counts as the button being held.
        return touch.Touches.Any(t => t.Phase is not (TouchPhase.Ended or TouchPhase.Cancelled));
    }

    private static float TouchAxis(InputBinding binding, IInputSource source)
    {
        if (source.ActiveTouch is not { } touch) return 0f;

        return binding.Axis switch
        {
            "LeftJoystickX"  => touch.LeftJoystick.Value.X,
            "LeftJoystickY"  => touch.LeftJoystick.Value.Y,
            "RightJoystickX" => touch.RightJoystick.Value.X,
            "RightJoystickY" => touch.RightJoystick.Value.Y,
            "PinchDelta"     => touch.PinchDelta,
            _                => 0f,
        };
    }

    // -------------------------------------------------------------------------
    // Names
    // -------------------------------------------------------------------------

    private static readonly ConcurrentDictionary<string, Keys?> KeyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a key name, accepting the browser's spelling as well as XNA's.
    /// </summary>
    /// <remarks>
    /// A bindings file written by the HTML5 port says <c>"KeyA"</c> or <c>"ArrowLeft"</c>, which a
    /// bare enum parse rejects — silently, because a binding that will not parse simply never
    /// fires. The same file has to work in both engines.
    /// </remarks>
    public static bool TryParseKey(string? name, out Keys key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var cached = KeyCache.GetOrAdd(name, Canonicalise);
        if (cached == null) return false;

        key = cached.Value;
        return true;
    }

    private static Keys? Canonicalise(string name)
    {
        string text = name.Trim();

        if (Enum.TryParse<Keys>(text, ignoreCase: true, out var direct)) return direct;

        // Browser KeyboardEvent.code spellings.
        string mapped = text switch
        {
            "ArrowLeft"    => "Left",
            "ArrowRight"   => "Right",
            "ArrowUp"      => "Up",
            "ArrowDown"    => "Down",
            "Escape"       => "Escape",
            "Backquote"    => "OemTilde",
            "Minus"        => "OemMinus",
            "Equal"        => "OemPlus",
            "BracketLeft"  => "OemOpenBrackets",
            "BracketRight" => "OemCloseBrackets",
            "Backslash"    => "OemPipe",
            "Semicolon"    => "OemSemicolon",
            "Quote"        => "OemQuotes",
            "Comma"        => "OemComma",
            "Period"       => "OemPeriod",
            "Slash"        => "OemQuestion",
            "ControlLeft"  => "LeftControl",
            "ControlRight" => "RightControl",
            "ShiftLeft"    => "LeftShift",
            "ShiftRight"   => "RightShift",
            "AltLeft"      => "LeftAlt",
            "AltRight"     => "RightAlt",
            "MetaLeft"     => "LeftWindows",
            "MetaRight"    => "RightWindows",
            _ when text.StartsWith("Key", StringComparison.OrdinalIgnoreCase)    && text.Length == 4 => text[3..],
            _ when text.StartsWith("Digit", StringComparison.OrdinalIgnoreCase)  && text.Length == 6 => "D" + text[5],
            _ when text.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) && text.Length == 7 => "NumPad" + text[6],
            _ => text,
        };

        return Enum.TryParse<Keys>(mapped, ignoreCase: true, out var parsed) ? parsed : null;
    }

    public static bool TryParseMouseButton(string? name, out MouseButton button)
    {
        button = default;
        return name != null && Enum.TryParse(name, ignoreCase: true, out button);
    }

    public static bool TryParseGamepadButton(string? name, out Buttons button)
    {
        button = default;
        return name != null && Enum.TryParse(name, ignoreCase: true, out button);
    }
}
