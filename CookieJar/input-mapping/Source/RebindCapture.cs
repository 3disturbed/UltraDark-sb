using Microsoft.Xna.Framework.Input;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Input;

namespace Cookies.InputMapping;

/// <summary>
/// "Press anything." Watches every key, mouse button, pad button and stick until one of them moves,
/// then hands back the binding that would reproduce it.
/// </summary>
/// <remarks>
/// Deliberately not a component: a rebinding screen owns one of these and calls
/// <see cref="Update"/> from wherever it already draws. That keeps it usable from a widget, a
/// script, or a test.
/// </remarks>
public sealed class RebindCapture
{
    /// <summary>How far a stick or trigger must move to count as a deliberate choice.</summary>
    public float AxisThreshold { get; set; } = 0.6f;

    /// <summary>Which devices may answer. Restrict it to rebind the pad without the keyboard.</summary>
    public InputDeviceKind Devices { get; set; } = InputDeviceKind.All;

    /// <summary>Keys that cancel rather than bind. Escape, by default.</summary>
    public IReadOnlyList<Keys> CancelKeys { get; set; } = new[] { Keys.Escape };

    /// <summary>The action being rebound, or null when nothing is being captured.</summary>
    public string? Action { get; private set; }

    /// <summary>Which slot of the action will be replaced.</summary>
    public int Slot { get; private set; }

    /// <summary>True between <see cref="Begin"/> and an answer.</summary>
    public bool IsCapturing => Action != null;

    /// <summary>True when the last capture ended because the player pressed a cancel key.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>Starts listening for <paramref name="action"/>'s new binding.</summary>
    public void Begin(string action, int slot = 0)
    {
        Action    = action;
        Slot      = slot;
        Cancelled = false;
    }

    /// <summary>Stops listening without binding anything.</summary>
    public void Cancel()
    {
        Action    = null;
        Cancelled = true;
    }

    /// <summary>
    /// Call once a frame while capturing. Returns the binding when something is pressed, and null
    /// until then. Capturing stops as soon as it returns one.
    /// </summary>
    public InputBinding? Update(int playerIndex = 0)
    {
        if (Action == null) return null;

        var input = EngineHost.Current?.Input;
        if (input == null) return null;

        if (Devices.HasFlag(InputDeviceKind.Keyboard))
        {
            foreach (var key in input.PressedKeys)
            {
                if (CancelKeys.Contains(key))
                {
                    Cancel();
                    return null;
                }

                Action = null;
                return new InputBinding { Device = "keyboard", Key = key.ToString() };
            }
        }

        if (Devices.HasFlag(InputDeviceKind.Mouse))
        {
            foreach (var button in Enum.GetValues<MouseButton>())
                if (input.IsMouseButtonPressed(button))
                {
                    Action = null;
                    return new InputBinding { Device = "mouse", Button = button.ToString() };
                }
        }

        if (Devices.HasFlag(InputDeviceKind.Gamepad) && input.IsGamepadConnected(playerIndex))
        {
            var pad = input.GetGamepad(playerIndex);

            foreach (var button in Enum.GetValues<Buttons>())
                if (pad.IsButtonPressed(button))
                {
                    Action = null;
                    return new InputBinding { Device = "gamepad", Button = button.ToString() };
                }

            foreach (string axis in GamepadAxes)
            {
                float value = pad.GetAxis(axis);
                if (MathF.Abs(value) < AxisThreshold) continue;

                Action = null;
                return new InputBinding { Device = "gamepad", Axis = axis, Invert = value < 0f };
            }
        }

        return null;
    }

    private static readonly string[] GamepadAxes =
        { "LeftX", "LeftY", "RightX", "RightY", "LeftTrigger", "RightTrigger" };
}
