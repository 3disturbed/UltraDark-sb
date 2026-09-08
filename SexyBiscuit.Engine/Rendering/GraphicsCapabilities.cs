using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// What the machine the game is actually running on can do.
/// </summary>
/// <remarks>
/// <para>
/// The settings menu builds its Advanced tab from this rather than from a fixed list, so
/// a knob the running platform cannot honour is never shown. Half the knobs in
/// <see cref="GraphicsSettings"/> exist on one engine only — the browser has no shadow
/// pass and no 2D light map; the desktop has no device pixel ratio — and a menu offering
/// a shadow slider that does nothing is worse than a menu with no shadow slider.
/// </para>
/// <para>
/// The two shader-dependent capabilities are the ones worth knowing about: the
/// <c>.fx</c> files are built by <c>Content.mgcb</c> and not by <c>dotnet build</c>, so a
/// checkout that has never run a content build has no shadow effect and no post-process
/// effects at all, and every pass silently does nothing. That is a capability, not a bug
/// to hide.
/// </para>
/// </remarks>
public sealed class GraphicsCapabilities
{
    /// <summary>The renderer's name, for the menu's status line.</summary>
    public string Renderer { get; init; } = "Unknown";

    /// <summary>The graphics adapter, as the driver describes it.</summary>
    public string Adapter { get; init; } = "Unknown";

    /// <summary>The largest texture edge the device accepts.</summary>
    public int MaxTextureSize { get; init; } = 2048;

    /// <summary>Multisample counts the device will actually give, always including 1.</summary>
    public IReadOnlyList<int> MsaaLevels { get; init; } = new[] { 1 };

    /// <summary>Back-buffer sizes the current adapter reports, largest first.</summary>
    public IReadOnlyList<(int Width, int Height)> DisplayModes { get; init; }
        = Array.Empty<(int, int)>();

    /// <summary>Whether a shadow pass can run, which needs a compiled depth effect.</summary>
    public bool Shadows { get; init; }

    /// <summary>Whether any post-processing pass can run, which needs compiled effects.</summary>
    public bool PostProcessing { get; init; }

    /// <summary>Whether the 2D light map is available. Desktop only; the browser has none.</summary>
    public bool Lighting2D { get; init; } = true;

    /// <summary>Whether anisotropic filtering is available.</summary>
    public bool Anisotropy { get; init; } = true;

    /// <summary>Whether the window can go fullscreen and change its back-buffer size.</summary>
    public bool DisplayControl { get; init; } = true;

    /// <summary>Whether a device pixel ratio exists to cap. It does not, off the web.</summary>
    public bool PixelRatio { get; init; }

    /// <summary>The platform, for the menu's status line.</summary>
    public string Platform { get; init; } = "Unknown";

    // -------------------------------------------------------------------------
    // Probing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asks the device what it can do. Safe with a null device, which is what a test and
    /// a headless server both have.
    /// </summary>
    public static GraphicsCapabilities Probe(GraphicsDevice? device, RenderSystem3D? renderer = null)
    {
        GraphicsAdapter? adapter = device?.Adapter ?? SafeDefaultAdapter();

        return new GraphicsCapabilities
        {
            Renderer       = "MonoGame DesktopGL",
            Platform       = PlatformName(),
            Adapter        = adapter?.Description ?? "Unknown",
            MaxTextureSize = device?.GraphicsProfile == GraphicsProfile.HiDef ? 4096 : 2048,
            MsaaLevels     = ProbeMsaa(device),
            DisplayModes   = ProbeDisplayModes(adapter),
            Shadows        = renderer?.ShadowDepthEffect != null,
            PostProcessing = renderer?.PostProcess.Any(p => p.Shader != null) ?? false,
        };
    }

    /// <summary>
    /// A preset for a machine nothing is known about yet, from what the device reports.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative. A first launch that stutters is the one a player
    /// remembers, and turning quality up is a thing they will happily do themselves;
    /// turning it down is a thing they do by uninstalling.
    /// </remarks>
    public string SuggestPreset()
    {
        if (DisplayModes.Count == 0) return "low";

        (int width, int height) = DisplayModes[0];
        long pixels = (long)width * height;

        if (MsaaLevels.Count >= 3 && pixels >= 3840L * 2160L) return "high";
        if (MsaaLevels.Count >= 2 && pixels >= 1920L * 1080L) return "medium";
        return "low";
    }

    private static GraphicsAdapter? SafeDefaultAdapter()
    {
        // Throws on a machine with no display at all, which is every build agent.
        try { return GraphicsAdapter.DefaultAdapter; }
        catch { return null; }
    }

    private static string PlatformName()
    {
        if (OperatingSystem.IsWindows()) return $"Windows {Environment.OSVersion.Version.Major}";
        if (OperatingSystem.IsMacOS())   return "macOS";
        if (OperatingSystem.IsAndroid()) return "Android";
        if (OperatingSystem.IsLinux())   return "Linux";
        return Environment.OSVersion.Platform.ToString();
    }

    private static List<int> ProbeMsaa(GraphicsDevice? device)
    {
        var levels = new List<int> { 1 };
        if (device is null) return levels;

        // No API asks "is 4x supported"; the presentation parameters report the ceiling,
        // so the answer is every power of two up to it.
        int max = Math.Max(1, device.PresentationParameters.MultiSampleCount);
        for (int level = 2; level <= max && level <= 16; level *= 2) levels.Add(level);
        return levels;
    }

    private static List<(int, int)> ProbeDisplayModes(GraphicsAdapter? adapter)
    {
        var modes = new List<(int, int)>();
        if (adapter is null) return modes;

        try
        {
            var seen = new HashSet<(int, int)>();
            foreach (DisplayMode mode in adapter.SupportedDisplayModes)
                if (seen.Add((mode.Width, mode.Height))) modes.Add((mode.Width, mode.Height));
        }
        catch
        {
            return modes;   // A headless agent has no modes, which is not a failure.
        }

        modes.Sort((a, b) => ((long)b.Item1 * b.Item2).CompareTo((long)a.Item1 * a.Item2));
        return modes;
    }
}
