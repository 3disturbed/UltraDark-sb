using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.Save;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The preset table, and what applying one does.
/// </summary>
/// <remarks>
/// The table is one JSON file both engines read, embedded into this assembly the same
/// way the shared glyph table is, so these assertions have a mirror in
/// <c>html5/tests/graphics.test.js</c> running against the same numbers.
/// </remarks>
public class GraphicsSettingsTests : IDisposable
{
    private readonly string _prefsPath = Path.Combine(Path.GetTempPath(),
        $"sb-prefs-{Guid.NewGuid():N}.json");

    public GraphicsSettingsTests()
    {
        // A test that wrote the developer's real prefs would be a nasty surprise.
        PlayerPrefs.PrefsPath = _prefsPath;
        PlayerPrefs.DeleteAll();
    }

    public void Dispose()
    {
        PlayerPrefs.DeleteAll();
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
        GC.SuppressFinalize(this);
    }

    // -------------------------------------------------------------------------
    // The table
    // -------------------------------------------------------------------------

    [Fact]
    public void TheEmbeddedPresetTableLoadsWithEveryPresetTheFileNames()
    {
        // Embedded rather than read from html5/: a shipped game carries the presets
        // without carrying the browser engine's source tree.
        Assert.Equal(6, GraphicsSettings.Presets.Presets.Count);

        foreach (string id in new[] { "battery", "low", "medium", "high", "ultra", "benchmark" })
            Assert.NotNull(GraphicsSettings.Presets.Find(id));
    }

    [Fact]
    public void APresetIsFoundWhateverCaseItIsAskedForIn()
    {
        Assert.NotNull(GraphicsSettings.Presets.Find("ULTRA"));
        Assert.NotNull(GraphicsSettings.Presets.Find("Ultra"));
        Assert.Null(GraphicsSettings.Presets.Find("nonsense"));
    }

    [Fact]
    public void AMeasuredFrameRateMapsOntoThePresetTheTableNames()
    {
        // The same five assertions the browser suite makes, against the same file.
        Assert.Equal("ultra",   GraphicsSettings.Presets.Suggest(240f));
        Assert.Equal("high",    GraphicsSettings.Presets.Suggest(90f));
        Assert.Equal("medium",  GraphicsSettings.Presets.Suggest(60f));
        Assert.Equal("low",     GraphicsSettings.Presets.Suggest(30f));
        Assert.Equal("battery", GraphicsSettings.Presets.Suggest(8f));
    }

    // -------------------------------------------------------------------------
    // Applying
    // -------------------------------------------------------------------------

    [Fact]
    public void ApplyingAPresetOverwritesEveryFieldItCoversAndNamesItself()
    {
        var settings = new GraphicsSettings();
        settings.RenderScale = 0.1f;
        settings.Shadows     = ShadowQuality.Ultra;

        Assert.True(settings.ApplyPreset("battery"));

        Assert.Equal("battery", settings.Preset);
        Assert.Equal(0.5f, settings.RenderScale);
        Assert.Equal(ShadowQuality.Off, settings.Shadows);
        Assert.Equal(30, settings.FrameCap);
    }

    [Fact]
    public void AnUnknownPresetChangesNothingAndSaysSo()
    {
        var settings = new GraphicsSettings();
        settings.ApplyPreset("high");

        Assert.False(settings.ApplyPreset("nonsense"));
        Assert.Equal("high", settings.Preset);
    }

    [Fact]
    public void SwitchingFromUltraToBatteryLeavesNoUltraValueBehind()
    {
        // Every preset states every field precisely so this cannot happen: a preset
        // that omitted one would inherit whatever the last one set.
        var settings = new GraphicsSettings();
        settings.ApplyPreset("ultra");
        settings.ApplyPreset("battery");

        var fresh = new GraphicsSettings();
        fresh.ApplyPreset("battery");

        Assert.Equal(fresh.ShadowMapSize, settings.ShadowMapSize);
        Assert.Equal(fresh.Anisotropy, settings.Anisotropy);
        Assert.Equal(fresh.MaxPixelRatio, settings.MaxPixelRatio);
        Assert.Equal(fresh.TessellationRadial, settings.TessellationRadial);
    }

    [Fact]
    public void ApplyingGlobalsRetunesThePrimitiveCacheRatherThanLeavingStaleMeshes()
    {
        // The cache is keyed on the shape and not on the segment counts, so without the
        // clear a sphere built at 24 segments survives a drop to 8 and the knob does
        // nothing until something asks for a shape nobody has asked for yet.
        var settings = new GraphicsSettings();
        settings.ApplyPreset("battery");
        settings.ApplyGlobals();

        Assert.Equal(8, PrimitiveMesh.RadialSegments);
        Assert.Equal(4, PrimitiveMesh.RingSegments);
        Assert.Equal(0.15f, ParticleSystem3D.DensityScale, 3);

        settings.ApplyPreset("ultra");
        settings.ApplyGlobals();

        Assert.Equal(48, PrimitiveMesh.RadialSegments);
        Assert.Equal(1f, ParticleSystem3D.DensityScale, 3);

        // Left as the rest of the suite expects to find them.
        new GraphicsSettings().ApplyGlobals();
    }

    // -------------------------------------------------------------------------
    // Persistence
    // -------------------------------------------------------------------------

    [Fact]
    public void SettingsRoundTripThroughPlayerPrefs()
    {
        var settings = new GraphicsSettings();
        settings.ApplyPreset("high");
        settings.RenderScale = 0.42f;
        settings.Save();

        PlayerPrefs.Load();
        GraphicsSettings? back = GraphicsSettings.Load();

        Assert.NotNull(back);
        Assert.Equal("high", back!.Preset);
        Assert.Equal(0.42f, back.RenderScale, 3);
        Assert.Equal(ShadowQuality.High, back.Shadows);
    }

    [Fact]
    public void NeverHavingChosenIsReportedAsNullRatherThanAsDefaults()
    {
        // Null is what triggers the first-run capability probe. Returning defaults
        // would make every launch look like a deliberate choice of medium.
        Assert.Null(GraphicsSettings.Load());
    }

    [Fact]
    public void ACorruptStoredBlobIsTreatedAsAFirstRunRatherThanThrowing()
    {
        PlayerPrefs.SetString(GraphicsSettings.PrefsKey, "{not json at all");
        Assert.Null(GraphicsSettings.Load());
    }

    // -------------------------------------------------------------------------
    // Capabilities
    // -------------------------------------------------------------------------

    [Fact]
    public void AProbeWithNoDeviceStillAnswersAndAnswersConservatively()
    {
        // Every build agent is headless, so this is the path CI always takes.
        var caps = GraphicsCapabilities.Probe(null);

        Assert.False(caps.Shadows);
        Assert.False(caps.PostProcessing);
        Assert.Contains(1, caps.MsaaLevels);
        Assert.False(caps.PixelRatio);
        Assert.True(caps.DisplayControl);
        Assert.NotNull(GraphicsSettings.Presets.Find(caps.SuggestPreset()));
    }
}
