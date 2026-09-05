using System.Text.Json;
using System.Text.Json.Serialization;

namespace SexyBiscuit.Editor.Assistant;

/// <summary>
/// Settings for the MCP server and the Assistant, stored per user (not per project) the same
/// way the recent-project list is.
/// </summary>
public sealed class AssistantSettings
{
    public int Version { get; set; } = 1;

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
    // Persistence
    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The per-user application data directory, not the executable's — it survives a rebuild
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
