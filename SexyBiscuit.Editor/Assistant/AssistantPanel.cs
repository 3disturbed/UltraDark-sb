using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SexyBiscuit.Engine.Mcp;
using SexyBiscuit.Engine.Mcp.ClaudeCode;

namespace SexyBiscuit.Editor.Assistant;

/// <summary>
/// The Assistant window: header with state and Stop, Chat / Activity / Diagnostics tabs, the
/// composer, and the settings modal. All state it shows lives in <see cref="AssistantHost"/>.
/// </summary>
public sealed class AssistantPanel
{
    private static readonly Vector4 Red    = new(0.75f, 0.20f, 0.20f, 1f);
    private static readonly Vector4 Green  = new(0.35f, 0.75f, 0.40f, 1f);
    private static readonly Vector4 Amber  = Throbber.Working;
    private static readonly Vector4 Grey   = new(0.50f, 0.50f, 0.55f, 1f);
    private static readonly Vector4 Blue   = new(0.45f, 0.65f, 1.00f, 1f);
    private static readonly Vector4 Dim    = new(0.55f, 0.55f, 0.58f, 1f);

    private readonly AssistantHost       _host;
    private readonly TranscriptRenderer  _renderer = new();

    private string _composer = "";
    private bool   _focusComposer;
    private bool   _settingsRequested;
    private bool   _newSessionRequested;
    private string _activityFilter = "";
    private bool   _activityFollow = true;
    private long   _selectedActivity = -1;
    private int    _lastActivityVersion = -1;
    private IReadOnlyList<ActivityEntry> _activity = Array.Empty<ActivityEntry>();

    // Working copy for the settings modal.
    private string _sPath = "", _sModel = "", _sEffort = "", _sRepo = "", _sBudget = "", _sToken = "";
    private int    _sMode, _sThinking, _sWaitTimeout, _sPort;
    private bool   _sWriteMcp, _sIncludeRepo, _sIncludeProjectServers, _sAutoStart;
    private bool   _sLoaded;

    private static readonly string[] EffortNames = { "(default)", "low", "medium", "high", "max" };

    public AssistantPanel(AssistantHost host) => _host = host;

    /// <summary>F8: show the panel and put the cursor in the composer.</summary>
    public void RequestFocus()
    {
        EditorState.ShowAssistant = true;
        _focusComposer = true;
        ImGui.SetWindowFocus("Assistant###Assistant");
    }

    public void RequestSettings() => _settingsRequested = true;

    // -------------------------------------------------------------------------
    // Window
    // -------------------------------------------------------------------------

    public void Draw(ImGuiRenderer imGui)
    {
        if (!EditorState.ShowAssistant) return;

        var transcript = _host.Transcript;
        string title = transcript.UnreadCount > 0 ? $"Assistant ({transcript.UnreadCount})###Assistant" : "Assistant###Assistant";

        if (EditorState.DockAssistantIntoDetails && EditorState.DetailsDockId != 0)
        {
            EditorState.DockAssistantIntoDetails = false;
            ImGui.SetNextWindowDockID(EditorState.DetailsDockId, ImGuiCond.Always);
            ImGui.SetNextWindowFocus();
        }

        // A sane size when the window floats for the first time; docked windows ignore this.
        ImGui.SetNextWindowSize(new Vector2(460f, 560f), ImGuiCond.FirstUseEver);

        bool open = true;
        if (!ImGui.Begin(title, ref open))
        {
            ImGui.End();
            if (!open) EditorState.ShowAssistant = false;
            return;
        }

        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) transcript.MarkRead();

        DrawHeader();

        if (ImGui.BeginTabBar("##AssistantTabs"))
        {
            if (ImGui.BeginTabItem("Chat"))
            {
                DrawChat(imGui);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Activity"))
            {
                DrawActivity();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Diagnostics"))
            {
                DrawDiagnostics();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
        if (!open) EditorState.ShowAssistant = false;
    }

    // -------------------------------------------------------------------------
    // Header
    // -------------------------------------------------------------------------

    private void DrawHeader()
    {
        var session = _host.Session;
        bool alive  = session?.IsAlive == true;
        bool busy   = _host.IsBusy;

        var dot = busy ? Amber : alive ? Green : session is { State: SessionState.Exited } ? Red : _host.Driver == InteractionDriver.External ? Blue : Grey;
        if (busy)
        {
            float pulse = 0.65f + 0.35f * MathF.Sin((float)ImGui.GetTime() * 5f);
            dot = new Vector4(dot.X, dot.Y, dot.Z, pulse);
        }

        var cursor = ImGui.GetCursorScreenPos();
        float lineHeight = ImGui.GetTextLineHeight();
        ImGui.GetWindowDrawList().AddCircleFilled(new Vector2(cursor.X + 6f, cursor.Y + lineHeight / 2f + 1f), 5f, ImGui.ColorConvertFloat4ToU32(dot));
        ImGui.Dummy(new Vector2(14f, lineHeight));
        ImGui.SameLine();
        ImGui.TextUnformatted(_host.StateText);

        if (alive || _host.Record != null)
        {
            string model = session?.Model ?? _host.Record?.Model ?? "";
            if (model.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("· " + model);
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"· ${(session?.ProcessCostUsd ?? 0):F2}");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"This process: ${(session?.ProcessCostUsd ?? 0):F4}\nThis project, all sessions: ${_host.LifetimeCostUsd:F4}\n" +
                                 $"Tokens in/out: {session?.InputTokens ?? 0:N0} / {session?.OutputTokens ?? 0:N0}\nTurns: {session?.Turns ?? 0}");
            }
        }

        // Controls on their own row, right-aligned, so a narrow dock never overlaps the state text.
        float buttonWidth = 62f;
        int buttons = 3 + (alive ? 0 : 1);
        float total = buttons * buttonWidth + (buttons - 1) * ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, ImGui.GetContentRegionAvail().X - total));

        ImGui.BeginDisabled(!busy);
        ImGui.PushStyleColor(ImGuiCol.Button, Red);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1f));
        if (ImGui.Button("Stop", new Vector2(buttonWidth, 0f))) _host.Interrupt();
        ImGui.PopStyleColor(2);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Interrupt the current turn and cancel running tool calls (Shift+F8)");

        if (!alive)
        {
            ImGui.SameLine();
            bool canStart = _host.CanStart;
            ImGui.BeginDisabled(!canStart);
            string label = session is { State: SessionState.Exited } || _host.Record != null ? "Resume" : "Start";
            if (ImGui.Button(label, new Vector2(buttonWidth, 0f))) _host.RestartSession();
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(canStart ? "Start the embedded Claude Code session for this project" : "Needs an open project and a working claude binary");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(_host.Disabled || _host.ProjectRoot == null || _host.Install.Info == null);
        if (ImGui.Button("New", new Vector2(buttonWidth, 0f))) _newSessionRequested = true;
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Start a fresh session; the current conversation is closed");

        ImGui.SameLine();
        if (ImGui.Button("Settings", new Vector2(buttonWidth, 0f))) _settingsRequested = true;

        // Second line: the external picture, when relevant.
        var external = _host.ExternalClient;
        if (_host.Driver == InteractionDriver.External || (external != null && DateTime.UtcNow - external.LastSeenUtc < TimeSpan.FromMinutes(10)))
        {
            string age = external == null ? "" : $" · last request {Math.Max(0, (DateTime.UtcNow - external.LastSeenUtc).TotalSeconds):F0} s ago";
            string wait = _host.Board.IsWaitingForPrompt ? " · waiting for your message" : _host.Board.PendingPromptCount > 0 ? $" · {_host.Board.PendingPromptCount} message(s) queued" : "";
            ImGui.TextColored(Blue, $"Terminal: {external?.Name ?? "Claude Code"} {external?.Version}{age}{wait}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy connect command") && _host.ConnectCommand is { } cmd) DesktopShell.SetClipboardText(cmd);
            ImGui.SameLine();
            if (ImGui.SmallButton("Write .mcp.json")) _host.WriteProjectMcpConfig();
        }

        ImGui.Separator();
    }

    // -------------------------------------------------------------------------
    // Chat
    // -------------------------------------------------------------------------

    private void DrawChat(ImGuiRenderer imGui)
    {
        var install = _host.Install;
        var session = _host.Session;

        if (!_host.Disabled && install.HasDetected && install.Info == null && session == null)
            DrawNotInstalled();
        else if (session is { State: SessionState.Exited, NotLoggedIn: true })
            DrawNotLoggedIn(session);

        DrawQuickActions();

        // Three lines of composer normally; in a short window the transcript keeps at least ~100 px
        // and the composer shrinks to one line, so replies are never squeezed out of sight.
        float lineHeight   = ImGui.GetTextLineHeight();
        float oneLine      = lineHeight + ImGui.GetStyle().FramePadding.Y * 2f + 6f;
        float threeLines   = lineHeight * 3f + ImGui.GetStyle().FramePadding.Y * 2f + 6f;
        float hintHeight   = ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y * 2f;
        float composerHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y - hintHeight - 100f, oneLine, threeLines);
        float footer = composerHeight + hintHeight;

        _renderer.Draw(_host, imGui, footer);
        DrawComposer(composerHeight);
    }

    private void DrawNotInstalled()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.18f, 0.15f, 0.12f, 1f));
        ImGui.BeginChild("##notinstalled", new Vector2(0f, 0f), ImGuiChildFlags.Border | ImGuiChildFlags.AutoResizeY, ImGuiWindowFlags.None);
        ImGui.PopStyleColor();

        ImGui.TextColored(Amber, "Claude Code was not found");
        ImGui.TextWrapped("The embedded assistant runs the Claude Code command-line tool. Install it, or point the editor at an existing binary. " +
                          "A Claude Code running in a terminal can still drive this editor: it connects through the project's .mcp.json.");
        ImGui.TextWrapped(ClaudeCodeLocator.InstallInstructions(OperatingSystem.IsWindows()));

        if (ImGui.Button("Locate...")) LocateBinary();
        ImGui.SameLine();
        if (ImGui.Button("Re-detect")) _host.RedetectAsync();
        ImGui.SameLine();
        if (ImGui.Button("Copy install command"))
            DesktopShell.SetClipboardText(OperatingSystem.IsWindows() ? "irm https://claude.ai/install.ps1 | iex" : "curl -fsSL https://claude.ai/install.sh | bash");

        if (_host.Install.Verdicts.Count > 0 && ImGui.TreeNodeEx("Searched locations", ImGuiTreeNodeFlags.None))
        {
            foreach (var v in _host.Install.Verdicts)
                ImGui.TextDisabled($"{v.Candidate.Path} ({v.Candidate.Source}): {v.Detail}");
            ImGui.TreePop();
        }

        ImGui.EndChild();
    }

    private void DrawNotLoggedIn(ClaudeCodeSession session)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.18f, 0.15f, 0.12f, 1f));
        ImGui.BeginChild("##notloggedin", new Vector2(0f, 0f), ImGuiChildFlags.Border | ImGuiChildFlags.AutoResizeY, ImGuiWindowFlags.None);
        ImGui.PopStyleColor();

        ImGui.TextColored(Amber, "Claude Code is not signed in");
        ImGui.TextWrapped("The Claude Code binary the editor runs needs its own one-time sign-in; the Claude desktop app's sign-in does not carry over. " +
                          "Open a terminal with it, type /login and follow the instructions (or set ANTHROPIC_API_KEY), then press Resume here.");
        string command = OperatingSystem.IsWindows() ? $"\"{session.Executable}\"" : $"'{session.Executable}'";
        if (ImGui.Button("Open a terminal to sign in"))
        {
            if (!ClaudeCodeInstall.OpenSignInTerminal(session.Executable))
                _host.Transcript.AddSystem("Could not open a terminal automatically. Run this yourself, then type /login: " + command, SystemSeverity.Warning);
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy the command")) DesktopShell.SetClipboardText(command);
        ImGui.SameLine();
        if (ImGui.Button("Resume")) _host.RestartSession();
        ImGui.TextDisabled(command);

        ImGui.EndChild();
    }

    private void DrawQuickActions()
    {
        bool enabled = _host.Session is { IsAlive: true } || _host.CanStart || _host.Driver == InteractionDriver.External;
        ImGui.BeginDisabled(!enabled);
        if (ImGui.SmallButton("Describe the scene")) _host.SendPrompt("Describe the current scene: what is in it, what is missing for it to play, and what you would improve first.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Fix build errors")) _host.SendPrompt("Build the C# project and fix every compile error, then reload the game code.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Undo last change")) _host.SendPrompt("Undo the last scene change you made and confirm what was reverted.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Save")) _host.SendPrompt("Save the scene now.");
        ImGui.EndDisabled();
    }

    private void DrawComposer(float height)
    {
        var pending = _host.Board.PendingQuestion;
        var driver  = _host.Driver;

        string placeholder = pending != null ? "Answer Claude's question..."
            : driver == InteractionDriver.External ? "Message the terminal session (it receives this on its next wait_for_user)..."
            : _host.Session is { IsAlive: true } s && s.IsBusy ? "Type to queue a message for when this turn ends..."
            : "Tell Claude what to build...   (Enter sends, Ctrl+Enter for a new line)";

        if (_focusComposer)
        {
            ImGui.SetKeyboardFocusHere();
            _focusComposer = false;
        }

        float buttonWidth = 64f;
        bool submit = ImGui.InputTextMultiline("##Composer", ref _composer, 16384,
            new Vector2(-(buttonWidth + ImGui.GetStyle().ItemSpacing.X), height),
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CtrlEnterForNewLine);

        if (_composer.Length == 0)
        {
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var pad = ImGui.GetStyle().FramePadding;
            var drawList = ImGui.GetWindowDrawList();
            drawList.PushClipRect(min, max, true);
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(min.X + pad.X + 2f, min.Y + pad.Y), ImGui.ColorConvertFloat4ToU32(Dim), placeholder, max.X - min.X - pad.X * 2f - 4f);
            drawList.PopClipRect();
        }

        ImGui.SameLine();
        string buttonLabel = pending != null ? "Answer" : _host.Session is { IsAlive: true } busySession && busySession.IsBusy ? "Queue" : "Send";
        if (ImGui.Button(buttonLabel, new Vector2(buttonWidth, height))) submit = true;

        if (submit && !string.IsNullOrWhiteSpace(_composer))
        {
            string text = _composer.Trim();
            _composer = "";
            if (pending is { State: QuestionState.Pending }) _host.AnswerQuestion(pending, text, null);
            else _host.SendPrompt(text);
            _renderer.JumpToLatest();
            _focusComposer = true;
        }

        string hint = driver switch
        {
            InteractionDriver.Embedded => "Embedded session · Enter sends · Shift+F8 stops",
            InteractionDriver.External => "A terminal Claude Code is driving · your text answers its wait_for_user",
            _                          => _host.Disabled ? "Embedded assistant off (--no-assistant)" : "F8 focuses this box · Ctrl+Z undoes scene changes",
        };
        ImGui.TextDisabled(hint);
    }

    // -------------------------------------------------------------------------
    // Activity
    // -------------------------------------------------------------------------

    private void DrawActivity()
    {
        var log = _host.Mcp.Activity;
        if (log.Version != _lastActivityVersion)
        {
            _lastActivityVersion = log.Version;
            _activity = log.Snapshot();
        }

        ImGui.SetNextItemWidth(200f);
        ImGui.InputTextWithHint("##activityFilter", "filter tool or arguments", ref _activityFilter, 256);
        ImGui.SameLine();
        ImGui.Checkbox("Follow", ref _activityFollow);
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear")) { log.Clear(); _selectedActivity = -1; }
        ImGui.SameLine();
        ImGui.TextDisabled($"{_activity.Count} call(s), {log.RunningCount} running");

        float detailHeight = _selectedActivity >= 0 ? 120f : 0f;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;

        if (ImGui.BeginTable("##activity", 6, flags, new Vector2(0f, -detailHeight - (detailHeight > 0 ? ImGui.GetStyle().ItemSpacing.Y : 0f))))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Time",      ImGuiTableColumnFlags.WidthFixed, 64f);
            ImGui.TableSetupColumn("Client",    ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Tool",      ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Arguments", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("Result",    ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("ms",        ImGuiTableColumnFlags.WidthFixed, 52f);
            ImGui.TableHeadersRow();

            foreach (var entry in _activity)
            {
                if (_activityFilter.Length > 0
                    && !entry.Tool.Contains(_activityFilter, StringComparison.OrdinalIgnoreCase)
                    && !entry.Arguments.Contains(_activityFilter, StringComparison.OrdinalIgnoreCase)) continue;

                ImGui.TableNextRow();
                var colour = entry.State switch
                {
                    ActivityState.Failed    => new Vector4(1f, 0.45f, 0.45f, 1f),
                    ActivityState.Cancelled => Amber,
                    ActivityState.Running   => Blue,
                    _                       => new Vector4(0.85f, 0.85f, 0.85f, 1f),
                };
                ImGui.PushStyleColor(ImGuiCol.Text, colour);

                ImGui.TableNextColumn();
                bool selected = entry.Seq == _selectedActivity;
                if (ImGui.Selectable(entry.StartedUtc.ToLocalTime().ToString("HH:mm:ss") + $"##row{entry.Seq}", selected, ImGuiSelectableFlags.SpanAllColumns))
                    _selectedActivity = selected ? -1 : entry.Seq;

                ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Client ?? "terminal");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Tool);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Truncate(entry.Arguments, 120));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.State == ActivityState.Running ? "running..." : Truncate(entry.Summary, 120));
                ImGui.TableNextColumn();
                if (entry.State == ActivityState.Running) Throbber.Draw(radius: 5f, thickness: 2f, colour: Throbber.Working);
                else ImGui.TextUnformatted(entry.Duration.TotalMilliseconds.ToString("F0"));

                ImGui.PopStyleColor();
            }

            if (_activityFollow && _activity.Count > 0 && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 40f) ImGui.SetScrollHereY(1f);
            ImGui.EndTable();
        }

        if (_selectedActivity >= 0)
        {
            var entry = _activity.FirstOrDefault(e => e.Seq == _selectedActivity);
            ImGui.BeginChild("##activityDetail", new Vector2(0f, detailHeight), ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar);
            if (entry != null)
            {
                ImGui.TextUnformatted($"{entry.Label}  ·  {entry.State}  ·  {entry.Duration.TotalMilliseconds:F0} ms  ·  {(entry.Mutating ? "mutating" : "read-only")}");
                ImGui.TextDisabled("Arguments: " + (entry.Arguments.Length == 0 ? "(none)" : entry.Arguments));
                ImGui.TextWrapped("Result: " + (entry.Summary.Length == 0 ? "(none)" : entry.Summary));
            }
            ImGui.EndChild();
        }
    }

    // -------------------------------------------------------------------------
    // Diagnostics
    // -------------------------------------------------------------------------

    private void DrawDiagnostics()
    {
        ImGui.BeginChild("##diagnostics", new Vector2(0f, 0f), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);

        var install = _host.Install;
        var session = _host.Session;
        var mcp     = _host.Mcp;

        Section("Claude Code");
        if (install.Info is { } info)
        {
            ImGui.TextUnformatted($"{info.Display}: {info.Path} ({info.Source})");
        }
        else
        {
            ImGui.TextColored(Amber, install.IsDetecting ? "Looking for a binary..." : "No working binary found.");
        }
        if (ImGui.SmallButton("Re-detect")) _host.RedetectAsync();
        ImGui.SameLine();
        if (ImGui.SmallButton("Locate...")) LocateBinary();
        if (install.Verdicts.Count > 0 && ImGui.TreeNodeEx("Candidates", ImGuiTreeNodeFlags.None))
        {
            foreach (var v in install.Verdicts)
                ImGui.TextDisabled($"[{(v.Works ? "ok" : "--")}] {v.Candidate.Path} ({v.Candidate.Source}): {v.Detail}");
            ImGui.TreePop();
        }

        Section("MCP server");
        ImGui.TextUnformatted($"{mcp.Status} at {mcp.Url?.ToString() ?? "(not listening)"}{(mcp.StatusMessage != null ? ": " + mcp.StatusMessage : "")}");
        ImGui.TextDisabled($"{mcp.Registry.Tools.Count} tools · {mcp.Server.RequestsServed} requests served");
        foreach (var client in mcp.Server.Clients)
            ImGui.TextDisabled($"client: {client.Name} {client.Version}{(client.IsEmbedded ? " (embedded)" : "")} · {client.RequestCount} requests · last {Math.Max(0, (DateTime.UtcNow - client.LastSeenUtc).TotalSeconds):F0} s ago");
        if (_host.ConnectCommand is { } connect)
        {
            ImGui.TextDisabled(connect);
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy##connect")) DesktopShell.SetClipboardText(connect);
        }
        if (_host.ProjectRoot != null && ImGui.SmallButton("Write .mcp.json")) _host.WriteProjectMcpConfig();

        Section("Session");
        if (session == null)
        {
            ImGui.TextDisabled(_host.Record != null ? $"No process. Stored session {_host.Record.SessionId} (last used {_host.Record.LastUsedUtc.ToLocalTime():g}, ${_host.Record.LifetimeCostUsd:F4}, {_host.Record.Turns} turns)." : "No session yet.");
        }
        else
        {
            ImGui.TextUnformatted($"State: {session.State}{(session.SubStatus != null ? ": " + session.SubStatus : "")}");
            ImGui.TextUnformatted($"Session id: {session.SessionId}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy##sid")) DesktopShell.SetClipboardText(session.SessionId);
            ImGui.TextDisabled($"Working directory: {session.WorkingDirectory}");
            ImGui.TextDisabled($"Started {session.StartedUtc.ToLocalTime():T} · pid exit {(session.ExitCode?.ToString() ?? "n/a")} · turns {session.Turns} · ${session.ProcessCostUsd:F4}");
            if (session.ExitReason != null) ImGui.TextColored(Amber, session.ExitReason);

            if (session.Init is { } init)
            {
                ImGui.TextDisabled($"init: Claude Code {init.ClaudeCodeVersion}, model {init.Model}, permission mode {init.PermissionMode}, api key source {init.ApiKeySource ?? "?"}, {init.Tools.Count} tools");
                ImGui.TextDisabled("mcp_servers: " + (init.McpServers.Count == 0 ? "(none)" : string.Join(", ", init.McpServers.Select(s => $"{s.Name}={s.Status}"))));
            }

            if (ImGui.TreeNodeEx("Command line", ImGuiTreeNodeFlags.None))
            {
                ImGui.PushTextWrapPos(0f);
                ImGui.TextUnformatted(session.ArgvDisplay);
                ImGui.PopTextWrapPos();
                if (ImGui.SmallButton("Copy##argv")) DesktopShell.SetClipboardText(session.ArgvDisplay);
                ImGui.TreePop();
            }

            var stderr = session.StderrTail;
            if (ImGui.TreeNodeEx($"stderr ({stderr.Count} lines)", ImGuiTreeNodeFlags.None))
            {
                foreach (var line in stderr) ImGui.TextUnformatted(line);
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx($"Frames ({session.FrameLog.Count})", ImGuiTreeNodeFlags.None))
            {
                foreach (var line in session.FrameLog) ImGui.TextUnformatted(line);
                ImGui.TreePop();
            }
        }

        if (_host.SessionFilePath is { } sessionFile)
        {
            ImGui.TextDisabled(sessionFile);
            if (File.Exists(sessionFile))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Reveal")) DesktopShell.RevealInFileManager(sessionFile);
            }
        }

        Section("Interaction");
        ImGui.TextDisabled($"Driver: {_host.Driver} · prompts queued: {_host.Board.PendingPromptCount} · waiting: {_host.Board.IsWaitingForPrompt} · questions pending: {_host.Board.PendingQuestions.Count}");
        ImGui.TextDisabled($"Settings file: {AssistantSettings.FilePath}");

        ImGui.EndChild();
    }

    private static void Section(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.85f, 1f), title);
        ImGui.Separator();
    }

    private void LocateBinary()
    {
        string? start = _host.Install.Info != null ? Path.GetDirectoryName(_host.Install.Info.Path) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        FileDialog.OpenFile("Locate the claude binary", start, Array.Empty<string>(), path =>
        {
            _host.Settings.ClaudePath = path;
            _host.Settings.Save();
            _host.RedetectAsync();
        });
    }

    // -------------------------------------------------------------------------
    // Modals
    // -------------------------------------------------------------------------

    /// <summary>Draw after every panel so the modals sit on top. Also drives the New-session confirmation.</summary>
    public void DrawModals()
    {
        if (_settingsRequested)
        {
            _settingsRequested = false;
            LoadSettingsCopy();
            ImGui.OpenPopup("Assistant Settings");
        }

        if (_newSessionRequested)
        {
            _newSessionRequested = false;
            ImGui.OpenPopup("New session?");
        }

        DrawSettingsModal();
        DrawNewSessionModal();
    }

    private void DrawNewSessionModal()
    {
        bool open = true;
        ImGui.SetNextWindowSize(new Vector2(420f, 0f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("New session?", ref open, ImGuiWindowFlags.NoCollapse)) return;

        ImGui.TextWrapped("Start a fresh session for this project? The current conversation is closed; Claude Code keeps its transcript on disk, but the panel starts empty.");
        ImGui.Spacing();
        if (ImGui.Button("Start fresh", new Vector2(120f, 0f)))
        {
            _host.NewSession();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120f, 0f))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void LoadSettingsCopy()
    {
        var s = _host.Settings;
        _sPath      = s.ClaudePath ?? "";
        _sModel     = s.Model;
        _sEffort    = s.Effort;
        _sRepo      = s.EngineRepoPath ?? "";
        _sBudget    = s.MaxBudgetUsdPerSession?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        _sToken     = s.McpToken ?? "";
        _sMode      = (int)s.PermissionMode;
        _sThinking  = (int)s.Thinking;
        _sWaitTimeout = s.WaitForUserDefaultTimeoutSeconds;
        _sPort      = s.McpPort;
        _sWriteMcp  = s.WriteProjectMcpConfig;
        _sIncludeRepo = s.IncludeEngineRepo;
        _sIncludeProjectServers = s.IncludeProjectMcpServers;
        _sAutoStart = s.AutoStartOnProjectOpen;
        _sLoaded    = true;
    }

    private void DrawSettingsModal()
    {
        bool open = true;
        ImGui.SetNextWindowSize(new Vector2(600f, 0f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("Assistant Settings", ref open, ImGuiWindowFlags.NoCollapse)) return;
        if (!_sLoaded) LoadSettingsCopy();

        var install = _host.Install;

        Section("Claude Code");
        ImGui.TextDisabled(install.Info != null ? $"Detected: {install.Info.Display} at {install.Info.Path} ({install.Info.Source})" : "No working binary detected.");
        ImGui.SetNextItemWidth(-160f);
        ImGui.InputTextWithHint("##claudePath", "binary path (blank = search PATH and known locations)", ref _sPath, 1024);
        ImGui.SameLine();
        if (ImGui.Button("Locate...##s")) LocateBinary();
        ImGui.SameLine();
        if (ImGui.Button("Re-detect##s")) _host.RedetectAsync();

        Section("Behaviour");
        ImGui.TextDisabled("What Claude may do without asking:");
        RadioMode(ClaudePermissionMode.Autonomous,  "Autonomous (recommended)", "No prompts. The activity log and the Stop button are the safety net.");
        RadioMode(ClaudePermissionMode.AcceptEdits, "Accept edits",             "File edits inside the project go through; shell commands and the like ask first.");
        RadioMode(ClaudePermissionMode.Ask,         "Ask",                      "Every non-read tool asks in the panel. Editor tools are always allowed.");
        RadioMode(ClaudePermissionMode.Auto,        "Auto",                     "Claude Code's own classifier decides what needs a prompt.");

        ImGui.SetNextItemWidth(220f);
        ImGui.InputTextWithHint("Model", "blank = default (e.g. opus, sonnet, haiku)", ref _sModel, 128);

        int effortIndex = Math.Max(0, Array.IndexOf(EffortNames, string.IsNullOrEmpty(_sEffort) ? "(default)" : _sEffort));
        ImGui.SetNextItemWidth(220f);
        if (ImGui.Combo("Effort", ref effortIndex, EffortNames, EffortNames.Length)) _sEffort = effortIndex == 0 ? "" : EffortNames[effortIndex];

        bool thinking = _sThinking == (int)ClaudeThinkingDisplay.Summarized;
        if (ImGui.Checkbox("Show thinking summaries", ref thinking)) _sThinking = thinking ? (int)ClaudeThinkingDisplay.Summarized : (int)ClaudeThinkingDisplay.Omitted;

        Section("Limits");
        ImGui.SetNextItemWidth(120f);
        ImGui.InputTextWithHint("Max spend per session (USD)", "none", ref _sBudget, 16);
        ImGui.SetNextItemWidth(120f);
        ImGui.InputInt("wait_for_user default timeout (s)", ref _sWaitTimeout);
        _sWaitTimeout = Math.Clamp(_sWaitTimeout, 10, 3600);

        Section("Project");
        ImGui.Checkbox("Start a session when a project opens", ref _sAutoStart);
        ImGui.Checkbox("Write the project's .mcp.json (lets a terminal Claude Code connect)", ref _sWriteMcp);
        ImGui.Checkbox("Give the session access to the engine source", ref _sIncludeRepo);
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##repo", "engine repository (blank = find it from the editor binary)", ref _sRepo, 1024);
        ImGui.Checkbox("Also load the project's own MCP servers", ref _sIncludeProjectServers);

        Section("MCP server (takes effect after the editor restarts)");
        ImGui.SetNextItemWidth(120f);
        ImGui.InputInt("Port", ref _sPort);
        ImGui.SetNextItemWidth(300f);
        ImGui.InputTextWithHint("Bearer token", "blank = none (loopback only)", ref _sToken, 256, ImGuiInputTextFlags.Password);

        Section("Sessions");
        if (_host.Settings.Sessions.Count == 0) ImGui.TextDisabled("None yet.");
        string? forget = null;
        foreach (var (root, record) in _host.Settings.Sessions.OrderByDescending(kv => kv.Value.LastUsedUtc).Take(12))
        {
            ImGui.TextDisabled($"{Path.GetFileName(root)} · {record.SessionId[..Math.Min(8, record.SessionId.Length)]} · {record.LastUsedUtc.ToLocalTime():g} · ${record.LifetimeCostUsd:F2} · {record.Turns} turns");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Forget##{root}")) forget = root;
        }
        if (forget != null) _host.Settings.Sessions.Remove(forget);

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Save", new Vector2(120f, 0f)))
        {
            SaveSettingsCopy();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120f, 0f))) ImGui.CloseCurrentPopup();
        ImGui.SameLine();
        ImGui.TextDisabled(AssistantSettings.FilePath);

        ImGui.EndPopup();
    }

    private void RadioMode(ClaudePermissionMode mode, string label, string explanation)
    {
        if (ImGui.RadioButton(label, _sMode == (int)mode)) _sMode = (int)mode;
        ImGui.SameLine();
        ImGui.TextDisabled(explanation);
    }

    private void SaveSettingsCopy()
    {
        var s = _host.Settings;
        bool pathChanged = (s.ClaudePath ?? "") != _sPath;

        s.ClaudePath                = string.IsNullOrWhiteSpace(_sPath) ? null : _sPath.Trim();
        s.Effort                    = _sEffort;
        s.Thinking                  = (ClaudeThinkingDisplay)_sThinking;
        s.EngineRepoPath            = string.IsNullOrWhiteSpace(_sRepo) ? null : _sRepo.Trim();
        s.MaxBudgetUsdPerSession    = double.TryParse(_sBudget, NumberStyles.Float, CultureInfo.InvariantCulture, out double budget) && budget > 0 ? budget : null;
        s.McpToken                  = string.IsNullOrWhiteSpace(_sToken) ? null : _sToken.Trim();
        s.WaitForUserDefaultTimeoutSeconds = _sWaitTimeout;
        s.McpPort                   = Math.Clamp(_sPort, 1024, 65535);
        s.WriteProjectMcpConfig     = _sWriteMcp;
        s.IncludeEngineRepo         = _sIncludeRepo;
        s.IncludeProjectMcpServers  = _sIncludeProjectServers;
        s.AutoStartOnProjectOpen    = _sAutoStart;

        // Mode and model apply to the running session at once.
        _host.ApplyPermissionMode((ClaudePermissionMode)_sMode);
        _host.ApplyModel(_sModel.Trim());
        s.Save();

        if (pathChanged) _host.RedetectAsync();
        _sLoaded = false;
    }

    private static string Truncate(string text, int max)
    {
        text = text.Replace('\n', ' ');
        return text.Length <= max ? text : text[..(max - 1)] + "...";
    }
}
