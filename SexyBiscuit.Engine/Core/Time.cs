namespace SexyBiscuit.Engine.Core;

/// <summary>
/// Global frame timing. Advanced once per frame by <see cref="SBEngine"/>;
/// every gameplay system reads from here rather than threading a delta around.
/// </summary>
/// <remarks>
/// <see cref="DeltaTime"/> is already scaled by <see cref="TimeScale"/>, so setting
/// <c>Time.TimeScale = 0</c> freezes gameplay while UI and input keep running off
/// <see cref="UnscaledDeltaTime"/>.
/// </remarks>
public static class Time
{
    /// <summary>Scaled seconds since the previous frame. Clamped by <see cref="MaximumDeltaTime"/>.</summary>
    public static float DeltaTime { get; private set; }

    /// <summary>Real seconds since the previous frame, ignoring <see cref="TimeScale"/>.</summary>
    public static float UnscaledDeltaTime { get; private set; }

    /// <summary>Scaled seconds elapsed since engine start.</summary>
    public static float TimeSinceStartup { get; private set; }

    /// <summary>Real seconds elapsed since engine start, ignoring <see cref="TimeScale"/>.</summary>
    public static float RealtimeSinceStartup { get; private set; }

    /// <summary>Length of one fixed-update step in scaled seconds.</summary>
    public static float FixedDeltaTime { get; internal set; } = 1f / 60f;

    /// <summary>Number of frames rendered since engine start.</summary>
    public static long FrameCount { get; private set; }

    /// <summary>
    /// Multiplier applied to <see cref="DeltaTime"/> and <see cref="FixedDeltaTime"/>.
    /// 0 pauses gameplay, 0.5 is slow motion, 2 is double speed. Never negative.
    /// </summary>
    public static float TimeScale
    {
        get => _timeScale;
        set => _timeScale = MathF.Max(0f, value);
    }
    private static float _timeScale = 1f;

    /// <summary>
    /// Upper bound on a single frame's unscaled delta, in seconds. Prevents a hitch or a
    /// breakpoint from producing a delta large enough to tunnel objects through walls.
    /// </summary>
    public static float MaximumDeltaTime { get; set; } = 0.1f;

    /// <summary>Smoothed frames per second, averaged over roughly the last half second.</summary>
    public static float Fps { get; private set; }
    private static float _fpsAccum;
    private static int   _fpsFrames;

    /// <summary>Called once per frame by the engine. Not part of the game-facing API.</summary>
    internal static void Advance(float rawDeltaSeconds)
    {
        UnscaledDeltaTime     = MathF.Min(rawDeltaSeconds, MaximumDeltaTime);
        DeltaTime             = UnscaledDeltaTime * _timeScale;
        RealtimeSinceStartup += UnscaledDeltaTime;
        TimeSinceStartup     += DeltaTime;
        FrameCount++;

        _fpsAccum  += UnscaledDeltaTime;
        _fpsFrames += 1;
        if (_fpsAccum >= 0.5f)
        {
            Fps        = _fpsFrames / _fpsAccum;
            _fpsAccum  = 0f;
            _fpsFrames = 0;
        }
    }

    /// <summary>Resets all counters. Used by tests and when restarting the engine in-process.</summary>
    internal static void Reset()
    {
        DeltaTime = UnscaledDeltaTime = TimeSinceStartup = RealtimeSinceStartup = 0f;
        FrameCount = 0;
        Fps = 0f;
        _fpsAccum = 0f;
        _fpsFrames = 0;
        _timeScale = 1f;
    }
}
