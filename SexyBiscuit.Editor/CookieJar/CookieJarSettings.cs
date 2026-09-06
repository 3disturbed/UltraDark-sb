using System.Text.Json;
using System.Text.Json.Serialization;
using SexyBiscuit.Engine.CookieJar;

namespace SexyBiscuit.Editor.CookieJar;

/// <summary>Where a baked cookie goes when the tool caller does not say.</summary>
public enum CookieBakeTarget
{
    /// <summary>The engine repository's own jar, so cookies are reviewed and shipped with the engine.</summary>
    EngineRepo,

    /// <summary>The per-user library, for a machine with no checkout.</summary>
    UserLibrary,
}

/// <summary>
/// The jars this user has, and which of them they trust.
/// </summary>
/// <remarks>
/// Deliberately per-user and never inside a project: a repository somebody clones must not be able
/// to ship a file that pre-trusts an attacker's jar. It is also separate from the assistant's
/// settings, because the list grows and the command-line tools need it without them.
/// </remarks>
public sealed class CookieJarSettings
{
    /// <summary>The settings format, so a future change can migrate rather than guess.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Jars in search order. The first to supply a cookie id wins.</summary>
    public List<StoredJar> Jars { get; set; } = new();

    /// <summary>Where <c>bake_cookie</c> writes by default.</summary>
    public CookieBakeTarget BakeTarget { get; set; } = CookieBakeTarget.EngineRepo;

    /// <summary>An explicit path to the builtin jar, when the repository is somewhere unusual.</summary>
    public string? BuiltinJarPath { get; set; }

    /// <summary>Where git lives, when it is not on the path.</summary>
    public string? GitPath { get; set; }

    // -------------------------------------------------------------------------
    // Persistence
    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new JsonStringEnumConverter() },
    };

    /// <summary>Beside the assistant settings and the recent-project list.</summary>
    public static string FilePath => Path.Combine(CookieJarLocator.SettingsRoot(), "cookie-jars.json");

    public static CookieJarSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new CookieJarSettings();
            return JsonSerializer.Deserialize<CookieJarSettings>(File.ReadAllText(FilePath), Json) ?? new CookieJarSettings();
        }
        catch (Exception ex)
        {
            ConsoleLog.Add("[cookiejar] settings could not be read, using defaults: " + ex.Message, LogLevel.Warning);
            return new CookieJarSettings();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            ConsoleLog.Add("[cookiejar] settings could not be saved: " + ex.Message, LogLevel.Warning);
        }
    }

    // -------------------------------------------------------------------------
    // Jars
    // -------------------------------------------------------------------------

    /// <summary>The configured jars plus the ones that exist without configuring: builtin and library.</summary>
    public IReadOnlyList<CookieJarSource> Sources()
    {
        var sources = new List<CookieJarSource>();

        foreach (var stored in Jars.Where(j => j.Cloned || j.Kind != CookieJarKind.Git))
            sources.Add(stored.ToSource());

        foreach (var discovered in CookieJarLocator.DefaultJars(BuiltinJarPath))
            if (!sources.Any(s => string.Equals(s.Name, discovered.Name, StringComparison.OrdinalIgnoreCase)))
                sources.Add(discovered);

        return sources;
    }

    public StoredJar? Find(string name)
        => Jars.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Records a jar without trusting or cloning it.</summary>
    public StoredJar Stage(string urlOrPath, string? name = null)
    {
        bool isGit = !Directory.Exists(urlOrPath);

        if (isGit && !GitRunner.IsAllowedUrl(urlOrPath))
            throw new CookieException($"'{urlOrPath}' is not a folder, and not a git URL this editor will clone.",
                                      "Use an https, ssh or local-path URL.");

        string chosen = name ?? CookieJarLocator.SlugFor(urlOrPath);
        if (Find(chosen) != null) chosen += "-" + Guid.NewGuid().ToString("N")[..4];

        var jar = new StoredJar
        {
            Name      = chosen,
            Kind      = isGit ? CookieJarKind.Git : CookieJarKind.Folder,
            Path      = isGit ? Path.Combine(CookieJarLocator.CloneRoot(), chosen) : Path.GetFullPath(urlOrPath),
            RemoteUrl = isGit ? urlOrPath : null,
            // A folder the user picked in the panel is trusted by that act; a repository is not.
            Trusted   = !isGit,
            Cloned    = !isGit,
        };

        Jars.Add(jar);
        Save();
        return jar;
    }

    public void Remove(string name)
    {
        Jars.RemoveAll(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
        Save();
    }
}

/// <summary>One jar as it is stored between sessions.</summary>
public sealed class StoredJar
{
    public string        Name      { get; set; } = "";
    public CookieJarKind Kind      { get; set; } = CookieJarKind.Folder;
    public string        Path      { get; set; } = "";
    public string?       RemoteUrl { get; set; }
    public string?       Branch    { get; set; }
    public bool          Enabled   { get; set; } = true;

    /// <summary>Set only by a person pressing Trust in the editor. No tool can set it.</summary>
    public bool      Trusted    { get; set; }
    public DateTime? TrustedUtc { get; set; }

    /// <summary>False until the repository has actually been fetched.</summary>
    public bool      Cloned         { get; set; }
    public string?   PinnedCommit   { get; set; }
    public DateTime? LastRefreshUtc { get; set; }

    public CookieJarSource ToSource()
        => new(Name, Kind, Path, RemoteUrl, Branch, Enabled, Trusted, TrustedUtc, PinnedCommit, LastRefreshUtc);
}
