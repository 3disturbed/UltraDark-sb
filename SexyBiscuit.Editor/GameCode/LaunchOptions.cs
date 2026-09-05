namespace SexyBiscuit.Editor.GameCode;

/// <summary>Command-line options the editor understands. Everything is optional.</summary>
public sealed class LaunchOptions
{
    /// <summary>A resume file written by a previous editor before it restarted itself.</summary>
    public string? ResumeFile { get; set; }

    /// <summary>A .sbproject to open at start-up, skipping the launcher.</summary>
    public string? ProjectFile { get; set; }

    /// <summary>A scene to open after the project.</summary>
    public string? ScenePath { get; set; }

    /// <summary>Overrides the MCP port from settings.</summary>
    public int? McpPort { get; set; }

    public bool ResetLayout { get; set; }

    /// <summary>Do not start an embedded Claude Code session; the panel still works in external mode.</summary>
    public bool NoAssistant { get; set; }

    // -------------------------------------------------------------------------
    // Headless modes (no window)
    // -------------------------------------------------------------------------

    /// <summary>Run the Claude Code integration check and exit.</summary>
    public bool AssistantSelfTest { get; set; }

    /// <summary>With the self-test: stop before spawning Claude Code.</summary>
    public bool DryRun { get; set; }

    /// <summary>With the self-test: a prompt other than the canned one.</summary>
    public string? SelfTestPrompt { get; set; }

    /// <summary>With the self-test: seconds before giving up.</summary>
    public int SelfTestTimeoutSeconds { get; set; } = 180;

    /// <summary>Print the MCP tool catalogue and exit.</summary>
    public bool DumpMcpTools { get; set; }

    /// <summary>With the dump: markdown instead of JSON.</summary>
    public bool Markdown { get; set; }

    /// <summary>True when the process should not open a window at all.</summary>
    public bool IsHeadless => AssistantSelfTest || DumpMcpTools;

    public List<string> Unknown { get; } = new();

    public static LaunchOptions Parse(string[] args)
    {
        var options = new LaunchOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (arg)
            {
                case "--resume":
                    // The file is optional; the default location is used when the next token is another flag.
                    options.ResumeFile = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
                    break;
                case "--project":            options.ProjectFile = Next(); break;
                case "--scene":              options.ScenePath   = Next(); break;
                case "--mcp-port":           options.McpPort     = int.TryParse(Next(), out int port) ? port : null; break;
                case "--reset-layout":       options.ResetLayout = true; break;
                case "--no-assistant":       options.NoAssistant = true; break;
                case "--assistant-selftest": options.AssistantSelfTest = true; break;
                case "--dry-run":            options.DryRun = true; break;
                case "--prompt":             options.SelfTestPrompt = Next(); break;
                case "--timeout":            options.SelfTestTimeoutSeconds = int.TryParse(Next(), out int t) ? t : 180; break;
                case "--dump-mcp-tools":     options.DumpMcpTools = true; break;
                case "--markdown":           options.Markdown = true; break;
                default:                     options.Unknown.Add(arg); break;
            }
        }

        return options;
    }
}
