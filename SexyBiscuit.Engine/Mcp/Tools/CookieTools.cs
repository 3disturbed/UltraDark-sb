using System.Text.Json.Nodes;
using SexyBiscuit.Engine.CookieJar;

namespace SexyBiscuit.Engine.Mcp.Tools;

/// <summary>
/// The CookieJar as tools: search the library, install a module into the open project, remove one,
/// and bake reusable work back out. Installing returns the cookie's own instructions, so the call
/// after it needs no reads.
/// </summary>
public sealed class CookieTools
{
    /// <summary>Agent instructions longer than this are pointed at rather than inlined.</summary>
    private const int AgentTextCap = 8 * 1024;

    private readonly ICookieHost _host;

    public CookieTools(ICookieHost host) => _host = host;

    // -------------------------------------------------------------------------
    // Finding
    // -------------------------------------------------------------------------

    [McpTool("search_cookies",
        "Search the CookieJar: the team's library of ready-made modules (a character controller, an input map, a HUD). " +
        "Call this before writing a common mechanic by hand. Returns one line per cookie with what it provides; " +
        "install_cookie then copies one into the open project and tells you how to wire it up.",
        MainThread = false, Label = "Search the CookieJar")]
    public McpToolResult SearchCookies(
        [McpParam("Words to match against id, name, tags and summary", Example = "double jump")] string? query = null,
        [McpParam("Only cookies carrying every one of these tags")] string[]? tags = null,
        [McpParam("Only cookies that run on this engine: 'csharp' or 'js'")] string? engine = null,
        [McpParam("Include ones already installed")] bool includeInstalled = true,
        [McpParam("Most results to return")] int limit = 20)
    {
        var catalogue = _host.Catalogue();
        var installed = _host.Lock;

        var rows = new JsonArray();
        foreach (var hit in catalogue.Search(query, tags, engine, limit))
        {
            var record = installed.Find(hit.Cookie.Id);
            if (record != null && !includeInstalled) continue;
            rows.Add(Row(hit.Cookie, record));
        }

        var jars = new JsonArray();
        foreach (var jar in catalogue.Jars)
            jars.Add(new JsonObject
            {
                ["name"]        = jar.Name,
                ["kind"]        = jar.Kind.ToString().ToLowerInvariant(),
                ["trusted"]     = jar.Trusted,
                ["enabled"]     = jar.Enabled,
                ["cookieCount"] = catalogue.All.Count(c => c.JarName == jar.Name),
            });

        return McpToolResult.Json(new JsonObject
        {
            ["count"]   = rows.Count,
            ["cookies"] = rows,
            ["jars"]    = jars,
            ["problems"] = Problems(catalogue.Problems),
        });
    }

    [McpTool("get_cookie",
        "Everything about one cookie: its manifest, what it provides, every file it would install, and its full " +
        "AGENT.md instructions.",
        MainThread = false, Label = "Read cookie {id}")]
    public McpToolResult GetCookie([McpParam("The cookie's id", Example = "double-jump")] string id)
    {
        var cookie = Require(id);
        var record = _host.Lock.Find(id);

        var files = new JsonArray();
        foreach (string file in cookie.Files()) files.Add(file);

        var view = Row(cookie, record);
        view["description"] = cookie.Manifest.Description;
        view["namespace"]   = cookie.Manifest.EffectiveNamespace;
        view["files"]       = files;
        view["agent"]       = Agent(cookie, out bool truncated);
        view["agentTruncated"] = truncated;

        return McpToolResult.Json(view);
    }

    [McpTool("list_installed_cookies",
        "What this project has installed, from CookieJar.lock.json, plus any file that has been edited or deleted " +
        "since it was installed.",
        MainThread = false, Label = "List installed cookies")]
    public McpToolResult ListInstalledCookies()
    {
        var project = RequireProject();
        var rows    = new JsonArray();
        var drift   = new JsonArray();

        foreach (var entry in _host.Lock.Cookies)
        {
            rows.Add(new JsonObject
            {
                ["id"]        = entry.Id,
                ["version"]   = entry.Version,
                ["jar"]       = entry.Jar,
                ["namespace"] = entry.Namespace,
                ["files"]     = entry.Files.Count,
                ["inCatalogue"] = _host.Catalogue().Find(entry.Id) != null,
            });

            foreach (var file in entry.Files)
            {
                string absolute = Path.Combine(project.Root, file.Path.Replace('/', Path.DirectorySeparatorChar));
                string? hash    = CookieLockFile.HashFile(absolute);

                if (hash == null)
                    drift.Add(new JsonObject { ["id"] = entry.Id, ["file"] = file.Path, ["state"] = "missing" });
                else if (hash != file.Sha256)
                    drift.Add(new JsonObject { ["id"] = entry.Id, ["file"] = file.Path, ["state"] = "modified" });
            }
        }

        return McpToolResult.Json(new JsonObject { ["installed"] = rows, ["drift"] = drift });
    }

    // -------------------------------------------------------------------------
    // Installing
    // -------------------------------------------------------------------------

    [McpTool("install_cookie",
        "Copy a cookie into the open project, with anything it requires, then build and hot-reload if it added C#. " +
        "The result carries the cookie's AGENT.md and its next steps: follow those rather than reading its files. " +
        "Nothing is written when the plan is blocked; read the conflicts and fix them.",
        Mutating = false, Label = "Install cookie {id}")]
    public async Task<McpToolResult> InstallCookie(
        [McpParam("The cookie's id", Example = "double-jump")] string id,
        [McpParam("Also install what it requires")] bool includeDependencies = true,
        [McpParam("Report the plan without writing anything")] bool dryRun = false,
        [McpParam("Replace files that are already there")] bool overwrite = false,
        CancellationToken cancellation = default)
    {
        var project   = RequireProject();
        var catalogue = _host.Catalogue();
        var installed = _host.Lock;

        Require(id);   // fail early with a good message

        var resolution = CookieDependencies.Resolve(catalogue, new[] { id }, installed, includeDependencies);

        if (resolution.Missing.Count > 0)
            throw new McpToolException(
                $"Cookie '{id}' requires {string.Join(", ", resolution.Missing)}, which no enabled jar supplies.",
                "Call search_cookies to see what is available, or add the jar that has them.");

        if (resolution.Cycles.Count > 0)
            throw new McpToolException(
                $"The requirements of '{id}' form a loop: {string.Join(" -> ", resolution.Cycles[0])}.",
                "That is a bug in the cookies; fix one of their manifests.");

        var options   = new CookieInstallOptions(overwrite, includeDependencies);
        var plans     = new List<CookieInstallPlan>();
        var conflicts = new JsonArray();
        bool blocked  = false;

        foreach (var cookie in resolution.Ordered)
        {
            // Something already installed and unchanged is simply skipped, not re-planned.
            if (!overwrite && installed.Find(cookie.Id) is { } already &&
                CookieVersion.Parse(already.Version).CompareTo(cookie.Version) == 0 && cookie.Id != id)
                continue;

            var plan = CookiePlanner.Plan(cookie, project, installed, options, catalogue.JarOf(cookie));
            plans.Add(plan);

            foreach (var conflict in plan.Conflicts)
            {
                conflicts.Add(new JsonObject
                {
                    ["cookie"]   = cookie.Id,
                    ["kind"]     = conflict.Kind.ToString(),
                    ["subject"]  = conflict.Subject,
                    ["detail"]   = conflict.Detail,
                    ["blocking"] = conflict.Blocking,
                });
                blocked |= conflict.Blocking;
            }
        }

        if (blocked || dryRun)
            return McpToolResult.Json(new JsonObject
            {
                ["status"]    = blocked ? "blocked" : "planned",
                ["cookie"]    = id,
                ["plan"]      = PlanView(plans),
                ["conflicts"] = conflicts,
            });

        foreach (var plan in plans)
        {
            var jar = catalogue.JarOf(plan.Cookie);
            if (jar is { Kind: not CookieJarKind.Builtin } &&
                !await _host.ConfirmInstallAsync(plan.Cookie, plan, cancellation).ConfigureAwait(false))
                throw new McpToolException(
                    $"Installing '{plan.Cookie.Id}' from jar '{jar.Name}' was declined in the editor.",
                    "Ask the user to approve it, or install a cookie from the builtin jar instead.");
        }

        var outcomes = new List<CookieInstallOutcome>();
        foreach (var plan in plans)
            outcomes.Add(CookieInstaller.Apply(plan, project, installed, catalogue.JarOf(plan.Cookie)));

        bool needsBuild = plans.Any(p => p.RequiresBuild);
        var  build      = await _host.AfterFilesChangedAsync(needsBuild, cancellation).ConfigureAwait(false);

        return McpToolResult.Json(InstallView(id, resolution, outcomes, build, conflicts));
    }

    [McpTool("uninstall_cookie",
        "Remove a cookie from the open project. A file is deleted only when it still matches what was installed, so " +
        "anything edited since is kept and reported.",
        Mutating = false, Destructive = true, Label = "Uninstall cookie {id}")]
    public async Task<McpToolResult> UninstallCookie(
        [McpParam("The cookie's id")] string id,
        [McpParam("Remove it even when another cookie needs it, or a file has been edited")] bool force = false,
        CancellationToken cancellation = default)
    {
        var project = RequireProject();

        CookieUninstallPlan plan;
        try
        {
            plan = CookieUninstaller.Plan(project.Root, _host.Lock, id, force);
        }
        catch (CookieException ex)
        {
            throw new McpToolException(ex.Message, ex.Hint);
        }

        if (!plan.IsApplicable)
            throw new McpToolException(
                $"'{id}' is required by {string.Join(", ", plan.Dependents)}.",
                "Remove those first, or pass force to break them deliberately.");

        var outcome = CookieUninstaller.Apply(project.Root, _host.Lock, plan);
        var build   = await _host.AfterFilesChangedAsync(plan.Cookie.Engines.Contains(CookieManifest.EngineCSharp), cancellation)
                                 .ConfigureAwait(false);

        var kept = new JsonArray();
        foreach (var file in outcome.Kept) kept.Add(new JsonObject { ["path"] = file.Path, ["reason"] = file.Reason });

        return McpToolResult.Json(new JsonObject
        {
            ["status"]  = "removed",
            ["cookie"]  = id,
            ["removed"] = Strings(outcome.Removed),
            ["kept"]    = kept,
            ["removedDirectories"] = Strings(outcome.RemovedDirectories),
            ["build"]   = BuildView(build),
        });
    }

    // -------------------------------------------------------------------------
    // Baking
    // -------------------------------------------------------------------------

    [McpTool("bake_cookie",
        "Save reusable work from the open project back into the CookieJar as a new cookie. Give it the files, a one-line " +
        "summary, and agent instructions saying how to wire it up in the next game. Do this whenever a task produces " +
        "something a second game would want.",
        Label = "Bake cookie {id}")]
    public McpToolResult BakeCookie(
        [McpParam("Kebab-case id, unique in the jar", Example = "double-jump")] string id,
        [McpParam("Display name")] string name,
        [McpParam("One line saying what it is; this is what a search returns")] string summary,
        [McpParam("How to use it once installed: what to add, what to set, what it does not do")] string agentInstructions,
        [McpParam("Project-relative files to include", Example = "Source/Spinner.cs")] string[] files,
        [McpParam("Tags for searching")] string[]? tags = null,
        [McpParam("Steps to take after installing")] string[]? nextSteps = null,
        [McpParam("Cookie ids this one needs")] string[]? requires = null,
        [McpParam("Semantic version")] string version = "1.0.0",
        [McpParam("Where to write it: a jar name, or a folder path")] string? jar = null,
        [McpParam("Replace an existing cookie with this id")] bool overwrite = false,
        [McpParam("Report the plan without writing anything")] bool dryRun = false)
    {
        var project = RequireProject();

        string destination = jar == null
            ? _host.DefaultBakeJar
            : _host.Jars.FirstOrDefault(j => string.Equals(j.Name, jar, StringComparison.OrdinalIgnoreCase))?.Path ?? jar;

        var request = new BakeRequest(
            Project:           project,
            Id:                id,
            Name:              name,
            Summary:           summary,
            AgentInstructions: agentInstructions,
            DestinationJar:    destination,
            Files:             files,
            Tags:              tags,
            Requires:          requires,
            NextSteps:         nextSteps,
            Version:           version,
            Overwrite:         overwrite);

        BakePlan plan;
        try
        {
            plan = CookieBaker.Plan(request);
        }
        catch (CookieException ex)
        {
            throw new McpToolException(ex.Message, ex.Hint);
        }

        var notes = new JsonArray();
        foreach (var conflict in plan.Conflicts)
            notes.Add(new JsonObject { ["subject"] = conflict.Subject, ["detail"] = conflict.Detail, ["blocking"] = conflict.Blocking });

        if (!plan.IsApplicable || dryRun)
            return McpToolResult.Json(new JsonObject
            {
                ["status"]    = plan.IsApplicable ? "planned" : "blocked",
                ["directory"] = plan.Directory,
                ["files"]     = Strings(plan.Files.Select(f => f.DestinationRelative).ToList()),
                ["manifest"]  = JsonNode.Parse(plan.Manifest.ToJson()),
                ["notes"]     = notes,
            });

        BakeOutcome outcome;
        try
        {
            outcome = CookieBaker.Apply(plan);
        }
        catch (CookieException ex)
        {
            throw new McpToolException(ex.Message, ex.Hint);
        }

        _host.Catalogue(refresh: true);

        return McpToolResult.Json(new JsonObject
        {
            ["status"]       = "baked",
            ["directory"]    = outcome.Directory,
            ["files"]        = Strings(outcome.Files),
            ["renamespaced"] = Strings(outcome.Renamespaced),
            ["manifest"]     = JsonNode.Parse(outcome.Manifest.ToJson()),
            ["validation"]   = Problems(outcome.Validation),
            ["notes"]        = notes,
            ["next"]         = "Check the generated AGENT.md reads like instructions, then commit the cookie.",
        });
    }

    // -------------------------------------------------------------------------
    // Jars
    // -------------------------------------------------------------------------

    [McpTool("list_cookie_jars", "The jars the catalogue is built from, and whether each may be installed from.",
        MainThread = false, Label = "List cookie jars")]
    public McpToolResult ListCookieJars()
    {
        var catalogue = _host.Catalogue();
        var rows      = new JsonArray();

        foreach (var jar in _host.Jars)
            rows.Add(new JsonObject
            {
                ["name"]           = jar.Name,
                ["kind"]           = jar.Kind.ToString().ToLowerInvariant(),
                ["path"]           = jar.Path,
                ["remoteUrl"]      = jar.RemoteUrl,
                ["branch"]         = jar.Branch,
                ["enabled"]        = jar.Enabled,
                ["trusted"]        = jar.Trusted,
                ["installable"]    = jar.IsInstallable,
                ["pinnedCommit"]   = jar.PinnedCommit,
                ["lastRefreshUtc"] = jar.LastRefreshUtc?.ToString("O"),
                ["cookieCount"]    = catalogue.All.Count(c => c.JarName == jar.Name),
            });

        return McpToolResult.Json(new JsonObject { ["jars"] = rows, ["problems"] = Problems(catalogue.Problems) });
    }

    [McpTool("add_cookie_jar",
        "Propose a new jar. This never clones and never trusts anything: it records the address and returns, and the " +
        "person at the editor decides. Tell the user what you proposed and ask them to approve it in the Cookie Jar panel.",
        Label = "Propose cookie jar {urlOrPath}")]
    public McpToolResult AddCookieJar(
        [McpParam("A git URL or a local folder", Example = "https://example.com/team-cookies.git")] string urlOrPath,
        [McpParam("What to call it")] string? name = null)
    {
        CookieJarSource staged;
        try
        {
            staged = _host.StageJar(urlOrPath, name);
        }
        catch (CookieException ex)
        {
            throw new McpToolException(ex.Message, ex.Hint);
        }

        return McpToolResult.Json(new JsonObject
        {
            ["status"] = "awaiting_approval",
            ["jar"]    = new JsonObject
            {
                ["name"]      = staged.Name,
                ["kind"]      = staged.Kind.ToString().ToLowerInvariant(),
                ["remoteUrl"] = staged.RemoteUrl,
                ["trusted"]   = staged.Trusted,
            },
            ["message"] = "Recorded, not cloned. Installing a cookie compiles and runs its code, so the user has to "
                        + "press Trust and clone in the editor's Cookie Jar panel before anything is fetched.",
        });
    }

    [McpTool("refresh_cookie_jar",
        "Fetch a trusted git jar and report what changed, including any cookie this project has installed.",
        Label = "Refresh jar {name}")]
    public async Task<McpToolResult> RefreshCookieJar(
        [McpParam("The jar's name")] string name,
        CancellationToken cancellation = default)
    {
        CookieJarRefresh refresh;
        try
        {
            refresh = await _host.RefreshJarAsync(name, cancellation).ConfigureAwait(false);
        }
        catch (CookieException ex)
        {
            throw new McpToolException(ex.Message, ex.Hint);
        }

        _host.Catalogue(refresh: true);

        return McpToolResult.Json(new JsonObject
        {
            ["status"]           = refresh.Status,
            ["jar"]              = refresh.Name,
            ["fromCommit"]       = refresh.FromCommit,
            ["toCommit"]         = refresh.ToCommit,
            ["changedCookies"]   = Strings(refresh.ChangedCookies),
            ["affectsInstalled"] = Strings(refresh.AffectsInstalled),
        });
    }

    // -------------------------------------------------------------------------
    // Views
    // -------------------------------------------------------------------------

    private Cookie Require(string id)
    {
        var catalogue = _host.Catalogue();
        if (catalogue.Find(id) is { } cookie) return cookie;

        var near = catalogue.Search(id, limit: 3).Select(h => h.Cookie.Id).ToList();
        throw new McpToolException(
            $"No cookie called '{id}' in any enabled jar.",
            near.Count > 0 ? "Did you mean: " + string.Join(", ", near) + "?" : "Call search_cookies to see what there is.");
    }

    private CookieProjectContext RequireProject()
        => _host.Project ?? throw new McpToolException("No project is open.", "Call open_project first.");

    private static JsonObject Row(Cookie cookie, InstalledCookie? installed)
    {
        var m = cookie.Manifest;
        return new JsonObject
        {
            ["id"]        = m.Id,
            ["name"]      = m.Name,
            ["version"]   = m.Version,
            ["summary"]   = m.Summary,
            ["tags"]      = Strings(m.Tags),
            ["engines"]   = Strings(m.Engines),
            ["requires"]  = Strings(m.Requires),
            ["provides"]  = new JsonObject
            {
                ["components"]   = Strings(m.Provides.Components),
                ["actorClasses"] = Strings(m.Provides.ActorClasses),
                ["scripts"]      = Strings(m.Provides.Scripts),
                ["prefabs"]      = Strings(m.Provides.Prefabs),
            },
            ["jar"]              = cookie.JarName,
            ["installed"]        = installed != null,
            ["installedVersion"] = installed?.Version,
        };
    }

    private static string Agent(Cookie cookie, out bool truncated)
    {
        string text = cookie.AgentInstructions();
        truncated = text.Length > AgentTextCap;
        return truncated ? text[..AgentTextCap] : text;
    }

    private JsonObject InstallView(
        string                            requested,
        DependencyResolution              resolution,
        IReadOnlyList<CookieInstallOutcome> outcomes,
        CookieBuildReport                 build,
        JsonArray                         conflicts)
    {
        var main = outcomes.LastOrDefault(o => o.Cookie.Id == requested) ?? outcomes.Last();

        var dependencies = new JsonArray();
        foreach (var outcome in outcomes.Where(o => o.Cookie.Id != requested))
            dependencies.Add(new JsonObject { ["id"] = outcome.Cookie.Id, ["version"] = outcome.Cookie.Manifest.Version });

        var files = new JsonArray();
        foreach (var outcome in outcomes)
            foreach (var file in outcome.Written)
                files.Add(new JsonObject
                {
                    ["path"]   = file.DestinationRelative,
                    ["action"] = file.Action == PlannedFileAction.Overwrite ? "overwritten" : "created",
                });

        string agent = Agent(main.Cookie, out bool truncated);

        return new JsonObject
        {
            ["status"]       = "installed",
            ["cookie"]       = new JsonObject
            {
                ["id"]      = main.Cookie.Id,
                ["name"]    = main.Cookie.Name,
                ["version"] = main.Cookie.Manifest.Version,
                ["jar"]     = main.Cookie.JarName,
            },
            ["dependencies"] = dependencies,
            ["files"]        = files,
            ["namespace"]    = main.Cookie.Manifest.EffectiveNamespace,
            ["usings"]       = Strings(outcomes.Where(o => o.Cookie.Manifest.SupportsEngine(CookieManifest.EngineCSharp))
                                               .Select(o => o.Cookie.Manifest.EffectiveNamespace).Distinct().ToList()),
            ["provides"]     = new JsonObject
            {
                ["components"]   = Strings(main.Cookie.Manifest.Provides.Components),
                ["actorClasses"] = Strings(main.Cookie.Manifest.Provides.ActorClasses),
                ["scripts"]      = Strings(main.Cookie.Manifest.Provides.Scripts),
                ["prefabs"]      = Strings(main.Cookie.Manifest.Provides.Prefabs),
            },
            ["unresolvedReferences"] = Strings(outcomes.SelectMany(o => o.Unresolved).Distinct().ToList()),
            ["build"]        = BuildView(build),
            ["agent"]        = agent,
            ["agentTruncated"] = truncated,
            ["nextSteps"]    = Strings(main.Cookie.Manifest.NextSteps),
            ["lockFile"]     = CookieLockFile.FileName,
            ["conflicts"]    = conflicts,
        };
    }

    private static JsonObject BuildView(CookieBuildReport build) => new()
    {
        ["required"]   = build.Required,
        ["ran"]        = build.Ran,
        ["succeeded"]  = build.Succeeded,
        ["errors"]     = build.Errors,
        ["warnings"]   = build.Warnings,
        ["generation"] = build.Generation,
        ["message"]    = build.Message,
    };

    private static JsonArray PlanView(IEnumerable<CookieInstallPlan> plans)
    {
        var rows = new JsonArray();
        foreach (var plan in plans)
            foreach (var file in plan.Files)
                rows.Add(new JsonObject
                {
                    ["cookie"] = plan.Cookie.Id,
                    ["path"]   = file.DestinationRelative,
                    ["action"] = file.Action.ToString(),
                    ["reason"] = file.Reason,
                });
        return rows;
    }

    private static JsonArray Problems(IEnumerable<CookieProblem> problems)
    {
        var rows = new JsonArray();
        foreach (var problem in problems)
            rows.Add(new JsonObject
            {
                ["severity"] = problem.Severity.ToString().ToLowerInvariant(),
                ["subject"]  = problem.Subject,
                ["message"]  = problem.Message,
            });
        return rows;
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values) array.Add(value);
        return array;
    }
}
