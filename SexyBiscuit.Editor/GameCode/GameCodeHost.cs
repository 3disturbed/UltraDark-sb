using System.Diagnostics;
using SexyBiscuit.Editor.Assistant;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Mcp;
using SexyBiscuit.Engine.Scene;
using Scene = SexyBiscuit.Engine.Core.Scene;

namespace SexyBiscuit.Editor.GameCode;

public enum BuildTarget
{
    Game,
    Editor,
}

public enum BuildJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
    Restarting,
}

/// <summary>One build, from queue to result, as the panel and the tools see it.</summary>
public sealed class BuildJob
{
    private readonly object _lock = new();
    private readonly List<string> _tail = new();

    public string        Id          { get; } = Guid.NewGuid().ToString("N")[..8];
    public BuildTarget   Target      { get; init; }
    public string        Configuration { get; init; } = "Debug";
    public BuildJobState State       { get; internal set; } = BuildJobState.Queued;
    public DateTime      StartedUtc  { get; internal set; } = DateTime.UtcNow;
    public DateTime?     FinishedUtc { get; internal set; }
    public BuildResult?  Result      { get; internal set; }
    public ReloadResult? Reload      { get; internal set; }
    public string?       Error       { get; internal set; }
    public bool          WillRestart { get; internal set; }

    internal CancellationTokenSource Cancellation { get; } = new();
    internal Task Completion { get; set; } = Task.CompletedTask;

    public TimeSpan Elapsed => (FinishedUtc ?? DateTime.UtcNow) - StartedUtc;

    public bool IsFinished => State is BuildJobState.Succeeded or BuildJobState.Failed or BuildJobState.Cancelled or BuildJobState.TimedOut or BuildJobState.Restarting;

    internal void AddLine(string line)
    {
        lock (_lock)
        {
            _tail.Add(line);
            if (_tail.Count > 200) _tail.RemoveAt(0);
        }
    }

    public IReadOnlyList<string> Tail(int count = 20)
    {
        lock (_lock) return _tail.Skip(Math.Max(0, _tail.Count - count)).ToArray();
    }
}

/// <summary>What a hot reload changed.</summary>
public sealed record ReloadResult(
    bool                  Reloaded,
    int                   Generation,
    IReadOnlyList<string> Appeared,
    IReadOnlyList<string> Disappeared,
    IReadOnlyList<string> ToolsAdded,
    IReadOnlyList<string> ToolsRemoved,
    bool                  UnloadCollected,
    IReadOnlyList<string> Warnings,
    bool                  StoppedPlayMode,
    string?               BackupPath);

/// <summary>
/// The editor's C# side: finds the project's csproj, builds it with dotnet, hot-reloads the
/// assembly with a scene round-trip, and keeps every type cache in the editor honest.
/// </summary>
public sealed class GameCodeHost : IDisposable
{
    public static GameCodeHost? Instance { get; private set; }

    /// <summary>
    /// Raised on the main thread before a generation is unloaded and after the next one is
    /// loaded. Panels that cache <see cref="Type"/> objects or <c>Func&lt;Actor&gt;</c> builders
    /// drop them here.
    /// </summary>
    public static event Action? TypesChanged;

    private readonly McpHost           _mcp;
    private readonly AssistantSettings _settings;
    private readonly List<BuildJob>    _jobs = new();
    private readonly List<McpRegistration> _toolRegistrations = new();
    private readonly Dictionary<Component, int> _throwCounts = new(ReferenceEqualityComparer.Instance);
    private readonly object _jobsLock = new();

    private FileSystemWatcher? _watcher;
    private DateTime?          _changePendingUtc;
    private Task?              _pendingReload;

    public GameCodeHost(McpHost mcp, AssistantSettings settings)
    {
        Instance  = this;
        _mcp      = mcp;
        _settings = settings;

        // The editor survives game code that throws; a shipped game does not, by design.
        ExceptionIsolation.Handler = HandleGameException;

        mcp.ProjectInfoContributors.Add(ContributeProjectInfo);
    }

    public CodeProject?        Project { get; private set; }
    public GameAssemblyLoader  Loader  { get; } = new();
    public GameTypeSnapshot?   Types   => _swapping ? null : Loader.Types;
    private bool               _swapping;
    public bool                IsReloading { get; private set; }
    public bool                AutoReload  { get; set; }
    public StandaloneRunner    Standalone  { get; } = new();

    public IReadOnlyList<BuildJob> Jobs
    {
        get { lock (_jobsLock) return _jobs.ToArray(); }
    }

    public BuildJob? LastBuild
    {
        get { lock (_jobsLock) return _jobs.LastOrDefault(j => j.Target == BuildTarget.Game); }
    }

    /// <summary>Debug, Release or Development — whichever the running editor was built as.</summary>
    public string Configuration
    {
        get
        {
            string dir = AppContext.BaseDirectory.Replace('\\', '/');
            if (dir.Contains("/bin/Release/", StringComparison.OrdinalIgnoreCase)) return "Release";
            if (dir.Contains("/bin/Development/", StringComparison.OrdinalIgnoreCase)) return "Development";
            return "Debug";
        }
    }

    public EngineRepo? Repo => EngineRepoLocator.Find(_settings.EngineRepoPath);

    /// <summary>Where the engine is for the generated props: the repo's engine project, and the editor's own binaries as the fallback.</summary>
    public EngineLocation EngineLocation
        => new(Repo?.EngineCsproj, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), AssemblyIdentity.RunningEngineMvid);

    /// <summary>True when the loaded game assembly was compiled against the engine build this editor runs, or nothing is loaded.</summary>
    public bool EngineMatches
        => Types?.EngineMvidCompiledAgainst is not { } mvid || mvid == AssemblyIdentity.RunningEngineMvid;

    public bool IsProjectType(Type type) => Loader.IsCurrentType(type);

    // -------------------------------------------------------------------------
    // Project lifecycle
    // -------------------------------------------------------------------------

    /// <summary>Called when a project opens: find its csproj, refresh the props, load its code.</summary>
    public void OnProjectOpened(string root)
    {
        StopWatching();
        Project = CodeProject.Find(root);

        if (Project == null)
        {
            ConsoleLog.Add("This project has no C# project yet. Use Project > Add C# Project, or let the assistant call create_code_project.", LogLevel.Info);
            if (Loader.Current != null) _ = ReloadAsync(build: false, unloadOnly: true, reason: "project without code opened");
            return;
        }

        RefreshProps();
        StartWatching();

        // Build and load so the scene's project components resolve; the reload round-trips the
        // scene, turning the placeholders the loader just created into real components.
        _ = ReloadAsync(build: true, reason: "project opened");
    }

    /// <summary>Creates the C# project for the open SexyBiscuit project. Existing files are kept.</summary>
    public GenerationResult CreateProject(bool overwrite = false)
    {
        var project = EditorState.CurrentProject ?? throw new McpToolException("No project is open.");
        string root = EditorState.ProjectPath;

        var result = CodeProjectGenerator.Generate(root, project.ProjectName, EngineLocation, overwrite);
        Project = CodeProject.Load(result.CsprojPath);
        StartWatching();
        return result;
    }

    private void RefreshProps()
    {
        if (Project == null) return;
        try
        {
            CodeProjectGenerator.WriteProps(Project.Root, EngineLocation);
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Could not refresh {CodeProjectGenerator.PropsFileName}: {ex.Message}", LogLevel.Warning);
        }
    }

    // -------------------------------------------------------------------------
    // Building
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the game project. A build already running for the same target is joined rather
    /// than duplicated — the assistant often asks twice.
    /// </summary>
    public BuildJob BuildGame(CancellationToken cancellation = default)
    {
        var project = Project ?? throw new McpToolException("This project has no C# project.", "Call create_code_project first.");
        var dotnet  = DotnetLocator.Find() ?? throw new McpToolException("The .NET SDK was not found.",
            "Install the .NET 8 SDK or later (https://dot.net) or set DOTNET_ROOT, then try again.");

        lock (_jobsLock)
        {
            var running = _jobs.LastOrDefault(j => j.Target == BuildTarget.Game && !j.IsFinished);
            if (running != null) return running;
        }

        var job = new BuildJob { Target = BuildTarget.Game, Configuration = Configuration };
        Track(job);

        job.Completion = Task.Run(async () =>
        {
            job.State = BuildJobState.Running;
            var runner = new DotnetBuildRunner(dotnet);
            var progress = new Progress<string>(line => { job.AddLine(line); ConsoleLog.Add("[build] " + line, LogLevel.Info); });

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, job.Cancellation.Token);

                var request = new BuildRequest(project.CsprojPath, Configuration)
                {
                    BuildProjectReferences = false,
                    WorkingDirectory       = project.Root,
                };
                var result = await runner.BuildAsync(request, progress, linked.Token).ConfigureAwait(false);

                // No engine output to compile against yet (a fresh checkout): let MSBuild build it once.
                if (!result.Success && result.Diagnostics.Any(d => d.Code is "CS0006" or "MSB3202" or "MSB4025"))
                {
                    job.AddLine("engine output missing; building project references once");
                    result = await runner.BuildAsync(request with { BuildProjectReferences = true }, progress, linked.Token).ConfigureAwait(false);
                }

                job.Result = result;
                job.State  = result.Success ? BuildJobState.Succeeded : result.TimedOut ? BuildJobState.TimedOut : result.Cancelled ? BuildJobState.Cancelled : BuildJobState.Failed;
                ConsoleLog.Add(result.Success
                        ? $"[build] {project.Name} built in {result.Duration.TotalSeconds:F1} s ({result.WarningCount} warning(s))."
                        : $"[build] {project.Name} failed: {result.ErrorCount} error(s).",
                    result.Success ? LogLevel.Info : LogLevel.Error);
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

    public BuildJob? FindJob(string? id)
    {
        lock (_jobsLock)
            return id == null ? _jobs.LastOrDefault() : _jobs.FirstOrDefault(j => j.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public bool CancelJob(BuildJob job)
    {
        if (job.IsFinished) return false;
        job.Cancellation.Cancel();
        return true;
    }

    internal void Track(BuildJob job)
    {
        lock (_jobsLock)
        {
            _jobs.Add(job);
            while (_jobs.Count > 10 && _jobs[0].IsFinished) _jobs.RemoveAt(0);
        }
    }

    // -------------------------------------------------------------------------
    // Hot reload
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds (unless told not to) and swaps the game assembly under the live scene. The scene
    /// is serialised, the old generation unloaded, the new one loaded and the scene restored —
    /// unsaved edits included. Runs the swap on the main thread through the dispatcher.
    /// </summary>
    public async Task<ReloadResult> ReloadAsync(bool build = true, bool stopPlayMode = true, bool allowEngineMismatch = false,
                                                bool unloadOnly = false, CancellationToken cancellation = default, string reason = "requested")
    {
        ConsoleLog.Add($"Game code reload ({reason})…", LogLevel.Info);

        if (IsReloading)
        {
            var pending = _pendingReload;
            if (pending != null) await pending.ConfigureAwait(false);
            if (Types != null) return new ReloadResult(false, Loader.Generation, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), true, new[] { "a reload was already running; its result stands" }, false, null);
        }

        string? dllPath = null;
        BuildJob? job = null;

        if (!unloadOnly)
        {
            var project = Project ?? throw new McpToolException("This project has no C# project.", "Call create_code_project first.");

            if (build)
            {
                job = BuildGame(cancellation);
                await job.Completion.ConfigureAwait(false);
                if (job.State != BuildJobState.Succeeded)
                    throw new McpToolException($"The build failed with {job.Result?.ErrorCount ?? 0} error(s); nothing was reloaded.",
                        job.Result != null ? string.Join(" | ", job.Result.Errors.Take(5).Select(e => e.ToString())) : job.Error);
            }

            dllPath = job?.Result?.OutputAssemblyPath ?? project.OutputAssemblyPath(Configuration);
            if (!File.Exists(dllPath))
                throw new McpToolException($"Built assembly not found at {dllPath}.", "Run build_project first.");

            if (!allowEngineMismatch)
            {
                string engineDll = Path.Combine(Path.GetDirectoryName(dllPath)!, "SexyBiscuit.Engine.dll");
                var mvid = File.Exists(engineDll) ? AssemblyIdentity.TryReadMvid(engineDll) : null;
                if (mvid.HasValue && mvid.Value != AssemblyIdentity.RunningEngineMvid)
                    throw new McpToolException(
                        "The game was compiled against a different engine build than this editor is running.",
                        "Call rebuild_engine_and_restart to bring the editor up to date, or pass allow_engine_mismatch=true if the engine change cannot affect the game.");
            }
        }

        var tcs = new TaskCompletionSource<ReloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingReload = tcs.Task;
        IsReloading = true;
        try
        {
            var result = await _mcp.Dispatcher.InvokeAsync(() => SwapOnMainThread(dllPath, stopPlayMode), cancellation).ConfigureAwait(false);
            if (job != null) job.Reload = result;
            tcs.TrySetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            throw;
        }
        finally
        {
            IsReloading    = false;
            _pendingReload = null;
        }
    }

    private ReloadResult SwapOnMainThread(string? dllPath, bool stopPlayMode)
    {
        try
        {
            return SwapCore(dllPath, stopPlayMode);
        }
        finally
        {
            _swapping = false;
        }
    }

    private ReloadResult SwapCore(string? dllPath, bool stopPlayMode)
    {
        var app    = EditorApp.Instance;
        var engine = app.Engine ?? throw new McpToolException("The engine is not running.");
        var warnings = new List<string>();
        bool stoppedPlay = false;

        if (EditorState.IsPlaying)
        {
            if (!stopPlayMode) throw new McpToolException("The scene is in play mode.", "Stop play mode first, or pass stop_play_mode=true.");
            app.ExitPlayMode();
            stoppedPlay = true;
        }

        var scene = engine.SceneManager.ActiveScene ?? new Scene("Untitled");
        scene.FlushPendingActors();
        string json         = SceneSerializer.Serialize(scene);
        string? selected    = EditorState.SelectedActor?.Name;
        string? selectedLayer = EditorState.SelectedLayer?.Name;
        string? backup      = WriteBackup(scene.Name, json, warnings);

        var previousTypes = Loader.Types is { } previous ? NamesOf(previous) : new HashSet<string>();
        var previousTools = _toolRegistrations.SelectMany(r => r.ToolNames).ToList();
        _swapping = true;

        // Release every reference to the old generation before unloading it.
        EditorState.SelectActor(null);
        EditorState.SelectedLayer = null;
        engine.SceneManager.AdoptScene(new Scene("(reloading)"));
        engine.Timers.ClearAll();
        engine.Coroutines.StopAll();
        Time.TimeScale = 1f;
        _throwCounts.Clear();

        GameTypeSnapshot? snapshot = null;
        UnloadReport unload;
        var toolsAdded = new List<string>();

        using (_mcp.Registry.SuspendNotifications())
        {
            foreach (var registration in _toolRegistrations) _mcp.Registry.Unregister(registration);
            _toolRegistrations.Clear();

            SceneSerializer.ClearTypeCache();
            RaiseTypesChanged();

            unload = Loader.Unload();
            if (!unload.Collected)
                warnings.Add($"generation {unload.Generation} is still referenced and stays in memory until the editor restarts (a static event or a cached type, usually)");

            if (dllPath != null)
            {
                try
                {
                    snapshot = Loader.Load(dllPath);
                }
                catch (Exception ex)
                {
                    warnings.Add($"could not load {dllPath}: {ex.Message}");
                }

                if (snapshot != null)
                {
                    foreach (var holder in snapshot.ToolHolders)
                    {
                        try
                        {
                            var options = new McpRegistrationOptions { NamePrefix = "game_", Source = "project" };
                            var registration = holder.IsAbstract && holder.IsSealed
                                ? _mcp.Registry.RegisterStatic(holder, options)
                                : holder.GetConstructor(Type.EmptyTypes) != null
                                    ? _mcp.Registry.RegisterInstance(Activator.CreateInstance(holder)!, options)
                                    : null;

                            if (registration == null)
                            {
                                warnings.Add($"{holder.FullName} has [McpTool] methods but no public parameterless constructor; skipped");
                                continue;
                            }

                            _toolRegistrations.Add(registration);
                            toolsAdded.AddRange(registration.ToolNames);
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"tools on {holder.FullName} not registered: {ex.GetBaseException().Message}");
                        }
                    }
                }
            }

            SceneSerializer.ClearTypeCache();

            Scene restored;
            try
            {
                restored = SceneSerializer.Deserialize(json);
            }
            catch (Exception ex)
            {
                warnings.Add($"the scene could not be restored ({ex.Message}); the backup is at {backup}");
                restored = new Scene(scene.Name);
            }

            engine.SceneManager.AdoptScene(restored);
            restored.FlushPendingActors();

            if (selected != null) EditorState.SelectActor(restored.FindByName(selected));
            if (selectedLayer != null) EditorState.SelectedLayer = restored.Layers.FirstOrDefault(l => l.Name == selectedLayer);

            _swapping = false;
            RaiseTypesChanged();
        }

        var newTypes = snapshot == null ? new HashSet<string>() : NamesOf(snapshot);
        var result = new ReloadResult(
            Reloaded:        snapshot != null,
            Generation:      Loader.Generation,
            Appeared:        newTypes.Except(previousTypes).OrderBy(n => n).ToArray(),
            Disappeared:     previousTypes.Except(newTypes).OrderBy(n => n).ToArray(),
            ToolsAdded:      toolsAdded,
            ToolsRemoved:    previousTools.Except(toolsAdded).ToArray(),
            UnloadCollected: unload.Collected,
            Warnings:        warnings,
            StoppedPlayMode: stoppedPlay,
            BackupPath:      backup);

        ConsoleLog.Add(snapshot != null
                ? $"Game code reloaded (generation {snapshot.Generation}): {snapshot.ComponentTypes.Count} component(s), {snapshot.ActorTypes.Count} actor class(es), {toolsAdded.Count} tool(s)."
                : "Game code unloaded.",
            LogLevel.Info);

        return result;
    }

    private static HashSet<string> NamesOf(GameTypeSnapshot snapshot)
        => snapshot.ActorTypes.Concat(snapshot.ComponentTypes).Select(t => t.FullName ?? t.Name).ToHashSet(StringComparer.Ordinal);

    private string? WriteBackup(string sceneName, string json, List<string> warnings)
    {
        if (Project == null) return null;

        try
        {
            string dir = Path.Combine(Project.ScratchDirectory, "reload-backups");
            Directory.CreateDirectory(dir);

            string safe = string.Concat(sceneName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string path = Path.Combine(dir, $"{safe}.{DateTime.Now:yyyyMMdd-HHmmss}.scene");
            File.WriteAllText(path, json);

            foreach (var old in Directory.GetFiles(dir, "*.scene").OrderByDescending(f => f).Skip(5))
                File.Delete(old);

            return path;
        }
        catch (Exception ex)
        {
            warnings.Add("no reload backup written: " + ex.Message);
            return null;
        }
    }

    private static void RaiseTypesChanged()
    {
        try
        {
            TypesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"A TypesChanged handler threw: {ex.Message}", LogLevel.Warning);
        }
    }

    // -------------------------------------------------------------------------
    // Per-frame work: file watcher debounce
    // -------------------------------------------------------------------------

    /// <summary>Call once per frame on the main thread.</summary>
    public void Update()
    {
        if (!AutoReload || _changePendingUtc == null || IsReloading || EditorState.IsPlaying) return;
        if (DateTime.UtcNow - _changePendingUtc.Value < TimeSpan.FromMilliseconds(750)) return;

        _changePendingUtc = null;
        _ = ReloadAsync(build: true, reason: "source changed").ContinueWith(t =>
        {
            if (t.IsFaulted) ConsoleLog.Add($"Auto-reload failed: {t.Exception?.GetBaseException().Message}", LogLevel.Error);
        }, TaskScheduler.Default);
    }

    private void StartWatching()
    {
        StopWatching();
        if (Project == null) return;

        try
        {
            Directory.CreateDirectory(Project.SourceDirectory);
            _watcher = new FileSystemWatcher(Project.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents   = true,
            };

            void Changed(object? _, FileSystemEventArgs e)
            {
                string path = e.FullPath.Replace('\\', '/');
                bool source = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && path.Contains("/Source/", StringComparison.OrdinalIgnoreCase);
                bool csproj = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
                if (source || csproj) _changePendingUtc = DateTime.UtcNow;
            }

            _watcher.Changed += Changed;
            _watcher.Created += Changed;
            _watcher.Deleted += Changed;
            _watcher.Renamed += (s, e) => Changed(s, e);
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Source watcher not started: {ex.Message}", LogLevel.Warning);
        }
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _changePendingUtc = null;
    }

    // -------------------------------------------------------------------------
    // Exceptions from game code
    // -------------------------------------------------------------------------

    private bool HandleGameException(Exception exception, object owner, string phase)
    {
        string who = owner is Component c ? $"{c.GetType().Name} on '{c.Actor?.Name}'" : owner.GetType().Name;
        string frame = FirstGameFrame(exception);
        ConsoleLog.Add($"[game] {who}.{phase}: {exception.GetType().Name}: {exception.Message}{frame}", LogLevel.Error);

        if (owner is Component component)
        {
            int count = _throwCounts.GetValueOrDefault(component) + 1;
            _throwCounts[component] = count;
            if (count >= 3)
            {
                component.Enabled = false;
                ConsoleLog.Add($"[game] {who} disabled after {count} consecutive exceptions. Fix it and reload_game_code.", LogLevel.Warning);
            }
        }

        return true;
    }

    private static string FirstGameFrame(Exception exception)
    {
        var trace = new StackTrace(exception, fNeedFileInfo: true);
        foreach (var frame in trace.GetFrames())
        {
            string? file = frame.GetFileName();
            if (file == null) continue;
            return $" at {Path.GetFileName(file)}:{frame.GetFileLineNumber()}";
        }
        return "";
    }

    // -------------------------------------------------------------------------

    private void ContributeProjectInfo(System.Text.Json.Nodes.JsonObject info)
    {
        var last = LastBuild;
        var code = new System.Text.Json.Nodes.JsonObject
        {
            ["hasCsproj"]   = Project != null,
            ["csproj"]      = Project?.CsprojPath,
            ["sourceDir"]   = Project?.SourceDirectory,
            ["generation"]  = Loader.Generation,
            ["engineMatch"] = EngineMatches,
            ["autoReload"]  = AutoReload,
            ["actorClasses"]    = Types == null ? null : new System.Text.Json.Nodes.JsonArray(Types.ActorTypes.Select(t => (System.Text.Json.Nodes.JsonNode)t.Name).ToArray()),
            ["componentTypes"]  = Types == null ? null : new System.Text.Json.Nodes.JsonArray(Types.ComponentTypes.Select(t => (System.Text.Json.Nodes.JsonNode)t.Name).ToArray()),
            ["projectTools"]    = new System.Text.Json.Nodes.JsonArray(_toolRegistrations.SelectMany(r => r.ToolNames).Select(n => (System.Text.Json.Nodes.JsonNode)n).ToArray()),
            ["lastBuild"]       = last == null ? null : new System.Text.Json.Nodes.JsonObject
            {
                ["id"]       = last.Id,
                ["state"]    = last.State.ToString().ToLowerInvariant(),
                ["errors"]   = last.Result?.ErrorCount ?? 0,
                ["warnings"] = last.Result?.WarningCount ?? 0,
            },
        };
        info["code"] = code;
    }

    public void Dispose()
    {
        StopWatching();
        Standalone.Stop();
        if (ReferenceEquals(ExceptionIsolation.Handler, (Func<Exception, object, string, bool>)HandleGameException))
            ExceptionIsolation.Handler = null;
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
}

/// <summary>Runs the built game as its own process and streams its output into the Output Log.</summary>
public sealed class StandaloneRunner
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };
    public int? Pid => IsRunning ? _process!.Id : null;
    public int? LastExitCode { get; private set; }

    public int Start(string dotnetPath, string dllPath, string workingDirectory)
    {
        Stop();

        var psi = new ProcessStartInfo
        {
            FileName               = dotnetPath,
            WorkingDirectory       = workingDirectory,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = false,
        };
        psi.ArgumentList.Add(dllPath);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) ConsoleLog.Add("[Game] " + e.Data, LogLevel.Info); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) ConsoleLog.Add("[Game] " + e.Data, LogLevel.Warning); };
        process.Exited += (_, _) =>
        {
            try { LastExitCode = process.ExitCode; } catch { }
            ConsoleLog.Add($"[Game] exited with code {LastExitCode}.", LogLevel.Info);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        return process.Id;
    }

    public bool Stop()
    {
        var process = _process;
        _process = null;
        if (process == null) return false;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            process.Dispose();
        }
    }
}
