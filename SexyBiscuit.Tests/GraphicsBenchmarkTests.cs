using System.Text.Json;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Debug;
using SexyBiscuit.Engine.Rendering;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The benchmark's arithmetic, from the shared fixture.
/// </summary>
/// <remarks>
/// The mirror of <c>html5/tests/graphicsBenchmark.test.js</c>. The benchmark decides which
/// preset a machine is offered, so the two engines agreeing on the average is not enough —
/// they have to agree on the percentile maths as well, or the same laptop is told "high"
/// by the desktop build and "medium" by the web one.
/// </remarks>
public class GraphicsBenchmarkTests
{
    private const float Tolerance = 0.01f;

    private static JsonElement Cases()
    {
        var repo = EngineRepoLocator.Find();
        Assert.NotNull(repo);

        string path = Path.Combine(repo!.Root, "html5", "tests", "fixtures", "benchmark-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("cases").Clone();
    }

    public static TheoryData<string> CaseNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (JsonElement c in Cases().EnumerateArray())
                names.Add(c.GetProperty("name").GetString()!);
            return names;
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ABenchmarkCaseSummarisesToTheNumbersBothEnginesAgreeOn(string name)
    {
        JsonElement test = FindCase(name);

        var frames = new List<float>();
        foreach (JsonElement t in test.GetProperty("frameTimes").EnumerateArray()) frames.Add(t.GetSingle());

        BenchmarkResult result = GraphicsBenchmark.Summarise(frames);
        JsonElement expect = test.GetProperty("expect");

        Close(expect.GetProperty("averageFps").GetSingle(), result.AverageFps, "averageFps");
        Close(expect.GetProperty("onePercentLowFps").GetSingle(), result.OnePercentLowFps, "onePercentLowFps");
        Close(expect.GetProperty("pointOnePercentLowFps").GetSingle(), result.PointOnePercentLowFps, "pointOnePercentLowFps");
        Close(expect.GetProperty("frameTimeStdDevMs").GetSingle(), result.FrameTimeStdDevMs, "frameTimeStdDevMs");
        Assert.Equal(expect.GetProperty("frames").GetInt32(), result.Frames);

        Assert.Equal(test.GetProperty("expectSuggested").GetString(), result.SuggestedPreset);
    }

    // -------------------------------------------------------------------------
    // Behaviour
    // -------------------------------------------------------------------------

    [Fact]
    public void TheWarmUpFramesAreDiscardedBecauseTheyTimeAPresetSwitch()
    {
        // Starting a run means changing preset, which reallocates render targets and
        // drops the primitive cache. Timing that would measure the switch, not the
        // hardware.
        var benchmark = new GraphicsBenchmark { Duration = 1f };
        benchmark.Start();

        for (int i = 0; i < GraphicsBenchmark.WarmUpFrames; i++)
            Assert.Null(benchmark.Tick(0.5f));

        BenchmarkResult? result = null;
        while (result is null) result = benchmark.Tick(1f / 60f);

        // Half-second frames would have dragged the average to about 2 fps.
        Assert.True(result.Value.AverageFps > 55f, $"The warm-up leaked in: {result.Value.AverageFps} fps.");
    }

    [Fact]
    public void AStallIsNotThisMachinesFrameRateAndIsLeftOut()
    {
        var benchmark = new GraphicsBenchmark { Duration = 0.5f };
        benchmark.Start();
        for (int i = 0; i < GraphicsBenchmark.WarmUpFrames; i++) benchmark.Tick(1f / 60f);

        benchmark.Tick(4f);   // An alt-tab, or a collection the size of a level load.

        BenchmarkResult? result = null;
        while (result is null) result = benchmark.Tick(1f / 60f);

        Assert.True(result.Value.AverageFps > 55f, $"The stall was averaged in: {result.Value.AverageFps} fps.");
    }

    [Fact]
    public void ARunReportsItsProgressAndStopsExactlyOnce()
    {
        var benchmark = new GraphicsBenchmark { Duration = 1f };
        benchmark.Start();
        for (int i = 0; i < GraphicsBenchmark.WarmUpFrames; i++) benchmark.Tick(1f / 60f);

        Assert.InRange(benchmark.Progress, 0f, 0.999f);

        int results = 0;
        for (int i = 0; i < 200; i++) if (benchmark.Tick(1f / 60f) != null) results++;

        Assert.Equal(1, results);
        Assert.False(benchmark.IsRunning);
        Assert.Equal(0f, benchmark.Progress);
    }

    [Fact]
    public void NoFramesAtAllIsAPrintableResultRatherThanACrash()
    {
        BenchmarkResult result = GraphicsBenchmark.Summarise(Array.Empty<float>());

        Assert.Equal(0f, result.AverageFps);
        Assert.Equal(0, result.Frames);
        Assert.Contains("suggests", result.ToString());
    }

    [Fact]
    public void AMachineThatHitchesIsNotOfferedThePresetItsAverageWouldEarn()
    {
        // Stutter is what a player notices, and the reason the suggestion looks at the
        // low as well as the average.
        Assert.Equal("ultra", GraphicsSettings.Presets.Suggest(240f, 200f));
        Assert.Equal("medium", GraphicsSettings.Presets.Suggest(240f, 8f));
        Assert.Equal("battery", GraphicsSettings.Presets.Suggest(10f, 5f));
    }

    // -------------------------------------------------------------------------

    private static void Close(float expected, float actual, string what)
        => Assert.True(MathF.Abs(expected - actual) <= Tolerance,
                       $"{what} was {actual}, expected {expected}.");

    private static JsonElement FindCase(string name)
    {
        foreach (JsonElement c in Cases().EnumerateArray())
            if (c.GetProperty("name").GetString() == name) return c;

        throw new InvalidOperationException($"No benchmark case named \"{name}\".");
    }
}
