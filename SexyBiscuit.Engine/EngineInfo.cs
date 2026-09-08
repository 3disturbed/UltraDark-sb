using System.Reflection;

namespace SexyBiscuit.Engine;

/// <summary>
/// What engine this is, and what it is running on.
/// </summary>
/// <remarks>
/// <para>
/// One authoritative version string. There were four, agreeing by accident: the editor
/// derived one from the assembly, the CookieJar's project context and the editor's project
/// file each hard-coded <c>"1.0.0"</c>, and the browser engine exported its own constant —
/// and no <c>&lt;Version&gt;</c> existed in any csproj, so the derived one was MSBuild's
/// default. Four sources that happen to match are one silent divergence away from a cookie
/// being refused, or accepted, for the wrong reason.
/// </para>
/// <para>
/// The version is now stated in <c>SexyBiscuit.Engine.csproj</c>, read from the assembly
/// here, and pinned equal to <c>html5/src/core/EngineInfo.js</c> by a cross-engine test.
/// </para>
/// </remarks>
public static class EngineInfo
{
    /// <summary>The engine version, as major.minor.patch.</summary>
    public static string Version { get; } =
        typeof(EngineInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>The engine's name, for a title bar or a status line.</summary>
    public const string Name = "SexyBiscuit";

    /// <summary>Which renderer this build talks to.</summary>
    public const string Renderer = "MonoGame DesktopGL";

    /// <summary>The operating system and architecture, as a player would recognise them.</summary>
    public static string Platform { get; } = DescribePlatform();

    /// <summary>Whether this is a debug build, which is worth saying on a status line.</summary>
    public static bool IsDebug { get; } =
        typeof(EngineInfo).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()
            ?.Configuration.Contains("Debug", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>
    /// The one-line summary the graphics menu shows at its foot, without the preset.
    /// </summary>
    /// <example>SexyBiscuit 1.0.0 · MonoGame DesktopGL · macOS arm64</example>
    public static string StatusLine
        => $"{Name} {Version}{(IsDebug ? " (debug)" : "")} · {Renderer} · {Platform}";

    private static string DescribePlatform()
    {
        string os =
            OperatingSystem.IsWindows() ? "Windows" :
            OperatingSystem.IsMacOS()   ? "macOS"   :
            OperatingSystem.IsAndroid() ? "Android" :
            OperatingSystem.IsIOS()     ? "iOS"     :
            OperatingSystem.IsLinux()   ? "Linux"   :
            Environment.OSVersion.Platform.ToString();

        return $"{os} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
    }
}
