using System.Text.Json.Nodes;
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
            if (!descriptor.Mutating) return;

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
        Registry.RegisterInstance(new EditorTools(this), editor);

        Resources.RegisterInstance(new SceneResources(SceneHost, Undo, Registry));
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

    public McpHostStatus Status        { get; private set; } = McpHostStatus.Stopped;
    public string?       StatusMessage { get; private set; }

    /// <summary>The URL clients connect to, once the listener is up.</summary>
    public Uri? Url => Transport?.BoundUrl;

    /// <summary>Other subsystems (the C# code host) add their own sections to get_project_info here.</summary>
    public List<Action<JsonObject>> ProjectInfoContributors { get; } = new();

    public static string EngineVersion
        => typeof(Actor).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

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
    public void Drain() => Dispatcher.Drain();

    /// <summary>The Stop button: withdraws every tool call in flight.</summary>
    public int RequestStopAll() => Server.CancelAll();

    /// <summary>A project opened: paths resolve against it, undo starts over, Claude Code learns the URL.</summary>
    public void OnProjectOpened(string root)
    {
        ProjectPaths.Root = root;
        Undo.Clear();

        if (!Settings.WriteProjectMcpConfig || Url == null) return;

        try
        {
            string outcome = McpConfigWriter.WriteProjectConfig(root, Url.ToString(), Settings.McpToken);
            if (outcome == "written")
                ConsoleLog.Add($"Wrote {Path.Combine(root, ".mcp.json")} so Claude Code in this project connects to the editor.", LogLevel.Info);
            else if (outcome.StartsWith("skipped", StringComparison.Ordinal))
                ConsoleLog.Add($".mcp.json not updated — {outcome}.", LogLevel.Warning);
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
        ConsoleLog.Add($"[Claude] {entry.Label} — {entry.State.ToString().ToLowerInvariant()} ({entry.Duration.TotalMilliseconds:F0} ms){detail}", level);
    }

    private static void Log(string message, bool isError)
        => ConsoleLog.Add(message, isError ? LogLevel.Error : LogLevel.Info);
}
