namespace SexyBiscuit.Engine.Core;

/// <summary>
/// The project root every relative asset, scene and script path resolves against.
/// </summary>
/// <remarks>
/// The engine used to resolve paths against the process working directory, which is wherever
/// the host happened to be launched from — right for a shipped game started from its own
/// folder, wrong for an editor that opens projects anywhere. The editor sets <see cref="Root"/>
/// when a project opens; a game's <c>Program.cs</c> may set it too. With no root set, the
/// behaviour is unchanged.
/// </remarks>
public static class ProjectPaths
{
    /// <summary>The open project's root directory, or null to fall back to the working directory.</summary>
    public static string? Root { get; set; }

    /// <summary>The directory relative paths resolve against right now.</summary>
    public static string EffectiveRoot => Root ?? Directory.GetCurrentDirectory();

    /// <summary>An absolute path for <paramref name="path"/>: rooted paths pass through.</summary>
    public static string Resolve(string path)
        => Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(EffectiveRoot, path));

    /// <summary>The path relative to the root, with forward slashes — the form scene files use.</summary>
    public static string MakeRelative(string path)
        => Path.GetRelativePath(EffectiveRoot, Resolve(path)).Replace('\\', '/');

    /// <summary>True when the path is the root or lives below it. Guards file-writing tools.</summary>
    public static bool IsInsideRoot(string path)
    {
        string root = Path.GetFullPath(EffectiveRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Resolve(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(full, root, comparison)
            || full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
