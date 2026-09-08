using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>One block of tool output.</summary>
public abstract record McpContent;

public sealed record McpTextContent(string Text) : McpContent;

public sealed record McpImageContent(string Base64Data, string MimeType) : McpContent;

/// <summary>What a tool hands back to Claude: content blocks, an error flag, optional JSON.</summary>
public sealed class McpToolResult
{
    public List<McpContent> Content           { get; } = new();
    public bool             IsError           { get; set; }
    public JsonNode?        StructuredContent { get; set; }

    /// <summary>
    /// Set by a mutating tool that took no action after all — a merged tool called in its
    /// reporting mode, such as <c>spawn_actor</c> with <c>list</c>. The host reads it to leave
    /// the scene's dirty flag alone.
    /// </summary>
    public bool NoChange { get; set; }

    public static McpToolResult Text(string text) => new McpToolResult().WithText(text);

    /// <summary>
    /// Serialises a value as compact JSON text, preceded by a one-line summary when given, and
    /// keeps the tree as <see cref="StructuredContent"/> for callers in this process.
    /// </summary>
    /// <remarks>
    /// The text is what the model reads. It used to be indented and to travel beside a
    /// <c>structuredContent</c> copy of the same tree; Claude Code forwards the structured copy
    /// and drops the text when both are present, so the summary line and every warning were
    /// invisible to the model, and the wire carried the payload twice. Now there is one compact
    /// copy on the wire (see <see cref="ToJsonNode"/>) and the summary leads it.
    /// </remarks>
    public static McpToolResult Json(object value, string? leadingSummary = null)
    {
        var node   = McpJson.ToNode(value);
        var result = new McpToolResult();
        string body = McpJson.ToText(node, indented: false);

        result.WithText(string.IsNullOrEmpty(leadingSummary) ? body : leadingSummary + "\n" + body);
        result.StructuredContent = WrapForStructured(node);
        return result;
    }

    public static McpToolResult Image(byte[] png, string? caption = null, string mimeType = "image/png")
    {
        var result = new McpToolResult();
        if (!string.IsNullOrEmpty(caption)) result.WithText(caption);
        result.Content.Add(new McpImageContent(Convert.ToBase64String(png), mimeType));
        return result;
    }

    public static McpToolResult Error(string message) => new McpToolResult { IsError = true }.WithText(message);

    public McpToolResult WithText(string text)
    {
        Content.Add(new McpTextContent(text));
        return this;
    }

    /// <summary>Appends a warning line and records it under <c>structuredContent.warnings</c>.</summary>
    public McpToolResult WithWarning(string warning)
    {
        Content.Add(new McpTextContent("Warning: " + warning));

        JsonObject obj;
        switch (StructuredContent)
        {
            case null:
                obj = new JsonObject();
                StructuredContent = obj;
                break;
            case JsonObject existing:
                obj = existing;
                break;
            default:
                obj = new JsonObject { ["value"] = StructuredContent.DeepClone() };
                StructuredContent = obj;
                break;
        }

        if (obj["warnings"] is not JsonArray warnings)
        {
            warnings = new JsonArray();
            obj["warnings"] = warnings;
        }

        warnings.Add(warning);
        return this;
    }

    /// <summary>The first text block, or an empty string. Used for logs and summaries.</summary>
    public string FirstText => Content.OfType<McpTextContent>().FirstOrDefault()?.Text ?? "";

    /// <summary>
    /// The wire shape: <c>{ content: […], isError?, structuredContent? }</c>. The structured copy
    /// is sent only when asked for (<see cref="McpToolRegistry.EmitStructuredContent"/>), because a
    /// client that has it ignores the text — and the text is where the summary and warnings are.
    /// </summary>
    public JsonObject ToJsonNode(bool includeStructuredContent = false)
    {
        var content = new JsonArray();
        foreach (var block in Content)
        {
            content.Add(block switch
            {
                McpTextContent t  => new JsonObject { ["type"] = "text", ["text"] = t.Text },
                McpImageContent i => new JsonObject { ["type"] = "image", ["data"] = i.Base64Data, ["mimeType"] = i.MimeType },
                _                 => throw new InvalidOperationException($"Unknown content block {block.GetType().Name}"),
            });
        }

        var o = new JsonObject { ["content"] = content };
        if (IsError) o["isError"] = true;
        if (includeStructuredContent && StructuredContent != null) o["structuredContent"] = StructuredContent.DeepClone();
        return o;
    }

    // MCP requires structuredContent to be an object; wrap arrays and scalars.
    private static JsonNode? WrapForStructured(JsonNode? node) => node switch
    {
        null           => null,
        JsonObject obj => obj.DeepClone(),
        _              => new JsonObject { ["value"] = node.DeepClone() },
    };
}

/// <summary>
/// An expected tool failure — "no such actor", "unknown property". The message (and hint) go
/// verbatim into an <c>isError</c> result so Claude can act on them; nothing is logged as a
/// bug.
/// </summary>
public sealed class McpToolException : Exception
{
    public McpToolException(string message, string? hint = null) : base(message) => Hint = hint;

    public string? Hint { get; }

    public string FullMessage => string.IsNullOrEmpty(Hint) ? Message : Message + " " + Hint;
}
