using SexyBiscuit.Engine.Code;

namespace SexyBiscuit.Engine.CookieJar;

/// <summary>
/// Finds the jars that exist without anyone configuring them: the engine repository's own
/// <c>CookieJar/</c> and the per-user library. Probing follows the same order the project template
/// locator uses, because the editor runs from a checkout, from a build output, or from an install,
/// and only one of those has the repository above it.
/// </summary>
public static class CookieJarLocator
{
    /// <summary>The folder name a jar has inside the engine repository.</summary>
    public const string BuiltinFolderName = "CookieJar";

    /// <summary>Overrides where the builtin jar is looked for.</summary>
    public const string EnvironmentVariable = "SEXYBISCUIT_COOKIEJAR";

    /// <summary>
    /// The engine repository's jar, or null when the editor is not running from a checkout.
    /// </summary>
    public static string? FindBuiltinJar(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        string? fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
            return Path.GetFullPath(fromEnv);

        // The repository knows where it is; ask it before guessing.
        if (EngineRepoLocator.Find() is { } repo)
        {
            string inRepo = Path.Combine(repo.Root, BuiltinFolderName);
            if (Directory.Exists(inRepo)) return inRepo;
        }

        foreach (string candidate in ProbeRoots())
        {
            string path = Path.Combine(candidate, BuiltinFolderName);
            if (Directory.Exists(path)) return Path.GetFullPath(path);
        }

        return null;
    }

    /// <summary>
    /// Where a cookie is baked when there is no engine repository, and where folder jars the user
    /// adds are kept. Beside the assistant settings and the recent-project list, so it survives a
    /// rebuild and works from a read-only install.
    /// </summary>
    public static string UserLibraryRoot()
        => Path.Combine(SettingsRoot(), "CookieJar");

    /// <summary>Where git jars are cloned. Never inside a project or the engine repository.</summary>
    public static string CloneRoot()
        => Path.Combine(SettingsRoot(), "CookieJarClones");

    /// <summary>The per-user SexyBiscuit folder, created if it is missing.</summary>
    public static string SettingsRoot()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SexyBiscuit");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// The jars that exist before the user configures anything: the builtin one when a checkout is
    /// found, and the per-user library when it has been created.
    /// </summary>
    public static IReadOnlyList<CookieJarSource> DefaultJars(string? explicitBuiltin = null)
    {
        var jars = new List<CookieJarSource>();

        if (FindBuiltinJar(explicitBuiltin) is { } builtin)
            jars.Add(CookieJarSource.Builtin(builtin));

        string library = UserLibraryRoot();
        if (Directory.Exists(library))
            jars.Add(CookieJarSource.Folder("library", library));

        return jars;
    }

    /// <summary>Turns a git URL or a folder path into a jar name that is safe as a directory name.</summary>
    public static string SlugFor(string urlOrPath)
    {
        string trimmed = urlOrPath.TrimEnd('/', '\\');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        string last = trimmed.Split('/', '\\', ':').LastOrDefault(part => part.Length > 0) ?? "jar";
        var slug = new string(last.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '-').ToArray())
            .Trim('-');

        return slug.Length == 0 ? "jar" : slug;
    }

    private static IEnumerable<string> ProbeRoots()
    {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();

        // The editor binary sits four levels deep in bin/, so six covers a checkout comfortably.
        string walk = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            string? parent = Path.GetDirectoryName(walk.TrimEnd(Path.DirectorySeparatorChar));
            if (parent == null || parent == walk) yield break;
            walk = parent;
            yield return walk;
        }
    }
}
