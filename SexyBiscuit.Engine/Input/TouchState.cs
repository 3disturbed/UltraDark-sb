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

/// <summary>
/// A software joystick driven by a single touch within a circular region.
/// Tracks the first touch whose initial contact falls in the left half of the screen.
/// </summary>
public sealed class VirtualJoystick
{
    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

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
    /// Automatically picks up the first new touch in the left screen half
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

            // Claim a new touch in the left half when we don't have one.
            if (_trackingId == -1
                && touch.Phase == TouchPhase.Began
                && touch.Position.X < screenWidth / 2f)
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

    /// <summary>Virtual joystick fed by the first left-half touch.</summary>
    public VirtualJoystick LeftJoystick { get; } = new();

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

        // Remove stale entries for touches that disappeared without an End event.
        var activeIds = new HashSet<int>(_touches.Select(t => t.Id));
        foreach (var stale in _prevPos.Keys.Where(k => !activeIds.Contains(k)).ToList())
            _prevPos.Remove(stale);

        // Pinch detection (two-touch distance delta).
        PinchDelta = ComputePinchDelta();

        // Virtual joystick.
        LeftJoystick.Update(_touches, _screenWidth);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private float ComputePinchDelta()
    {
        if (_touches.Count < 2) return 0f;

        var a = _touches[0];
        var b = _touches[1];

        var currDist = Vector2.Distance(a.Position, b.Position);
        var prevA    = _prevPos.TryGetValue(a.Id, out var pa) ? pa : a.Position;
        var prevB    = _prevPos.TryGetValue(b.Id, out var pb) ? pb : b.Position;
        var prevDist = Vector2.Distance(prevA, prevB);

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
