using System.Text.Json;
using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.Mcp.ClaudeCode;

/// <summary>
/// Writes the MCP server entry Claude Code reads from a project's <c>.mcp.json</c>, without
/// disturbing anything else in that file.
/// </summary>
public static class McpConfigWriter
{
    public const string ServerName = "sexybiscuit";

    /// <summary>
    /// Claude Code's default per-call HTTP timeout is 60 seconds; a question to the user can
    /// take far longer, so the entry asks for an hour.
    /// </summary>
    public const int DefaultTimeoutMs = 3_600_000;

    /// <summary>The server entry for a URL, with an optional bearer token header.</summary>
    public static JsonObject ServerEntry(string url, string? bearerToken = null, int timeoutMs = DefaultTimeoutMs, bool embedded = false)
    {
        var entry = new JsonObject
        {
            ["type"]    = "http",
            ["url"]     = url,
            ["timeout"] = timeoutMs,
        };

        var headers = new JsonObject();
        if (!string.IsNullOrEmpty(bearerToken)) headers["Authorization"] = "Bearer " + bearerToken;
        if (embedded) headers[McpServer.ClientHeader] = "embedded";
        if (headers.Count > 0) entry["headers"] = headers;

        return entry;
    }

    /// <summary>A complete config document holding only this server — what <c>--mcp-config</c> takes inline.</summary>
    public static string InlineConfig(string url, string? bearerToken = null, bool embedded = true)
        => new JsonObject
        {
            ["mcpServers"] = new JsonObject { [ServerName] = ServerEntry(url, bearerToken, embedded: embedded) },
        }.ToJsonString();

    /// <summary>
    /// Merges the server entry into existing <c>.mcp.json</c> text (or creates the document),
    /// preserving other servers and unknown keys. Returns null — do not write — when the
    /// existing text is not a JSON object, rather than clobbering someone's hand-written file.
    /// </summary>
    public static string? Merge(string? existingJson, string url, string? bearerToken = null)
    {
        JsonObject root;

        if (string.IsNullOrWhiteSpace(existingJson))
        {
            root = new JsonObject();
        }
        else
        {
            try
            {
                if (JsonNode.Parse(existingJson) is not JsonObject parsed) return null;
                root = parsed;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }

        servers[ServerName] = ServerEntry(url, bearerToken);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>True when the file already carries exactly this URL, so it need not be rewritten.</summary>
    public static bool AlreadyCurrent(string? existingJson, string url)
    {
        if (string.IsNullOrWhiteSpace(existingJson)) return false;

        try
        {
            return JsonNode.Parse(existingJson) is JsonObject root
                && root["mcpServers"]?[ServerName]?["url"]?.GetValue<string>() == url;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes or updates <c>&lt;root&gt;/.mcp.json</c>. Returns what happened, for the log: written,
    /// unchanged, or skipped because the existing file could not be parsed.
    /// </summary>
    public static string WriteProjectConfig(string projectRoot, string url, string? bearerToken = null)
    {
        string path = Path.Combine(projectRoot, ".mcp.json");
        string? existing = File.Exists(path) ? File.ReadAllText(path) : null;

        if (AlreadyCurrent(existing, url) && string.IsNullOrEmpty(bearerToken)) return "unchanged";

        string? merged = Merge(existing, url, bearerToken);
        if (merged == null) return "skipped: existing .mcp.json is not a JSON object";

        File.WriteAllText(path, merged);
        return "written";
    }

    /// <summary>The one-liner a user can paste to register the server with Claude Code by hand.</summary>
    public static string ConnectCommand(string url) => $"claude mcp add --transport http {ServerName} {url}";
}
