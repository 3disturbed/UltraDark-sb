using System.Text.Json.Nodes;
using SexyBiscuit.Editor.Assistant;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Mcp;
using Scene = SexyBiscuit.Engine.Core.Scene;

namespace SexyBiscuit.Editor.GameCode;

/// <summary>The C# workflow as tools: create the project, build, hot-reload, run, rebuild the engine.</summary>
public sealed class GameCodeTools
{
    private readonly McpHost       _mcp;
    private readonly GameCodeHost  _code;
    private readonly EditorRestart _restart;

    public GameCodeTools(McpHost mcp, GameCodeHost code, EditorRestart restart)
    {
        _mcp     = mcp;
        _code    = code;
        _restart = restart;
    }

    // -------------------------------------------------------------------------
    // Project
    // -------------------------------------------------------------------------

    [McpTool("get_code_project",
        "Describe the open project's C# code project: csproj path, Source/ files, output DLL, the loaded assembly " +
        "generation, whether it was compiled against the engine build this editor runs, and the last build. Read-only; " +
        "use create_code_project to add one.",
        MainThread = false)]
    public McpToolResult GetCodeProject()
    {
        var project = _code.Project;
        var dotnet  = DotnetLocator.Find();
        var types   = _code.Types;

        var info = new JsonObject
        {
            ["exists"]        = project != null,
            ["csproj"]        = project?.CsprojPath,
            ["root"]          = project?.Root,
            ["sourceDir"]     = project?.SourceDirectory,
            ["rootNamespace"] = project?.RootNamespace,
            ["assemblyName"]  = project?.AssemblyName,
            ["configuration"] = _code.Configuration,
            ["outputDll"]     = project?.OutputAssemblyPath(_code.Configuration),
            ["sourceFiles"]   = project == null ? new JsonArray() : new JsonArray(project.SourceFiles().Select(f => (JsonNode)Path.GetRelativePath(project.Root, f).Replace('\\', '/')).ToArray()),
            ["generation"]    = _code.Loader.Generation,
            ["loaded"]        = types != null,
            ["engineMatch"]   = _code.EngineMatches,
            ["autoReload"]    = _code.AutoReload,
            ["dotnet"]        = dotnet == null ? null : new JsonObject { ["path"] = dotnet.Path, ["sdk"] = dotnet.SdkVersion },
            ["lastBuild"]     = _code.LastBuild == null ? null : JobView(_code.LastBuild, includeDiagnostics: false),
        };

        if (types != null)
        {
            info["actorClasses"]   = new JsonArray(types.ActorTypes.Select(t => (JsonNode)t.Name).ToArray());
            info["componentTypes"] = new JsonArray(types.ComponentTypes.Select(t => (JsonNode)t.Name).ToArray());
            info["toolHolders"]    = new JsonArray(types.ToolHolders.Select(t => (JsonNode)t.Name).ToArray());
        }

        if (project == null)
            info["hint"] = "No C# project yet. create_code_project adds one with Unreal-style starter classes.";

        return McpToolResult.Json(info);
    }

    [McpTool("create_code_project",
        "Add a C# project to the open SexyBiscuit project: <Name>.csproj at the project root, Source/ with starter classes " +
        "(a GameMode, PlayerController and Character, a Spinner component, an example [McpTool] class), a per-machine " +
        "SexyBiscuit.props pointing at this engine, and .gitignore. Existing files are never overwritten unless overwrite " +
        "is true. By default it then builds, hot-loads the assembly, and swaps a plain GameMode in the scene for the " +
        "project's own.",
        MainThread = false, Label = "Create the C# project")]
    public async Task<McpToolResult> CreateCodeProject(
        [McpParam("Overwrite generated files that already exist")] bool overwrite = false,
        [McpParam("Build and hot-load after creating")] bool build = true,
        [McpParam("Replace a plain GameMode actor in the scene with the project's GameMode")] bool swapGameMode = true,
        CancellationToken cancellation = default)
    {
        var generated = await _mcp.Dispatcher.InvokeAsync(() => _code.CreateProject(overwrite), cancellation);

        var result = new JsonObject
        {
            ["csproj"]  = generated.CsprojPath,
            ["created"] = new JsonArray(generated.Created.Select(c => (JsonNode)c.Replace('\\', '/')).ToArray()),
            ["skipped"] = new JsonArray(generated.Skipped.Select(c => (JsonNode)c.Replace('\\', '/')).ToArray()),
        };

        if (!build)
            return McpToolResult.Json(result, $"Created {generated.Created.Count} file(s); call build_project when ready.");

        var reload = await _code.ReloadAsync(build: true, cancellation: cancellation, reason: "create_code_project");
        result["build"]  = _code.LastBuild == null ? null : JobView(_code.LastBuild, includeDiagnostics: true);
        result["reload"] = ReloadView(reload);

        if (swapGameMode && reload.Reloaded)
            result["gameModeSwapped"] = await _mcp.Dispatcher.InvokeAsync(SwapGameMode, cancellation);

        return McpToolResult.Json(result, $"C# project ready: {generated.Created.Count} file(s) created, assembly generation {reload.Generation}.");
    }

    // Replace a stock GameMode with the project's subclass so Play uses the project's classes at once.
    private JsonNode? SwapGameMode()
    {
        var scene = EditorApp.Instance.Engine?.SceneManager.ActiveScene;
        var types = _code.Types;
        if (scene == null || types == null) return null;

        var projectMode = types.ActorTypes.FirstOrDefault(t => typeof(GameMode).IsAssignableFrom(t));
        if (projectMode == null) return null;

        var stock = scene.Layers.SelectMany(l => l.Actors).FirstOrDefault(a => a.GetType() == typeof(GameMode));
        if (stock == null) return null;

        var replacement = (Actor)Activator.CreateInstance(projectMode)!;
        replacement.Name = stock.Name;
        replacement.Tag  = stock.Tag;
        string layer = stock.Layer_?.Name ?? "default";

        stock.Destroy();
        scene.AddActor(replacement, layer);
        scene.FlushPendingActors();
        EditorState.SceneDirty = true;

        return new JsonObject { ["from"] = "GameMode", ["to"] = projectMode.Name, ["actor"] = replacement.Name };
    }

    // -------------------------------------------------------------------------
    // Building
    // -------------------------------------------------------------------------

    [McpTool("build_project",
        "Compile the project's C# code with dotnet build and return structured diagnostics {file, line, column, code, " +
        "severity, message}. Waits up to wait_seconds (default 40); if the build is still running you get status " +
        "'running' and a build_id to poll with get_build_status. Building alone does not change the editor — call " +
        "reload_game_code (which builds for you) to make the new code live. The game compiles against the engine build " +
        "this editor runs; after editing engine source call rebuild_engine_and_restart instead.",
        MainThread = false, Label = "Build the project")]
    public async Task<McpToolResult> BuildProject(
        [McpParam("Seconds to wait before returning a build_id to poll")] int waitSeconds = 40,
        CancellationToken cancellation = default)
    {
        var job = _code.BuildGame(cancellation);
        await WaitFor(job, waitSeconds, cancellation);

        var view = JobView(job, includeDiagnostics: true);
        var result = McpToolResult.Json(view, Headline(job));
        if (job.State is BuildJobState.Failed or BuildJobState.TimedOut) result.IsError = true;
        return result;
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [McpTool("run_tests",
        "Run a test suite and return the totals and the failing names, not the log. project: 'engine' (the engine's " +
        "xunit suite), 'templates' (only the template smoke tests, which run every template's scripts on the C# engine), " +
        "'html5' (npm test in html5/: the JavaScript engine and the tools) or 'lint' (npm run lint). filter narrows " +
        "engine tests by name (FullyQualifiedName~filter) or html5 tests by pattern. Blocks until the run finishes or " +
        "waitSeconds pass; the first run after a change includes a build.",
        MainThread = false, Label = "Run tests")]
    public async Task<McpToolResult> RunTests(
        [McpParam("engine, templates, html5 or lint")] string project = "engine",
        [McpParam("Name filter")] string? filter = null,
        [McpParam("Seconds to wait before giving up")] int waitSeconds = 600,
        CancellationToken cancellation = default)
    {
        var repo = _code.Repo
            ?? throw new McpToolException("The engine source was not found.", "Set the engine repository in the assistant settings or SEXYBISCUIT_REPO.");
        var timeout  = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 10, 3600));
        var started  = DateTime.UtcNow;
        var progress = new Progress<string>(line => { if (IsWorthLogging(line)) ConsoleLog.Add("[test] " + line, LogLevel.Info); });
        string kind  = project.Trim().ToLowerInvariant();

        TestSummary summary;
        string      command;
        int         exitCode;
        bool        timedOut;

        switch (kind)
        {
            case "engine":
            case "templates":
            {
                var dotnet = DotnetLocator.Find() ?? throw new McpToolException("The .NET SDK was not found.", "Install the .NET 8 SDK.");
                var args = new List<string> { "test", repo.TestsCsproj, "--nologo" };
                string? effective = kind == "templates" ? "TemplateTests" : filter;
                if (!string.IsNullOrWhiteSpace(effective)) { args.Add("--filter"); args.Add($"FullyQualifiedName~{effective.Trim()}"); }
                command = "dotnet " + string.Join(' ', args);

                var run = await new DotnetBuildRunner(dotnet).RunDotnetAsync(args, repo.Root, timeout, progress, cancellation);
                summary  = TestResultParsers.ParseDotnet(run.Lines);
                exitCode = run.ExitCode;
                timedOut = run.TimedOut;
                break;
            }
            case "html5":
            case "lint":
            {
                string html5 = Path.Combine(repo.Root, "html5");
                ProcessRun run;
                if (kind == "html5" && !string.IsNullOrWhiteSpace(filter))
                {
                    string node = NodeLocator.FindNode() ?? throw new McpToolException("node was not found.", "Install node 22 or newer.");
                    var args = new List<string> { "--test", "--test-name-pattern", filter.Trim(), "tests/" };
                    command = "node " + string.Join(' ', args);
                    run = await ProcessRunner.RunAsync(node, args, html5, timeout, progress, null, cancellation);
                }
                else
                {
                    string npm = NodeLocator.FindNpm() ?? throw new McpToolException("npm was not found.", "Install node 22 or newer.");
                    var args = kind == "lint" ? new List<string> { "run", "lint" } : new List<string> { "test" };
                    command = "npm " + string.Join(' ', args);
                    run = await ProcessRunner.RunAsync(npm, args, html5, timeout, progress, null, cancellation);
                }

                summary  = kind == "lint" ? LintSummary(run) : TestResultParsers.ParseNode(run.Lines);
                exitCode = run.ExitCode;
                timedOut = run.TimedOut;
                break;
            }
            default:
                throw new McpToolException($"Unknown project '{project}'.", "Use engine, templates, html5 or lint.");
        }

        var view = new JsonObject
        {
            ["project"]  = kind,
            ["command"]  = command,
            ["passed"]   = summary.Passed,
            ["failed"]   = summary.Failed,
            ["skipped"]  = summary.Skipped,
            ["total"]    = summary.Total,
            ["duration"] = summary.Duration ?? $"{(DateTime.UtcNow - started).TotalSeconds:0} s",
            ["exitCode"] = exitCode,
            ["ok"]       = summary.Success && exitCode == 0,
        };
        if (timedOut) view["timedOut"] = true;
        if (summary.Failing.Count > 0)
        {
            view["failing"] = new JsonArray(summary.Failing.Take(20).Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["message"] = f.Message }).ToArray());
            if (summary.Failing.Count > 20) view["failingOmitted"] = summary.Failing.Count - 20;
        }
        if (summary.BuildErrors.Count > 0)
            view["buildErrors"] = new JsonArray(summary.BuildErrors.Take(10).Select(e => (JsonNode)e).ToArray());

        string headline = summary.Incomplete
            ? timedOut ? $"{kind}: timed out after {timeout.TotalSeconds:0} s"
                       : $"{kind}: no test summary (exit {exitCode}){(summary.BuildErrors.Count > 0 ? ", the build failed" : "")}"
            : $"{kind}: {summary.Passed} passed, {summary.Failed} failed" +
              (summary.Skipped > 0 ? $", {summary.Skipped} skipped" : "") + $" of {summary.Total}" +
              (summary.Duration != null ? $" in {summary.Duration}" : "");

        ConsoleLog.Add("[test] " + headline, summary.Success ? LogLevel.Info : LogLevel.Warning);
        var result = McpToolResult.Json(view, headline);
        if (summary.Incomplete) result.IsError = true;
        return result;
    }

    /// <summary>The lint run as a summary: one "test" per module checked, one failure per reported problem.</summary>
    private static TestSummary LintSummary(ProcessRun run)
    {
        int modules = 0;
        var problems = new List<FailingTest>();
        foreach (var line in run.Lines)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d+) modules? checked");
            if (m.Success) { modules = int.Parse(m.Groups[1].Value); continue; }
            if (line.Contains("No problems found", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("problem", StringComparison.OrdinalIgnoreCase))
                problems.Add(new FailingTest(line.Trim(), null));
        }
        if (run.ExitCode != 0 && problems.Count == 0)
            problems.Add(new FailingTest($"lint exited with code {run.ExitCode}", run.Lines.LastOrDefault(l => l.Trim().Length > 0)?.Trim()));
        int total = Math.Max(modules, problems.Count);
        return new TestSummary(total - problems.Count, problems.Count, 0, total, null, problems, Array.Empty<string>());
    }

    private static bool IsWorthLogging(string line)
        => line.StartsWith("  Failed ", StringComparison.Ordinal)
        || line.StartsWith("Passed!", StringComparison.Ordinal) || line.StartsWith("Failed!", StringComparison.Ordinal)
        || line.StartsWith("not ok", StringComparison.Ordinal) || line.TrimStart().StartsWith("✖", StringComparison.Ordinal)
        || line.Contains(": error ", StringComparison.Ordinal);

    [McpTool("get_build_status",
        "Status and diagnostics of a build started by build_project, reload_game_code, create_code_project, run_standalone " +
        "or rebuild_engine_and_restart (the latest when build_id is omitted). For an engine rebuild, state 'restarting' " +
        "means the editor is about to restart — stop calling tools, wait 15-30 seconds, then call get_context.",
        MainThread = false)]
    public McpToolResult GetBuildStatus([McpParam("A build id from an earlier result")] string? buildId = null)
    {
        var job = _code.FindJob(buildId);
        if (job == null)
        {
            return McpToolResult.Json(new JsonObject
            {
                ["state"] = "unknown",
                ["note"]  = buildId == null ? "No build has run in this editor session." : "No such build; build ids do not survive an editor restart.",
            });
        }

        return McpToolResult.Json(JobView(job, includeDiagnostics: true), Headline(job));
    }

    [McpTool("cancel_build", "Cancel a running build.", MainThread = false)]
    public McpToolResult CancelBuild([McpParam("The build id")] string buildId)
    {
        var job = _code.FindJob(buildId) ?? throw new McpToolException($"No build '{buildId}'.");
        bool cancelled = _code.CancelJob(job);
        return McpToolResult.Json(new JsonObject { ["build_id"] = job.Id, ["cancelled"] = cancelled, ["state"] = job.State.ToString().ToLowerInvariant() });
    }

    [McpTool("reload_game_code",
        "Build the C# project (unless build=false) and hot-reload the assembly into the running editor: the scene is " +
        "serialised, the old assembly unloaded, the new one loaded and the scene restored with unsaved edits intact. Play " +
        "mode is stopped first. Reports which actor and component classes and which game_ tools appeared or disappeared. " +
        "Refuses when the game was compiled against a different engine build than this editor runs — call " +
        "rebuild_engine_and_restart — unless allow_engine_mismatch is true.",
        MainThread = false, Label = "Reload game code")]
    public async Task<McpToolResult> ReloadGameCode(
        [McpParam("Build first")] bool build = true,
        [McpParam("Stop play mode if it is running")] bool stopPlayMode = true,
        [McpParam("Load even if the engine build differs")] bool allowEngineMismatch = false,
        [McpParam("Seconds to wait before returning a build_id to poll")] int waitSeconds = 40,
        CancellationToken cancellation = default)
    {
        var reloadTask = _code.ReloadAsync(build, stopPlayMode, allowEngineMismatch, cancellation: cancellation, reason: "reload_game_code");
        var finished   = await Task.WhenAny(reloadTask, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 1, 600)), cancellation));

        if (finished != reloadTask)
        {
            return McpToolResult.Json(new JsonObject
            {
                ["status"]   = "running",
                ["build_id"] = _code.LastBuild?.Id,
                ["hint"]     = "Still building. Poll get_build_status with build_id; its 'reload' field appears when the swap is done.",
            });
        }

        var reload = await reloadTask;
        var view = ReloadView(reload);
        if (_code.LastBuild != null && build) view["build"] = JobView(_code.LastBuild, includeDiagnostics: true);

        var result = McpToolResult.Json(view, reload.Reloaded
            ? $"Reloaded generation {reload.Generation}: +{reload.Appeared.Count} type(s), -{reload.Disappeared.Count}, {reload.ToolsAdded.Count} tool(s)."
            : "Nothing was reloaded.");
        foreach (var warning in reload.Warnings) result.WithWarning(warning);
        return result;
    }

    // -------------------------------------------------------------------------
    // Types and files
    // -------------------------------------------------------------------------

    [McpTool("list_actor_classes",
        "Actor classes you can place or name in spawn_actor's class: the engine's gameplay classes (Actor, GameMode, " +
        "Character, PlayerController…) and the project's own, with source 'engine' or 'project', base class and doc summary.")]
    public McpToolResult ListActorClasses([McpParam("'engine', 'project' or omit for both")] string? source = null)
    {
        var list = new JsonArray();
        foreach (var type in ReflectionUtil.FindActorTypes().OrderBy(t => ComponentReflection.Source(t)).ThenBy(t => t.Name))
        {
            string src = _code.IsProjectType(type) ? "project" : ComponentReflection.Source(type);
            if (source != null && !string.Equals(src, source, StringComparison.OrdinalIgnoreCase)) continue;

            list.Add(new JsonObject
            {
                ["name"]      = type.Name,
                ["fullName"]  = type.FullName,
                ["source"]    = src,
                ["baseClass"] = type.BaseType?.Name,
                ["summary"]   = XmlDocs.Summary(type),
                ["placeable"] = ReflectionUtil.IsPlaceable(type),
            });
        }

        return McpToolResult.Json(list, $"{list.Count} actor class(es).");
    }

    [McpTool("create_class",
        "Generate a starter C# file for a component, actor, gamemode, playercontroller, character or tool (a static class " +
        "with an [McpTool] method) under Source/<kind folder>/<Name>.cs in the project's namespace. Returns the path; " +
        "edit it with your file tools, then call reload_game_code. Never overwrites an existing file. Creates the C# " +
        "project first when the project has none.",
        MainThread = false, Label = "Create class {name}")]
    public async Task<McpToolResult> CreateClass(
        [McpParam("component, actor, gamemode, playercontroller, character or tool")] string kind,
        [McpParam("Class name (PascalCase identifier)")] string name,
        [McpParam("Namespace; defaults to the project's")] string? @namespace = null,
        [McpParam("Folder under Source/; defaults by kind (Components, Actors, Gameplay, Tools)")] string? folder = null,
        CancellationToken cancellation = default)
    {
        if (!Enum.TryParse<ClassKind>(kind.Replace("_", ""), ignoreCase: true, out var classKind))
            throw new McpToolException($"Unknown kind '{kind}'.", "Use component, actor, gamemode, playercontroller, character or tool.");

        if (!CodeProjectGenerator.IsIdentifier(name))
            throw new McpToolException($"'{name}' is not a valid C# identifier.", "Use PascalCase letters and digits, e.g. EnemySpawner.");

        bool createdProject = false;
        if (_code.Project == null)
        {
            await _mcp.Dispatcher.InvokeAsync(() => _code.CreateProject(), cancellation);
            createdProject = true;
        }

        var project = _code.Project!;
        string ns   = string.IsNullOrWhiteSpace(@namespace) ? project.RootNamespace : @namespace;
        string dir  = Path.Combine(project.SourceDirectory, string.IsNullOrWhiteSpace(folder) ? CodeProjectGenerator.FolderFor(classKind) : folder);
        string path = Path.Combine(dir, name + ".cs");

        var result = new JsonObject
        {
            ["path"]          = path,
            ["className"]     = name,
            ["kind"]          = classKind.ToString().ToLowerInvariant(),
            ["createdProject"] = createdProject,
        };

        if (File.Exists(path))
        {
            result["exists"] = true;
            return McpToolResult.Json(result, $"{path} already exists; edit it instead.");
        }

        Directory.CreateDirectory(dir);
        File.WriteAllText(path, CodeProjectGenerator.RenderClass(classKind, name, ns));
        result["exists"] = false;
        result["next"]   = "Edit the file, then call reload_game_code.";
        return McpToolResult.Json(result, $"Created {path}.");
    }

    // -------------------------------------------------------------------------
    // Standalone
    // -------------------------------------------------------------------------

    [McpTool("run_standalone",
        "Build the project (a full build, engine included) and launch the game as its own process with the project root " +
        "as working directory, using ProjectSettings.json and its StartScene. Its output streams into the Output Log " +
        "tagged [Game]. A previous instance is stopped first.",
        MainThread = false, Label = "Run the game standalone")]
    public async Task<McpToolResult> RunStandalone(
        [McpParam("Seconds to wait for the build before returning a build_id")] int waitSeconds = 120,
        CancellationToken cancellation = default)
    {
        var project = _code.Project ?? throw new McpToolException("This project has no C# project.", "Call create_code_project first.");
        var dotnet  = DotnetLocator.Find() ?? throw new McpToolException("The .NET SDK was not found.");

        var job = _code.BuildGame(cancellation);
        await WaitFor(job, waitSeconds, cancellation);

        if (!job.IsFinished)
            return McpToolResult.Json(JobView(job, includeDiagnostics: false), "Still building; poll get_build_status, then call run_standalone again.");

        if (job.State != BuildJobState.Succeeded)
        {
            var failed = McpToolResult.Json(JobView(job, includeDiagnostics: true), Headline(job));
            failed.IsError = true;
            return failed;
        }

        string dll = job.Result?.OutputAssemblyPath ?? project.OutputAssemblyPath(_code.Configuration);
        int pid = _code.Standalone.Start(dotnet.Path, dll, project.Root);

        return McpToolResult.Json(new JsonObject
        {
            ["pid"]      = pid,
            ["command"]  = $"{dotnet.Path} {dll}",
            ["cwd"]      = project.Root,
            ["build_id"] = job.Id,
        }, $"Started the game (pid {pid}). Its output appears in read_console as [Game].");
    }

    [McpTool("stop_standalone", "Stop the game process started by run_standalone.", MainThread = false)]
    public McpToolResult StopStandalone()
    {
        bool stopped = _code.Standalone.Stop();
        return McpToolResult.Json(new JsonObject { ["stopped"] = stopped, ["exitCode"] = _code.Standalone.LastExitCode });
    }

    // -------------------------------------------------------------------------
    // Engine
    // -------------------------------------------------------------------------

    [McpTool("get_engine_repo",
        "Where the engine source is (repository root, engine/editor/test projects, solution), whether it is a git " +
        "checkout and on which branch, the running editor's engine build id, and the exact dotnet commands to build the " +
        "engine, the editor and the tests. Read this before editing engine code.",
        MainThread = false)]
    public McpToolResult GetEngineRepo()
    {
        var repo = _code.Repo;
        if (repo == null)
        {
            return McpToolResult.Json(new JsonObject
            {
                ["found"] = false,
                ["hint"]  = "The editor is not running from a source checkout. Set SEXYBISCUIT_REPO to the repository root to enable engine rebuilds.",
            });
        }

        string? branch = null;
        bool? dirty = null;
        if (repo.IsGitCheckout && GitHelper.IsGitInstalled())
        {
            try
            {
                branch = GitHelper.GetCurrentBranch(repo.Root);
                dirty  = GitHelper.GetStatus(repo.Root).Count > 0;
            }
            catch (Exception)
            {
            }
        }

        return McpToolResult.Json(new JsonObject
        {
            ["found"]               = true,
            ["root"]                = repo.Root,
            ["engineCsproj"]        = repo.EngineCsproj,
            ["editorCsproj"]        = repo.EditorCsproj,
            ["testsCsproj"]         = repo.TestsCsproj,
            ["solution"]            = repo.Solution,
            ["isGitCheckout"]       = repo.IsGitCheckout,
            ["branch"]              = branch,
            ["dirty"]               = dirty,
            ["editorConfiguration"] = _code.Configuration,
            ["editorBinDir"]        = AppContext.BaseDirectory,
            ["runningEngineMvid"]   = AssemblyIdentity.RunningEngineMvid.ToString(),
            ["buildCommands"] = new JsonObject
            {
                ["engine"] = $"dotnet build SexyBiscuit.Engine/SexyBiscuit.Engine.csproj -c {_code.Configuration} -warnaserror",
                ["editor"] = $"dotnet build SexyBiscuit.Editor/SexyBiscuit.Editor.csproj -c {_code.Configuration}",
                ["tests"]  = "dotnet test SexyBiscuit.Tests/SexyBiscuit.Tests.csproj",
            },
            ["note"] = "Engine changes only take effect in the editor after rebuild_engine_and_restart; the standalone game picks them up on its next run_standalone.",
        });
    }

    [McpTool("rebuild_engine_and_restart",
        "Rebuild the engine and the editor from source and restart the editor so engine changes take effect. The build " +
        "runs into a staging folder first, so a failure leaves the running editor untouched and returns diagnostics " +
        "without restarting. On success the editor saves the scene, writes a resume file, and restarts a couple of " +
        "seconds after this result is delivered; it reopens the same project and scene, restores the selection, and " +
        "resumes the assistant session. While it restarts, MCP calls fail for 10-30 seconds: stop calling tools, wait, " +
        "then call get_context until it answers, and re-list tools. Never repeat the rebuild.",
        MainThread = false, Label = "Rebuild the engine and restart")]
    public async Task<McpToolResult> RebuildEngineAndRestart(
        [McpParam("Debug, Release or Development; defaults to the running editor's")] string? configuration = null,
        [McpParam("Also build the test project before restarting")] bool runTests = false,
        [McpParam("Seconds to wait before returning a build_id to poll")] int waitSeconds = 40,
        CancellationToken cancellation = default)
    {
        var job = _restart.Begin(configuration, runTests, cancellation);
        await WaitFor(job, waitSeconds, cancellation);

        var view = JobView(job, includeDiagnostics: true);
        if (job.State == BuildJobState.Restarting)
        {
            view["status"]      = "restart_scheduled";
            view["eta_seconds"] = 3;
            view["instruction"] = "Stop calling tools now. The editor restarts in a few seconds and resumes this project and scene; you will be resumed too. Wait 15-30 seconds, then call get_context until it answers.";
            return McpToolResult.Json(view, "Engine and editor rebuilt. Restarting the editor.");
        }

        var result = McpToolResult.Json(view, Headline(job));
        if (job.State is BuildJobState.Failed or BuildJobState.TimedOut) result.IsError = true;
        return result;
    }

    [McpTool("set_auto_reload", "Turn automatic build + hot-reload on file save in Source/ on or off. Off by default; you normally call reload_game_code explicitly.")]
    public McpToolResult SetAutoReload([McpParam("true to enable")] bool enabled)
    {
        _code.AutoReload = enabled;
        return McpToolResult.Json(new JsonObject { ["enabled"] = enabled });
    }

    // -------------------------------------------------------------------------

    private static async Task WaitFor(BuildJob job, int waitSeconds, CancellationToken cancellation)
    {
        var cap = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 1, 600));
        await Task.WhenAny(job.Completion, Task.Delay(cap, cancellation));
    }

    private static string Headline(BuildJob job) => job.State switch
    {
        BuildJobState.Queued     => $"Build {job.Id} queued.",
        BuildJobState.Running    => $"Build {job.Id} still running after {job.Elapsed.TotalSeconds:F0} s; poll get_build_status.",
        BuildJobState.Succeeded  => $"Build {job.Id} succeeded in {job.Result?.Duration.TotalSeconds ?? job.Elapsed.TotalSeconds:F1} s with {job.Result?.WarningCount ?? 0} warning(s).",
        BuildJobState.Failed     => $"Build {job.Id} failed with {job.Result?.ErrorCount ?? 0} error(s).",
        BuildJobState.Cancelled  => $"Build {job.Id} was cancelled.",
        BuildJobState.TimedOut   => $"Build {job.Id} timed out.",
        BuildJobState.Restarting => $"Build {job.Id} succeeded; the editor is restarting.",
        _                        => $"Build {job.Id}: {job.State}.",
    };

    internal static JsonObject JobView(BuildJob job, bool includeDiagnostics)
    {
        var view = new JsonObject
        {
            ["build_id"]      = job.Id,
            ["target"]        = job.Target.ToString().ToLowerInvariant(),
            ["configuration"] = job.Configuration,
            ["status"]        = job.State.ToString().ToLowerInvariant(),
            ["elapsed_s"]     = Math.Round(job.Elapsed.TotalSeconds, 1),
        };

        if (job.Result is { } result)
        {
            view["errorCount"]   = result.ErrorCount;
            view["warningCount"] = result.WarningCount;
            view["outputDll"]    = result.OutputAssemblyPath;

            if (includeDiagnostics)
            {
                // The first errors are the ones that matter; the rest are usually the same one again.
                view["errors"]   = Diagnostics(result.Errors.Take(20));
                view["warnings"] = Diagnostics(result.Warnings.Take(5));
                if (result.ErrorCount > 20)  view["errorsOmitted"]   = result.ErrorCount - 20;
                if (result.WarningCount > 5) view["warningsOmitted"] = result.WarningCount - 5;
            }
        }

        if (job.Error != null) view["error"] = job.Error;
        if (job.Reload != null) view["reload"] = ReloadView(job.Reload);
        if (!job.IsFinished || job.State is BuildJobState.Failed or BuildJobState.TimedOut)
            view["tail"] = new JsonArray(job.Tail().Select(l => (JsonNode)l).ToArray());

        return view;
    }

    private static JsonArray Diagnostics(IEnumerable<BuildDiagnostic> diagnostics)
        => new(diagnostics.Select(d => (JsonNode)new JsonObject
        {
            ["file"]     = d.File,
            ["line"]     = d.Line,
            ["column"]   = d.Column,
            ["code"]     = d.Code,
            ["severity"] = d.Severity.ToString().ToLowerInvariant(),
            ["message"]  = d.Message,
        }).ToArray());

    internal static JsonObject ReloadView(ReloadResult reload) => new()
    {
        ["reloaded"]        = reload.Reloaded,
        ["generation"]      = reload.Generation,
        ["appeared"]        = new JsonArray(reload.Appeared.Select(a => (JsonNode)a).ToArray()),
        ["disappeared"]     = new JsonArray(reload.Disappeared.Select(a => (JsonNode)a).ToArray()),
        ["toolsAdded"]      = new JsonArray(reload.ToolsAdded.Select(a => (JsonNode)a).ToArray()),
        ["toolsRemoved"]    = new JsonArray(reload.ToolsRemoved.Select(a => (JsonNode)a).ToArray()),
        ["unloadCollected"] = reload.UnloadCollected,
        ["stoppedPlayMode"] = reload.StoppedPlayMode,
        ["backupPath"]      = reload.BackupPath,
        ["warnings"]        = new JsonArray(reload.Warnings.Select(a => (JsonNode)a).ToArray()),
    };
}
