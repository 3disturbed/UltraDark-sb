using System.Text.Json;
using System.Text.Json.Serialization;
using SexyBiscuit.Engine.Mcp.ClaudeCode;

namespace SexyBiscuit.Editor.Assistant;

/// <summary>What the editor remembers about one project's Claude Code session.</summary>
public sealed class SessionRecord
{
    public string   SessionId       { get; set; } = "";
    public DateTime CreatedUtc      { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedUtc     { get; set; } = DateTime.UtcNow;
    public string?  ClaudeVersion   { get; set; }
    public string?  Model           { get; set; }
    public double   LifetimeCostUsd { get; set; }
    public int      Turns           { get; set; }
}

/// <summary>
/// Settings for the MCP server and the Assistant, stored per user (not per project) the same
/// way the recent-project list is.
/// </summary>
public sealed class AssistantSettings
{
    public int Version { get; set; } = 2;

    // -------------------------------------------------------------------------
    // MCP server
    // -------------------------------------------------------------------------

    /// <summary>First port to try; the server walks upwards when it is taken.</summary>
    public int McpPort { get; set; } = 7331;

    /// <summary>Optional bearer token every client must send. Null means loopback-only, no auth.</summary>
    public string? McpToken { get; set; }

    /// <summary>Write the server entry into the open project's .mcp.json so a terminal Claude Code connects.</summary>
    public bool WriteProjectMcpConfig { get; set; } = true;

    // -------------------------------------------------------------------------
    // Engine source
    // -------------------------------------------------------------------------

    /// <summary>The engine repository root, when it cannot be found by walking up from the editor binary.</summary>
    public string? EngineRepoPath { get; set; }

    /// <summary>Give the embedded assistant access to the engine repository as well as the project.</summary>
    public bool IncludeEngineRepo { get; set; } = true;

    // -------------------------------------------------------------------------
    // Claude Code
    // -------------------------------------------------------------------------

    /// <summary>A specific <c>claude</c> binary; null lets the editor look for one.</summary>
    public string? ClaudePath { get; set; }

    /// <summary>Model alias or id; empty means the CLI's default.</summary>
    public string Model { get; set; } = "";

    /// <summary>Effort level; empty means the CLI's default.</summary>
    public string Effort { get; set; } = "";

    public ClaudePermissionMode  PermissionMode { get; set; } = ClaudePermissionMode.Autonomous;
    public ClaudeThinkingDisplay Thinking       { get; set; } = ClaudeThinkingDisplay.Omitted;

    /// <summary>Stops a session once it has spent this much. Null means no cap.</summary>
    public double? MaxBudgetUsdPerSession { get; set; }

    /// <summary>Start (or resume) a session as soon as a project opens.</summary>
    public bool AutoStartOnProjectOpen { get; set; } = true;

    /// <summary>Also load the MCP servers the project's own .mcp.json lists. Off keeps the session to the editor's server.</summary>
    public bool IncludeProjectMcpServers { get; set; }

    public int TranscriptMaxEntries { get; set; } = 2000;

    /// <summary>How long <c>wait_for_user</c> blocks when the caller gives no timeout.</summary>
    public int WaitForUserDefaultTimeoutSeconds { get; set; } = 900;

    /// <summary>Extra <c>claude</c> arguments, appended verbatim.</summary>
    public List<string> ExtraArgs { get; set; } = new();

    /// <summary>Sessions by normalised project root.</summary>
    public Dictionary<string, SessionRecord> Sessions { get; set; } = new();

    // -------------------------------------------------------------------------
    // Sessions
    // -------------------------------------------------------------------------

    /// <summary>The dictionary key for a project root: absolute, no trailing separator, case-folded on Windows.</summary>
    public static string NormaliseRoot(string root)
    {
        string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    public SessionRecord? FindSession(string root)
        => Sessions.TryGetValue(NormaliseRoot(root), out var record) ? record : null;

    public void RememberSession(string root, SessionRecord record)
    {
        Sessions[NormaliseRoot(root)] = record;
        Save();
    }

    public void ForgetSession(string root)
    {
        if (Sessions.Remove(NormaliseRoot(root))) Save();
    }

    // -------------------------------------------------------------------------
    // Persistence
    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The per-user application data directory, not the executable's: it survives a rebuild
    /// and works from a read-only install, exactly like the recent-project list.
    /// </summary>
    public static string FilePath
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SexyBiscuit");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "assistant-settings.json");
        }
    }

    public static AssistantSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AssistantSettings>(File.ReadAllText(FilePath), Json) ?? new AssistantSettings();
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Could not read the assistant settings: {ex.Message}", LogLevel.Warning);
        }

        return new AssistantSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Could not save the assistant settings: {ex.Message}", LogLevel.Warning);
        }
    }
}
