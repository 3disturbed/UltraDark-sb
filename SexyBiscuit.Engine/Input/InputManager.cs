using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

// Alias the XNA struct types to avoid ambiguity with our own wrappers.
using XnaKeyboardState  = Microsoft.Xna.Framework.Input.KeyboardState;
using XnaMouseState     = Microsoft.Xna.Framework.Input.MouseState;

namespace SexyBiscuit.Engine.Input;

/// <summary>
/// Unified input manager.  Updated once per frame by <c>SBEngine.Update</c>.
/// Provides per-frame keyboard/mouse/gamepad queries plus a string-keyed
/// action-map layer with rebinding and persistence.
/// </summary>
public sealed class InputManager
{
    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------
    private readonly EngineConfig _config;

    // -------------------------------------------------------------------------
    // Keyboard
    // -------------------------------------------------------------------------
    private XnaKeyboardState _kbCurrent;
    private XnaKeyboardState _kbPrevious;

    // -------------------------------------------------------------------------
    // Mouse
    // -------------------------------------------------------------------------
    private XnaMouseState _msCurrent;
    private XnaMouseState _msPrevious;

    private bool _cursorLocked;
    private Point _lockedPosition;

    // -------------------------------------------------------------------------
    // Gamepads (0–3)
    // -------------------------------------------------------------------------
    private readonly GamepadState[] _gamepads =
    {
        new(0), new(1), new(2), new(3)
    };

    // -------------------------------------------------------------------------
    // Touch
    // -------------------------------------------------------------------------
    public TouchManager Touch { get; } = new();

    // -------------------------------------------------------------------------
    // Action map
    // -------------------------------------------------------------------------
    private ActionMap _actionMap;
    private ActionMap _defaultMap;   // kept for ResetBindings()

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public InputManager(EngineConfig config)
    {
        _config     = config ?? throw new ArgumentNullException(nameof(config));
        _defaultMap = ActionMap.Default();
        _actionMap  = ActionMap.Default();
    }

    // =========================================================================
    // Frame update
    // =========================================================================
    public void Update(float dt)
    {
        Sample(Keyboard.GetState(), Mouse.GetState());

        if (_cursorLocked)
        {
            // Warp the cursor back to the locked position each frame.
            Mouse.SetPosition(_lockedPosition.X, _lockedPosition.Y);
        }

        // --- Gamepads ---
        foreach (var gp in _gamepads)
            gp.Update(dt);

        // --- Touch ---
        Touch.Update();
    }

    private bool _primed;

    // The per-frame state step, separated from the hardware reads so it can be tested.
    internal void Sample(KeyboardState keyboard, MouseState mouse)
    {
        _kbPrevious = _kbCurrent;
        _kbCurrent  = keyboard;
        _msPrevious = _msCurrent;
        _msCurrent  = mouse;

        // The first sample after construction, or after ResetDeltas, has no meaningful
        // "previous": reporting the cursor's absolute position as a delta spun the player
        // round on the first frame of play.
        if (!_primed)
        {
            _kbPrevious = _kbCurrent;
            _msPrevious = _msCurrent;
            _primed     = true;
        }
    }

    /// <summary>
    /// Forgets the previous frame, so the next <see cref="Update"/> reports no key presses,
    /// no scroll and no mouse movement. Call when input resumes after a gap, such as the
    /// editor entering play mode, or the stale frame becomes a jump.
    /// </summary>
    public void ResetDeltas() => _primed = false;

    // =========================================================================
    // Keyboard
    // =========================================================================

    /// <summary>True while the key is held down this frame.</summary>
    public bool IsKeyDown(Keys key)
        => _kbCurrent.IsKeyDown(key);

    /// <summary>True only on the frame the key was first pressed (down now, up last frame).</summary>
    public bool IsKeyPressed(Keys key)
        => _kbCurrent.IsKeyDown(key) && !_kbPrevious.IsKeyDown(key);

    /// <summary>True only on the frame the key was released (up now, down last frame).</summary>
    public bool IsKeyReleased(Keys key)
        => !_kbCurrent.IsKeyDown(key) && _kbPrevious.IsKeyDown(key);

    // =========================================================================
    // Mouse — position & movement
    // =========================================================================

    /// <summary>Current mouse cursor position in screen pixels.</summary>
    public Vector2 MousePosition
        => new(_msCurrent.X, _msCurrent.Y);

    /// <summary>
    /// Pixel movement of the cursor since last frame. While the cursor is locked it is the
    /// movement since the lock point, because every frame ends with the cursor warped back
    /// there: measuring against the previous sample would cancel a steady motion out.
    /// </summary>
    public Vector2 MouseDelta
        => _cursorLocked
            ? new(_msCurrent.X - _lockedPosition.X, _msCurrent.Y - _lockedPosition.Y)
            : new(_msCurrent.X - _msPrevious.X,     _msCurrent.Y - _msPrevious.Y);

    /// <summary>Scroll wheel delta this frame (positive = scroll up).</summary>
    public float ScrollDelta
        => (_msCurrent.ScrollWheelValue - _msPrevious.ScrollWheelValue) / 120f;

    // =========================================================================
    // Mouse — buttons
    // =========================================================================

    /// <summary>True while the mouse button is held this frame.</summary>
    public bool IsMouseButtonDown(MouseButton btn)
        => GetButtonState(_msCurrent, btn) == ButtonState.Pressed;

    /// <summary>True only on the frame the button was first pressed.</summary>
    public bool IsMouseButtonPressed(MouseButton btn)
        => GetButtonState(_msCurrent, btn)  == ButtonState.Pressed
        && GetButtonState(_msPrevious, btn) == ButtonState.Released;

    /// <summary>True only on the frame the button was released.</summary>
    public bool IsMouseButtonReleased(MouseButton btn)
        => GetButtonState(_msCurrent, btn)  == ButtonState.Released
        && GetButtonState(_msPrevious, btn) == ButtonState.Pressed;

    // =========================================================================
    // Mouse — cursor management
    // =========================================================================

    /// <summary>
    /// How the host shows or hides the window's cursor. <c>SBEngine</c> and the editor set it;
    /// with nothing set the request only updates <see cref="IsCursorVisible"/>.
    /// </summary>
    public Action<bool>? CursorVisibilityChanged { get; set; }

    /// <summary>What the game last asked for. The editor keeps its own cursor outside play mode.</summary>
    public bool IsCursorVisible { get; private set; } = true;

    /// <summary>True while <see cref="LockCursor"/> is in effect.</summary>
    public bool IsCursorLocked => _cursorLocked;

    public void ShowCursor() => SetCursorVisible(true);

    public void HideCursor() => SetCursorVisible(false);

    private void SetCursorVisible(bool visible)
    {
        IsCursorVisible = visible;
        if (CursorVisibilityChanged != null) CursorVisibilityChanged(visible);
        else if (SBEngine.Instance is { } game) game.IsMouseVisible = visible;
    }

    /// <summary>
    /// Locks the cursor to its current position. The cursor is hidden automatically.
    /// Mouse delta is still reported correctly.
    /// </summary>
    public void LockCursor()
    {
        // The lock point is where the cursor is now, not where the last sample saw it: before
        // the first Update of a session that sample is empty and the lock landed at (0, 0).
        MouseState now;
        try { now = Mouse.GetState(); }
        catch (Exception) { now = _msCurrent; }   // no window (tests): fall back to the last sample

        _cursorLocked   = true;
        _lockedPosition = new Point(now.X, now.Y);
        _msCurrent      = now;                     // so the first locked delta is zero
        HideCursor();
    }

    /// <summary>Unlocks the cursor and restores visibility.</summary>
    public void UnlockCursor()
    {
        _cursorLocked = false;
        ShowCursor();
    }

    /// <summary>
    /// Custom cursor texture is not supported on DesktopGL (MonoGame 3.8).
    /// The call is intentionally a no-op; a diagnostic message is emitted in
    /// debug builds.
    /// </summary>
    public void SetCursorTexture(Texture2D tex)
    {
        System.Diagnostics.Debug.WriteLine(
            "[InputManager] SetCursorTexture: custom cursors are not implemented " +
            "on the DesktopGL backend. Use HideCursor() and draw a sprite manually.");
    }

    // =========================================================================
    // Gamepad
    // =========================================================================

    /// <summary>Returns the <see cref="GamepadState"/> wrapper for the given player (0–3).</summary>
    public GamepadState GetGamepad(int playerIndex)
    {
        ValidatePlayerIndex(playerIndex);
        return _gamepads[playerIndex];
    }

    /// <summary>Returns true if the gamepad at the given player index is currently connected.</summary>
    public bool IsGamepadConnected(int playerIndex)
    {
        ValidatePlayerIndex(playerIndex);
        return _gamepads[playerIndex].IsConnected;
    }

    /// <summary>Starts a timed rumble effect on the specified controller.</summary>
    public void SetRumble(int playerIndex, float lowFreq, float highFreq, float duration)
    {
        ValidatePlayerIndex(playerIndex);
        _gamepads[playerIndex].SetRumble(lowFreq, highFreq, duration);
    }

    // =========================================================================
    // Action map — loading & persistence
    // =========================================================================

    /// <summary>
    /// Loads an action map from a JSON file, replacing the current map.
    /// Also caches the loaded map as the new "default" for <see cref="ResetBindings"/>.
    /// </summary>
    public void LoadActionMap(string jsonPath)
    {
        _actionMap  = ActionMap.LoadFromJson(jsonPath);
        _defaultMap = ActionMap.LoadFromJson(jsonPath);   // reset baseline
    }

    /// <summary>Replaces all bindings for the named action.</summary>
    public void RebindAction(string actionName, InputBinding newBinding)
    {
        if (!_actionMap.Actions.TryGetValue(actionName, out var action))
        {
            action = new InputAction { Name = actionName };
            _actionMap.Actions[actionName] = action;
        }
        // Replace all bindings for this action with the single new one.
        action.Bindings.Clear();
        action.Bindings.Add(newBinding);
    }

    /// <summary>Serialises the current action map to <paramref name="path"/>.</summary>
    public void SaveBindings(string path) => _actionMap.SaveToFile(path);

    /// <summary>Deserialises bindings from <paramref name="path"/> into the current action map.</summary>
    public void LoadBindings(string path)
    {
        var loaded = ActionMap.LoadFromJson(path);
        foreach (var (name, action) in loaded.Actions)
            _actionMap.Actions[name] = action;
    }

    /// <summary>Restores the action map to the state it was in when last loaded from disk, or the built-in default.</summary>
    public void ResetBindings()
        => _actionMap = _defaultMap.Clone();

    // =========================================================================
    // Action map — queries
    // =========================================================================

    /// <summary>
    /// True on the frame the action transitions from inactive to active
    /// (equivalent to "button pressed" semantics).
    /// </summary>
    public bool IsPressed(string action)
    {
        if (!_actionMap.Actions.TryGetValue(action, out var act)) return false;
        return act.Bindings.Any(EvaluatePressed);
    }

    /// <summary>True while any binding for the action is continuously active.</summary>
    public bool IsHeld(string action)
    {
        if (!_actionMap.Actions.TryGetValue(action, out var act)) return false;
        return act.Bindings.Any(EvaluateHeld);
    }

    /// <summary>True on the frame the action transitions from active to inactive.</summary>
    public bool IsReleased(string action)
    {
        if (!_actionMap.Actions.TryGetValue(action, out var act)) return false;
        return act.Bindings.Any(EvaluateReleased);
    }

    /// <summary>
    /// Returns the axis value (-1..1) for the action.
    /// For digital bindings the value is -1, 0, or +1.
    /// Returns the first non-zero value across all bindings, or 0.
    /// </summary>
    public float GetAxis(string action)
    {
        if (!_actionMap.Actions.TryGetValue(action, out var act)) return 0f;
        foreach (var binding in act.Bindings)
        {
            var v = EvaluateAxis(binding);
            if (v != 0f) return v;
        }
        return 0f;
    }

    // =========================================================================
    // Action evaluation internals
    // =========================================================================

    private bool EvaluateHeld(InputBinding b) => b.Device switch
    {
        "keyboard" => EvalKeyboardHeld(b),
        "mouse"    => EvalMouseHeld(b),
        "gamepad"  => EvalGamepadHeld(b),
        _          => false
    };

    private bool EvaluatePressed(InputBinding b) => b.Device switch
    {
        "keyboard" => EvalKeyboardPressed(b),
        "mouse"    => EvalMousePressed(b),
        "gamepad"  => EvalGamepadPressed(b),
        _          => false
    };

    private bool EvaluateReleased(InputBinding b) => b.Device switch
    {
        "keyboard" => EvalKeyboardReleased(b),
        "mouse"    => EvalMouseReleased(b),
        "gamepad"  => EvalGamepadReleased(b),
        _          => false
    };

    private float EvaluateAxis(InputBinding b)
    {
        float raw = b.Device switch
        {
            "keyboard" => EvalKeyboardAxis(b),
            "mouse"    => EvalMouseAxis(b),
            "gamepad"  => EvalGamepadAxis(b),
            _          => 0f
        };
        raw *= b.Scale;
        if (b.Invert) raw = -raw;
        return raw;
    }

    // -----------------------------------------------------------------------
    // Keyboard evaluation
    // -----------------------------------------------------------------------

    private bool EvalKeyboardHeld(InputBinding b)
    {
        if (b.Key != null && TryParseKey(b.Key, out var k)) return IsKeyDown(k);
        if (b.PosKey != null && TryParseKey(b.PosKey, out var pk)) return IsKeyDown(pk);
        if (b.NegKey != null && TryParseKey(b.NegKey, out var nk)) return IsKeyDown(nk);
        return false;
    }

    private bool EvalKeyboardPressed(InputBinding b)
    {
        if (b.Key != null && TryParseKey(b.Key, out var k)) return IsKeyPressed(k);
        if (b.PosKey != null && TryParseKey(b.PosKey, out var pk)) return IsKeyPressed(pk);
        if (b.NegKey != null && TryParseKey(b.NegKey, out var nk)) return IsKeyPressed(nk);
        return false;
    }

    private bool EvalKeyboardReleased(InputBinding b)
    {
        if (b.Key != null && TryParseKey(b.Key, out var k)) return IsKeyReleased(k);
        if (b.PosKey != null && TryParseKey(b.PosKey, out var pk)) return IsKeyReleased(pk);
        if (b.NegKey != null && TryParseKey(b.NegKey, out var nk)) return IsKeyReleased(nk);
        return false;
    }

    private float EvalKeyboardAxis(InputBinding b)
    {
        float v = 0f;
        if (b.PosKey != null && TryParseKey(b.PosKey, out var pk) && IsKeyDown(pk)) v += 1f;
        if (b.NegKey != null && TryParseKey(b.NegKey, out var nk) && IsKeyDown(nk)) v -= 1f;
        if (v == 0f && b.Key != null && TryParseKey(b.Key, out var k) && IsKeyDown(k)) v = 1f;
        return v;
    }

    // -----------------------------------------------------------------------
    // Mouse evaluation
    // -----------------------------------------------------------------------

    private bool EvalMouseHeld(InputBinding b)
    {
        if (b.Button != null && TryParseMouseButton(b.Button, out var mb)) return IsMouseButtonDown(mb);
        return false;
    }

    private bool EvalMousePressed(InputBinding b)
    {
        if (b.Button != null && TryParseMouseButton(b.Button, out var mb)) return IsMouseButtonPressed(mb);
        return false;
    }

    private bool EvalMouseReleased(InputBinding b)
    {
        if (b.Button != null && TryParseMouseButton(b.Button, out var mb)) return IsMouseButtonReleased(mb);
        return false;
    }

    private float EvalMouseAxis(InputBinding b) => b.Axis switch
    {
        "MouseX" => MouseDelta.X,
        "MouseY" => MouseDelta.Y,
        "Scroll" => ScrollDelta,
        _        => 0f
    };

    // -----------------------------------------------------------------------
    // Gamepad evaluation (player 0 only for action map)
    // -----------------------------------------------------------------------

    private bool EvalGamepadHeld(InputBinding b)
    {
        var gp = _gamepads[0];
        if (!gp.IsConnected) return false;
        if (b.Button != null && TryParseGpButton(b.Button, out var btn)) return gp.IsButtonDown(btn);
        if (b.Axis   != null)
        {
            var v = gp.GetAxis(b.Axis);
            return MathF.Abs(v) > 0.3f;
        }
        return false;
    }

    private bool EvalGamepadPressed(InputBinding b)
    {
        var gp = _gamepads[0];
        if (!gp.IsConnected) return false;
        if (b.Button != null && TryParseGpButton(b.Button, out var btn)) return gp.IsButtonPressed(btn);
        return false;
    }

    private bool EvalGamepadReleased(InputBinding b)
    {
        var gp = _gamepads[0];
        if (!gp.IsConnected) return false;
        if (b.Button != null && TryParseGpButton(b.Button, out var btn)) return gp.IsButtonReleased(btn);
        return false;
    }

    private float EvalGamepadAxis(InputBinding b)
    {
        var gp = _gamepads[0];
        if (!gp.IsConnected) return 0f;
        if (b.Axis != null) return gp.GetAxis(b.Axis);
        return 0f;
    }

    // =========================================================================
    // Parsing helpers
    // =========================================================================

    private static bool TryParseKey(string name, out Keys key)
        => Enum.TryParse(name, ignoreCase: true, out key);

    private static bool TryParseMouseButton(string name, out MouseButton btn)
        => Enum.TryParse(name, ignoreCase: true, out btn);

    private static bool TryParseGpButton(string name, out Buttons btn)
        => Enum.TryParse(name, ignoreCase: true, out btn);

    // =========================================================================
    // Mouse button → ButtonState helpers
    // =========================================================================

    private static ButtonState GetButtonState(XnaMouseState ms, MouseButton btn) => btn switch
    {
        MouseButton.Left     => ms.LeftButton,
        MouseButton.Middle   => ms.MiddleButton,
        MouseButton.Right    => ms.RightButton,
        MouseButton.XButton1 => ms.XButton1,
        MouseButton.XButton2 => ms.XButton2,
        _                    => ButtonState.Released
    };

    // =========================================================================
    // Misc helpers
    // =========================================================================

    private static void ValidatePlayerIndex(int idx)
    {
        if (idx < 0 || idx > 3)
            throw new ArgumentOutOfRangeException(nameof(idx), "Player index must be 0–3.");
    }
}
