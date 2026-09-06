namespace SexyBiscuit.Engine.Build;

/// <summary>
/// Maps a <see cref="BuildPlatform"/> to the .NET runtime identifier <c>dotnet publish</c> needs.
/// </summary>
public static class RuntimeIdentifiers
{
    /// <summary>The RID for a desktop platform, or null for one that is not published this way (web, mobile).</summary>
    public static string? For(BuildPlatform platform) => platform switch
    {
        BuildPlatform.Windows_x64   or BuildPlatform.Steam_Windows => "win-x64",
        BuildPlatform.Windows_x86                                  => "win-x86",
        BuildPlatform.Linux_x64     or BuildPlatform.Steam_Linux   => "linux-x64",
        BuildPlatform.macOS_x64                                    => "osx-x64",
        BuildPlatform.macOS_ARM64   or BuildPlatform.Steam_macOS   => "osx-arm64",
        _                                                          => null,
    };

    /// <summary>The platform a RID spelling names, or null. Accepts the enum names and the dashed forms too.</summary>
    public static BuildPlatform? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string key = text.Trim().ToLowerInvariant().Replace('_', '-');

        return key switch
        {
            "win-x64" or "windows-x64"     => BuildPlatform.Windows_x64,
            "win-x86" or "windows-x86"     => BuildPlatform.Windows_x86,
            "linux-x64"                    => BuildPlatform.Linux_x64,
            "osx-x64" or "macos-x64"       => BuildPlatform.macOS_x64,
            "osx-arm64" or "macos-arm64"   => BuildPlatform.macOS_ARM64,
            "android"                      => BuildPlatform.Android,
            "ios"                          => BuildPlatform.iOS,
            "steam-windows"                => BuildPlatform.Steam_Windows,
            "steam-linux"                  => BuildPlatform.Steam_Linux,
            "steam-macos"                  => BuildPlatform.Steam_macOS,
            "web" or "html5"               => BuildPlatform.Web,
            _                              => null,
        };
    }

    /// <summary>The targets <c>--all</c> stands for: the web build, the three desktop RIDs, and an APK.</summary>
    /// <remarks>
    /// Android is in here deliberately: most people who will try a prototype have a phone in
    /// their hand and no desktop open, so an APK on the downloads page is not an extra, it is
    /// the build most testers will actually take.
    /// </remarks>
    public static readonly BuildPlatform[] DefaultTargets =
    {
        BuildPlatform.Web, BuildPlatform.Windows_x64, BuildPlatform.macOS_ARM64,
        BuildPlatform.Linux_x64, BuildPlatform.Android,
    };

    /// <summary>True for a platform whose published binary is a Windows executable.</summary>
    public static bool IsWindows(BuildPlatform platform)
        => platform is BuildPlatform.Windows_x64 or BuildPlatform.Windows_x86 or BuildPlatform.Steam_Windows;
}
