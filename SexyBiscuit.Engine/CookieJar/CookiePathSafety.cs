namespace SexyBiscuit.Engine.CookieJar;

/// <summary>
/// Decides where a cookie is allowed to write. Every destination an install produces goes through
/// here before anything touches the disk.
/// </summary>
/// <remarks>
/// <see cref="Core.ProjectPaths.IsInsideRoot"/> answers the same question against the ambient
/// project root. That is the wrong shape for a planner, which has to be a pure function of the
/// root it is handed so it can be tested and dry-run; hence the root-explicit sibling here.
/// </remarks>
public static class CookiePathSafety
{
    /// <summary>
    /// Folders an install must never write into, whatever a manifest asks for: build output, the
    /// project's own scratch folder, and the repository metadata.
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenFolders =
        new[] { ".sexybiscuit", "bin", "obj", ".git", ".vs" };

    /// <summary>File extensions an install must never overwrite: the project's own definition.</summary>
    public static readonly IReadOnlyList<string> ForbiddenExtensions =
        new[] { ".sbproject", ".csproj", ".sln", ".props", ".targets" };

    /// <summary>macOS and Windows compare paths without case; Linux does not.</summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>True when <paramref name="candidate"/> resolves to <paramref name="root"/> or below it.</summary>
    public static bool IsInside(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate)) return false;

        try
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full     = Path.GetFullPath(candidate);
            return full.StartsWith(fullRoot, PathComparison);
        }
        catch (Exception)
        {
            return false;   // an unparseable path is not inside anything
        }
    }

    /// <summary>
    /// Resolves <paramref name="relative"/> against <paramref name="root"/>, or returns null when
    /// the result would escape, be absolute, or land somewhere an install must not write.
    /// </summary>
    public static string? SafeCombine(string root, string relative)
        => Explain(root, relative, out _) ? Path.GetFullPath(Path.Combine(root, relative)) : null;

    /// <summary>
    /// The same check as <see cref="SafeCombine"/>, with the reason it failed. The reason is what
    /// the install plan reports, so a refusal says which rule was hit.
    /// </summary>
    public static bool Explain(string root, string relative, out string? reason)
    {
        reason = null;

        if (string.IsNullOrWhiteSpace(relative))
        {
            reason = "the destination is empty.";
            return false;
        }

        if (Path.IsPathRooted(relative) || relative.StartsWith("\\\\", StringComparison.Ordinal))
        {
            reason = $"'{relative}' is an absolute path; a cookie only writes inside the project.";
            return false;
        }

        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch (Exception ex)
        {
            reason = $"'{relative}' is not a usable path: {ex.Message}";
            return false;
        }

        if (!IsInside(root, combined))
        {
            reason = $"'{relative}' resolves outside the project root.";
            return false;
        }

        string withinProject = Path.GetRelativePath(root, combined).Replace('\\', '/');
        foreach (string segment in withinProject.Split('/'))
        {
            if (ForbiddenFolders.Any(f => string.Equals(segment, f, PathComparison)))
            {
                reason = $"'{relative}' writes into '{segment}', which is build output or private to the project.";
                return false;
            }
        }

        string extension = Path.GetExtension(combined);
        if (ForbiddenExtensions.Any(e => string.Equals(extension, e, PathComparison)))
        {
            reason = $"'{relative}' would write a project file ({extension}); a cookie may not redefine the project.";
            return false;
        }

        return true;
    }

    /// <summary>A path relative to <paramref name="root"/>, always with forward slashes.</summary>
    public static string Relative(string root, string absolute)
        => Path.GetRelativePath(root, absolute).Replace('\\', '/');
}
