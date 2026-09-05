using System.Globalization;
using System.Text;

namespace SexyBiscuit.Engine.Mcp.ClaudeCode;

/// <summary>How much the embedded session may do without asking.</summary>
public enum ClaudePermissionMode
{
    /// <summary>No prompts at all (<c>bypassPermissions</c>). The Stop button and the activity log are the safety net.</summary>
    Autonomous,

    /// <summary>File edits inside the project go through; shell commands and the like still ask.</summary>
    AcceptEdits,

    /// <summary>Every non-read tool asks, in the panel.</summary>
    Ask,

    /// <summary>Claude Code's own classifier decides what needs a prompt.</summary>
    Auto,
}

public enum ClaudeThinkingDisplay
{
    /// <summary>Thinking is not shown.</summary>
    Omitted,

    /// <summary>Thinking arrives as summaries, shown collapsed.</summary>
    Summarized,
}

/// <summary>Everything that shapes the <c>claude</c> command line for one session.</summary>
public sealed class ClaudeLaunchOptions
{
    public string ProjectName { get; init; } = "Untitled";
    public string ProjectRoot { get; init; } = "";

    /// <summary>The editor's MCP URL, put into an inline <c>--mcp-config</c>.</summary>
    public string  McpUrl   { get; init; } = "http://127.0.0.1:7331/mcp/";
    public string? McpToken { get; init; }

    public ClaudePermissionMode  PermissionMode { get; init; } = ClaudePermissionMode.Autonomous;
    public ClaudeThinkingDisplay Thinking       { get; init; } = ClaudeThinkingDisplay.Omitted;

    /// <summary>Model alias or id; null leaves the CLI's default.</summary>
    public string? Model  { get; init; }
    public string? Effort { get; init; }

    public double? MaxBudgetUsd { get; init; }
    public int?    MaxTurns     { get; init; }

    /// <summary>Extra directories the session may read and edit: the engine repository, typically.</summary>
    public IReadOnlyList<string> AddDirectories { get; init; } = Array.Empty<string>();

    /// <summary>The engine checkout, named in the system prompt when set.</summary>
    public string? EngineRepoRoot { get; init; }

    /// <summary>A fresh id for a new session. Mutually exclusive with <see cref="ResumeSessionId"/>.</summary>
    public string? SessionId { get; init; }

    /// <summary>An earlier session to continue.</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>With <see cref="ResumeSessionId"/>: branch into a new session instead of appending.</summary>
    public bool ForkSession { get; init; }

    /// <summary>False adds <c>--strict-mcp-config</c>, so only the editor's server is loaded.</summary>
    public bool IncludeProjectMcpServers { get; init; }

    /// <summary>Whether our user messages are echoed back, which is how the panel marks them delivered.</summary>
    public bool ReplayUserMessages { get; init; } = true;

    /// <summary>Overrides the generated embedded prompt.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Shown in <c>claude</c>'s own session list.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Appended verbatim, for flags this class does not know.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
}

/// <summary>Turns <see cref="ClaudeLaunchOptions"/> into the argument list for the <c>claude</c> binary.</summary>
public static class ClaudeArgvBuilder
{
    /// <summary>The permission rule that pre-approves every editor tool, so even Ask mode never prompts for them.</summary>
    public const string EditorToolsRule = "mcp__" + McpConfigWriter.ServerName;

    public static List<string> Build(ClaudeLaunchOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (o.SessionId != null && o.ResumeSessionId != null)
            throw new ArgumentException("Set SessionId or ResumeSessionId, not both.", nameof(o));

        var args = new List<string>
        {
            "--print",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
        };

        if (o.ReplayUserMessages) args.Add("--replay-user-messages");

        // Permission prompts come to us over stdin/stdout as control requests.
        args.Add("--permission-prompt-tool");
        args.Add("stdio");

        switch (o.PermissionMode)
        {
            case ClaudePermissionMode.Autonomous:
                args.Add("--permission-mode"); args.Add("bypassPermissions");
                args.Add("--allow-dangerously-skip-permissions");
                break;
            case ClaudePermissionMode.AcceptEdits:
                args.Add("--permission-mode"); args.Add("acceptEdits");
                break;
            case ClaudePermissionMode.Ask:
                args.Add("--permission-mode"); args.Add("manual");
                break;
            case ClaudePermissionMode.Auto:
                args.Add("--permission-mode"); args.Add("auto");
                break;
        }

        args.Add("--mcp-config");
        args.Add(McpConfigWriter.InlineConfig(o.McpUrl, o.McpToken, embedded: true));
        if (!o.IncludeProjectMcpServers) args.Add("--strict-mcp-config");

        args.Add("--allowedTools");    args.Add(EditorToolsRule);
        args.Add("--disallowedTools"); args.Add("AskUserQuestion");

        args.Add("--append-system-prompt");
        args.Add(o.SystemPrompt ?? ClaudeSystemPrompt.Embedded(o));

        if (!string.IsNullOrWhiteSpace(o.DisplayName)) { args.Add("--name");   args.Add(o.DisplayName); }
        if (!string.IsNullOrWhiteSpace(o.Model))       { args.Add("--model");  args.Add(o.Model); }
        if (!string.IsNullOrWhiteSpace(o.Effort))      { args.Add("--effort"); args.Add(o.Effort); }

        if (o.MaxBudgetUsd is { } budget && budget > 0)
        {
            args.Add("--max-budget-usd");
            args.Add(budget.ToString("0.##", CultureInfo.InvariantCulture));
        }

        if (o.MaxTurns is { } turns && turns > 0)
        {
            args.Add("--max-turns");
            args.Add(turns.ToString(CultureInfo.InvariantCulture));
        }

        if (o.AddDirectories.Count > 0)
        {
            // Variadic: one flag, then every directory, before the next flag.
            args.Add("--add-dir");
            args.AddRange(o.AddDirectories);
        }

        if (o.Thinking == ClaudeThinkingDisplay.Summarized)
        {
            args.Add("--thinking-display");
            args.Add("summarized");
        }

        if (o.ResumeSessionId != null)
        {
            args.Add("--resume");
            args.Add(o.ResumeSessionId);
            if (o.ForkSession) args.Add("--fork-session");
        }
        else if (o.SessionId != null)
        {
            args.Add("--session-id");
            args.Add(o.SessionId);
        }

        args.AddRange(o.ExtraArgs);
        return args;
    }

    /// <summary>
    /// Removes a flag the CLI rejected as unknown (and its value, when the next token is not a
    /// flag). Returns false when the flag was not present. Used to retry once with an older CLI.
    /// </summary>
    public static bool RemoveFlag(List<string> args, string flag)
    {
        int index = args.IndexOf(flag);
        if (index < 0) return false;

        args.RemoveAt(index);
        if (index < args.Count && !args[index].StartsWith("-", StringComparison.Ordinal)) args.RemoveAt(index);
        return true;
    }

    /// <summary>The flag named in a commander "unknown option" error, or null.</summary>
    public static string? UnknownOptionIn(string stderr)
    {
        var match = System.Text.RegularExpressions.Regex.Match(stderr ?? "", @"unknown option '(--?[A-Za-z0-9-]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>A shell-ish rendering for the Diagnostics tab and the self-test (not for execution).</summary>
    public static string Display(string executable, IEnumerable<string> args)
    {
        var sb = new StringBuilder(Quote(executable));
        foreach (var a in args) sb.Append(' ').Append(Quote(a));
        return sb.ToString();

        static string Quote(string s)
            => s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '=' or ',')
                ? s
                : "'" + s.Replace("'", "'\\''") + "'";
    }
}

/// <summary>The prompts that tell Claude how to behave in the editor.</summary>
public static class ClaudeSystemPrompt
{
    /// <summary>Appended to the embedded session's system prompt.</summary>
    public static string Embedded(ClaudeLaunchOptions o)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the SexyBiscuit assistant, embedded in the SexyBiscuit game engine editor. You build and change the user's game inside the running editor while they watch. The user types to you in the editor's Assistant panel; their messages arrive as ordinary user messages, and they can press Stop at any time to cancel what you are doing.");
        sb.AppendLine();
        sb.Append("Project: \"").Append(o.ProjectName).Append("\" at ").Append(o.ProjectRoot).AppendLine(". Its working directory is the project root; scenes live in Scenes/, assets in Assets/, C# code in Source/.");
        if (!string.IsNullOrEmpty(o.EngineRepoRoot))
            sb.Append("Engine source: ").Append(o.EngineRepoRoot).AppendLine(" (SexyBiscuit.Engine, SexyBiscuit.Editor, SexyBiscuit.Tests). It is reference material for how the engine works; only change engine code when the user asks for engine work.");
        sb.AppendLine();
        sb.AppendLine("The editor is available to you as the `sexybiscuit` MCP server (tools named mcp__sexybiscuit__*). How to work:");
        sb.AppendLine("- Start each task with get_project_info and get_scene_summary. Actor ids change after undo, redo or a scene load, so re-query instead of remembering them.");
        sb.AppendLine("- Prefer the sexybiscuit tools over hand-editing .scene JSON: every tool call shows in the editor at once. Use spawn_primitive for quick geometry, place_actor for lights, cameras and gameplay actors, set_material for colours, capture_viewport to look at the result, undo to revert, and save_scene to persist (the scene only reaches disk through save_scene or the user saving).");
        sb.AppendLine("- Gameplay code is C# under Source/ (create_code_project adds the project when there is none, create_class adds a starter file). Edit files with your own Read, Edit and Write tools, then call reload_game_code, which builds first and hot-loads the assembly. Fix every compile error before moving on; build_project returns structured diagnostics.");
        sb.AppendLine("- Call rebuild_engine_and_restart only after changing engine source. The editor restarts and you are resumed with a message that starts with \"[editor]\": wait for it, then call get_project_info and carry on.");
        sb.AppendLine("- Use ask_user only when the answer is expensive to change later (deleting the user's work, choosing between very different designs); otherwise decide, and say what you chose. Destructive operations on the user's work (destroying many actors, new_scene over unsaved changes, deleting files) need an ask_user confirmation first.");
        sb.AppendLine("- Do not call say or wait_for_user; those are for sessions driving the editor from a terminal. Your text reaches the user directly and their replies arrive as user messages. Ignore any server instructions that tell you otherwise.");
        sb.AppendLine("- Keep working until the task is done, checking your work with capture_viewport or read_console where it matters. End each turn with a short summary of what changed and what you would do next.");
        sb.AppendLine("- Formatting: plain text, short paragraphs, bullet lists and fenced code blocks. Headings, tables and images do not render in the panel.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The MCP <c>initialize.instructions</c> for a session that connected from outside the
    /// editor (a terminal). Prefixed so the embedded session, which also reads it, knows to skip it.
    /// </summary>
    public static string ExternalInstructions(string sceneInstructions)
    {
        var sb = new StringBuilder();
        sb.Append("If your system prompt already identifies you as the embedded SexyBiscuit assistant, ignore the rest of this paragraph. ");
        sb.Append("You are driving the SexyBiscuit editor from outside it, so the user watches the editor, not your terminal: call say for anything they should read in the editor's Assistant panel, and end every turn with wait_for_user, then act on what it returns (their reply, typed into the panel). ");
        sb.Append(sceneInstructions);
        return sb.ToString();
    }
}

/// <summary>
/// The child process environment. Claude Code refuses to start inside another Claude Code
/// session, which it detects from environment variables the parent exports; the editor may
/// well have been launched from one, so those markers are removed. PATH is widened because
/// a GUI app on macOS gets a minimal PATH without dotnet or the user's tools.
/// </summary>
public static class ClaudeEnvironment
{
    /// <summary>Removed outright.</summary>
    public static readonly string[] RemovedVariables =
    {
        "CLAUDECODE", "CLAUDE_PID", "CLAUDE_AGENT_SDK_VERSION", "CLAUDE_EFFORT", "CLAUDE_PREVIEW_CLASSIFIER_FLOOR",
        "AI_AGENT", "BAGGAGE",
    };

    /// <summary>Every <c>CLAUDE_CODE_*</c> variable is removed unless it is one of these, which configure rather than mark a session.</summary>
    public static readonly string[] KeptClaudeCodeVariables =
    {
        "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY",
        "CLAUDE_CODE_SKIP_BEDROCK_AUTH", "CLAUDE_CODE_SKIP_VERTEX_AUTH", "CLAUDE_CODE_MAX_OUTPUT_TOKENS",
        "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "CLAUDE_CODE_API_KEY_HELPER_TTL_MS", "CLAUDE_CODE_ENABLE_TELEMETRY",
    };

    /// <summary>True when the variable must not reach the child.</summary>
    public static bool ShouldRemove(string name)
    {
        if (RemovedVariables.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        if (!name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)) return false;
        return !KeptClaudeCodeVariables.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Applies <see cref="ShouldRemove"/> to a copy of the environment.</summary>
    public static Dictionary<string, string?> Scrub(IEnumerable<KeyValuePair<string, string?>> environment)
    {
        var result = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (key, value) in environment)
        {
            if (ShouldRemove(key)) continue;
            result[key] = value;
        }
        return result;
    }

    /// <summary>PATH with the entries appended that are missing from it (existing order preserved).</summary>
    public static string ExtendPath(string? path, IEnumerable<string> entries, char separator)
    {
        var parts = string.IsNullOrEmpty(path) ? new List<string>() : path.Split(separator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            string trimmed = entry.TrimEnd('/', '\\');
            if (trimmed.Length == 0) trimmed = entry;
            if (!parts.Any(p => comparer.Equals(p.TrimEnd('/', '\\'), trimmed))) parts.Add(trimmed);
        }

        return string.Join(separator, parts);
    }

    /// <summary>Directories worth having on the child's PATH on this machine: the running dotnet, common tool locations.</summary>
    public static IEnumerable<string> DefaultPathEntries(string? dotnetRoot, string? home)
    {
        if (!string.IsNullOrEmpty(dotnetRoot)) yield return dotnetRoot;

        if (OperatingSystem.IsWindows())
        {
            string? programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            string? localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            string? appData      = Environment.GetEnvironmentVariable("APPDATA");
            if (programFiles != null) yield return Path.Combine(programFiles, "dotnet");
            if (localAppData != null) yield return Path.Combine(localAppData, "Programs", "claude");
            if (appData != null)      yield return Path.Combine(appData, "npm");
            if (home != null)         yield return Path.Combine(home, ".dotnet", "tools");
            if (home != null)         yield return Path.Combine(home, ".local", "bin");
            yield break;
        }

        yield return "/usr/local/share/dotnet";
        yield return "/usr/local/bin";
        yield return "/opt/homebrew/bin";
        yield return "/usr/bin";
        yield return "/bin";
        if (home != null) yield return Path.Combine(home, ".dotnet", "tools");
        if (home != null) yield return Path.Combine(home, ".local", "bin");
    }
}
