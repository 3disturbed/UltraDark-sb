using SexyBiscuit.Engine.Input;

namespace Cookies.InputMapping;

/// <summary>
/// Turns a binding into something readable, for a prompt on screen or a row in a settings menu.
/// </summary>
public static class InputGlyphs
{
    /// <summary>"Space", "LMB", "A button", "Left Stick" — one short label for a binding.</summary>
    public static string Describe(InputBinding binding) => binding.Device switch
    {
        "keyboard" => Keyboard(binding),
        "mouse"    => Mouse(binding),
        "gamepad"  => Gamepad(binding),
        "touch"    => Touch(binding),
        _          => "unbound",
    };

    /// <summary>
    /// The label for an action, preferring a device. Falls back to the first binding, so a prompt
    /// still says something when the player has no pad.
    /// </summary>
    public static string DescribeAction(ActionMap map, string action, string? preferDevice = null)
    {
        if (!map.Actions.TryGetValue(action, out var bound) || bound.Bindings.Count == 0) return "unbound";

        if (preferDevice != null)
            foreach (var binding in bound.Bindings)
                if (string.Equals(binding.Device, preferDevice, StringComparison.OrdinalIgnoreCase))
                    return Describe(binding);

        return Describe(bound.Bindings[0]);
    }

    /// <summary>Which device the player is using, so prompts can follow them without being asked.</summary>
    public static string ActiveDevice(InputManager input, int playerIndex = 0)
    {
        var player = input.GetPlayer(playerIndex);

        if (player.Gamepad is { IsConnected: true } pad)
        {
            if (pad.LeftStick.LengthSquared() > 0.25f || pad.RightStick.LengthSquared() > 0.25f) return "gamepad";
            foreach (var button in Enum.GetValues<Microsoft.Xna.Framework.Input.Buttons>())
                if (pad.IsButtonDown(button)) return "gamepad";
        }

        if (input.Touch.Touches.Count > 0) return "touch";
        return "keyboard";
    }

    private static string Keyboard(InputBinding binding)
    {
        if (binding.PosKey != null && binding.NegKey != null) return $"{Pretty(binding.NegKey)}/{Pretty(binding.PosKey)}";
        return Pretty(binding.Key ?? binding.PosKey ?? binding.NegKey ?? "unbound");
    }

    private static string Mouse(InputBinding binding) => binding.Axis switch
    {
        "MouseX" => "Mouse X",
        "MouseY" => "Mouse Y",
        "Scroll" => "Scroll wheel",
        _        => binding.Button switch
        {
            "Left"     => "LMB",
            "Right"    => "RMB",
            "Middle"   => "MMB",
            "XButton1" => "Mouse 4",
            "XButton2" => "Mouse 5",
            _          => "Mouse",
        },
    };

    private static string Gamepad(InputBinding binding)
    {
        if (binding.Axis != null)
            return binding.Axis switch
            {
                "LeftX" or "LeftY"   => "Left Stick",
                "RightX" or "RightY" => "Right Stick",
                "LeftTrigger"        => "LT",
                "RightTrigger"       => "RT",
                _                    => binding.Axis,
            };

        return binding.Button switch
        {
            null            => "Gamepad",
            "LeftShoulder"  => "LB",
            "RightShoulder" => "RB",
            "LeftStick"     => "L3",
            "RightStick"    => "R3",
            "DPadUp"        => "D-Pad Up",
            "DPadDown"      => "D-Pad Down",
            "DPadLeft"      => "D-Pad Left",
            "DPadRight"     => "D-Pad Right",
            var other       => other.Length == 1 ? other + " button" : Spaced(other),
        };
    }

    private static string Touch(InputBinding binding) => binding.Axis switch
    {
        "LeftJoystickX" or "LeftJoystickY"   => "Left thumb",
        "RightJoystickX" or "RightJoystickY" => "Right thumb",
        "PinchDelta"                         => "Pinch",
        _                                    => "Tap",
    };

    private static string Pretty(string key) => key switch
    {
        "Space"        => "Space",
        "LeftShift"    => "Left Shift",
        "RightShift"   => "Right Shift",
        "LeftControl"  => "Left Ctrl",
        "RightControl" => "Right Ctrl",
        "LeftAlt"      => "Left Alt",
        "RightAlt"     => "Right Alt",
        "Escape"       => "Esc",
        "Enter"        => "Enter",
        _              => key.StartsWith("D", StringComparison.Ordinal) && key.Length == 2 && char.IsDigit(key[1])
                          ? key[1..]
                          : Spaced(key),
    };

    /// <summary>"RightShoulder" reads as "Right Shoulder".</summary>
    private static string Spaced(string name)
    {
        var text = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) text.Append(' ');
            text.Append(name[i]);
        }
        return text.ToString();
    }
}
