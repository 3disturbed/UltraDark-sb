using System.Globalization;
using System.Text.Json;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.UI;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The clock and the colour ramp, from the shared fixture.
/// </summary>
/// <remarks>
/// The mirror of <c>html5/tests/timeOfDay.test.js</c>. A day/night cycle is a clock and a
/// colour ramp, and both are pure arithmetic that two independent implementations agree on
/// only where something forces them to. A sun elevation off by a degree moves dawn; a
/// channel off by a byte makes the desktop dusk warm and the web dusk blue. Neither would
/// fail any other test in this repository.
/// </remarks>
public class TimeOfDayTests
{
    private const float Tolerance = 0.001f;

    private static JsonElement Fixture()
    {
        var repo = EngineRepoLocator.Find();
        Assert.NotNull(repo);

        string path = Path.Combine(repo!.Root, "html5", "tests", "fixtures", "day-night-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    public static TheoryData<double> SampleTimes
    {
        get
        {
            var times = new TheoryData<double>();
            foreach (JsonElement s in Fixture().GetProperty("samples").EnumerateArray())
                times.Add(s.GetProperty("time").GetDouble());
            return times;
        }
    }

    // -------------------------------------------------------------------------
    // The fixture
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(SampleTimes))]
    public void ADayNightSampleMatchesWhatTheBrowserEngineMakesOfTheSameMoment(double time)
    {
        JsonElement fixture = Fixture();
        JsonElement sample  = FindSample(fixture, time);

        var clock = new TimeOfDay
        {
            SunTilt    = fixture.GetProperty("sunTilt").GetSingle(),
            SunAzimuth = fixture.GetProperty("sunAzimuth").GetSingle(),
        };
        clock.Time01 = (float)time;

        Assert.Equal(sample.GetProperty("clock").GetString(), clock.Clock);
        Assert.Equal(sample.GetProperty("phase").GetString(), clock.Phase.ToString());
        Assert.Equal(sample.GetProperty("isNight").GetBoolean(), clock.IsNight);

        Close(sample.GetProperty("sunElevation").GetSingle(), clock.SunElevation, "sunElevation");
        Close(sample.GetProperty("darkness").GetSingle(), clock.Darkness, "darkness");

        JsonElement direction = sample.GetProperty("sunDirection");
        Vector3 sun = clock.SunDirection;
        Close(direction[0].GetSingle(), sun.X, "sunDirection.x");
        Close(direction[1].GetSingle(), sun.Y, "sunDirection.y");
        Close(direction[2].GetSingle(), sun.Z, "sunDirection.z");

        JsonElement sky = sample.GetProperty("sky");
        SkySample actual = clock.Sky;

        SameColour(sky.GetProperty("zenith").GetString()!, actual.Zenith, "zenith");
        SameColour(sky.GetProperty("horizon").GetString()!, actual.Horizon, "horizon");
        SameColour(sky.GetProperty("sun").GetString()!, actual.Sun, "sun");
        SameColour(sky.GetProperty("ambientSky").GetString()!, actual.AmbientSky, "ambientSky");
        SameColour(sky.GetProperty("ambientGround").GetString()!, actual.AmbientGround, "ambientGround");
        SameColour(sky.GetProperty("fog").GetString()!, actual.Fog, "fog");

        Close(sky.GetProperty("sunIntensity").GetSingle(), actual.SunIntensity, "sunIntensity");
        Close(sky.GetProperty("fogDensity").GetSingle(), actual.FogDensity, "fogDensity");
    }

    // -------------------------------------------------------------------------
    // The clock
    // -------------------------------------------------------------------------

    [Fact]
    public void ADayAdvancesToTheNextOneAndSaysSoExactlyOnce()
    {
        var clock = new TimeOfDay { DayLength = 10f, StartAt = 0.9f };
        clock.Awake();

        var days = new List<int>();
        clock.NewDay = day => days.Add(day);

        // Two seconds is a fifth of a day, which crosses midnight once.
        for (int i = 0; i < 20; i++) clock.Advance(0.1f);

        Assert.Equal(new[] { 2 }, days);
        Assert.Equal(2, clock.DayNumber);
    }

    [Fact]
    public void AFrameLongEnoughToCrossSeveralMidnightsCountsEveryDay()
    {
        // A game left at a breakpoint must not lose a week when it resumes.
        var clock = new TimeOfDay { DayLength = 10f, StartAt = 0f };
        clock.Awake();

        var days = new List<int>();
        clock.NewDay = day => days.Add(day);
        clock.Advance(35f);          // Three and a half days.

        Assert.Equal(new[] { 2, 3, 4 }, days);
        Assert.Equal(0.5f, clock.Time01, 3);
    }

    [Fact]
    public void EachPhaseIsAnnouncedOnceAsTheSunCrossesIntoIt()
    {
        var clock = new TimeOfDay { DayLength = 240f, StartAt = 0f };
        clock.Awake();

        var phases = new List<DayPhase>();
        clock.PhaseChanged = (_, now) => phases.Add(now);

        // A whole day in small steps, so no phase is stepped over.
        for (int i = 0; i < 2400; i++) clock.Advance(0.1f);

        Assert.Equal(new[] { DayPhase.Dawn, DayPhase.Day, DayPhase.Dusk, DayPhase.Night }, phases);
    }

    [Fact]
    public void APausedClockDoesNotMove()
    {
        // The cookie this replaces could not do this: "It does not pause. The clock runs
        // during a menu unless you set Time.timeScale = 0."
        var clock = new TimeOfDay { DayLength = 10f, StartAt = 0.5f, Paused = true };
        clock.Awake();

        clock.Advance(100f);
        Assert.Equal(0.5f, clock.Time01, 4);
        Assert.Equal(1, clock.DayNumber);
    }

    [Fact]
    public void SettingTheClockBackwardsPastMidnightGoesBackADayRatherThanForward()
    {
        var clock = new TimeOfDay { StartAt = 0.1f };
        clock.Awake();
        clock.DayNumber = 4;

        clock.Time01 = 0.9f;      // Yesterday evening.
        Assert.Equal(3, clock.DayNumber);

        clock.Time01 = 0.1f;      // And forwards again.
        Assert.Equal(4, clock.DayNumber);
    }

    [Fact]
    public void TheTiltMovesDawnToWhereTheLightActuallyChanges()
    {
        // Phases come off the sun's elevation, not off fractions of a day, so a flatter
        // arc really does mean a longer twilight rather than the same clock times.
        var steep = new TimeOfDay { SunTilt = 0f };
        var flat  = new TimeOfDay { SunTilt = 80f };

        steep.Time01 = flat.Time01 = 0.26f;
        Assert.True(steep.SunElevation > flat.SunElevation,
                    "A steeper arc must put the sun higher at the same hour.");

        flat.Time01 = 0.5f;
        Assert.Equal(DayPhase.Day, flat.Phase);
    }

    [Fact]
    public void TheGradientWrapsSoTheLastMinuteOfADayBlendsIntoMidnight()
    {
        // Without the wrap, 23:59 snaps to the final key instead of blending, and the
        // sky jumps a visible amount once a day.
        SkySample almost = SkyGradient.Shared.Sample(0.999f);
        SkySample midnight = SkyGradient.Shared.Sample(0f);

        Assert.InRange(Math.Abs(almost.Zenith.R - midnight.Zenith.R), 0, 4);
        Assert.InRange(Math.Abs(almost.Zenith.B - midnight.Zenith.B), 0, 6);
    }

    [Fact]
    public void AnyTimeAtAllIsFoldedIntoOneDay()
    {
        Assert.Equal(SkyGradient.Shared.Sample(0.25f), SkyGradient.Shared.Sample(3.25f));
        Assert.Equal(SkyGradient.Shared.Sample(0.75f), SkyGradient.Shared.Sample(-0.25f));
    }

    // -------------------------------------------------------------------------

    private static JsonElement FindSample(JsonElement fixture, double time)
    {
        foreach (JsonElement s in fixture.GetProperty("samples").EnumerateArray())
            if (Math.Abs(s.GetProperty("time").GetDouble() - time) < 1e-9) return s;

        throw new InvalidOperationException($"No day/night sample at {time}.");
    }

    private static void Close(float expected, float actual, string what)
        => Assert.True(MathF.Abs(expected - actual) <= Tolerance,
                       $"{what} was {actual}, expected {expected}.");

    /// <summary>
    /// Compares two colours, allowing one byte per channel.
    /// </summary>
    /// <remarks>
    /// One byte, and not zero, because the two engines do not have the same numbers to
    /// work with: a component's time is a <c>float</c> here and a double in the browser,
    /// so the same moment is a hundred-millionth apart and a channel sitting exactly on
    /// a rounding boundary falls either side of it. One byte of sky is invisible; a
    /// wrong key, a wrong blend or a missing wrap is several, and still fails this.
    /// </remarks>
    private static void SameColour(string expected, Color actual, string what)
    {
        Color want = UiDocument.ParseColour(expected) ?? Color.Black;
        string got = $"#{actual.R:x2}{actual.G:x2}{actual.B:x2}";

        Assert.True(Math.Abs(want.R - actual.R) <= 1
                 && Math.Abs(want.G - actual.G) <= 1
                 && Math.Abs(want.B - actual.B) <= 1,
                    $"{what} was {got}, expected {expected}.");
    }
}
