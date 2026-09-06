using System.Text.Json.Nodes;
using SexyBiscuit.Editor.GameCode;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Mcp;
using SexyBiscuit.Engine.Mcp.ClaudeCode;
using SexyBiscuit.Engine.Mcp.Tools;

namespace SexyBiscuit.Editor.Assistant;

/// <summary>
/// Owns the Assistant: the embedded Claude Code session, the interaction board external
/// sessions talk through, the transcript the panel draws, and the per-project session
/// records. Pumped once a frame from <see cref="EditorApp.Update"/>.
/// </summary>
public sealed class AssistantHost : IDisposable
{
    public static AssistantHost? Instance { get; private set; }

    private static readonly TimeSpan RestartBudgetWindow = TimeSpan.FromMinutes(1);

    private readonly McpHost        _mcp;
    private readonly GameCodeHost   _code;
    private readonly EditorRestart? _restart;
    private readonly HashSet<string> _droppedFlags = new(StringComparer.Ordinal);
    private readonly List<string>    _carryOver    = new();
    private readonly List<DateTime>  _autoRestarts = new();

    private string?  _projectRoot;
    private string?  _projectName;
    private bool     _startRequested;
    private bool     _startForced;
    private bool     _notFoundReported;
    private string?  _resumeSessionId;
    private string?  _resumeMessage;
    private bool     _lastStartWasResume;
    private bool     _sessionExitHandled;
    private bool     _stoppedByUser;
    private double   _lastProcessCost;
    private Action?  _afterExit;
    private DateTime _promptQueuedSinceUtc;
    private bool     _promptQueuedNoticeShown;

    public AssistantHost(McpHost mcp, GameCodeHost code, EditorRestart? restart, AssistantSettings settings, bool disabled)
    {
        Instance  = this;
        _mcp      = mcp;
        _code     = code;
        _restart  = restart;
        Settings  = settings;
        Disabled  = disabled;

        Board       = new InteractionBoard();
        Transcript  = new Transcript { MaxEntries = Math.Max(100, settings.TranscriptMaxEntries) };
        Install     = new ClaudeCodeInstall();
        Interaction = new UserInteraction(Board, settings);

        mcp.Registry.RegisterInstance(Interaction, new McpRegistrationOptions { Source = "editor" });
        mcp.Server.Instructions = ClaudeSystemPrompt.ExternalInstructions(SceneResources.Instructions);
        mcp.Server.ClientActivity += OnClientActivity;
        mcp.ProjectInfoContributors.Add(ContributeProjectInfo);

        if (restart != null) restart.BeforeRestartAsync += PrepareForRestartAsync;

        if (!disabled) Install.DetectAsync(settings.ClaudePath);
        else Transcript.AddSystem("The embedded assistant is off (--no-assistant). Connect Claude Code from a terminal instead; see the Diagnostics tab.");
    }

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    public AssistantSettings  Settings    { get; }
    public InteractionBoard   Board       { get; }
    public Transcript         Transcript  { get; }
    public ClaudeCodeInstall  Install     { get; }
    public UserInteraction    Interaction { get; }
    public ClaudeCodeSession? Session     { get; private set; }
    public SessionRecord?     Record      { get; private set; }
    public bool               Disabled    { get; }
    public McpHost            Mcp         => _mcp;

    public string? ProjectRoot => _projectRoot;
    public string? ProjectName => _projectName;

    /// <summary>The most recent external (terminal) MCP client, if any was seen.</summary>
    public McpClientInfo? ExternalClient { get; private set; }

    public InteractionDriver Driver => Board.Driver;

    /// <summary>Something is happening that the user might want to stop.</summary>
    public bool IsBusy => Session?.IsBusy == true || Board.PendingQuestion != null;

    /// <summary>True when Start makes sense: a project is open, a binary exists, nothing is running.</summary>
    public bool CanStart => !Disabled && _projectRoot != null && Install.Info != null && Session is not { IsAlive: true };

    /// <summary>Lifetime cost for this project across sessions, from the record.</summary>
    public double LifetimeCostUsd => Record?.LifetimeCostUsd ?? 0;

    /// <summary>A short state line for the toolbar and the panel header.</summary>
    public string StateText
    {
        get
        {
            if (Disabled) return "Assistant off";
            if (Session is { } s && s.IsAlive)
            {
                return s.State switch
                {
                    SessionState.Starting             => s.SubStatus ?? "Idle",
                    SessionState.Ready                => s.QueuedCount > 0 ? "Sending..." : "Idle",
                    SessionState.Working              => s.SubStatus ?? "Working...",
                    SessionState.WaitingForPermission => "Waiting for permission",
                    SessionState.Interrupting         => "Stopping...",
                    SessionState.Stopping             => "Exiting...",
                    _                                 => s.State.ToString(),
                };
            }
            if (Session is { State: SessionState.Exited } e)
                return e.NotLoggedIn ? "Not signed in" : _stoppedByUser ? "Session stopped" : "Session exited";
            if (Board.IsWaitingForPrompt) return "Terminal session waiting for you";
            if (Driver == InteractionDriver.External) return "Terminal session connected";
            if (Install.IsDetecting) return "Looking for Claude Code...";
            if (Install.HasDetected && Install.Info == null) return "Claude Code not found";
            if (_projectRoot == null) return "Open a project";
            return "No session";
        }
    }

    // -------------------------------------------------------------------------
    // Per-frame
    // -------------------------------------------------------------------------

    public void Update()
    {
        Session?.Pump();
        Board.EmbeddedSessionActive = Session?.IsAlive == true;

        DrainBoardEvents();
        HandleSessionExit();
        HandleDeferredStart();
        NoticeStalePrompts();

        var pending = Board.PendingQuestion;
        EditorState.AssistantBusy      = IsBusy;
        EditorState.AssistantBusyLabel = pending != null ? "Waiting for your answer" : Session?.SubStatus ?? "Working...";
    }

    private void DrainBoardEvents()
    {
        while (Board.TryDequeueEvent(out var e) && e != null)
        {
            switch (e.Kind)
            {
                case BoardEventKind.Said:
                    if (e.Text != null)
                    {
                        string text = e.Level is "warning" or "error" ? $"[{e.Level}] {e.Text}" : e.Text;
                        Transcript.AddSaid(text);
                    }
                    break;

                case BoardEventKind.QuestionAsked:
                    if (e.Question != null) Transcript.AddQuestion(e.Question);
                    break;

                case BoardEventKind.QuestionResolved:
                    Transcript.Touch();
                    break;

                case BoardEventKind.PromptQueued:
                    if (_promptQueuedSinceUtc == default) _promptQueuedSinceUtc = DateTime.UtcNow;
                    break;

                case BoardEventKind.PromptConsumed:
                {
                    var entry = e.Text == null ? null : Transcript.Entries.OfType<UserEntry>().FirstOrDefault(u => u.State == UserEntryState.Queued && u.Text == e.Text);
                    if (entry != null) entry.State = UserEntryState.Acked;
                    if (Board.PendingPromptCount == 0) { _promptQueuedSinceUtc = default; _promptQueuedNoticeShown = false; }
                    Transcript.Touch();
                    break;
                }

                case BoardEventKind.WaitStarted:
                case BoardEventKind.WaitEnded:
                    Transcript.Touch();
                    break;
            }
        }
    }

    // A terminal session that never calls wait_for_user leaves typed prompts stranded; say so once.
    private void NoticeStalePrompts()
    {
        if (Board.PendingPromptCount == 0 || Board.IsWaitingForPrompt || Session?.IsAlive == true || _promptQueuedNoticeShown) return;
        if (_promptQueuedSinceUtc == default || DateTime.UtcNow - _promptQueuedSinceUtc < TimeSpan.FromSeconds(60)) return;

        _promptQueuedNoticeShown = true;
        Transcript.AddSystem("Your message is queued, but no Claude Code session is waiting for it. A terminal session picks it up when it calls wait_for_user; or press Start to run the embedded assistant.", SystemSeverity.Warning);
    }

    private void HandleSessionExit()
    {
        var s = Session;
        if (s == null || s.State != SessionState.Exited || _sessionExitHandled) return;
        _sessionExitHandled = true;

        if (_afterExit != null)
        {
            var next = _afterExit;
            _afterExit = null;
            next();
            return;
        }

        if (_stoppedByUser) return;

        if (s.UnknownOption != null && !_droppedFlags.Contains(s.UnknownOption) && AllowAutoRestart())
        {
            _droppedFlags.Add(s.UnknownOption);
            _carryOver.AddRange(s.TakeQueued());
            StartSession(resume: _lastStartWasResume);
            return;
        }

        if (s.ResumeFailed && _lastStartWasResume && _projectRoot != null && AllowAutoRestart())
        {
            Settings.ForgetSession(_projectRoot);
            Record = null;
            _carryOver.AddRange(s.TakeQueued());
            StartSession(resume: false);
            return;
        }

        // Not signed in, or a plain crash: the panel shows the reason and offers Restart.
    }

    private bool AllowAutoRestart()
    {
        var now = DateTime.UtcNow;
        _autoRestarts.RemoveAll(t => now - t > RestartBudgetWindow);
        if (_autoRestarts.Count >= 3) return false;
        _autoRestarts.Add(now);
        return true;
    }

    private void HandleDeferredStart()
    {
        if (!_startRequested) return;
        if (Disabled || Session is { IsAlive: true })
        {
            _startRequested = false;
            return;
        }

        if (!Install.HasDetected)
        {
            if (!Install.IsDetecting) Install.DetectAsync(Settings.ClaudePath);
            return;
        }

        if (Install.Info == null)
        {
            _startRequested = false;
            if (!_notFoundReported)
            {
                _notFoundReported = true;
                Transcript.AddSystem("Claude Code was not found on this machine, so the embedded assistant cannot start. " +
                                     ClaudeCodeLocator.InstallInstructions(OperatingSystem.IsWindows()) +
                                     " You can also point the editor at a binary in Assistant Settings, or drive the editor from a terminal Claude Code (see Diagnostics).", SystemSeverity.Warning);
            }
            return;
        }

        if (!_startForced && Board.Driver == InteractionDriver.External)
        {
            _startRequested = false;
            Transcript.AddSystem("A terminal Claude Code session is driving the editor, so no embedded session was started. Press Start to run one anyway.");
            return;
        }

        _startRequested = false;
        _startForced    = false;
        StartSession(resume: true);
    }

    // -------------------------------------------------------------------------
    // Project and restart hooks
    // -------------------------------------------------------------------------

    public void OnProjectOpened(string root)
    {
        string name = EditorState.CurrentProject?.ProjectName ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        bool switched = _projectRoot != null && !string.Equals(AssistantSettings.NormaliseRoot(_projectRoot), AssistantSettings.NormaliseRoot(root), StringComparison.Ordinal);

        if (switched && Session is { IsAlive: true })
        {
            _afterExit = null;
            StopSession(quiet: true);
        }

        _projectRoot      = root;
        _projectName      = name;
        _notFoundReported = false;
        Record            = Settings.FindSession(root);

        if (Transcript.Entries.Count > 0 && switched) Transcript.AddSystem($"Project '{name}' opened.");

        if (!Disabled && (Settings.AutoStartOnProjectOpen || _resumeSessionId != null))
        {
            _startRequested = true;
            _startForced    = _resumeSessionId != null;
        }
    }

    /// <summary>The editor came back from a self-restart: resume the same session and tell it what happened.</summary>
    public void OnEditorResumed(RelaunchState state)
    {
        if (Disabled) return;

        _resumeSessionId = string.IsNullOrEmpty(state.AssistantSessionId) ? Record?.SessionId : state.AssistantSessionId;
        if (_resumeSessionId == null) return;

        string summary   = state.BuildSummary != null ? $", {state.BuildSummary}" : "";
        string selection = state.SelectedActorName != null ? $" '{state.SelectedActorName}' is selected again." : "";
        _resumeMessage = $"[editor] The SexyBiscuit editor restarted ({state.Reason}{summary}). The project and scene were reopened.{selection} " +
                         "Continue where you left off; call get_context, then get_scene_summary if you need to re-check the scene, and re-list tools if any are missing.";

        _startRequested = true;
        _startForced    = true;
    }

    private Task PrepareForRestartAsync(RelaunchState state)
    {
        var s = Session;
        state.AssistantSessionId = Record?.SessionId ?? s?.SessionId;
        if (s == null || !s.IsAlive) return Task.CompletedTask;

        // Give the turn that asked for the restart a moment to deliver its result, then stop cleanly.
        // Synchronous on purpose: there is no synchronisation context to come back to.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (s.IsBusy && DateTime.UtcNow < deadline)
        {
            s.Pump();
            Thread.Sleep(50);
        }

        Transcript.AddSystem("The editor is restarting; this session resumes afterwards.");
        _stoppedByUser = true;
        try
        {
            s.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }

        Settings.Save();
        return Task.CompletedTask;
    }

    private void OnClientActivity(McpClientInfo info)
    {
        if (info.IsEmbedded) return;
        ExternalClient = info;
        Board.LastExternalSeenUtc = DateTime.UtcNow;
    }

    private void ContributeProjectInfo(JsonObject info)
    {
        info["assistant"] = new JsonObject
        {
            ["embeddedSession"] = Session?.IsAlive == true,
            ["driver"]          = Driver.ToString().ToLowerInvariant(),
            ["claudeCode"]      = Install.Info?.Display,
        };
    }

    // -------------------------------------------------------------------------
    // Sessions
    // -------------------------------------------------------------------------

    /// <summary>Starts a session for the open project, resuming its stored session when asked and one exists.</summary>
    public void StartSession(bool resume = true, bool fork = false)
    {
        if (Disabled) return;

        var info = Install.Info;
        if (info == null)
        {
            Transcript.AddSystem("Claude Code is not available. " + ClaudeCodeLocator.InstallInstructions(OperatingSystem.IsWindows()), SystemSeverity.Warning);
            return;
        }

        if (_projectRoot == null)
        {
            Transcript.AddSystem("Open a project first; the assistant works inside a project folder.", SystemSeverity.Warning);
            return;
        }

        if (Session is { IsAlive: true })
        {
            _afterExit = () => StartSession(resume, fork);
            StopSession(quiet: true);
            return;
        }

        Session?.Dispose();

        string  root     = _projectRoot;
        var     record   = Settings.FindSession(root);
        string? resumeId = null;
        string? newId    = null;

        if (_resumeSessionId != null)
        {
            resumeId = _resumeSessionId;
            record ??= new SessionRecord { SessionId = resumeId };
            if (record.SessionId != resumeId) record.SessionId = resumeId;
            Settings.RememberSession(root, record);
        }
        else if (resume && record != null && !string.IsNullOrEmpty(record.SessionId))
        {
            resumeId = record.SessionId;
        }

        if (resumeId == null)
        {
            newId  = Guid.NewGuid().ToString();
            record = new SessionRecord { SessionId = newId, ClaudeVersion = info.Version?.ToString() };
            Settings.RememberSession(root, record);
        }

        _lastStartWasResume = resumeId != null;
        Record = record;

        var repo = Settings.IncludeEngineRepo ? _code.Repo : null;
        var options = new ClaudeLaunchOptions
        {
            ProjectName              = _projectName ?? Path.GetFileName(root),
            ProjectRoot              = root,
            McpUrl                   = _mcp.Url?.ToString() ?? $"http://127.0.0.1:{Settings.McpPort}/mcp/",
            McpToken                 = string.IsNullOrWhiteSpace(Settings.McpToken) ? null : Settings.McpToken,
            PermissionMode           = Settings.PermissionMode,
            Thinking                 = Settings.Thinking,
            Model                    = NullIfEmpty(Settings.Model),
            Effort                   = NullIfEmpty(Settings.Effort),
            MaxBudgetUsd             = Settings.MaxBudgetUsdPerSession,
            AddDirectories           = repo != null ? new[] { repo.Root } : Array.Empty<string>(),
            EngineRepoRoot           = repo?.Root,
            SessionId                = newId,
            ResumeSessionId          = resumeId,
            ForkSession              = fork && resumeId != null,
            IncludeProjectMcpServers = Settings.IncludeProjectMcpServers,
            DisplayName              = $"SexyBiscuit: {_projectName}",
            ExtraArgs                = Settings.ExtraArgs,
        };

        var argv = ClaudeArgvBuilder.Build(options);
        foreach (var flag in _droppedFlags) ClaudeArgvBuilder.RemoveFlag(argv, flag);

        var session = new ClaudeCodeSession(info, argv, root, resumeId ?? newId!, Transcript, Log);
        session.Initialized   += OnSessionInitialized;
        session.TurnCompleted += OnTurnCompleted;

        Session             = session;
        _sessionExitHandled = false;
        _stoppedByUser      = false;
        _lastProcessCost    = 0;

        Transcript.AddSystem(resumeId != null
            ? $"Resuming session {Short(resumeId)} with {info.Display}{(fork ? " (forked)" : "")}."
            : $"New session {Short(newId!)} with {info.Display}.");

        session.Start();

        if (_resumeMessage != null)
        {
            session.SendUserMessage(_resumeMessage, synthetic: true);
            _resumeMessage = null;
        }
        _resumeSessionId = null;

        foreach (var text in _carryOver) session.SendUserMessage(text);
        _carryOver.Clear();

        ConsoleLog.Add($"[Assistant] {(resumeId != null ? "Resumed" : "Started")} Claude Code session {Short(resumeId ?? newId!)}.", LogLevel.Info);
    }

    /// <summary>Starts over: the old session is stopped and forgotten for this project.</summary>
    public void NewSession()
    {
        if (_projectRoot == null) return;

        Settings.ForgetSession(_projectRoot);
        Record = null;
        Transcript.Clear();

        if (Session is { IsAlive: true })
        {
            _afterExit = () => StartSession(resume: false);
            StopSession(quiet: true);
        }
        else
        {
            StartSession(resume: false);
        }
    }

    /// <summary>Resumes the stored session after an exit or a manual stop.</summary>
    public void RestartSession() => StartSession(resume: true);

    /// <summary>Branches the stored session into a new one, keeping its history.</summary>
    public void DuplicateSession() => StartSession(resume: true, fork: true);

    /// <summary>Asks the process to exit. The panel keeps the transcript.</summary>
    public void StopSession(bool quiet = false)
    {
        var s = Session;
        if (s == null || !s.IsAlive) return;

        _stoppedByUser = true;
        if (!quiet) Transcript.AddSystem("Session stopped.");
        _ = s.StopAsync(TimeSpan.FromSeconds(1.5));
    }

    /// <summary>The Stop button: interrupts the turn, cancels every in-flight tool call and pending wait.</summary>
    public void Interrupt()
    {
        Session?.Interrupt();
        int cancelled = _mcp.RequestStopAll();
        Board.CancelAll(editorClosing: false);
        if (cancelled > 0) ConsoleLog.Add($"[Assistant] Stopped {cancelled} tool call(s).", LogLevel.Warning);
    }

    /// <summary>Text from the composer: to the embedded session, or to a waiting terminal session, or start a session.</summary>
    public void SendPrompt(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        if (Session is { IsAlive: true } s)
        {
            s.SendUserMessage(text);
            return;
        }

        if (Board.Driver == InteractionDriver.External)
        {
            Transcript.AddUser(text, Board.IsWaitingForPrompt ? UserEntryState.Sent : UserEntryState.Queued);
            Board.QueuePrompt(text);
            return;
        }

        if (Disabled || _projectRoot == null || Install.Info == null)
        {
            Transcript.AddUser(text, UserEntryState.Queued);
            Board.QueuePrompt(text);
            Transcript.AddSystem(Disabled
                ? "The embedded assistant is off (--no-assistant). A terminal Claude Code session picks this up when it calls wait_for_user."
                : _projectRoot == null
                    ? "Open a project first."
                    : "Claude Code was not found. " + ClaudeCodeLocator.InstallInstructions(OperatingSystem.IsWindows()), SystemSeverity.Warning);
            return;
        }

        _carryOver.Add(text);
        StartSession(resume: true);
    }

    public bool WithdrawQueued(UserEntry entry)
    {
        bool removed = Session?.WithdrawQueued(entry) == true || Board.WithdrawPrompt(entry.Text) || _carryOver.Remove(entry.Text);
        if (removed)
        {
            entry.Text += "  (withdrawn)";
            entry.State = UserEntryState.Acked;
            Transcript.Touch();
        }
        return removed;
    }

    public void AnswerQuestion(PendingQuestion question, string text, int? choiceIndex)
    {
        Board.Answer(question.Id, text, choiceIndex);
        Transcript.Touch();
    }

    public void ApplyPermissionMode(ClaudePermissionMode mode)
    {
        if (Settings.PermissionMode == mode) return;
        Settings.PermissionMode = mode;
        Settings.Save();
        Session?.SetPermissionMode(mode);
    }

    public void ApplyModel(string model)
    {
        if (Settings.Model == model) return;
        Settings.Model = model;
        Settings.Save();
        Session?.SetModel(model);
    }

    /// <summary>Re-runs binary detection, for the Locate/Re-detect buttons.</summary>
    public Task RedetectAsync() => Install.DetectAsync(Settings.ClaudePath);

    public void WriteProjectMcpConfig()
    {
        if (_projectRoot == null || _mcp.Url == null) return;
        try
        {
            string outcome = McpConfigWriter.WriteProjectConfig(_projectRoot, _mcp.Url.ToString(), Settings.McpToken);
            Transcript.AddSystem(outcome == "written" ? $"Wrote {Path.Combine(_projectRoot, ".mcp.json")}." : $".mcp.json {outcome}.");
        }
        catch (Exception ex)
        {
            Transcript.AddSystem($"Could not write .mcp.json: {ex.Message}", SystemSeverity.Error);
        }
    }

    public string? ConnectCommand => _mcp.Url == null ? null : McpConfigWriter.ConnectCommand(_mcp.Url.ToString());

    /// <summary>Where Claude Code keeps this project's session transcripts.</summary>
    public string? SessionFilePath
    {
        get
        {
            if (_projectRoot == null || Record == null) return null;
            string folder = new string(Path.GetFullPath(_projectRoot).Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            string home   = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(home, "projects", folder, Record.SessionId + ".jsonl");
        }
    }

    // -------------------------------------------------------------------------

    private void OnSessionInitialized(InitFrame init)
    {
        if (Record == null || _projectRoot == null) return;

        Record.ClaudeVersion = init.ClaudeCodeVersion ?? Record.ClaudeVersion;
        Record.Model         = init.Model ?? Record.Model;
        Record.LastUsedUtc   = DateTime.UtcNow;

        // A forked session gets a new id from the CLI; remember that one from now on.
        if (!string.IsNullOrEmpty(init.SessionId) && init.SessionId != Record.SessionId)
            Record.SessionId = init.SessionId;

        Settings.RememberSession(_projectRoot, Record);
    }

    private void OnTurnCompleted(ResultFrame result)
    {
        if (Record == null || _projectRoot == null || Session == null) return;

        double delta = Session.ProcessCostUsd - _lastProcessCost;
        _lastProcessCost = Session.ProcessCostUsd;

        Record.LifetimeCostUsd += Math.Max(0, delta);
        Record.Turns++;
        Record.LastUsedUtc = DateTime.UtcNow;
        Settings.RememberSession(_projectRoot, Record);
    }

    /// <summary>Called from <see cref="EditorApp.OnExiting"/>: releases waiters, stops the process, saves.</summary>
    public void Shutdown()
    {
        Board.CancelAll(editorClosing: true);

        if (Session is { IsAlive: true } s)
        {
            _stoppedByUser = true;
            try { s.StopAsync(TimeSpan.FromSeconds(1.5)).GetAwaiter().GetResult(); }
            catch (Exception) { }
        }

        Session?.Dispose();
        Session = null;
        Settings.Save();
    }

    public void Dispose()
    {
        Shutdown();
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;
    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static void Log(string message, bool isError) => ConsoleLog.Add(message, isError ? LogLevel.Error : LogLevel.Info);
}
