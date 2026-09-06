using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SexyBiscuit.Engine.Mcp.ClaudeCode;

// -------------------------------------------------------------------------
// Entries
// -------------------------------------------------------------------------

public enum EntryKind
{
    User,
    Assistant,
    Thinking,
    ToolCall,
    Question,
    Permission,
    System,
    Result,
}

/// <summary>One row of the Assistant panel's chat.</summary>
public abstract class TranscriptEntry
{
    public long     Id         { get; internal set; }
    public DateTime CreatedUtc { get; internal set; } = DateTime.UtcNow;
    public abstract EntryKind Kind { get; }
}

public enum UserEntryState
{
    /// <summary>Waiting locally for the session to be ready.</summary>
    Queued,

    /// <summary>Written to the CLI.</summary>
    Sent,

    /// <summary>The CLI echoed it back.</summary>
    Acked,
}

public sealed class UserEntry : TranscriptEntry
{
    public override EntryKind Kind => EntryKind.User;
    public string         Text        { get; set; } = "";
    public UserEntryState State       { get; set; }
    public bool           IsSynthetic { get; set; }
}

public sealed class AssistantTextEntry : TranscriptEntry
{
    public override EntryKind Kind => EntryKind.Assistant;
    public string  Text       { get; set; } = "";
    public bool    Streaming  { get; set; }
    public string? MessageId  { get; set; }
    public int     BlockIndex { get; set; }

    /// <summary>Came through the <c>say</c> tool rather than the session's own text.</summary>
    public bool ViaSay { get; set; }
}

public sealed class ThinkingEntry : TranscriptEntry
{
    public override EntryKind Kind => EntryKind.Thinking;
    public string  Text       { get; set; } = "";
    public bool    Streaming  { get; set; }
    public string? MessageId  { get; set; }
    public int     BlockIndex { get; set; }
}

public enum ToolCallStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed class ToolCallEntry : TranscriptEntry
{
    /// <summary>Results longer than this are truncated for display.</summary>
    public const int MaxResultChars = 64 * 1024;

    public override EntryKind Kind => EntryKind.ToolCall;
    public string         ToolUseId     { get; set; } = "";
    public string         Name          { get; set; } = "";
    public string         Label         { get; set; } = "";
    public StringBuilder  InputJson     { get; } = new();
    public bool           InputComplete { get; set; }
    public string         ArgsCompact   { get; set; } = "";
    public ToolCallStatus Status        { get; set; } = ToolCallStatus.Running;
    public TimeSpan       Duration      { get; set; }
    public string         ResultSummary { get; set; } = "";
    public string         ResultFull    { get; set; } = "";
    public bool           ResultIsImage { get; set; }
    public string?        ImageBase64   { get; set; }
    public string?        ImageMediaType { get; set; }
    public string?        MessageId     { get; set; }
    public int            BlockIndex    { get; set; }

    /// <summary>The result's full length before <see cref="MaxResultChars"/> capped the display copy: what the model read.</summary>
    public long           ResultChars   { get; set; }

    /// <summary>Decoded size of an image result, in bytes.</summary>
    public int            ResultImageBytes { get; set; }

    /// <summary>The input parsed, when complete and valid.</summary>
    public JsonElement? Input
    {
        get
        {
            if (InputJson.Length == 0) return null;
            try { return JsonDocument.Parse(InputJson.ToString()).RootElement.Clone(); }
            catch (JsonException) { return null; }
        }
    }
}

public sealed class QuestionEntry : TranscriptEntry
{
    public override EntryKind Kind => EntryKind.Question;
    public PendingQuestion Question { get; init; } = null!;
    public string          Text     => Question.Text;
    public QuestionState   State    => Question.State;
}

public enum PermissionDecision
{
    Pending,
    Allowed,
    AlwaysAllowed,
    Denied,
    Withdrawn,
    AutoDenied,
}

public sealed class PermissionEntry : TranscriptEntry
{
    public override EntryKind Kind => EntryKind.Permission;
    public string       RequestId     { get; set; } = "";
    public string       ToolName      { get; set; } = "";
    public string       Title         { get; set; } = "";
    public string       Description   { get; set; } = "";
    public string       InputJson     { get; set; } = "";
    public JsonElement? Input         { get; set; }
    public JsonElement? Suggestions   { get; set; }
    public bool         DefaultToNo   { get; set; }
    public bool         SuppressAlwaysAllow { get; set; }
    public PermissionDecision Decision { get; set; }
}

public enum SystemSeverity
{
    Info,
    Warning,
    Error,
}

public sealed class SystemEntry : TranscriptEntry
{
    public override EntryKind Kind => EntryKind.System;
    public string         Text     { get; set; } = "";
    public SystemSeverity Severity { get; set; }
}

public sealed class ResultEntry : TranscriptEntry
{
    public override EntryKind Kind => EntryKind.Result;
    public double  TotalCostUsd { get; set; }
    public int     NumTurns     { get; set; }
    public long    DurationMs   { get; set; }
    public bool    IsError      { get; set; }
    public string? Subtype      { get; set; }
    public string? Text         { get; set; }
}

// -------------------------------------------------------------------------
// Transcript
// -------------------------------------------------------------------------

/// <summary>
/// The chat as the panel shows it, built by applying stream-json frames. Streaming deltas are
/// coalesced into one entry per content block; the final <c>assistant</c> frame replaces the
/// streamed text authoritatively; a <c>tool_result</c> closes the matching tool call. Main
/// thread only.
/// </summary>
public sealed class Transcript
{
    private readonly List<TranscriptEntry> _entries = new();
    private long _nextId;
    private string? _currentMessageId;

    public int MaxEntries { get; set; } = 2000;

    public IReadOnlyList<TranscriptEntry> Entries => _entries;

    /// <summary>Bumps on every change, so a renderer can cache.</summary>
    public int Version { get; private set; }

    /// <summary>Entries added since <see cref="MarkRead"/>, excluding the user's own.</summary>
    public int UnreadCount { get; private set; }

    /// <summary>What the session is doing right now, for the header and the viewport banner. Null when idle.</summary>
    public string? BusyStatus { get; private set; }

    /// <summary>The last API-level error the CLI reported in an assistant frame ("Not logged in", rate limits...).</summary>
    public string? LastApiError { get; private set; }

    public event Action<TranscriptEntry>? EntryAdded;

    public void MarkRead() => UnreadCount = 0;

    public void SetBusy(string? status)
    {
        if (BusyStatus == status) return;
        BusyStatus = status;
        Version++;
    }

    public void Touch() => Version++;

    // -------------------------------------------------------------------------
    // Adding
    // -------------------------------------------------------------------------

    public T Add<T>(T entry) where T : TranscriptEntry
    {
        entry.Id = ++_nextId;
        _entries.Add(entry);
        if (entry.Kind != EntryKind.User) UnreadCount++;

        // Drop from the front, but never a tool call still running or a question still pending.
        while (_entries.Count > MaxEntries)
        {
            int index = _entries.FindIndex(e => e is not ToolCallEntry { Status: ToolCallStatus.Running }
                                             && e is not QuestionEntry { State: QuestionState.Pending }
                                             && e is not PermissionEntry { Decision: PermissionDecision.Pending });
            if (index < 0) break;
            _entries.RemoveAt(index);
        }

        Version++;
        EntryAdded?.Invoke(entry);
        return entry;
    }

    public UserEntry AddUser(string text, UserEntryState state, bool synthetic = false)
        => Add(new UserEntry { Text = text, State = state, IsSynthetic = synthetic });

    public SystemEntry AddSystem(string text, SystemSeverity severity = SystemSeverity.Info)
        => Add(new SystemEntry { Text = text, Severity = severity });

    public AssistantTextEntry AddSaid(string text)
        => Add(new AssistantTextEntry { Text = text, ViaSay = true });

    public QuestionEntry AddQuestion(PendingQuestion question)
        => Add(new QuestionEntry { Question = question });

    public void Clear()
    {
        _entries.Clear();
        UnreadCount = 0;
        BusyStatus  = null;
        Version++;
    }

    public ToolCallEntry? FindToolCall(string toolUseId)
        => _entries.OfType<ToolCallEntry>().LastOrDefault(t => t.ToolUseId == toolUseId);

    public PermissionEntry? FindPermission(string requestId)
        => _entries.OfType<PermissionEntry>().LastOrDefault(p => p.RequestId == requestId);

    /// <summary>The user entry a replayed message acknowledges: the oldest sent one with the same text.</summary>
    public UserEntry? FindSentUser(string text)
        => _entries.OfType<UserEntry>().FirstOrDefault(u => u.State == UserEntryState.Sent && u.Text == text);

    public IEnumerable<ToolCallEntry> RunningToolCalls => _entries.OfType<ToolCallEntry>().Where(t => t.Status == ToolCallStatus.Running);

    // -------------------------------------------------------------------------
    // Applying frames
    // -------------------------------------------------------------------------

    public void Apply(StreamFrame frame)
    {
        switch (frame)
        {
            case StatusFrame status:
                SetBusy(status.Status == "requesting" ? "Thinking..." : status.Status);
                break;

            case StreamEventFrame ev:
                ApplyStreamEvent(ev);
                break;

            case AssistantFrame assistant:
                ApplyAssistant(assistant);
                break;

            case UserFrame user:
                ApplyUser(user);
                break;

            case ResultFrame result:
                ApplyResult(result);
                break;

            case ControlRequestFrame { IsPermissionRequest: true } request:
                Add(new PermissionEntry
                {
                    RequestId   = request.RequestId,
                    ToolName    = request.ToolName ?? "",
                    Title       = request.Title ?? ToolLabels.Friendly(request.ToolName ?? "", request.Input),
                    Description = request.Description ?? request.DecisionReason ?? "",
                    Input       = request.Input,
                    InputJson   = request.Input is { } input ? McpJson.PrettyPrint(input) : "",
                    Suggestions = request.PermissionSuggestions,
                    DefaultToNo = request.DefaultToNo,
                    SuppressAlwaysAllow = request.SuppressAlwaysAllowRule,
                });
                SetBusy("Waiting for permission");
                break;

            case ControlCancelFrame cancel:
            {
                var entry = FindPermission(cancel.RequestId);
                if (entry is { Decision: PermissionDecision.Pending })
                {
                    entry.Decision = PermissionDecision.Withdrawn;
                    Version++;
                }
                break;
            }
        }
    }

    private void ApplyStreamEvent(StreamEventFrame ev)
    {
        switch (ev.EventType)
        {
            case "message_start":
                _currentMessageId = ev.MessageId;
                SetBusy("Thinking...");
                break;

            case "content_block_start":
                switch (ev.BlockType)
                {
                    case "text":
                        Add(new AssistantTextEntry { Streaming = true, MessageId = _currentMessageId, BlockIndex = ev.Index, Text = ev.Text ?? "" });
                        SetBusy("Writing...");
                        break;
                    case "thinking":
                        Add(new ThinkingEntry { Streaming = true, MessageId = _currentMessageId, BlockIndex = ev.Index, Text = ev.Text ?? "" });
                        SetBusy("Thinking...");
                        break;
                    case "tool_use":
                    {
                        string name = ev.ToolName ?? "";
                        Add(new ToolCallEntry
                        {
                            ToolUseId  = ev.ToolUseId ?? "",
                            Name       = name,
                            Label      = ToolLabels.Friendly(name, null),
                            MessageId  = _currentMessageId,
                            BlockIndex = ev.Index,
                        });
                        SetBusy($"Calling {ToolLabels.ShortName(name)}...");
                        break;
                    }
                }
                break;

            case "content_block_delta":
            {
                var target = FindStreaming(ev.Index);
                switch (ev.DeltaType)
                {
                    case "text_delta" when target is AssistantTextEntry text:
                        text.Text += ev.Text;
                        Version++;
                        break;
                    case "thinking_delta" when target is ThinkingEntry thinking:
                        thinking.Text += ev.Text;
                        Version++;
                        break;
                    case "input_json_delta" when target is ToolCallEntry call:
                        call.InputJson.Append(ev.Text);
                        Version++;
                        break;
                }
                break;
            }

            case "content_block_stop":
            {
                var target = FindStreaming(ev.Index);
                switch (target)
                {
                    case AssistantTextEntry text:
                        text.Streaming = false;
                        Version++;
                        break;
                    case ThinkingEntry thinking:
                        thinking.Streaming = false;
                        Version++;
                        break;
                    case ToolCallEntry call:
                        FinishInput(call);
                        SetBusy($"Running {call.Label}...");
                        break;
                }
                break;
            }
        }
    }

    // The block still streaming at this index in the current message.
    private TranscriptEntry? FindStreaming(int index)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            switch (_entries[i])
            {
                case AssistantTextEntry { Streaming: true } t when t.BlockIndex == index && t.MessageId == _currentMessageId: return t;
                case ThinkingEntry { Streaming: true } th when th.BlockIndex == index && th.MessageId == _currentMessageId:  return th;
                case ToolCallEntry { InputComplete: false } c when c.BlockIndex == index && c.MessageId == _currentMessageId: return c;
            }
        }
        return null;
    }

    private void FinishInput(ToolCallEntry call)
    {
        call.InputComplete = true;
        var input = call.Input;
        call.Label       = ToolLabels.Friendly(call.Name, input);
        call.ArgsCompact = ToolLabels.CompactArguments(input);
        Version++;
    }

    private void ApplyAssistant(AssistantFrame frame)
    {
        if (frame.Error != null || frame.IsApiErrorMessage)
        {
            string text = frame.Text;
            LastApiError = string.IsNullOrWhiteSpace(text) ? frame.Error : text;
            AddSystem(string.IsNullOrWhiteSpace(text) ? $"API error: {frame.Error}" : text, SystemSeverity.Error);
            return;
        }

        if (frame.MessageId != null) _currentMessageId = frame.MessageId;

        for (int index = 0; index < frame.Content.Count; index++)
        {
            switch (frame.Content[index])
            {
                case TextBlock text:
                {
                    var existing = FindText(frame.MessageId, index, text.Text);
                    if (existing == null)
                    {
                        if (text.Text.Length > 0)
                            Add(new AssistantTextEntry { Text = text.Text, MessageId = frame.MessageId, BlockIndex = index });
                    }
                    else
                    {
                        existing.Text      = text.Text;
                        existing.Streaming = false;
                        Version++;
                    }
                    break;
                }
                case ThinkingBlock thinking:
                {
                    var existing = _entries.OfType<ThinkingEntry>().LastOrDefault(t => t.MessageId == frame.MessageId && (t.BlockIndex == index || t.Streaming));
                    if (existing == null)
                    {
                        if (thinking.Text.Length > 0)
                            Add(new ThinkingEntry { Text = thinking.Text, MessageId = frame.MessageId, BlockIndex = index });
                    }
                    else
                    {
                        existing.Text      = thinking.Text;
                        existing.Streaming = false;
                        Version++;
                    }
                    break;
                }
                case ToolUseBlock use:
                {
                    var existing = FindToolCall(use.Id);
                    if (existing == null)
                    {
                        existing = Add(new ToolCallEntry { ToolUseId = use.Id, Name = use.Name, MessageId = frame.MessageId, BlockIndex = index });
                    }

                    existing.InputJson.Clear();
                    if (use.Input is { } input) existing.InputJson.Append(input.GetRawText());
                    FinishInput(existing);
                    SetBusy($"Running {existing.Label}...");
                    break;
                }
            }
        }
    }

    // A streamed text entry for this message: same index, or whose text is a prefix of the final text.
    private AssistantTextEntry? FindText(string? messageId, int index, string finalText)
    {
        AssistantTextEntry? byIndex = null, byPrefix = null;
        foreach (var t in _entries.OfType<AssistantTextEntry>())
        {
            if (t.ViaSay || t.MessageId != messageId) continue;
            if (t.BlockIndex == index) byIndex = t;
            if (t.Streaming || finalText.StartsWith(t.Text, StringComparison.Ordinal)) byPrefix = t;
        }
        return byIndex ?? byPrefix;
    }

    private void ApplyUser(UserFrame frame)
    {
        if (frame.IsReplay && frame.Text != null)
        {
            var sent = FindSentUser(frame.Text);
            if (sent != null)
            {
                sent.State = UserEntryState.Acked;
                Version++;
            }
        }

        foreach (var result in frame.ToolResults)
        {
            var call = FindToolCall(result.ToolUseId);
            if (call == null)
            {
                // A result for a call we never saw (resumed session): still worth a row.
                call = Add(new ToolCallEntry { ToolUseId = result.ToolUseId, Name = "(earlier call)", Label = "Earlier tool call" });
            }

            call.Status   = result.IsError ? ToolCallStatus.Failed : ToolCallStatus.Succeeded;
            call.Duration = DateTime.UtcNow - call.CreatedUtc;

            var summary = ResultSummariser.Summarise(result.Text, result.HasImage, result.ImageBytes, result.ImageMediaType);
            call.ResultSummary  = summary.Summary;
            call.ResultFull     = summary.Full;
            call.ResultIsImage  = result.HasImage;
            call.ImageBase64    = result.ImageBase64;
            call.ImageMediaType = result.ImageMediaType;
            call.ResultChars    = result.Text.Length;
            call.ResultImageBytes = result.ImageBytes;
            Version++;
        }

        if (frame.ToolResults.Count > 0) SetBusy("Thinking...");
    }

    private void ApplyResult(ResultFrame result)
    {
        foreach (var t in _entries.OfType<AssistantTextEntry>().Where(t => t.Streaming)) t.Streaming = false;
        foreach (var t in _entries.OfType<ThinkingEntry>().Where(t => t.Streaming)) t.Streaming = false;
        foreach (var call in RunningToolCalls.ToList())
        {
            call.Status        = ToolCallStatus.Cancelled;
            call.ResultSummary = "no result (turn ended)";
            call.Duration      = DateTime.UtcNow - call.CreatedUtc;
        }

        Add(new ResultEntry
        {
            TotalCostUsd = result.TotalCostUsd,
            NumTurns     = result.NumTurns,
            DurationMs   = result.DurationMs,
            IsError      = result.IsError,
            Subtype      = result.Subtype,
            Text         = result.IsError ? result.ResultText : null,
        });

        SetBusy(null);
    }
}

// -------------------------------------------------------------------------
// Markdown
// -------------------------------------------------------------------------

public enum MarkdownBlockKind
{
    Paragraph,
    Bullet,
    Numbered,
    Heading,
    Code,
}

public sealed record MarkdownBlock(MarkdownBlockKind Kind, string Text, string? Language = null, int Number = 0);

/// <summary>
/// Just enough markdown for a chat panel drawn with a single font: paragraphs, bullets,
/// numbered items, headings (rendered as bold-ish lines) and fenced code. Inline emphasis
/// markers are stripped rather than rendered.
/// </summary>
public static class MarkdownLite
{
    private static readonly Regex Bullet   = new(@"^\s*[-*•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^\s*(\d+)[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Heading  = new(@"^\s*#{1,6}\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Bold     = new(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex Link     = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

    public static List<MarkdownBlock> Split(string text)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrEmpty(text)) return blocks;

        var paragraph = new StringBuilder();
        var code      = new StringBuilder();
        string? codeLanguage = null;
        bool inCode = false;

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, StripInline(paragraph.ToString())));
            paragraph.Clear();
        }

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine;

            if (inCode)
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, code.ToString().TrimEnd('\n'), codeLanguage));
                    code.Clear();
                    inCode = false;
                }
                else
                {
                    code.Append(line).Append('\n');
                }
                continue;
            }

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                inCode = true;
                codeLanguage = line.Trim().Substring(3).Trim();
                if (codeLanguage.Length == 0) codeLanguage = null;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            var heading = Heading.Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading, StripInline(heading.Groups[1].Value)));
                continue;
            }

            var bullet = Bullet.Match(line);
            if (bullet.Success)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Bullet, StripInline(bullet.Groups[1].Value)));
                continue;
            }

            var numbered = Numbered.Match(line);
            if (numbered.Success)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Numbered, StripInline(numbered.Groups[2].Value), null, int.Parse(numbered.Groups[1].Value)));
                continue;
            }

            if (paragraph.Length > 0) paragraph.Append('\n');
            paragraph.Append(line);
        }

        FlushParagraph();
        if (inCode) blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, code.ToString().TrimEnd('\n'), codeLanguage));
        return blocks;
    }

    /// <summary>Removes bold markers and turns links into "text (url)".</summary>
    public static string StripInline(string text)
    {
        text = Bold.Replace(text, m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        text = Link.Replace(text, "$1 ($2)");
        return text;
    }
}

// -------------------------------------------------------------------------
// Labels and summaries
// -------------------------------------------------------------------------

/// <summary>Human labels for tool calls in the transcript and the viewport banner.</summary>
public static class ToolLabels
{
    public const string McpPrefix = "mcp__" + McpConfigWriter.ServerName + "__";

    /// <summary>The tool name without the MCP server prefix.</summary>
    public static string ShortName(string name)
        => name.StartsWith(McpPrefix, StringComparison.Ordinal) ? name[McpPrefix.Length..] : name;

    public static string Friendly(string name, JsonElement? input)
    {
        string tool = ShortName(name);
        string? a(params string[] keys) => FirstString(input, keys);

        switch (tool)
        {
            case "Read":            return "Read " + (Short(a("file_path")) ?? "a file");
            case "Edit":            return "Edit " + (Short(a("file_path")) ?? "a file");
            case "Write":           return "Write " + (Short(a("file_path")) ?? "a file");
            case "MultiEdit":       return "Edit " + (Short(a("file_path")) ?? "a file");
            case "NotebookEdit":    return "Edit notebook " + (Short(a("notebook_path")) ?? "");
            case "Bash":            return "Run: " + (Truncate(a("command"), 80) ?? "a command");
            case "Glob":            return "Find files " + (a("pattern") ?? "");
            case "Grep":            return "Search for " + (Truncate(a("pattern"), 60) ?? "a pattern");
            case "WebFetch":        return "Fetch " + (Truncate(a("url"), 80) ?? "a page");
            case "WebSearch":       return "Search the web: " + (Truncate(a("query"), 60) ?? "");
            case "Task":            return "Delegate: " + (Truncate(a("description"), 60) ?? "a subtask");
            case "TodoWrite":       return "Update the task list";
            case "AskUserQuestion": return "Ask a question";

            case "spawn_actor":      return $"Spawn actor '{a("name") ?? "?"}'";
            case "spawn_primitive":  return $"Spawn {a("shape") ?? "primitive"}" + (a("name") is { } pn ? $" '{pn}'" : "");
            case "place_actor":      return $"Place {a("preset") ?? "actor"}" + (a("name") is { } an ? $" '{an}'" : "");
            case "destroy_actor":    return $"Destroy '{a("actor") ?? "?"}'";
            case "rename_actor":     return $"Rename '{a("actor") ?? "?"}' to '{a("newName", "name") ?? "?"}'";
            case "duplicate_actor":  return $"Duplicate '{a("actor") ?? "?"}'";
            case "set_transform":    return $"Move '{a("actor") ?? "?"}'";
            case "translate":        return $"Translate '{a("actor") ?? "?"}'";
            case "rotate":           return $"Rotate '{a("actor") ?? "?"}'";
            case "look_at":          return $"Aim '{a("actor") ?? "?"}'";
            case "move_to_layer":    return $"Move '{a("actor") ?? "?"}' to layer {a("layer") ?? "?"}";
            case "add_component":    return $"Add {a("componentType") ?? "component"} to '{a("actor") ?? "?"}'";
            case "remove_component": return $"Remove {a("componentType") ?? "component"} from '{a("actor") ?? "?"}'";
            case "set_property":     return $"Set {a("componentType") ?? "?"}.{a("property") ?? "?"} on '{a("actor") ?? "?"}'";
            case "set_properties":   return $"Set properties on '{a("actor") ?? "?"}'";
            case "set_material":     return $"Set material on '{a("actor") ?? "?"}'";
            case "save_scene":       return "Save scene" + (a("path") is { } sp ? $" '{sp}'" : "");
            case "load_scene":       return $"Load scene '{a("path") ?? "?"}'";
            case "new_scene":        return $"New scene '{a("name") ?? "Untitled"}'";
            case "undo":             return "Undo";
            case "redo":             return "Redo";
            case "capture_viewport": return "Look at the viewport";
            case "capture_scene_from": return "Look at the scene from a point";
            case "select_actor":     return $"Select '{a("actor") ?? "?"}'";
            case "focus_actor":      return $"Focus '{a("actor") ?? "?"}'";
            case "play":             return "Play";
            case "pause":            return "Pause";
            case "stop":             return "Stop play mode";
            case "read_console":     return "Read the Output Log";
            case "log_message":      return "Log: " + (Truncate(a("message"), 60) ?? "");
            case "build_project":    return "Build the project";
            case "reload_game_code": return "Reload game code";
            case "create_class":     return $"Create class {a("name") ?? "?"}";
            case "create_code_project": return "Create the C# project";
            case "run_standalone":   return "Run the game standalone";
            case "rebuild_engine_and_restart": return "Rebuild the engine and restart";
            case "ask_user":         return "Ask: " + (Truncate(a("question"), 60) ?? "");
            case "say":              return "Say: " + (Truncate(a("message"), 60) ?? "");
            case "wait_for_user":    return "Wait for the user";
            case "get_context":      return "Read the context";
            case "get_session_usage": return "Read the session meter";
            case "apply_scene_edits": return "Apply scene edits";
            case "spawn_many":       return $"Spawn many {a("what") ?? "actors"}";
            case "run_scene_report": return "Play the scene and report";
            case "run_tests":        return $"Run the {a("project") ?? "engine"} tests";
            case "export_build":     return "Export the build";
            case "get_build_report": return "Read the build report";
            case "get_project_info": return "Read project info";
            case "get_scene_summary": return "Read the scene";
            case "get_actor":        return $"Inspect '{a("actor") ?? "?"}'";
        }

        string humanised = Humanise(tool);
        string? first = FirstString(input, "actor", "name", "path", "file_path", "pattern", "query");
        return first != null ? $"{humanised} '{Truncate(first, 40)}'" : humanised;
    }

    /// <summary><c>spawn_actor</c> → <c>Spawn actor</c>.</summary>
    public static string Humanise(string tool)
    {
        if (tool.Length == 0) return "Tool";
        string words = tool.Replace('_', ' ').Replace("mcp  ", "").Trim();
        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>A one-line rendering of the arguments for the tool row.</summary>
    public static string CompactArguments(JsonElement? input)
    {
        if (input is not { ValueKind: JsonValueKind.Object } o) return "";

        var parts = new List<string>();
        foreach (var p in o.EnumerateObject())
        {
            string value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
            parts.Add($"{p.Name}={Truncate(value.Replace('\n', ' '), 60)}");
        }

        return Truncate(string.Join(", ", parts), 200) ?? "";
    }

    private static string? FirstString(JsonElement? input, params string[] keys)
    {
        if (input is not { ValueKind: JsonValueKind.Object } o) return null;

        foreach (var key in keys)
        {
            foreach (var p in o.EnumerateObject())
            {
                if (!string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
                if (p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return p.Value.GetRawText();
            }
        }
        return null;
    }

    private static string? Short(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? path : string.Join("/", parts[^2..]);
    }

    private static string? Truncate(string? text, int max)
    {
        if (text == null) return null;
        text = text.Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "...";
    }
}

/// <summary>Turns a tool result into a one-line summary plus a display-sized full text.</summary>
public static class ResultSummariser
{
    public sealed record ResultSummary(string Summary, string Full);

    private static readonly Regex Base64Run = new(@"^[A-Za-z0-9+/=\r\n]{2048,}$", RegexOptions.Compiled);

    public static ResultSummary Summarise(string text, bool hasImage, int imageBytes, string? imageMediaType)
    {
        text ??= "";

        if (hasImage)
        {
            string label = $"image ({imageMediaType ?? "image"}, {imageBytes / 1024} KB)";
            string full  = text.Length > 0 ? label + "\n" + Cap(text) : label;
            return new ResultSummary(text.Length > 0 ? label + ": " + FirstLine(text) : label, full);
        }

        if (text.Length > 2048 && Base64Run.IsMatch(text))
            return new ResultSummary($"binary data ({text.Length * 3 / 4 / 1024} KB)", $"binary data ({text.Length} characters of base64)");

        string first = FirstLine(text);
        string summary = text.Length > 8 * 1024 ? $"{first} ... ({text.Length / 1024} KB)" : first;
        return new ResultSummary(summary, Cap(text));
    }

    private static string FirstLine(string text)
    {
        string line = text.Replace("\r", "").Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
        return line.Length <= 200 ? line : line[..199] + "...";
    }

    private static string Cap(string text)
        => text.Length <= ToolCallEntry.MaxResultChars ? text : text[..ToolCallEntry.MaxResultChars] + $"\n... ({text.Length - ToolCallEntry.MaxResultChars} more characters)";
}
