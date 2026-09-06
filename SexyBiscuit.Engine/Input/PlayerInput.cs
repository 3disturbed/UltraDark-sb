using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace SexyBiscuit.Engine.Input;

/// <summary>Which devices a player owns. A device belongs to one player at a time.</summary>
[Flags]
public enum InputDeviceKind
{
    None             = 0,
    Keyboard         = 1,
    Mouse            = 2,
    Gamepad          = 4,
    Touch            = 8,
    KeyboardAndMouse = Keyboard | Mouse,
    All              = Keyboard | Mouse | Gamepad | Touch,
}

/// <summary>
/// One player's view of the input devices, and the actions they resolve to. Player zero owns
/// everything by default, so a single-player game behaves exactly as it did before this existed.
/// </summary>
/// <remarks>
/// A per-player object rather than a player-index argument on the manager's queries: an argument
/// can only say "the same keyboard, a different pad", and couch co-op needs player two to read
/// neither the keyboard nor pad zero.
/// </remarks>
public sealed class PlayerInput : IInputSource
{
    private readonly InputManager _manager;
    private readonly Dictionary<string, VirtualAction> _virtual = new(StringComparer.OrdinalIgnoreCase);

    internal PlayerInput(InputManager manager, int playerIndex)
    {
        _manager     = manager;
        PlayerIndex  = playerIndex;
        GamepadIndex = playerIndex;
    }

    /// <summary>Which player this is. Zero is the local player a single-player game uses.</summary>
    public int PlayerIndex { get; }

    /// <summary>Which gamepad this player reads. Defaults to their player index.</summary>
    public int GamepadIndex { get; set; }

    /// <summary>The devices this player owns. Everything, until a co-op director divides them up.</summary>
    public InputDeviceKind Devices { get; set; } = InputDeviceKind.All;

    /// <summary>A map just for this player, or null to share the manager's.</summary>
    public ActionMap? OverrideMap { get; set; }

    /// <summary>The map this player's actions resolve through.</summary>
    public ActionMap Map => OverrideMap ?? _manager.ActionMap;

    public bool Owns(InputDeviceKind device) => (Devices & device) != 0;

    // -------------------------------------------------------------------------
    // Actions
    // -------------------------------------------------------------------------

    /// <summary>True on the frame this player's binding for the action goes down.</summary>
    public bool IsPressed(string action)
        => _virtual.TryGetValue(action, out var v) && v.Down && !v.WasDown
        || ActionEvaluator.IsPressed(Map, this, action);

    /// <summary>True while this player holds the action.</summary>
    public bool IsHeld(string action)
        => _virtual.TryGetValue(action, out var v) && v.Down
        || ActionEvaluator.IsHeld(Map, this, action);

    /// <summary>True on the frame this player lets the action go.</summary>
    public bool IsReleased(string action)
        => _virtual.TryGetValue(action, out var v) && !v.Down && v.WasDown
        || ActionEvaluator.IsReleased(Map, this, action);

    /// <summary>The action's value from -1 to 1.</summary>
    public float GetAxis(string action)
    {
        if (_virtual.TryGetValue(action, out var v) && v.Axis != 0f) return v.Axis;
        return ActionEvaluator.GetAxis(Map, this, action);
    }

    // -------------------------------------------------------------------------
    // The virtual layer
    // -------------------------------------------------------------------------
    //
    // An on-screen touch button, a remote player's replicated input and an AI driving a pawn all
    // need to reach the same actions a device would, without each growing its own bypass around
    // the action map. They push here instead, and OnPlayerTick cannot tell the difference.

    /// <summary>Holds an action down until it is released.</summary>
    public void PressVirtual(string action) => State(action).Down = true;

    /// <summary>Lets a virtually held action go.</summary>
    public void ReleaseVirtual(string action)
    {
        if (_virtual.TryGetValue(action, out var v)) v.Down = false;
    }

    /// <summary>Drives an axis. A non-zero value wins over the devices; zero falls back to them.</summary>
    public void SetVirtualAxis(string action, float value) => State(action).Axis = value;

    /// <summary>Forgets one virtual action entirely.</summary>
    public void ClearVirtual(string action) => _virtual.Remove(action);

    /// <summary>Forgets every virtual action, which is what a disconnect or a scene change wants.</summary>
    public void ClearAllVirtual() => _virtual.Clear();

    private VirtualAction State(string action)
    {
        if (!_virtual.TryGetValue(action, out var state)) _virtual[action] = state = new VirtualAction();
        return state;
    }

    /// <summary>Advances the virtual layer's edges. Called once a frame by the manager.</summary>
    internal void Tick()
    {
        foreach (var state in _virtual.Values) state.WasDown = state.Down;
    }

    private sealed class VirtualAction
    {
        public float Axis;
        public bool  Down;
        public bool  WasDown;
    }

    // -------------------------------------------------------------------------
    // Devices
    // -------------------------------------------------------------------------

    /// <summary>This player's gamepad, or null when they own none or it is unplugged.</summary>
    public GamepadState? Gamepad
        => Owns(InputDeviceKind.Gamepad) && _manager.IsGamepadConnected(GamepadIndex)
            ? _manager.GetGamepad(GamepadIndex)
            : null;

    /// <summary>True when this player has a gamepad plugged in.</summary>
    public bool HasGamepad => Gamepad != null;

    /// <summary>Shakes this player's gamepad, if they have one.</summary>
    public void SetRumble(float low, float high, float duration)
    {
        if (Owns(InputDeviceKind.Gamepad)) _manager.SetRumble(GamepadIndex, low, high, duration);
    }

    /// <summary>
    /// True when this player did anything at all this frame. What a "press to join" screen watches
    /// on the pads nobody has claimed yet.
    /// </summary>
    public bool AnyInputThisFrame(float stickThreshold = 0.5f)
    {
        if (Owns(InputDeviceKind.Gamepad) && Gamepad is { } pad)
        {
            foreach (Buttons button in Enum.GetValues<Buttons>())
                if (pad.IsButtonPressed(button)) return true;

            if (MathF.Abs(pad.LeftStick.X)  > stickThreshold || MathF.Abs(pad.LeftStick.Y)  > stickThreshold) return true;
            if (MathF.Abs(pad.RightStick.X) > stickThreshold || MathF.Abs(pad.RightStick.Y) > stickThreshold) return true;
            if (pad.LeftTrigger > stickThreshold || pad.RightTrigger > stickThreshold) return true;
        }

        if (Owns(InputDeviceKind.Keyboard) && _manager.PressedKeys.Count > 0) return true;

        return false;
    }

    // -------------------------------------------------------------------------
    // IInputSource — a device this player does not own reads as nothing happening
    // -------------------------------------------------------------------------

    public bool IsKeyDown(Keys key)     => Owns(InputDeviceKind.Keyboard) && _manager.IsKeyDown(key);
    public bool IsKeyPressed(Keys key)  => Owns(InputDeviceKind.Keyboard) && _manager.IsKeyPressed(key);
    public bool IsKeyReleased(Keys key) => Owns(InputDeviceKind.Keyboard) && _manager.IsKeyReleased(key);

    public bool IsMouseButtonDown(MouseButton button)     => Owns(InputDeviceKind.Mouse) && _manager.IsMouseButtonDown(button);
    public bool IsMouseButtonPressed(MouseButton button)  => Owns(InputDeviceKind.Mouse) && _manager.IsMouseButtonPressed(button);
    public bool IsMouseButtonReleased(MouseButton button) => Owns(InputDeviceKind.Mouse) && _manager.IsMouseButtonReleased(button);

    public Vector2 MouseDelta  => Owns(InputDeviceKind.Mouse) ? _manager.MouseDelta  : Vector2.Zero;
    public float   ScrollDelta => Owns(InputDeviceKind.Mouse) ? _manager.ScrollDelta : 0f;

    GamepadState? IInputSource.ActiveGamepad => Gamepad;
    TouchManager? IInputSource.ActiveTouch   => Owns(InputDeviceKind.Touch) ? _manager.Touch : null;
}
