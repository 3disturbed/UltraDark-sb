using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SexyBiscuit.Engine.Mcp.ClaudeCode;

namespace SexyBiscuit.Editor.Assistant;

public enum SessionState
{
    /// <summary>The process is up but has not sent <c>system/init</c> yet.</summary>
    Starting,

    /// <summary>Between turns: a user message is sent immediately.</summary>
    Ready,

    /// <summary>A turn is in progress.</summary>
    Working,

    /// <summary>A <c>can_use_tool</c> prompt is waiting for the user.</summary>
    WaitingForPermission,

    /// <summary>An interrupt was sent; waiting for the turn's <c>result</c>.</summary>
    Interrupting,

    /// <summary>We asked it to exit.</summary>
    Stopping,

    Exited,
}

/// <summary>
/// One Claude Code process in stream-json mode. Two background threads read stdout and stderr
/// into queues; everything else (state, transcript, stdin writes) happens on the main thread in
/// <see cref="Pump"/>, so the panel never races the parser.
/// </summary>
public sealed class ClaudeCodeSession : IDisposable
{
    private static readonly TimeSpan InitWatchdog       = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan InterruptGrace     = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PermissionAutoDeny = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExitDrainGrace     = TimeSpan.FromSeconds(2);
    private const int TailLines = 200;

    private sealed record RawLine(string Text, string? Error);
    private sealed class ExitMarker { public static readonly ExitMarker Instance = new(); }
    private sealed class StdoutClosed { public static readonly StdoutClosed Instance = new(); }

    private readonly ClaudeInstallInfo    _install;
    private readonly List<string>         _argv;
    private readonly string               _cwd;
    private readonly Transcript           _transcript;
    private readonly Action<string, bool>? _log;
    private readonly ConcurrentQueue<object> _incoming = new();
    private readonly Queue<UserEntry>     _queued   = new();
    private readonly object               _stderrLock = new();
    private readonly List<string>         _stderr   = new();
    private readonly List<string>         _frameLog = new();
    private readonly object               _stdinLock = new();

    private Process?     _process;
    private StreamWriter? _stdin;
    private DateTime     _startedUtc;
    private DateTime?    _exitSeenUtc;
    private DateTime?    _interruptUtc;
    private bool         _stdoutClosed;
    private bool         _turnPending;
    private DateTime?    _turnStartedUtc;
    private bool         _initWarned;
    private bool         _finished;

    public ClaudeCodeSession(ClaudeInstallInfo install, IReadOnlyList<string> argv, string workingDirectory, string sessionId, Transcript transcript, Action<string, bool>? log = null)
    {
        _install    = install;
        _argv       = argv.ToList();
        _cwd        = workingDirectory;
        _transcript = transcript;
        _log        = log;
        SessionId   = sessionId;
    }

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    public string       SessionId { get; }
    public SessionState State     { get; private set; } = SessionState.Starting;

    public string Executable  => _install.Path;
    public ClaudeInstallInfo Install => _install;
    public IReadOnlyList<string> Argv => _argv;
    public string ArgvDisplay => ClaudeArgvBuilder.Display(_install.Path, _argv);
    public string WorkingDirectory => _cwd;

    public InitFrame?   Init         { get; private set; }
    public bool         InitSeen     => Init != null;
    public bool         McpConnected { get; private set; }
    public string?      Model        { get; private set; }
    public ResultFrame? LastResult   { get; private set; }
    public double       ProcessCostUsd { get; private set; }
    public long         InputTokens  { get; private set; }
    public long         OutputTokens { get; private set; }
    public int          Turns        { get; private set; }

    public int?    ExitCode      { get; private set; }
    public string? ExitReason    { get; private set; }
    public bool    NotLoggedIn   { get; private set; }
    public bool    ResumeFailed  { get; private set; }
    public string? UnknownOption { get; private set; }
    public DateTime StartedUtc   => _startedUtc;

    /// <summary>
    /// When the current turn began, or null when no turn is running.
    /// </summary>
    /// <remarks>
    /// The activity indicator shows this as elapsed time. A spinner alone says something
    /// is happening; a spinner with "0:47" on it is the difference between a slow turn and
    /// a hung one, and decides whether the user reaches for Stop.
    /// </remarks>
    public DateTime? TurnStartedUtc => _turnStartedUtc;

    /// <summary>How long the current turn has been running. Zero when idle.</summary>
    public TimeSpan TurnElapsed
        => _turnStartedUtc is { } started ? DateTime.UtcNow - started : TimeSpan.Zero;

    public bool IsAlive => _process != null && !_finished;

    /// <summary>A turn is running, or a prompt is up, or we are interrupting.</summary>
    public bool IsBusy => IsAlive && (_turnPending || State is SessionState.WaitingForPermission or SessionState.Interrupting);

    /// <summary>What to show next to the state: the transcript's busy text while working.</summary>
    public string? SubStatus => State switch
    {
        SessionState.Starting             => _turnPending ? "Starting..." : null,
        SessionState.Working              => _transcript.BusyStatus ?? "Working...",
        SessionState.WaitingForPermission => "Waiting for permission",
        SessionState.Interrupting         => "Stopping...",
        SessionState.Stopping             => "Exiting...",
        _                                 => null,
    };

    public int QueuedCount => _queued.Count;

    public IReadOnlyList<string> StderrTail
    {
        get { lock (_stderrLock) return _stderr.ToArray(); }
    }

    /// <summary>The last frames, raw and truncated, for the Diagnostics tab.</summary>
    public IReadOnlyList<string> FrameLog => _frameLog;

    public IEnumerable<PermissionEntry> PendingPermissions
        => _transcript.Entries.OfType<PermissionEntry>().Where(p => p.Decision == PermissionDecision.Pending);

    public event Action<InitFrame>?   Initialized;
    public event Action<ResultFrame>? TurnCompleted;
    public event Action?              Exited;

    /// <summary>Every parsed frame, before the transcript sees it. The self-test listens here.</summary>
    public event Action<StreamFrame>? FrameReceived;

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public void Start()
    {
        if (_process != null) throw new InvalidOperationException("The session was already started.");

        _startedUtc = DateTime.UtcNow;
        var psi = ClaudeProcess.StartInfo(_install.Path, _argv, _cwd);

        try
        {
            _process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            _process = null;
            Finish(-1, $"Claude Code could not be started: {ex.Message}", SystemSeverity.Error);
            return;
        }

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => _incoming.Enqueue(ExitMarker.Instance);

        _stdin = _process.StandardInput;
        _stdin.AutoFlush = true;

        var stdout = _process.StandardOutput;
        var stderr = _process.StandardError;
        new Thread(() => ReadStdout(stdout)) { IsBackground = true, Name = "claude-stdout" }.Start();
        new Thread(() => ReadStderr(stderr)) { IsBackground = true, Name = "claude-stderr" }.Start();

        State = SessionState.Starting;
    }

    private void ReadStdout(StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                if (StreamJsonParser.TryParse(line, out var frame, out var error)) _incoming.Enqueue(frame!);
                else _incoming.Enqueue(new RawLine(line, error));
            }
        }
        catch (Exception ex)
        {
            _incoming.Enqueue(new RawLine("(stdout reader stopped: " + ex.Message + ")", null));
        }
        finally
        {
            _incoming.Enqueue(StdoutClosed.Instance);
        }
    }

    private void ReadStderr(StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lock (_stderrLock)
                {
                    _stderr.Add(line);
                    if (_stderr.Count > TailLines) _stderr.RemoveAt(0);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Asks the process to exit (stdin EOF), then kills it after the grace period.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        var process = _process;
        if (process == null || _finished) return;

        State = SessionState.Stopping;
        lock (_stdinLock)
        {
            try { _stdin?.Close(); } catch (Exception) { }
            _stdin = null;
        }

        try
        {
            using var cts = new CancellationTokenSource(grace);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill();
        }
        catch (Exception)
        {
        }
    }

    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false } p) p.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        Kill();
        _process?.Dispose();
    }

    // -------------------------------------------------------------------------
    // Sending
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a user turn. Written at once unless a turn is in progress, in which case it is
    /// queued locally (a grey entry) and sent when the result arrives.
    /// </summary>
    public UserEntry SendUserMessage(string text, bool synthetic = false)
    {
        var entry = _transcript.AddUser(text, UserEntryState.Queued, synthetic);

        if (!IsAlive)
        {
            _queued.Enqueue(entry);
            return entry;
        }

        if (State is SessionState.Starting or SessionState.Ready) Write(entry);
        else _queued.Enqueue(entry);
        return entry;
    }

    /// <summary>Removes a message that has not been sent yet.</summary>
    public bool WithdrawQueued(UserEntry entry)
    {
        if (entry.State != UserEntryState.Queued || !_queued.Contains(entry)) return false;

        var remaining = _queued.Where(e => !ReferenceEquals(e, entry)).ToList();
        _queued.Clear();
        foreach (var e in remaining) _queued.Enqueue(e);
        return true;
    }

    /// <summary>Queued, unsent texts, for handing to a replacement session.</summary>
    public List<string> TakeQueued()
    {
        var texts = _queued.Select(e => e.Text).ToList();
        _queued.Clear();
        return texts;
    }

    private void Write(UserEntry entry)
    {
        if (!WriteLine(StreamJsonWriter.UserMessage(entry.Text, SessionId)))
        {
            _queued.Enqueue(entry);
            return;
        }

        entry.State  = UserEntryState.Sent;
        _turnPending = true;
        _turnStartedUtc ??= DateTime.UtcNow;
        if (State == SessionState.Ready) State = SessionState.Working;
        _transcript.SetBusy("Thinking...");
    }

    private void FlushQueued()
    {
        if (_queued.Count == 0 || State != SessionState.Ready) return;
        Write(_queued.Dequeue());
    }

    private bool WriteLine(string line)
    {
        lock (_stdinLock)
        {
            if (_stdin == null) return false;
            try
            {
                _stdin.Write(line);
                _stdin.Write('\n');
                return true;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[assistant] stdin write failed: {ex.Message}", true);
                return false;
            }
        }
    }

    /// <summary>Stops the current turn. The CLI answers with a result; if it does not within five seconds the process is killed.</summary>
    public void Interrupt()
    {
        if (!IsAlive) return;

        foreach (var pending in PendingPermissions.ToList())
            RespondToPermission(pending, PermissionDecision.Denied, "The user pressed Stop.");

        if (!_turnPending && State != SessionState.WaitingForPermission) return;

        WriteLine(StreamJsonWriter.Interrupt(Guid.NewGuid().ToString("N")));
        State         = SessionState.Interrupting;
        _interruptUtc = DateTime.UtcNow;
        _transcript.SetBusy("Stopping...");
    }

    public void RespondToPermission(PermissionEntry entry, PermissionDecision decision, string? denyMessage = null)
    {
        if (entry.Decision != PermissionDecision.Pending) return;

        string line = decision switch
        {
            PermissionDecision.Allowed       => StreamJsonWriter.ControlResponseAllow(entry.RequestId, entry.Input),
            PermissionDecision.AlwaysAllowed => StreamJsonWriter.ControlResponseAllow(entry.RequestId, entry.Input, entry.SuppressAlwaysAllow ? null : entry.Suggestions),
            _                                => StreamJsonWriter.ControlResponseDeny(entry.RequestId, denyMessage ?? "The user denied this."),
        };

        WriteLine(line);
        entry.Decision = decision;
        _transcript.Touch();

        if (State == SessionState.WaitingForPermission && !PendingPermissions.Any())
        {
            State = SessionState.Working;
            _transcript.SetBusy("Working...");
        }
    }

    public void SetPermissionMode(ClaudePermissionMode mode)
    {
        string cli = mode switch
        {
            ClaudePermissionMode.Autonomous  => "bypassPermissions",
            ClaudePermissionMode.AcceptEdits => "acceptEdits",
            ClaudePermissionMode.Ask         => "default",
            _                                => "auto",
        };
        WriteLine(StreamJsonWriter.SetPermissionMode(Guid.NewGuid().ToString("N"), cli));
    }

    public void SetModel(string? model)
        => WriteLine(StreamJsonWriter.SetModel(Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(model) ? null : model));

    // -------------------------------------------------------------------------
    // Pumping (main thread)
    // -------------------------------------------------------------------------

    /// <summary>Applies queued frames within a time budget, then runs the watchdogs. Call every frame.</summary>
    public void Pump(int maxFrames = 500, double budgetMs = 4)
    {
        var watch = Stopwatch.StartNew();
        int handled = 0;

        while (handled < maxFrames && watch.Elapsed.TotalMilliseconds < budgetMs && _incoming.TryDequeue(out var item))
        {
            handled++;
            switch (item)
            {
                case StreamFrame frame:  Handle(frame); break;
                case RawLine raw:        LogFrame("RAW " + raw.Text); break;
                case ExitMarker:         _exitSeenUtc ??= DateTime.UtcNow; break;
                case StdoutClosed:       _stdoutClosed = true; break;
            }
        }

        if (_finished) return;

        if (_exitSeenUtc is { } exitSeen && (_stdoutClosed || DateTime.UtcNow - exitSeen > ExitDrainGrace) && _incoming.IsEmpty)
        {
            FinishFromProcess();
            return;
        }

        var now = DateTime.UtcNow;

        if (!InitSeen && _turnPending && !_initWarned && now - _startedUtc > InitWatchdog)
        {
            _initWarned = true;
            _transcript.AddSystem($"Claude Code has not answered after {InitWatchdog.TotalSeconds:F0} s. Check the Diagnostics tab; the Stop button kills the process.", SystemSeverity.Warning);
        }

        if (_interruptUtc is { } interrupt && now - interrupt > InterruptGrace && State == SessionState.Interrupting)
        {
            _interruptUtc = null;
            _transcript.AddSystem("Claude Code did not stop when asked; killing the process.", SystemSeverity.Warning);
            Kill();
        }

        foreach (var pending in PendingPermissions.ToList())
        {
            if (now - pending.CreatedUtc > PermissionAutoDeny)
            {
                RespondToPermission(pending, PermissionDecision.AutoDenied, "No answer from the user within ten minutes.");
                _transcript.AddSystem($"Permission for {pending.ToolName} was denied automatically after ten minutes without an answer.", SystemSeverity.Warning);
            }
        }
    }

    private void Handle(StreamFrame frame)
    {
        LogFrame(frame.Raw);
        FrameReceived?.Invoke(frame);

        switch (frame)
        {
            case InitFrame init:
                Init         = init;
                Model        = init.Model;
                McpConnected = init.McpServers.Any(s => s.Name == McpConfigWriter.ServerName && s.Status == "connected");

                if (State == SessionState.Starting) State = _turnPending ? SessionState.Working : SessionState.Ready;

                var ours = init.McpServers.FirstOrDefault(s => s.Name == McpConfigWriter.ServerName);
                if (McpConnected)
                {
                    _transcript.AddSystem($"Claude Code {init.ClaudeCodeVersion} · {init.Model} · editor tools connected.");
                }
                else
                {
                    string servers = init.McpServers.Count == 0 ? "none" : string.Join(", ", init.McpServers.Select(s => $"{s.Name}: {s.Status}"));
                    _transcript.AddSystem($"Claude Code {init.ClaudeCodeVersion} started, but the editor's MCP server is {(ours == null ? "missing" : ours.Status)} (servers: {servers}). Editor tools will not work; check the Diagnostics tab.", SystemSeverity.Error);
                }

                Initialized?.Invoke(init);
                FlushQueued();
                break;

            case StatusFrame status:
                _transcript.Apply(status);
                if (State == SessionState.Ready && _turnPending) State = SessionState.Working;
                break;

            case AssistantFrame assistant:
                if (assistant.Model != null && !assistant.Model.StartsWith("<", StringComparison.Ordinal)) Model = assistant.Model;
                _transcript.Apply(assistant);
                if (assistant.Error != null || assistant.IsApiErrorMessage) NoteApiError(assistant.Text);
                if (State == SessionState.Ready) State = SessionState.Working;
                break;

            case StreamEventFrame or UserFrame:
                _transcript.Apply(frame);
                if (State == SessionState.Ready && _turnPending) State = SessionState.Working;
                break;

            case ResultFrame result:
                _transcript.Apply(result);
                LastResult      = result;
                ProcessCostUsd  = result.TotalCostUsd;
                InputTokens    += result.InputTokens + result.CacheReadTokens + result.CacheCreationTokens;
                OutputTokens   += result.OutputTokens;
                Turns++;
                _turnPending  = false;
                _turnStartedUtc = null;
                _interruptUtc = null;
                if (result.IsError) NoteApiError(result.ResultText);
                if (State is SessionState.Working or SessionState.WaitingForPermission or SessionState.Interrupting or SessionState.Starting)
                    State = SessionState.Ready;
                TurnCompleted?.Invoke(result);
                FlushQueued();
                break;

            case ControlRequestFrame request:
                if (request.IsPermissionRequest)
                {
                    _transcript.Apply(request);
                    State = SessionState.WaitingForPermission;
                }
                else
                {
                    // Hooks and SDK MCP servers are never registered by the editor, so nothing else is expected;
                    // answer anyway so the CLI does not wait on us.
                    WriteLine(new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"]     = "control_response",
                        ["response"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["subtype"]    = "error",
                            ["request_id"] = request.RequestId,
                            ["error"]      = $"The SexyBiscuit editor does not handle '{request.Subtype}'.",
                        },
                    }.ToJsonString());
                }
                break;

            case ControlCancelFrame cancel:
                _transcript.Apply(cancel);
                if (State == SessionState.WaitingForPermission && !PendingPermissions.Any()) State = SessionState.Working;
                break;

            case SystemFrame system when system.Subtype == "compact_boundary":
                _transcript.AddSystem("Context compacted.");
                break;
        }
    }

    private void NoteApiError(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) || text.Contains("/login", StringComparison.Ordinal)
            || text.Contains("authentication", StringComparison.OrdinalIgnoreCase) || text.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase))
            NotLoggedIn = true;
    }

    private void LogFrame(string raw)
    {
        _frameLog.Add(raw.Length <= 400 ? raw : raw[..399] + "...");
        if (_frameLog.Count > TailLines) _frameLog.RemoveAt(0);
    }

    // -------------------------------------------------------------------------
    // Exit
    // -------------------------------------------------------------------------

    private void FinishFromProcess()
    {
        int code = -1;
        try { code = _process?.ExitCode ?? -1; } catch (Exception) { }

        string stderr;
        lock (_stderrLock) stderr = string.Join("\n", _stderr);
        string firstError = _stderr.Count == 0 ? "" : (_stderr.FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? _stderr[0]).Trim();

        if (State == SessionState.Stopping)
        {
            Finish(code, null, SystemSeverity.Info);
            return;
        }

        if (NotLoggedIn || stderr.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) || stderr.Contains("/login", StringComparison.Ordinal))
        {
            NotLoggedIn = true;
            Finish(code, "Claude Code is not signed in. Run `claude` in a terminal once and sign in (or set ANTHROPIC_API_KEY), then press Restart.", SystemSeverity.Error);
            return;
        }

        UnknownOption = ClaudeArgvBuilder.UnknownOptionIn(stderr);
        if (UnknownOption != null)
        {
            Finish(code, $"This Claude Code does not know the {UnknownOption} option; retrying without it.", SystemSeverity.Warning);
            return;
        }

        if (stderr.Contains("No conversation found", StringComparison.OrdinalIgnoreCase)
            || (stderr.Contains("session", StringComparison.OrdinalIgnoreCase) && stderr.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            ResumeFailed = true;
            Finish(code, "The previous session could not be resumed; starting a fresh one.", SystemSeverity.Warning);
            return;
        }

        if (stderr.Contains("nested", StringComparison.OrdinalIgnoreCase) || stderr.Contains("CLAUDECODE", StringComparison.Ordinal))
        {
            Finish(code, "Claude Code refused to start inside another Claude Code session: " + firstError, SystemSeverity.Error);
            return;
        }

        if (code == 0 && !_turnPending)
        {
            Finish(code, "Claude Code exited.", SystemSeverity.Info);
            return;
        }

        Finish(code, $"Claude Code exited with code {code}{(firstError.Length > 0 ? ": " + firstError : "")}. Press Restart to resume the session.", code == 0 ? SystemSeverity.Info : SystemSeverity.Error);
    }

    private void Finish(int code, string? message, SystemSeverity severity)
    {
        if (_finished) return;
        _finished  = true;
        ExitCode   = code;
        ExitReason = message;
        State      = SessionState.Exited;
        _turnPending = false;
        _turnStartedUtc = null;

        foreach (var pending in PendingPermissions.ToList())
            pending.Decision = PermissionDecision.Withdrawn;

        foreach (var call in _transcript.RunningToolCalls.ToList())
        {
            call.Status        = ToolCallStatus.Cancelled;
            call.ResultSummary = "process exited";
        }

        _transcript.SetBusy(null);
        if (message != null)
        {
            _transcript.AddSystem(message, severity);
            if (severity != SystemSeverity.Info) _log?.Invoke("[Assistant] " + message, severity == SystemSeverity.Error);
        }
        _transcript.Touch();

        lock (_stdinLock)
        {
            try { _stdin?.Dispose(); } catch (Exception) { }
            _stdin = null;
        }

        Exited?.Invoke();
    }
}
