using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

// Alias the XNA struct to avoid collision with our wrapper class name.
using XnaGamePadState = Microsoft.Xna.Framework.Input.GamePadState;

namespace SexyBiscuit.Engine.Input;

/// <summary>
/// Per-player gamepad state wrapper. Tracks current and previous
/// <see cref="XnaGamePadState"/> to provide pressed/released queries,
/// applies dead-zone filtering, and manages timed rumble effects.
/// </summary>
public sealed class GamepadState
{
    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------

    /// <summary>Stick values whose magnitude is below this threshold are clamped to zero.</summary>
    public float DeadZone { get; set; } = 0.15f;

    // -------------------------------------------------------------------------
    // Player identity
    // -------------------------------------------------------------------------
    public int PlayerIndex { get; }

    // -------------------------------------------------------------------------
    // Raw XNA states
    // -------------------------------------------------------------------------
    private XnaGamePadState _current;
    private XnaGamePadState _previous;

    // -------------------------------------------------------------------------
    // Rumble tracking
    // -------------------------------------------------------------------------
    private float _rumbleLow;
    private float _rumbleHigh;
    private float _rumbleTimeRemaining;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public GamepadState(int playerIndex)
    {
        if (playerIndex < 0 || playerIndex > 3)
            throw new ArgumentOutOfRangeException(nameof(playerIndex), "Player index must be 0–3.");
        PlayerIndex = playerIndex;
    }

    // -------------------------------------------------------------------------
    // Frame update (called by InputManager)
    // -------------------------------------------------------------------------
    internal void Update(float dt)
    {
        _previous = _current;
        _current  = GamePad.GetState(PlayerIndex);

        // Tick active rumble timer.
        if (_rumbleTimeRemaining > 0f)
        {
            _rumbleTimeRemaining -= dt;
            if (_rumbleTimeRemaining <= 0f)
            {
                _rumbleTimeRemaining = 0f;
                GamePad.SetVibration(PlayerIndex, 0f, 0f);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Connectivity
    // -------------------------------------------------------------------------
    public bool IsConnected => _current.IsConnected;

    // -------------------------------------------------------------------------
    // Sticks (dead-zone applied, rescaled so the usable range starts at 0)
    // -------------------------------------------------------------------------
    public Vector2 LeftStick  => ApplyDeadZone(_current.ThumbSticks.Left);
    public Vector2 RightStick => ApplyDeadZone(_current.ThumbSticks.Right);

    // -------------------------------------------------------------------------
    // Triggers (raw 0–1 float)
    // -------------------------------------------------------------------------
    public float LeftTrigger  => _current.Triggers.Left;
    public float RightTrigger => _current.Triggers.Right;

    // -------------------------------------------------------------------------
    // Buttons
    // -------------------------------------------------------------------------
    /// <summary>Returns true while the button is held this frame.</summary>
    public bool IsButtonDown(Buttons btn)
        => _current.IsButtonDown(btn);

    /// <summary>Returns true only on the frame the button was first pressed.</summary>
    public bool IsButtonPressed(Buttons btn)
        => _current.IsButtonDown(btn) && !_previous.IsButtonDown(btn);

    /// <summary>Returns true only on the frame the button was released.</summary>
    public bool IsButtonReleased(Buttons btn)
        => !_current.IsButtonDown(btn) && _previous.IsButtonDown(btn);

    // -------------------------------------------------------------------------
    // Rumble
    // -------------------------------------------------------------------------
    /// <summary>
    /// Activates rumble motors and stops them automatically after
    /// <paramref name="duration"/> seconds.
    /// </summary>
    public void SetRumble(float lowFreq, float highFreq, float duration)
    {
        _rumbleLow           = Math.Clamp(lowFreq,  0f, 1f);
        _rumbleHigh          = Math.Clamp(highFreq, 0f, 1f);
        _rumbleTimeRemaining = MathF.Max(duration, 0f);
        GamePad.SetVibration(PlayerIndex, _rumbleLow, _rumbleHigh);
    }

    /// <summary>Stops any active rumble immediately.</summary>
    public void StopRumble()
    {
        _rumbleLow = _rumbleHigh = _rumbleTimeRemaining = 0f;
        GamePad.SetVibration(PlayerIndex, 0f, 0f);
    }

    // -------------------------------------------------------------------------
    // Axis helpers — consumed by InputManager action evaluation
    // -------------------------------------------------------------------------
    /// <summary>
    /// Returns a scalar axis value in -1..1 for the named axis.
    /// Recognised names: LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger.
    /// </summary>
    public float GetAxis(string axisName) => axisName switch
    {
        "LeftX"        =>  LeftStick.X,
        "LeftY"        =>  LeftStick.Y,
        "RightX"       =>  RightStick.X,
        "RightY"       =>  RightStick.Y,
        "LeftTrigger"  =>  LeftTrigger,
        "RightTrigger" =>  RightTrigger,
        _              =>  0f
    };

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------
    private Vector2 ApplyDeadZone(Vector2 v)
    {
        var magnitude = v.Length();
        if (magnitude < DeadZone) return Vector2.Zero;

        // Rescale the usable range so it begins at exactly 0 beyond the dead zone.
        var dir   = v / magnitude;                               // normalise
        var scale = (magnitude - DeadZone) / (1f - DeadZone);   // remap [dz..1] → [0..1]
        return dir * Math.Clamp(scale, 0f, 1f);
    }
}
