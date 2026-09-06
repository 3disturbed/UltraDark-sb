using SexyBiscuit.Editor.Assistant;
using SexyBiscuit.Editor.GameCode;
using SexyBiscuit.Engine.CookieJar;

namespace SexyBiscuit.Editor.CookieJar;

/// <summary>
/// The CookieJar inside the running editor: the catalogue and its cache, the build that follows an
/// install, the confirmation a non-builtin jar needs, and the only code path that clones one.
/// </summary>
public sealed class EditorCookieHost : ICookieHost
{
    private readonly Func<AssistantHost?> _assistant;
    private CookieCatalogue?              _catalogue;
    private CookieLockFile?               _lock;
    private string?                       _lockRoot;

    public EditorCookieHost(Func<AssistantHost?> assistant)
    {
        _assistant = assistant;
        Settings   = CookieJarSettings.Load();
    }

    public CookieJarSettings Settings { get; }

    /// <summary>Raised when the catalogue or the jar list changed, so the panel can redraw.</summary>
    public event Action? Changed;

    public IReadOnlyList<CookieJarSource> Jars => Settings.Sources();

    public CookieProjectContext? Project
    {
        get
        {
            string? root = EditorState.ProjectPath;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;

            var project = CookieProjectContext.ForRoot(root) with { EngineVersion = McpHost.EngineVersion };
            return project;
        }
    }

    public CookieLockFile Lock
    {
        get
        {
            string? root = EditorState.ProjectPath;
            if (string.IsNullOrEmpty(root)) return _lock ??= new CookieLockFile();

            if (_lock == null || !string.Equals(_lockRoot, root, StringComparison.Ordinal))
            {
                _lock     = CookieLockFile.Load(root);
                _lockRoot = root;
                foreach (var problem in _lock.Problems) ConsoleLog.Add("[cookiejar] " + problem.Message, LogLevel.Warning);
            }

            return _lock;
        }
    }

    public string DefaultBakeJar
    {
        get
        {
            if (Settings.BakeTarget == CookieBakeTarget.EngineRepo &&
                CookieJarLocator.FindBuiltinJar(Settings.BuiltinJarPath) is { } builtin)
                return builtin;

            string library = CookieJarLocator.UserLibraryRoot();
            Directory.CreateDirectory(library);
            return library;
        }
    }

    public CookieCatalogue Catalogue(bool refresh = false)
    {
        if (!refresh && _catalogue != null) return _catalogue;

        _catalogue = CookieCatalogue.Scan(Jars);

        foreach (var problem in _catalogue.Problems.Where(p => p.Severity == CookieSeverity.Error))
            ConsoleLog.Add($"[cookiejar] {problem.Subject}: {problem.Message}", LogLevel.Warning);

        Changed?.Invoke();
        return _catalogue;
    }

    /// <summary>
    /// Adds a small <c>cookies</c> section to <c>get_project_info</c>, so the first call an agent
    /// makes already says what the library holds and what this project has installed.
    /// </summary>
    public void ContributeProjectInfo(System.Text.Json.Nodes.JsonObject info)
    {
        var catalogue = Catalogue();

        var jars = new System.Text.Json.Nodes.JsonArray();
        foreach (var jar in catalogue.Jars)
            jars.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["name"]    = jar.Name,
                ["kind"]    = jar.Kind.ToString().ToLowerInvariant(),
                ["trusted"] = jar.Trusted,
            });

        var installed = new System.Text.Json.Nodes.JsonArray();
        foreach (var entry in Lock.Cookies)
            installed.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = entry.Id, ["version"] = entry.Version });

        info["cookies"] = new System.Text.Json.Nodes.JsonObject
        {
            ["available"] = catalogue.All.Count,
            ["installed"] = installed,
            ["jars"]      = jars,
            ["resource"]  = "sexybiscuit://cookies",
            ["hint"]      = "search_cookies before writing a common mechanic by hand.",
        };
    }

    /// <summary>Drops the cached catalogue and lock file, so the next read sees the new project.</summary>
    public void OnProjectOpened(string root)
    {
        _lock      = null;
        _lockRoot  = null;
        _catalogue = null;
        Catalogue(refresh: true);
    }

    public async Task<CookieBuildReport> AfterFilesChangedAsync(bool needsBuild, CancellationToken cancellation = default)
    {
        var code = GameCodeHost.Instance;

        if (!needsBuild) return CookieBuildReport.NotNeeded;
        if (code?.Project == null)
            return new CookieBuildReport(Required: true, Ran: false, Succeeded: true,
                                         Message: "the project has no C# project yet; call create_code_project, then reload_game_code.");

        try
        {
            var result = await code.ReloadAsync(build: true, reason: "cookie install", cancellation: cancellation)
                                   .ConfigureAwait(false);

            return new CookieBuildReport(Required: true, Ran: true, Succeeded: result.Reloaded,
                                         Errors: 0, Warnings: result.Warnings.Count, Generation: result.Generation);
        }
        catch (Exception ex)
        {
            var diagnostics = code.LastBuild?.Result;
            return new CookieBuildReport(Required: true, Ran: true, Succeeded: false,
                                         Errors:   diagnostics?.ErrorCount   ?? 1,
                                         Warnings: diagnostics?.WarningCount ?? 0,
                                         Message:  ex.Message);
        }
    }

    /// <summary>
    /// Asks the person at the editor before code from a jar that is not the engine's own is
    /// compiled and run. The assistant's permission mode does not reach this: it is the editor's
    /// own question, on the same board that backs <c>ask_user</c>.
    /// </summary>
    public async Task<bool> ConfirmInstallAsync(Cookie cookie, CookieInstallPlan plan, CancellationToken cancellation = default)
    {
        var board = _assistant()?.Board;
        if (board == null) return true;   // no assistant running: the user is driving the panel themselves

        int files = plan.Writable.Count();
        string question =
            $"Install the cookie '{cookie.Id}' {cookie.Version} from jar '{cookie.JarName}'? "
          + $"It writes {files} file(s) into this project"
          + (plan.RequiresBuild ? ", and its C# will be compiled and run." : ".");

        try
        {
            var answer = await board.AskAsync(question, new[] { "Install", "Cancel" }, allowFreeText: false,
                                              TimeSpan.FromMinutes(5), cancellation, client: "cookiejar")
                                    .ConfigureAwait(false);

            return answer != null && answer.ChoiceIndex == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public CookieJarSource StageJar(string urlOrPath, string? name = null)
    {
        var stored = Settings.Stage(urlOrPath, name);
        ConsoleLog.Add($"[cookiejar] jar '{stored.Name}' recorded, awaiting approval in the Cookie Jar panel.", LogLevel.Info);
        Catalogue(refresh: true);
        return stored.ToSource();
    }

    /// <summary>
    /// Trusts and clones a jar. Reached only from the panel: this is the one code path that puts
    /// somebody else's code on the machine, and it is deliberately not reachable from a tool.
    /// </summary>
    public async Task<string> TrustAndCloneAsync(string name, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var stored = Settings.Find(name) ?? throw new CookieException($"No jar called '{name}'.");

        stored.Trusted    = true;
        stored.TrustedUtc = DateTime.UtcNow;

        if (stored.Kind == CookieJarKind.Git && !stored.Cloned)
        {
            var git = new GitRunner(Settings.GitPath);
            Directory.CreateDirectory(CookieJarLocator.CloneRoot());

            if (Directory.Exists(stored.Path)) Directory.Delete(stored.Path, recursive: true);

            var result = await git.CloneAsync(stored.RemoteUrl!, stored.Path, stored.Branch,
                                              progress: progress, cancellation: cancellation).ConfigureAwait(false);

            if (!result.Success)
            {
                stored.Trusted = false;
                Settings.Save();
                throw new CookieException($"Cloning '{stored.RemoteUrl}' failed: {result.Text}");
            }

            stored.Cloned         = true;
            stored.PinnedCommit   = await git.RevParseAsync(stored.Path, cancellation: cancellation).ConfigureAwait(false);
            stored.LastRefreshUtc = DateTime.UtcNow;
        }

        Settings.Save();
        Catalogue(refresh: true);
        return stored.Path;
    }

    public async Task<CookieJarRefresh> RefreshJarAsync(string name, CancellationToken cancellation = default)
    {
        var stored = Settings.Find(name) ?? throw new CookieException($"No jar called '{name}'.");

        if (stored.Kind != CookieJarKind.Git)
            return new CookieJarRefresh(name, null, null, Array.Empty<string>(), Array.Empty<string>(), "not a git jar; nothing to fetch");

        if (!stored.Trusted || !stored.Cloned)
            throw new CookieException($"Jar '{name}' has not been trusted, so there is nothing to refresh.",
                                      "Approve it in the editor's Cookie Jar panel first.");

        var git    = new GitRunner(Settings.GitPath);
        string? before = await git.RevParseAsync(stored.Path, cancellation: cancellation).ConfigureAwait(false);

        var pull = await git.PullFastForwardAsync(stored.Path, cancellation: cancellation).ConfigureAwait(false);
        if (!pull.Success)
            throw new CookieException($"Refreshing '{name}' failed: {pull.Text}",
                                      "The jar may have been force-pushed; remove and re-add it.");

        string? after = await git.RevParseAsync(stored.Path, cancellation: cancellation).ConfigureAwait(false);

        var changedCookies = new List<string>();
        if (before != null && after != null && before != after)
            foreach (string file in await git.ChangedFilesAsync(stored.Path, before, after, cancellation).ConfigureAwait(false))
            {
                string top = file.Replace('\\', '/').Split('/')[0];
                if (top.Length > 0 && !changedCookies.Contains(top, StringComparer.Ordinal)) changedCookies.Add(top);
            }

        stored.PinnedCommit   = after;
        stored.LastRefreshUtc = DateTime.UtcNow;
        Settings.Save();
        Catalogue(refresh: true);

        var affected = changedCookies.Where(id => Lock.Find(id) != null).ToList();

        return new CookieJarRefresh(name, before, after, changedCookies, affected,
                                    before == after ? "already up to date" : "updated");
    }
}
