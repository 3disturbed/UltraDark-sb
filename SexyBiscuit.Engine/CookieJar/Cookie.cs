namespace SexyBiscuit.Engine.CookieJar;

/// <summary>One module pack on disk: its manifest, where it lives, and which jar it came from.</summary>
public sealed class Cookie
{
    /// <summary>The file every cookie must carry, telling an agent how to use it once installed.</summary>
    public const string AgentFileName = "AGENT.md";

    /// <summary>The manifest file name.</summary>
    public const string ManifestFileName = "cookie.json";

    private Cookie(CookieManifest manifest, string directory, string jarName)
    {
        Manifest  = manifest;
        Directory = directory;
        JarName   = jarName;
    }

    public CookieManifest Manifest { get; }

    /// <summary>The cookie's folder, named after its id.</summary>
    public string Directory { get; }

    /// <summary>The name of the jar it was found in, never a path: paths differ per machine.</summary>
    public string JarName { get; }

    public string Id      => Manifest.Id;
    public string Name    => Manifest.Name;
    public string Summary => Manifest.Summary;

    /// <summary>The parsed version, or 0.0.0 when the manifest's is unparseable.</summary>
    public CookieVersion Version => CookieVersion.Parse(Manifest.Version);

    /// <summary>The absolute path of the cookie's <c>AGENT.md</c>.</summary>
    public string AgentPath => Path.Combine(Directory, AgentFileName);

    /// <summary>
    /// The cookie's agent instructions. Returned in full by <c>install_cookie</c>, which is what
    /// lets the caller act without opening anything.
    /// </summary>
    public string AgentInstructions()
    {
        try
        {
            return File.ReadAllText(AgentPath);
        }
        catch (Exception ex)
        {
            throw new CookieException($"Cookie '{Id}' has no readable {AgentFileName}: {ex.Message}",
                                      "Every cookie must ship agent instructions.");
        }
    }

    /// <summary>Every file in the cookie, relative to its folder, with forward slashes.</summary>
    public IReadOnlyList<string> Files()
    {
        if (!System.IO.Directory.Exists(Directory)) return Array.Empty<string>();

        var files = new List<string>();
        foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories))
            files.Add(Path.GetRelativePath(Directory, path).Replace('\\', '/'));

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>
    /// Reads one cookie folder. Returns null and appends to <paramref name="problems"/> rather than
    /// throwing: one unreadable cookie must not blank the jar it sits in.
    /// </summary>
    public static Cookie? Load(string directory, string jarName, ICollection<CookieProblem> problems)
    {
        string folderName   = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string manifestPath = Path.Combine(directory, ManifestFileName);

        if (!File.Exists(manifestPath)) return null;   // not a cookie folder at all; not a problem

        CookieManifest manifest;
        try
        {
            manifest = CookieManifest.Load(manifestPath);
        }
        catch (Exception ex)
        {
            problems.Add(new CookieProblem(CookieSeverity.Error, folderName, ex.Message));
            return null;
        }

        var found = manifest.Validate(folderName).ToList();
        if (!File.Exists(Path.Combine(directory, AgentFileName)))
            found.Add(new CookieProblem(CookieSeverity.Error, folderName,
                                        $"{AgentFileName} is required: it is what an agent is told after installing."));

        foreach (var problem in found) problems.Add(problem);
        if (found.Any(p => p.Severity == CookieSeverity.Error)) return null;

        return new Cookie(manifest, Path.GetFullPath(directory), jarName);
    }

    public override string ToString() => $"{Id} {Version} ({JarName})";
}
