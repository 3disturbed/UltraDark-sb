using SexyBiscuit.Editor.CookieJar;
using System.Text.Json.Nodes;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Mcp;
using SexyBiscuit.Engine.Mcp.ClaudeCode;
using SexyBiscuit.Engine.Mcp.Tools;

namespace SexyBiscuit.Editor.Assistant;

public enum McpHostStatus
{
    Stopped,
    Running,
    Failed,
}

/// <summary>
/// The editor's MCP server: wires the engine's protocol, registries and scene tools to the
/// editor's state, pumps tool calls onto the game thread once a frame, and keeps the undo stack
/// and activity log the Assistant panel shows.
/// </summary>
public sealed class McpHost : IDisposable
{
    public static McpHost? Instance { get; private set; }

    public McpHost(AssistantSettings settings)
    {
        Instance  = this;
        Settings  = settings;

        Dispatcher = new QueuedMcpDispatcher();
        Dispatcher.BindMainThread();

        SceneHost = new EditorSceneHost();
        Undo      = new SceneUndoStack(SceneHost);
        Activity  = new ActivityLog();

        Registry  = new McpToolRegistry(Dispatcher) { Activity = Activity, Log = Log };
        Resources = new McpResourceRegistry(Dispatcher);
        Server    = new McpServer(Registry, Resources, new McpServerInfo("sexybiscuit", EngineVersion, SceneResources.Instructions)) { Log = Log };

        // Undo snapshots before every mutation (outside play mode, where Stop restores anyway);
        // flush and dirty-mark after every call so the outliner sees the change this frame.
        Registry.BeforeMutation = (descriptor, arguments, _) =>
        {
            if (EditorState.IsPlaying) return;
            Undo.Snapshot(McpToolRegistry.FormatLabel(descriptor.LabelTemplate, descriptor.Name, arguments));
        };

        Registry.AfterInvoke = (descriptor, _, result) =>
        {
            SceneHost.ActiveScene?.FlushPendingActors();
            if (!descriptor.Mutating || result.NoChange) return;

            EditorState.SceneDirty = true;
            if (EditorState.IsPlaying)
                result.WithWarning("the scene is in play mode; changes made now are discarded when play stops.");
        };

        Activity.EntryChanged += EchoToConsole;

        var engine = new McpRegistrationOptions { Source = "engine" };
        var editor = new McpRegistrationOptions { Source = "editor" };

        Registry.RegisterInstance(new SceneTools(SceneHost, Undo), engine);
        Registry.RegisterInstance(new ActorTools(SceneHost), engine);
        Registry.RegisterInstance(new ComponentTools(SceneHost), engine);
        Registry.RegisterInstance(new MaterialTools(SceneHost), engine);
        Registry.RegisterInstance(new UndoTools(Undo), engine);
        Registry.RegisterInstance(new BatchTools(SceneHost, Registry), engine);
        Registry.RegisterInstance(new EditorTools(this), editor);

        Cookies = new EditorCookieHost(() => AssistantHost.Instance);
        ProjectInfoContributors.Add(Cookies.ContributeProjectInfo);
        Registry.RegisterInstance(new CookieTools(Cookies), editor);

        Resources.RegisterInstance(new SceneResources(SceneHost, Undo, Registry));
        Resources.RegisterInstance(new CookieResources(Cookies));
    }

    public AssistantSettings    Settings   { get; }
    public McpToolRegistry      Registry   { get; }
    public McpResourceRegistry  Resources  { get; }
    public QueuedMcpDispatcher  Dispatcher { get; }
    public SceneUndoStack       Undo       { get; }
    public ActivityLog          Activity   { get; }
    public EditorSceneHost      SceneHost  { get; }
    public McpServer            Server     { get; }
    public McpHttpTransport?    Transport  { get; private set; }
    public ViewportCapture      Capture    { get; } = new();

    /// <summary>The module library: jars, the catalogue, and installing into the open project.</summary>
    public EditorCookieHost     Cookies    { get; }

    public McpHostStatus Status        { get; private set; } = McpHostStatus.Stopped;
    public string?       StatusMessage { get; private set; }

    /// <summary>The URL clients connect to, once the listener is up.</summary>
    public Uri? Url => Transport?.BoundUrl;

    /// <summary>Other subsystems (the C# code host) add their own sections to get_project_info here.</summary>
    public List<Action<JsonObject>> ProjectInfoContributors { get; } = new();

    /// <summary>
    /// Where the engine source is, for <c>get_project_info engineRepo=true</c>. Set by the
    /// composition root from the C# code tools, which own the repo locator; null when the editor
    /// is not running from a checkout, or in a headless catalogue.
    /// </summary>
    public Func<JsonObject>? EngineRepoInfo { get; set; }

    /// <summary>The engine version, from the one place it is stated.</summary>
    public static string EngineVersion => SexyBiscuit.Engine.EngineInfo.Version;

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /// <summary>Binds the listener. Failure is logged, not thrown: the editor works without MCP.</summary>
    public void Start()
    {
        if (Transport != null) return;

        try
        {
            Transport = new McpHttpTransport(Server, new McpHttpTransportOptions
            {
                Port        = Settings.McpPort,
                BearerToken = string.IsNullOrWhiteSpace(Settings.McpToken) ? null : Settings.McpToken,
            }, Log);
            Transport.Start();

            Status        = McpHostStatus.Running;
            StatusMessage = null;
            ConsoleLog.Add($"MCP server listening on {Url}. Connect Claude Code with: {McpConfigWriter.ConnectCommand(Url!.ToString())}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Transport     = null;
            Status        = McpHostStatus.Failed;
            StatusMessage = ex.Message;
            ConsoleLog.Add($"MCP server failed to start: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>Cancels in-flight calls, closes the listener and fails queued work. Idempotent.</summary>
    public void Stop()
    {
        Server.CancelAll();

        var transport = Transport;
        Transport = null;
        if (transport != null)
        {
            try
            {
                transport.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                ConsoleLog.Add($"MCP server stop: {ex.Message}", LogLevel.Warning);
            }
        }

        Dispatcher.Shutdown();
        Status = McpHostStatus.Stopped;
    }

    public void Dispose()
    {
        Stop();
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    /// <summary>Runs queued tool work. Call at the top of every Update, before the scene flush.</summary>
    public void Drain()
    {
        Dispatcher.Drain();
        RefreshCiStatusIfDue();
    }

    // -------------------------------------------------------------------------
    // CI status
    // -------------------------------------------------------------------------

    private static readonly TimeSpan CiRefreshInterval = TimeSpan.FromMinutes(10);
    private DateTime _ciRefreshedAt = DateTime.MinValue;

    /// <summary>
    /// The last CI run on main as one line (<c>main@9f7fc4f success 2h ago</c>), or null without
    /// gh, a network or a checkout. get_context includes it so a session sees a red main before
    /// it builds on it. Refreshed off the game thread at project open and every ten minutes.
    /// </summary>
    public string? CiStatusLine { get; private set; }

    private void RefreshCiStatusIfDue()
    {
        if (DateTime.UtcNow - _ciRefreshedAt < CiRefreshInterval) return;
        _ciRefreshedAt = DateTime.UtcNow;

        string? root = EngineRepoLocator.Find()?.Root;
        if (root == null) return;

        _ = Task.Run(async () =>
        {
            var status = await GitHubActionsStatus.QueryAsync(root).ConfigureAwait(false);
            CiStatusLine = status?.Summary(DateTimeOffset.UtcNow);
        });
    }

    /// <summary>The Stop button: withdraws every tool call in flight.</summary>
    public int RequestStopAll() => Server.CancelAll();

    /// <summary>A project opened: paths resolve against it, undo starts over, Claude Code learns the URL.</summary>
    public void OnProjectOpened(string root)
    {
        ProjectPaths.Root = root;
        Undo.Clear();
        _ciRefreshedAt = DateTime.MinValue;   // a fresh line for the new project's first get_context

        if (!Settings.WriteProjectMcpConfig || Url == null) return;

        try
        {
            string outcome = McpConfigWriter.WriteProjectConfig(root, Url.ToString(), Settings.McpToken);
            if (outcome == "written")
                ConsoleLog.Add($"Wrote {Path.Combine(root, ".mcp.json")} so Claude Code in this project connects to the editor.", LogLevel.Info);
            else if (outcome.StartsWith("skipped", StringComparison.Ordinal))
                ConsoleLog.Add($".mcp.json not updated: {outcome}.", LogLevel.Warning);
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Could not write .mcp.json: {ex.Message}", LogLevel.Warning);
        }
    }

    // -------------------------------------------------------------------------

    // Mutations and failures go to the Output Log; reads stay in the activity log so the console
    // stays readable while Claude looks around.
    private static void EchoToConsole(ActivityEntry entry)
    {
        if (entry.State == ActivityState.Running) return;
        if (!entry.Mutating && entry.State == ActivityState.Succeeded) return;

        var level = entry.State switch
        {
            ActivityState.Succeeded => LogLevel.Info,
            ActivityState.Cancelled => LogLevel.Warning,
            _                       => LogLevel.Error,
        };

        string detail = entry.State == ActivityState.Succeeded ? "" : ": " + entry.Summary;
        ConsoleLog.Add($"[Claude] {entry.Label}: {entry.State.ToString().ToLowerInvariant()} ({entry.Duration.TotalMilliseconds:F0} ms){detail}", level);
    }

    private static void Log(string message, bool isError)
        => ConsoleLog.Add(message, isError ? LogLevel.Error : LogLevel.Info);
}
