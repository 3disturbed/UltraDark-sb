using System.Diagnostics;
using SexyBiscuit.Editor.Assistant;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Mcp;
using SexyBiscuit.Engine.Scene;

namespace SexyBiscuit.Editor.GameCode;

/// <summary>
/// Rebuilds the engine and the editor from source and restarts the editor, so an engine change
/// made during a session takes effect without the user leaving their seat.
/// </summary>
/// <remarks>
/// The build runs into a staging folder first: the running editor's binaries are memory-mapped
/// and must not be overwritten, and a failed build must leave the editor exactly as it was. A
/// detached script then waits for this process to exit, rebuilds in place (already proven to
/// compile) and starts the editor again with <c>--resume</c>.
/// </remarks>
public sealed class EditorRestart
{
    private readonly McpHost      _mcp;
    private readonly GameCodeHost _code;
    private bool _restartScheduled;

    public EditorRestart(McpHost mcp, GameCodeHost code)
    {
        _mcp  = mcp;
        _code = code;
    }

    /// <summary>Raised on the main thread just before the editor shuts down to restart; the Assistant persists its session here.</summary>
    public event Func<RelaunchState, Task>? BeforeRestartAsync;

    public bool IsRestartScheduled => _restartScheduled;

    /// <summary>Starts the staged rebuild. The job reaches <see cref="BuildJobState.Restarting"/> only on success.</summary>
    public BuildJob Begin(string? configuration, bool runTests, CancellationToken cancellation = default)
    {
        if (_restartScheduled) throw new McpToolException("A restart is already scheduled.");

        var repo   = _code.Repo   ?? throw new McpToolException("The editor is not running from a source checkout, so it cannot rebuild itself.",
                         "Set SEXYBISCUIT_REPO to the repository root, or run the editor with dotnet run from the checkout.");
        var dotnet = DotnetLocator.Find() ?? throw new McpToolException("The .NET SDK was not found.", "Install the .NET SDK or set DOTNET_ROOT.");

        string config  = string.IsNullOrWhiteSpace(configuration) ? _code.Configuration : configuration;
        string staging = Path.Combine(repo.Root, "SexyBiscuit.Editor", "bin", config, "staging");

        var job = new BuildJob { Target = BuildTarget.Editor, Configuration = config, WillRestart = true };
        _code.Track(job);

        job.Completion = Task.Run(async () =>
        {
            job.State = BuildJobState.Running;
            var runner   = new DotnetBuildRunner(dotnet);
            var progress = new Progress<string>(line => { job.AddLine(line); ConsoleLog.Add("[build] " + line, LogLevel.Info); });

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, job.Cancellation.Token);

                var result = await runner.BuildAsync(new BuildRequest(repo.EditorCsproj, config)
                {
                    OutputDirectory  = staging,
                    WorkingDirectory = repo.Root,
                    Timeout          = TimeSpan.FromMinutes(20),
                }, progress, linked.Token).ConfigureAwait(false);

                job.Result = result;
                if (!result.Success)
                {
                    job.State = result.TimedOut ? BuildJobState.TimedOut : result.Cancelled ? BuildJobState.Cancelled : BuildJobState.Failed;
                    ConsoleLog.Add($"[build] Editor rebuild failed: {result.ErrorCount} error(s). The running editor is untouched.", LogLevel.Error);
                    return;
                }

                if (runTests)
                {
                    var tests = await runner.BuildAsync(new BuildRequest(repo.TestsCsproj, config) { WorkingDirectory = repo.Root }, progress, linked.Token).ConfigureAwait(false);
                    if (!tests.Success)
                    {
                        job.Result = tests;
                        job.State  = BuildJobState.Failed;
                        ConsoleLog.Add("[build] The test project does not build; not restarting.", LogLevel.Error);
                        return;
                    }
                }

                job.State = BuildJobState.Restarting;
                ConsoleLog.Add($"[build] Engine and editor rebuilt in {result.Duration.TotalSeconds:F1} s. Restarting shortly…", LogLevel.Info);
                ScheduleRestart(job, repo, dotnet, config, staging);
            }
            catch (Exception ex)
            {
                job.Error = ex.Message;
                job.State = BuildJobState.Failed;
                ConsoleLog.Add($"[build] {ex.Message}", LogLevel.Error);
            }
            finally
            {
                job.FinishedUtc = DateTime.UtcNow;
            }
        });

        return job;
    }

    // The tool result that announced the restart has to leave the building first: wait until the
    // MCP server has been quiet for a second (or twenty seconds, whichever comes first).
    private void ScheduleRestart(BuildJob job, EngineRepo repo, DotnetInfo dotnet, string configuration, string staging)
    {
        _restartScheduled = true;

        _ = Task.Run(async () =>
        {
            var started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(20))
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (DateTime.UtcNow - started >= TimeSpan.FromSeconds(1.5) && DateTime.UtcNow - _mcp.Server.LastResponseUtc >= TimeSpan.FromSeconds(1))
                    break;
            }

            try
            {
                await _mcp.Dispatcher.InvokeAsync(() => PerformRestartAsync(job, repo, dotnet, configuration, staging)).Unwrap().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _restartScheduled = false;
                ConsoleLog.Add($"Restart failed: {ex.Message}", LogLevel.Error);
            }
        });
    }

    private async Task PerformRestartAsync(BuildJob job, EngineRepo repo, DotnetInfo dotnet, string configuration, string staging)
    {
        var app = EditorApp.Instance;

        // 1. Save the scene — to its own file, or an autosave when it has none.
        string? scenePath = null;
        bool autosaved = false;
        var scene = app.Engine?.SceneManager.ActiveScene;
        if (scene != null)
        {
            if (EditorState.IsPlaying) app.ExitPlayMode();
            scene = app.Engine!.SceneManager.ActiveScene!;
            scene.FlushPendingActors();

            try
            {
                if (!string.IsNullOrEmpty(EditorState.CurrentScenePath) && EditorState.CurrentProject != null)
                {
                    string full = Path.IsPathRooted(EditorState.CurrentScenePath) ? EditorState.CurrentScenePath : Path.Combine(EditorState.ProjectPath, EditorState.CurrentScenePath);
                    SceneSerializer.SaveToFile(scene, full);
                    scenePath = full;
                    EditorState.SceneDirty = false;
                }
                else
                {
                    string dir = Path.Combine(EditorState.CurrentProject != null ? EditorState.ProjectPath : Path.GetTempPath(), ".sexybiscuit", "autosave");
                    Directory.CreateDirectory(dir);
                    scenePath = Path.Combine(dir, $"{scene.Name}.scene");
                    SceneSerializer.SaveToFile(scene, scenePath);
                    autosaved = true;
                }
            }
            catch (Exception ex)
            {
                ConsoleLog.Add($"Could not save the scene before restarting: {ex.Message}", LogLevel.Warning);
            }
        }

        // 2. The resume file.
        var state = new RelaunchState
        {
            Reason            = "engine-rebuild",
            EditorPid         = Environment.ProcessId,
            WorkingDirectory  = Environment.CurrentDirectory,
            ProjectFile       = EditorState.CurrentProjectFile,
            ScenePath         = scenePath,
            SceneWasAutosaved = autosaved,
            SelectedActorName = EditorState.SelectedActor?.Name,
            SelectedLayer     = EditorState.SelectedLayer?.Name,
            McpPort           = _mcp.Url?.Port ?? _mcp.Settings.McpPort,
            BuildId           = job.Id,
            BuildSummary      = job.Result == null ? null : $"{job.Result.WarningCount} warning(s), {job.Result.Duration.TotalSeconds:F1} s",
            EditorMvid        = AssemblyIdentity.RunningEngineMvid.ToString(),
        };

        if (BeforeRestartAsync != null)
        {
            foreach (var handler in BeforeRestartAsync.GetInvocationList().Cast<Func<RelaunchState, Task>>())
            {
                try { await handler(state).ConfigureAwait(true); }
                catch (Exception ex) { ConsoleLog.Add($"A restart handler failed: {ex.Message}", LogLevel.Warning); }
            }
        }

        string resumeFile = RelaunchStateFile.DefaultPath;
        RelaunchStateFile.Save(state, resumeFile);

        // 3. The relaunch script, detached so it outlives us.
        string appData = Path.GetDirectoryName(resumeFile)!;
        string editorDll = typeof(Program).Assembly.Location;
        var plan = new RelaunchPlan(
            WaitForPid:       Environment.ProcessId,
            DotnetPath:       dotnet.Path,
            RepoRoot:         repo.Root,
            EditorCsproj:     repo.EditorCsproj,
            Configuration:    configuration,
            EditorDll:        editorDll,
            StagingDll:       Path.Combine(staging, Path.GetFileName(editorDll)),
            WorkingDirectory: Environment.CurrentDirectory,
            ResumeFile:       resumeFile,
            LogFile:          Path.Combine(appData, "relaunch.log"),
            ExtraArguments:   app.Options.RelaunchArguments());

        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(appData, "relaunch.ps1");
            File.WriteAllText(script, RelaunchScript.RenderPowerShell(plan));
            psi = new ProcessStartInfo("powershell") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", script })
                psi.ArgumentList.Add(a);
        }
        else
        {
            string script = Path.Combine(appData, "relaunch.sh");
            File.WriteAllText(script, RelaunchScript.RenderSh(plan));
            psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(script);
        }

        Process.Start(psi);
        ConsoleLog.Add("Restarting the editor…", LogLevel.Info);

        // 4. Leave. OnExiting stops the MCP server and the assistant.
        app.Exit();
    }
}
