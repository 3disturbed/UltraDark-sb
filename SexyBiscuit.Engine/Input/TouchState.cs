using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input.Touch;

namespace SexyBiscuit.Engine.Input;

// ---------------------------------------------------------------------------
// TouchPhase
// ---------------------------------------------------------------------------

/// <summary>Lifecycle phase of a single touch contact.</summary>
public enum TouchPhase
{
    Began,
    Moved,
    Stationary,
    Ended,
    Cancelled
}

// ---------------------------------------------------------------------------
// TouchPoint
// ---------------------------------------------------------------------------

/// <summary>
/// Immutable snapshot of a single touch contact at one point in time.
/// </summary>
public sealed record TouchPoint
{
    public int        Id       { get; init; }
    public Vector2    Position { get; init; }
    public Vector2    Delta    { get; init; }
    public TouchPhase Phase    { get; init; }
}

// ---------------------------------------------------------------------------
// VirtualJoystick
// ---------------------------------------------------------------------------

/// <summary>Which half of the screen a <see cref="VirtualJoystick"/> claims touches from.</summary>
public enum JoystickSide
{
    /// <summary>Claims touches starting in the left half. The movement stick.</summary>
    Left,

    /// <summary>Claims touches starting in the right half. The look stick.</summary>
    Right,

    /// <summary>Claims any touch, wherever it starts.</summary>
    Any,
}

/// <summary>
/// A software joystick driven by a single touch within a circular region.
/// Tracks the first touch whose initial contact falls in its half of the screen.
/// </summary>
public sealed class VirtualJoystick
{
    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    /// <summary>
    /// Which half of the screen this stick claims from.
    /// </summary>
    /// <remarks>
    /// Two sticks on opposite halves can never fight over the same finger, which is what lets a
    /// player move and look at once. <see cref="JoystickSide.Any"/> is for a game with a single
    /// stick and no second one to collide with.
    /// </remarks>
    public JoystickSide Side { get; set; } = JoystickSide.Left;

    /// <summary>World-space centre of the virtual joystick circle.</summary>
    public Vector2 Center { get; set; }

    /// <summary>Radius in pixels. Touches outside this range are still tracked but clamped.</summary>
    public float Radius { get; set; } = 80f;

    // -----------------------------------------------------------------------
    // Output
    // -----------------------------------------------------------------------

    /// <summary>
    /// Normalised stick value in the range -1..1 on each axis.
    /// Zero when no touch is active.
    /// </summary>
    public Vector2 Value { get; private set; }

    /// <summary>True while a touch is actively driving this joystick.</summary>
    public bool IsActive { get; private set; }

    // -----------------------------------------------------------------------
    // Internal state
    // -----------------------------------------------------------------------
    private int  _trackingId = -1;

    // -----------------------------------------------------------------------
    // Update
    // -----------------------------------------------------------------------

    /// <summary>
    /// Called each frame with the full list of active touch points.
    /// Automatically picks up the first new touch in this stick's half of the screen
    /// and releases when that touch ends.
    /// </summary>
    /// <param name="touches">All active touches this frame.</param>
    /// <param name="screenWidth">Total screen width, used to decide left/right half.</param>
    public void Update(IEnumerable<TouchPoint> touches, int screenWidth)
    {
        bool foundTrack = false;

        foreach (var touch in touches)
        {
            // Continue tracking the already-claimed touch.
            if (touch.Id == _trackingId)
            {
                if (touch.Phase is TouchPhase.Ended or TouchPhase.Cancelled)
                {
                    Release();
                    return;
                }

                var offset = touch.Position - Center;
                if (offset.LengthSquared() > 0)
                {
                    var clamped = offset.Length() > Radius
                        ? Vector2.Normalize(offset) * Radius
                        : offset;
                    Value = clamped / Radius;
                }
                else
                {
                    Value = Vector2.Zero;
                }

                IsActive   = true;
                foundTrack = true;
                break;
            }

            // Claim a new touch in our half when we don't have one.
            if (_trackingId == -1
                && touch.Phase == TouchPhase.Began
                && OwnsPosition(touch.Position, screenWidth))
            {
                _trackingId = touch.Id;
                Center      = touch.Position;   // anchor where the finger landed
                Value       = Vector2.Zero;
                IsActive    = true;
                foundTrack  = true;
            }
        }

        if (!foundTrack)
            Release();
    }

    private bool OwnsPosition(Vector2 position, int screenWidth) => Side switch
    {
        JoystickSide.Left  => position.X < screenWidth / 2f,
        JoystickSide.Right => position.X >= screenWidth / 2f,
        _                  => true,
    };

    private void Release()
    {
        _trackingId = -1;
        Value       = Vector2.Zero;
        IsActive    = false;
    }
}

// ---------------------------------------------------------------------------
// TouchManager
// ---------------------------------------------------------------------------

/// <summary>
/// Reads the MonoGame <see cref="TouchPanel"/> each frame, produces
/// <see cref="TouchPoint"/> snapshots with delta positions, detects two-finger
/// pinch gestures, and drives a <see cref="VirtualJoystick"/> for the left half
/// of the screen.
/// </summary>
public sealed class TouchManager
{
    // -----------------------------------------------------------------------
    // Public outputs
    // -----------------------------------------------------------------------

    /// <summary>All active touch points this frame.</summary>
    public IReadOnlyList<TouchPoint> Touches => _touches;

    /// <summary>Virtual joystick fed by the first left-half touch. Conventionally movement.</summary>
    public VirtualJoystick LeftJoystick { get; } = new() { Side = JoystickSide.Left };

    /// <summary>Virtual joystick fed by the first right-half touch. Conventionally aim or look.</summary>
    public VirtualJoystick RightJoystick { get; } = new() { Side = JoystickSide.Right };

    /// <summary>
    /// Change in distance between two simultaneous touches since last frame.
    /// Positive = fingers moving apart (zoom in), negative = pinching (zoom out).
    /// Zero when fewer than two touches are present.
    /// </summary>
    public float PinchDelta { get; private set; }

    // -----------------------------------------------------------------------
    // Internal state
    // -----------------------------------------------------------------------
    private readonly List<TouchPoint>                    _touches     = new();
    private readonly Dictionary<int, Vector2>            _prevPos     = new();
    private int _screenWidth  = 1920;
    private int _screenHeight = 1080;

    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    /// <summary>
    /// Supply screen dimensions so the virtual joystick can determine which half
    /// a touch is in. Call this whenever the window is resized.
    /// </summary>
    public void SetScreenSize(int width, int height)
    {
        _screenWidth  = width;
        _screenHeight = height;
    }

    // -----------------------------------------------------------------------
    // Frame update
    // -----------------------------------------------------------------------

    /// <summary>Reads MonoGame's TouchPanel and updates all derived state.</summary>
    public void Update()
    {
        var rawCollection = TouchPanel.GetState();
        _touches.Clear();

        // Build touch points with deltas.
        foreach (var raw in rawCollection)
        {
            var phase = MapPhase(raw.State);
            _prevPos.TryGetValue(raw.Id, out var prev);
            var delta = raw.Position - prev;

            _touches.Add(new TouchPoint
            {
                Id       = raw.Id,
                Position = raw.Position,
                Delta    = delta,
                Phase    = phase
            });

            if (phase is TouchPhase.Ended or TouchPhase.Cancelled)
                _prevPos.Remove(raw.Id);
            else
                _prevPos[raw.Id] = raw.Position;
        }
        Derive();
    }

    /// <summary>
    /// Supplies this frame's touches from somewhere other than the TouchPanel, and updates
    /// everything derived from them.
    /// </summary>
    /// <remarks>
    /// <see cref="TouchPanel.GetState()"/> needs a real device and a game window, so nothing that
    /// runs headlessly can exercise the joysticks, the pinch or the action bindings built on them.
    /// This is the same code path with the device swapped out — the position, delta and phase
    /// bookkeeping is identical, so a test drives what a phone would.
    /// </remarks>
    /// <summary>
    /// Supplies this frame's touches from somewhere other than the touch panel: a test, or a
    /// desktop simulating a finger with the mouse so an on-screen control can be tried in the
    /// editor, which has no touch panel at all.
    /// </summary>
    public void SubmitTouches(IEnumerable<TouchPoint> touches) => ApplyTouches(touches);

    internal void ApplyTouches(IEnumerable<TouchPoint> touches)
    {
        _touches.Clear();

        foreach (var touch in touches)
        {
            _prevPos.TryGetValue(touch.Id, out var prev);

            _touches.Add(touch with
            {
                // A touch that has just begun has no previous position to differ from.
                Delta = touch.Phase == TouchPhase.Began ? Vector2.Zero : touch.Position - prev,
            });

            if (touch.Phase is TouchPhase.Ended or TouchPhase.Cancelled)
                _prevPos.Remove(touch.Id);
            else
                _prevPos[touch.Id] = touch.Position;
        }

        Derive();
    }

    /// <summary>Recomputes everything derived from this frame's touch list.</summary>
    private void Derive()
    {
        // Remove stale entries for touches that disappeared without an End event.
        var activeIds = new HashSet<int>(_touches.Select(t => t.Id));
        foreach (var stale in _prevPos.Keys.Where(k => !activeIds.Contains(k)).ToList())
            _prevPos.Remove(stale);

        // Pinch detection (two-touch distance delta).
        PinchDelta = ComputePinchDelta();

        // Virtual joysticks. They claim opposite halves, so a finger on one can
        // never be stolen by the other.
        LeftJoystick.Update(_touches, _screenWidth);
        RightJoystick.Update(_touches, _screenWidth);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// How much the distance between two fingers changed since the last frame.
    /// Positive is a spread, negative a pinch.
    /// </summary>
    /// <remarks>
    /// The previous positions come from each touch's own <see cref="TouchPoint.Delta"/> rather
    /// than from <c>_prevPos</c>. That dictionary has already been advanced to this frame's
    /// positions by the time this runs, so reading it gave the current distance twice and the
    /// pinch delta was always exactly zero.
    /// </remarks>
    private float ComputePinchDelta()
    {
        if (_touches.Count < 2) return 0f;

        var a = _touches[0];
        var b = _touches[1];

        var currDist = Vector2.Distance(a.Position, b.Position);
        var prevDist = Vector2.Distance(a.Position - a.Delta, b.Position - b.Delta);

        return currDist - prevDist;
    }

    private static TouchPhase MapPhase(TouchLocationState state) => state switch
    {
        TouchLocationState.Pressed   => TouchPhase.Began,
        TouchLocationState.Moved     => TouchPhase.Moved,
        TouchLocationState.Released  => TouchPhase.Ended,
        TouchLocationState.Invalid   => TouchPhase.Cancelled,
        _                            => TouchPhase.Stationary
    };
}
