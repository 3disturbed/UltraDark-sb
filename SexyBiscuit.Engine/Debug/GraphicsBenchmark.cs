using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

namespace SexyBiscuit.Engine.Debug;

/// <summary>
/// The result of a timed run: what the hardware actually managed.
/// </summary>
/// <remarks>
/// The low percentiles matter more than the average, and are the reason this reports
/// them. A game averaging 90 fps with a 1% low of 18 stutters visibly and is a worse
/// experience than a steady 60; an average alone cannot tell those apart, so a preset
/// chosen on the average alone would be chosen wrong.
/// </remarks>
public readonly record struct BenchmarkResult(
    float AverageFps,
    float OnePercentLowFps,
    float PointOnePercentLowFps,
    float FrameTimeStdDevMs,
    int   Frames,
    float Seconds,
    int   DrawCalls,
    int   Triangles,
    string SuggestedPreset)
{
    /// <summary>The one line the menu shows when a run finishes.</summary>
    public override string ToString()
        => $"{AverageFps:0} fps average · {OnePercentLowFps:0} fps 1% low · "
         + $"{PointOnePercentLowFps:0} fps 0.1% low · ±{FrameTimeStdDevMs:0.0} ms · "
         + $"{Triangles:N0} tris, {DrawCalls} draws · suggests {SuggestedPreset}";
}

/// <summary>
/// Measures what a machine can actually do, and maps the answer onto a preset.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of <c>html5/src/debug/GraphicsBenchmark.js</c>. A pure accumulator: it is
/// handed a frame time each frame and is asked for a result when it has enough, so it
/// runs identically on a renderer it knows nothing about, and a test can drive it with
/// a list of numbers.
/// </para>
/// <para>
/// The first frames of a run are discarded. Starting a benchmark means switching preset,
/// which reallocates render targets, drops the primitive cache and recompiles nothing in
/// particular — and timing that would measure the switch rather than the hardware.
/// </para>
/// </remarks>
public sealed class GraphicsBenchmark
{
    /// <summary>Frames thrown away at the start, while the new preset settles.</summary>
    public const int WarmUpFrames = 30;

    private readonly List<float> _frameTimes = new();

    private float _elapsed;
    private int   _warmedUp;

    /// <summary>How long a run lasts, in seconds of real time after the warm-up.</summary>
    public float Duration { get; set; } = 8f;

    /// <summary>Whether a run is in progress.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>0..1 through the current run, for a progress bar.</summary>
    public float Progress => IsRunning && Duration > 0f ? Math.Clamp(_elapsed / Duration, 0f, 1f) : 0f;

    /// <summary>The last completed run, or null.</summary>
    public BenchmarkResult? Result { get; private set; }

    /// <summary>The settings in force before the run, to be put back afterwards.</summary>
    public GraphicsSettings? Restore { get; set; }

    /// <summary>Starts a run, discarding anything a previous one collected.</summary>
    public void Start()
    {
        _frameTimes.Clear();
        _elapsed  = 0f;
        _warmedUp = 0;
        Result    = null;
        IsRunning = true;
    }

    /// <summary>Abandons a run without producing a result.</summary>
    public void Cancel()
    {
        IsRunning = false;
        _frameTimes.Clear();
    }

    /// <summary>
    /// Feeds one frame in. Returns the result on the frame the run finishes, else null.
    /// </summary>
    /// <param name="unscaledDeltaSeconds">
    /// Real seconds, never scaled ones: a game paused behind the menu has a time scale of
    /// zero, and a benchmark measured in scaled time would never advance.
    /// </param>
    /// <param name="stats">The renderer's counters, recorded with the result.</param>
    public BenchmarkResult? Tick(float unscaledDeltaSeconds, RenderStats? stats = null)
    {
        if (!IsRunning) return null;

        if (_warmedUp < WarmUpFrames) { _warmedUp++; return null; }

        // A frame long enough to be a stall — an alt-tab, a garbage collection the size
        // of a level load — is not this machine's frame rate and must not be averaged in.
        if (unscaledDeltaSeconds > 0f && unscaledDeltaSeconds < 1f)
        {
            _frameTimes.Add(unscaledDeltaSeconds);
            _elapsed += unscaledDeltaSeconds;
        }

        if (_elapsed < Duration) return null;

        IsRunning = false;
        Result    = Summarise(_frameTimes, stats);
        return Result;
    }

    // -------------------------------------------------------------------------
    // The arithmetic, which both engines do identically
    // -------------------------------------------------------------------------

    /// <summary>Turns a list of frame times into a result.</summary>
    public static BenchmarkResult Summarise(IReadOnlyList<float> frameTimes, RenderStats? stats = null)
    {
        if (frameTimes.Count == 0)
            return new BenchmarkResult(0f, 0f, 0f, 0f, 0, 0f, 0, 0,
                                       GraphicsSettings.Presets.Suggest(0f, 0f));

        var sorted = new List<float>(frameTimes);
        sorted.Sort();          // Ascending, so the slowest frames are at the end.

        float total = 0f;
        foreach (float t in frameTimes) total += t;

        float average = total / frameTimes.Count;
        float averageFps = average > 0f ? 1f / average : 0f;

        float variance = 0f;
        foreach (float t in frameTimes) variance += (t - average) * (t - average);
        variance /= frameTimes.Count;

        float onePercentLow = LowFps(sorted, 0.01f);

        return new BenchmarkResult(
            AverageFps:            averageFps,
            OnePercentLowFps:      onePercentLow,
            PointOnePercentLowFps: LowFps(sorted, 0.001f),
            FrameTimeStdDevMs:     MathF.Sqrt(variance) * 1000f,
            Frames:                frameTimes.Count,
            Seconds:               total,
            DrawCalls:             stats?.DrawCalls ?? 0,
            Triangles:             stats?.Triangles ?? 0,
            SuggestedPreset:       GraphicsSettings.Presets.Suggest(averageFps, onePercentLow));
    }

    /// <summary>
    /// The average frame rate over the slowest fraction of frames.
    /// </summary>
    /// <remarks>
    /// The average of the tail, not the single worst frame at that percentile. One frame
    /// is noise — a scheduler hiccup lands there as readily as a real stall — and the
    /// average over the tail is what a player experiences as the game hitching.
    /// </remarks>
    private static float LowFps(List<float> ascending, float fraction)
    {
        int count = Math.Max(1, (int)MathF.Ceiling(ascending.Count * fraction));

        float total = 0f;
        for (int i = ascending.Count - count; i < ascending.Count; i++) total += ascending[i];

        float mean = total / count;
        return mean > 0f ? 1f / mean : 0f;
    }
}
