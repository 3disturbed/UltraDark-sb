using System.Text.Json;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// One quality preset, as the shared table describes it.
/// </summary>
public sealed class GraphicsPreset
{
    public string Id      { get; init; } = "";
    public string Name    { get; init; } = "";
    public string Summary { get; init; } = "";

    /// <summary>Every field, already parsed onto a settings object.</summary>
    internal GraphicsSettings Values { get; init; } = new();

    /// <summary>Copies this preset's values onto a settings object.</summary>
    public void CopyTo(GraphicsSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.RenderScale           = Values.RenderScale;
        target.VSync                 = Values.VSync;
        target.FrameCap              = Values.FrameCap;
        target.Shadows               = Values.Shadows;
        target.ShadowDistance        = Values.ShadowDistance;
        target.MaxLightsPerObject    = Values.MaxLightsPerObject;
        target.TessellationRadial    = Values.TessellationRadial;
        target.TessellationRings     = Values.TessellationRings;
        target.DrawDistance          = Values.DrawDistance;
        target.FrustumCulling        = Values.FrustumCulling;
        target.SortOpaqueFrontToBack = Values.SortOpaqueFrontToBack;
        target.Skybox                = Values.Skybox;
        target.ParticleDensity       = Values.ParticleDensity;
        target.PostProcessing        = Values.PostProcessing;
        target.Bloom                 = Values.Bloom;
        target.Vignette              = Values.Vignette;
        target.ColourGrade           = Values.ColourGrade;
        target.Scanline              = Values.Scanline;
        target.Lighting2D            = Values.Lighting2D;
        target.TextureFiltering      = Values.TextureFiltering;
        target.Anisotropy            = Values.Anisotropy;
        target.StreamingRadius       = Values.StreamingRadius;
        target.MaxLoadsPerFrame      = Values.MaxLoadsPerFrame;

        target.Msaa                  = Values.Msaa;
        target.ShadowMapSize         = Values.ShadowMapSize;

        target.MaxPixelRatio         = Values.MaxPixelRatio;
        target.HighDpi               = Values.HighDpi;
        target.Antialias             = Values.Antialias;
        target.PowerPreference       = Values.PowerPreference;
        target.ShaderPrecision       = Values.ShaderPrecision;
        target.ThrottleWhenHidden    = Values.ThrottleWhenHidden;
    }
}

/// <summary>A frame rate, and the preset a machine reaching it should be offered.</summary>
public readonly record struct GraphicsThreshold(string Preset, float MinFps);

/// <summary>
/// The quality presets, loaded from the file the browser engine reads too.
/// </summary>
/// <remarks>
/// <para>
/// The table is data rather than two switch statements because "Battery Saver" has to mean
/// the same numbers on a laptop running the native build and on the same laptop running the
/// web build. A constant that drifted between the engines would make one game feel like two,
/// and no regular expression over the other side's source would see it.
/// </para>
/// <para>
/// Embedded into the assembly, like the shared glyph table, so a shipped game carries the
/// presets without carrying the <c>html5</c> folder.
/// </para>
/// </remarks>
public sealed class GraphicsPresetTable
{
    /// <summary>The table every caller uses. Parsed once.</summary>
    public static GraphicsPresetTable Shared { get; } = Load();

    /// <summary>Every preset, in the order the file lists them, which is worst to best.</summary>
    public IReadOnlyList<GraphicsPreset> Presets { get; private init; } = Array.Empty<GraphicsPreset>();

    /// <summary>Frame rate to preset, best first. The first threshold cleared wins.</summary>
    public IReadOnlyList<GraphicsThreshold> Thresholds { get; private init; } = Array.Empty<GraphicsThreshold>();

    /// <summary>The preset a game starts on when nothing else has decided.</summary>
    public string Default { get; private init; } = "medium";

    /// <summary>A preset by id, case-insensitively, or null.</summary>
    public GraphicsPreset? Find(string id)
    {
        foreach (GraphicsPreset preset in Presets)
            if (string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase)) return preset;
        return null;
    }

    /// <summary>The best preset a machine hitting this frame rate should be offered.</summary>
    public string Suggest(float averageFps)
    {
        foreach (GraphicsThreshold threshold in Thresholds)
            if (averageFps >= threshold.MinFps) return threshold.Preset;

        return Presets.Count > 0 ? Presets[0].Id : Default;
    }

    /// <summary>How far above the low's own answer an average is allowed to reach.</summary>
    /// <remarks>
    /// Two rungs is the allowance for a benchmark's own noise — a single scheduler
    /// hiccup should not cost a good machine two quality levels — while still stopping
    /// a badly stuttering one being offered what its average alone would earn.
    /// </remarks>
    public const int StutterAllowance = 2;

    /// <summary>
    /// The preset a machine should be offered, judged on its average and its stutter.
    /// </summary>
    /// <remarks>
    /// Stutter is what a player actually notices. A machine averaging a hundred frames a
    /// second with a one-percent low of six is not a hundred-frame machine; it is one
    /// that hitches for a whole second in every six, and offering it the preset its
    /// average earns would be offering it the preset it just failed at.
    /// </remarks>
    public string Suggest(float averageFps, float onePercentLowFps)
    {
        int byAverage = IndexOf(Suggest(averageFps));
        int byLow     = IndexOf(Suggest(onePercentLowFps));

        int chosen = Math.Min(byAverage, byLow + StutterAllowance);
        chosen = Math.Clamp(chosen, 0, Math.Max(0, Presets.Count - 1));

        return Presets.Count > 0 ? Presets[chosen].Id : Default;
    }

    /// <summary>A preset's position in the table, which is ordered cheapest first.</summary>
    private int IndexOf(string id)
    {
        for (int i = 0; i < Presets.Count; i++)
            if (string.Equals(Presets[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    // -------------------------------------------------------------------------
    // Parsing
    // -------------------------------------------------------------------------

    private static GraphicsPresetTable Load()
    {
        using Stream? stream = typeof(GraphicsPresetTable).Assembly
            .GetManifestResourceStream("SexyBiscuit.Engine.graphics-presets.json");

        // An empty table rather than a throw: a broken build should render, not refuse to start.
        if (stream is null) return new GraphicsPresetTable();

        using var document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;

        var presets = new List<GraphicsPreset>();
        foreach (JsonElement element in root.GetProperty("presets").EnumerateArray())
            presets.Add(ReadPreset(element));

        var thresholds = new List<GraphicsThreshold>();
        if (root.TryGetProperty("thresholds", out JsonElement list))
            foreach (JsonElement element in list.EnumerateArray())
                thresholds.Add(new GraphicsThreshold(
                    element.GetProperty("preset").GetString() ?? "",
                    element.GetProperty("minFps").GetSingle()));

        return new GraphicsPresetTable
        {
            Presets    = presets,
            Thresholds = thresholds,
            Default    = root.TryGetProperty("default", out JsonElement d) ? d.GetString() ?? "medium" : "medium",
        };
    }

    private static GraphicsPreset ReadPreset(JsonElement element)
    {
        var values = new GraphicsSettings();

        JsonElement shared = element.GetProperty("shared");
        values.RenderScale           = shared.GetProperty("renderScale").GetSingle();
        values.VSync                 = shared.GetProperty("vsync").GetBoolean();
        values.FrameCap              = shared.GetProperty("frameCap").GetInt32();
        values.Shadows               = Enum.Parse<ShadowQuality>(shared.GetProperty("shadows").GetString()!, true);
        values.ShadowDistance        = shared.GetProperty("shadowDistance").GetSingle();
        values.MaxLightsPerObject    = shared.GetProperty("maxLightsPerObject").GetInt32();
        values.TessellationRadial    = shared.GetProperty("tessellationRadial").GetInt32();
        values.TessellationRings     = shared.GetProperty("tessellationRings").GetInt32();
        values.DrawDistance          = shared.GetProperty("drawDistance").GetSingle();
        values.FrustumCulling        = shared.GetProperty("frustumCulling").GetBoolean();
        values.SortOpaqueFrontToBack = shared.GetProperty("sortOpaqueFrontToBack").GetBoolean();
        values.Skybox                = shared.GetProperty("skybox").GetBoolean();
        values.ParticleDensity       = shared.GetProperty("particleDensity").GetSingle();
        values.PostProcessing        = shared.GetProperty("postProcessing").GetBoolean();
        values.Bloom                 = shared.GetProperty("bloom").GetBoolean();
        values.Vignette              = shared.GetProperty("vignette").GetBoolean();
        values.ColourGrade           = shared.GetProperty("colourGrade").GetBoolean();
        values.Scanline              = shared.GetProperty("scanline").GetBoolean();
        values.Lighting2D            = Enum.Parse<Lighting2DQuality>(shared.GetProperty("lighting2D").GetString()!, true);
        values.TextureFiltering      = Enum.Parse<TextureFiltering>(shared.GetProperty("textureFiltering").GetString()!, true);
        values.Anisotropy            = shared.GetProperty("anisotropy").GetInt32();
        values.StreamingRadius       = shared.GetProperty("streamingRadius").GetInt32();
        values.MaxLoadsPerFrame      = shared.GetProperty("maxLoadsPerFrame").GetInt32();

        JsonElement native = element.GetProperty("native");
        values.Msaa          = native.GetProperty("msaa").GetInt32();
        values.ShadowMapSize = native.GetProperty("shadowMapSize").GetInt32();

        JsonElement web = element.GetProperty("web");
        values.MaxPixelRatio      = web.GetProperty("maxPixelRatio").GetSingle();
        values.HighDpi            = web.GetProperty("highDpi").GetBoolean();
        values.Antialias          = web.GetProperty("antialias").GetBoolean();
        values.PowerPreference    = web.GetProperty("powerPreference").GetString() ?? "default";
        values.ShaderPrecision    = web.GetProperty("shaderPrecision").GetString() ?? "highp";
        values.ThrottleWhenHidden = web.GetProperty("throttleWhenHidden").GetBoolean();

        return new GraphicsPreset
        {
            Id      = element.GetProperty("id").GetString() ?? "",
            Name    = element.GetProperty("name").GetString() ?? "",
            Summary = element.TryGetProperty("summary", out JsonElement s) ? s.GetString() ?? "" : "",
            Values  = values,
        };
    }
}
