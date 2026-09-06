using System.Collections.Concurrent;
using Microsoft.Xna.Framework.Input;

namespace SexyBiscuit.Engine.Input;

/// <summary>
/// Turns whatever a script or a bindings file calls a key into the <see cref="Keys"/> value.
/// </summary>
/// <remarks>
/// The browser reports <c>KeyboardEvent.code</c> ("KeyA", "ArrowLeft", "Digit1") and the
/// HTML5 port normalises that to XNA's spelling ("A", "Left", "D1") so one action map works in
/// both engines. This is the same table read the other way, so a script that says
/// <c>Input.isKeyHeld("a")</c>, <c>"KeyA"</c> or <c>"A"</c> gets the same key on both sides.
/// Mirrors <c>html5/src/input/Keys.js</c>; keep the two in step.
/// </remarks>
public static class KeyNames
{
    // -------------------------------------------------------------------------
    // Tables
    // -------------------------------------------------------------------------

    /// <summary>Explicit browser-code to XNA-name pairs; anything regular is handled by rule.</summary>
    private static readonly Dictionary<string, string> CodeMap = new(StringComparer.Ordinal)
    {
        ["ArrowUp"] = "Up", ["ArrowDown"] = "Down", ["ArrowLeft"] = "Left", ["ArrowRight"] = "Right",
        ["ShiftLeft"] = "LeftShift", ["ShiftRight"] = "RightShift",
        ["ControlLeft"] = "LeftControl", ["ControlRight"] = "RightControl",
        ["AltLeft"] = "LeftAlt", ["AltRight"] = "RightAlt",
        ["MetaLeft"] = "LeftWindows", ["MetaRight"] = "RightWindows",
        ["Backspace"] = "Back", ["Backquote"] = "OemTilde", ["Minus"] = "OemMinus", ["Equal"] = "OemPlus",
        ["BracketLeft"] = "OemOpenBrackets", ["BracketRight"] = "OemCloseBrackets",
        ["Backslash"] = "OemPipe", ["Semicolon"] = "OemSemicolon", ["Quote"] = "OemQuotes",
        ["Comma"] = "OemComma", ["Period"] = "OemPeriod", ["Slash"] = "OemQuestion",
        ["NumpadAdd"] = "Add", ["NumpadSubtract"] = "Subtract", ["NumpadMultiply"] = "Multiply",
        ["NumpadDivide"] = "Divide", ["NumpadDecimal"] = "Decimal", ["NumpadEnter"] = "Enter",
        ["ContextMenu"] = "Apps", ["PrintScreen"] = "PrintScreen", ["ScrollLock"] = "Scroll",
        ["CapsLock"] = "CapsLock", ["NumLock"] = "NumLock", ["Pause"] = "Pause",
    };

    private static readonly ConcurrentDictionary<string, Keys?> Cache = new(StringComparer.Ordinal);

    // -------------------------------------------------------------------------
    // API
    // -------------------------------------------------------------------------

    /// <summary>The XNA spelling of a browser code or a loosely spelt name: "KeyA", "a" and "A" all give "A".</summary>
    public static string Canonical(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        if (CodeMap.TryGetValue(name, out var mapped)) return mapped;

        if (name.StartsWith("Key", StringComparison.Ordinal) && name.Length == 4) return name.Substring(3);
        if (name.StartsWith("Digit", StringComparison.Ordinal) && name.Length == 6) return "D" + name.Substring(5);
        if (name.StartsWith("Numpad", StringComparison.Ordinal)) return "NumPad" + name.Substring(6);

        if (name.Length == 1)
        {
            if (char.IsLetter(name[0])) return name.ToUpperInvariant();
            if (char.IsDigit(name[0]))  return "D" + name;
        }

        return name;
    }

    /// <summary>Parses any accepted spelling into a <see cref="Keys"/> value. Case-insensitive on the final name.</summary>
    public static bool TryParse(string? name, out Keys key)
    {
        key = Keys.None;
        if (string.IsNullOrEmpty(name)) return false;

        var cached = Cache.GetOrAdd(name, static n =>
            Enum.TryParse<Keys>(Canonical(n), ignoreCase: true, out var parsed) && parsed != Keys.None
                ? parsed
                : null);

        if (cached is null) return false;
        key = cached.Value;
        return true;
    }
}
