namespace SexyBiscuit.Engine.CookieJar;

/// <summary>What happened when the editor rebuilt and reloaded after a cookie landed.</summary>
public sealed record CookieBuildReport(
    bool    Required,
    bool    Ran,
    bool    Succeeded,
    int     Errors      = 0,
    int     Warnings    = 0,
    int     Generation  = 0,
    string? Message     = null)
{
    /// <summary>Nothing to build: the cookie added no C#, or there is no code project.</summary>
    public static CookieBuildReport NotNeeded => new(Required: false, Ran: false, Succeeded: true);
}

/// <summary>What a jar refresh found.</summary>
public sealed record CookieJarRefresh(
    string                Name,
    string?               FromCommit,
    string?               ToCommit,
    IReadOnlyList<string> ChangedCookies,
    IReadOnlyList<string> AffectsInstalled,
    string                Status);

/// <summary>
/// The editor's side of the CookieJar, as little of it as the tools need. Keeping the tools in the
/// engine behind this interface is what lets them be tested by name through a registry, and what
/// lets the headless tool dump cover their schemas.
/// </summary>
public interface ICookieHost
{
    /// <summary>The merged catalogue, cached; <paramref name="refresh"/> rescans the jars.</summary>
    CookieCatalogue Catalogue(bool refresh = false);

    /// <summary>Every configured jar, in search order.</summary>
    IReadOnlyList<CookieJarSource> Jars { get; }

    /// <summary>The open project, or null when the editor has none.</summary>
    CookieProjectContext? Project { get; }

    /// <summary>The open project's install record.</summary>
    CookieLockFile Lock { get; }

    /// <summary>Where <c>bake_cookie</c> writes by default.</summary>
    string DefaultBakeJar { get; }

    /// <summary>Rebuilds and hot-reloads the game code after files changed, when it is needed.</summary>
    Task<CookieBuildReport> AfterFilesChangedAsync(bool needsBuild, CancellationToken cancellation = default);

    /// <summary>
    /// Asks the person at the editor to confirm an install from a jar that is not the builtin one.
    /// Returning false refuses the install.
    /// </summary>
    Task<bool> ConfirmInstallAsync(Cookie cookie, CookieInstallPlan plan, CancellationToken cancellation = default);

    /// <summary>
    /// Records a jar the user might want, without cloning it. Trusting and cloning happens in the
    /// editor, so that no tool call can put third-party code on the machine.
    /// </summary>
    CookieJarSource StageJar(string urlOrPath, string? name = null);

    /// <summary>Fetches an already-trusted jar and reports what moved.</summary>
    Task<CookieJarRefresh> RefreshJarAsync(string name, CancellationToken cancellation = default);
}

/// <summary>
/// A host with no editor behind it: the catalogue is real, everything that would change the
/// machine is refused. Used by the tests and by the headless tool dump.
/// </summary>
public sealed class HeadlessCookieHost : ICookieHost
{
    private CookieCatalogue? _catalogue;

    public HeadlessCookieHost(IReadOnlyList<CookieJarSource>? jars = null, CookieProjectContext? project = null)
    {
        Jars    = jars ?? CookieJarLocator.DefaultJars();
        Project = project;
        Lock    = project != null ? CookieLockFile.Load(project.Root) : new CookieLockFile();
    }

    public IReadOnlyList<CookieJarSource> Jars    { get; }
    public CookieProjectContext?          Project { get; }
    public CookieLockFile                 Lock    { get; }

    public string DefaultBakeJar =>
        Jars.FirstOrDefault(j => j.Kind == CookieJarKind.Builtin)?.Path ?? CookieJarLocator.UserLibraryRoot();

    public CookieCatalogue Catalogue(bool refresh = false)
    {
        if (refresh || _catalogue == null) _catalogue = CookieCatalogue.Scan(Jars);
        return _catalogue;
    }

    public Task<CookieBuildReport> AfterFilesChangedAsync(bool needsBuild, CancellationToken cancellation = default)
        => Task.FromResult(needsBuild
            ? new CookieBuildReport(Required: true, Ran: false, Succeeded: true, Message: "no editor is running, so nothing was built.")
            : CookieBuildReport.NotNeeded);

    /// <summary>Always true: there is nobody to ask, and nothing here installs from a git jar.</summary>
    public Task<bool> ConfirmInstallAsync(Cookie cookie, CookieInstallPlan plan, CancellationToken cancellation = default)
        => Task.FromResult(true);

    public CookieJarSource StageJar(string urlOrPath, string? name = null)
        => throw new CookieException("There is no editor open to approve a new jar.",
                                     "Add the jar from the editor's Cookie Jar panel.");

    public Task<CookieJarRefresh> RefreshJarAsync(string name, CancellationToken cancellation = default)
        => throw new CookieException("There is no editor open to refresh a jar.");
}
