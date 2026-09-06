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
public sealed class InputManager : IInputSource
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

        // The virtual layer's edges advance once a frame, whoever is pushing it.
        foreach (var player in _players) player?.Tick();
    }

    private readonly PlayerInput?[] _players = new PlayerInput?[4];
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

    /// <summary>True while <see cref="LockCursor()"/> is in effect.</summary>
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
        catch (Exception) { now = _msCurrent; }
        LockCursor(now);
    }

    // Locks at an explicit position; the hardware read above is what tests cannot do.
    internal void LockCursor(MouseState now)
    {
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
    public bool IsPressed(string action) => GetPlayer(0).IsPressed(action);

    /// <summary>True while any binding for <paramref name="action"/> is held.</summary>
    public bool IsHeld(string action) => GetPlayer(0).IsHeld(action);

    /// <summary>True on the frame any binding for <paramref name="action"/> comes up.</summary>
    public bool IsReleased(string action) => GetPlayer(0).IsReleased(action);

    /// <summary>
    /// The action's value from -1 to 1. The first binding with a non-zero value wins, so a stick
    /// at rest falls through to the keys behind it.
    /// </summary>
    public float GetAxis(string action) => GetPlayer(0).GetAxis(action);

    // =========================================================================
    // Players
    // =========================================================================

    // The whole machine's view of the devices, for the evaluator. Implemented explicitly so the
    // manager's public surface does not change by a character.
    GamepadState? IInputSource.ActiveGamepad => _gamepads[0];
    TouchManager? IInputSource.ActiveTouch   => Touch;

    /// <summary>The live action map, so a rebinding screen can read and edit it.</summary>
    /// <remarks>
    /// <see cref="RebindAction"/> replaces every binding an action has, which is right for "bind
    /// this to that" and wrong for editing one slot of several. Editing this map in place is how a
    /// rebinding UI keeps an action's gamepad and touch alternates.
    /// </remarks>
    public ActionMap ActionMap => _actionMap;

    /// <summary>
    /// One player's view of the devices. Player zero owns every device and pad zero, so the
    /// manager's own queries -- which go through it -- behave exactly as they always have.
    /// </summary>
    public PlayerInput GetPlayer(int playerIndex)
    {
        ValidatePlayerIndex(playerIndex);
        return _players[playerIndex] ??= new PlayerInput(this, playerIndex);
    }

    /// <summary>The player views that have been asked for, in index order.</summary>
    public IEnumerable<PlayerInput> Players => _players.Where(p => p != null)!;

    /// <summary>Feeds one gamepad a state directly, so per-player routing can be tested.</summary>
    internal void SampleGamepad(int playerIndex, Microsoft.Xna.Framework.Input.GamePadState state)
    {
        ValidatePlayerIndex(playerIndex);
        _gamepads[playerIndex].Update(0f, state);
    }

    /// <summary>Keys that went down this frame, for a "press anything to join" screen.</summary>
    public IReadOnlyList<Keys> PressedKeys
    {
        get
        {
            var pressed = new List<Keys>();
            foreach (var key in _kbCurrent.GetPressedKeys())
                if (!_kbPrevious.IsKeyDown(key)) pressed.Add(key);
            return pressed;
        }
    }



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
