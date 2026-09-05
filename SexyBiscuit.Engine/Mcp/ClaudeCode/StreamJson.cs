using System.Text.Json;
using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.Mcp.ClaudeCode;

// -------------------------------------------------------------------------
// Frames the CLI writes
// -------------------------------------------------------------------------

/// <summary>One line of Claude Code's <c>--output-format stream-json</c>, decoded as far as the editor needs.</summary>
/// <remarks>
/// The parser is deliberately tolerant: unknown frame types become <see cref="UnknownFrame"/>,
/// unknown fields are ignored, and the raw line is kept for the Diagnostics tab. Claude Code
/// adds fields between releases and the editor must keep working when it does.
/// </remarks>
public abstract record StreamFrame(string Type)
{
    /// <summary>The session id the CLI stamped on the frame, when present.</summary>
    public string? SessionId { get; init; }

    /// <summary>The whole line, for diagnostics and for fields this parser does not model.</summary>
    public string Raw { get; init; } = "";
}

/// <summary>An MCP server as <c>system/init</c> reports it: <c>connected</c>, <c>failed</c>, <c>needs-auth</c>, <c>pending</c>.</summary>
public sealed record McpServerStatus(string Name, string Status);

/// <summary>The first frame of a session: what the CLI is running with.</summary>
public sealed record InitFrame(
    string?                        Model,
    string?                        PermissionMode,
    IReadOnlyList<string>          Tools,
    IReadOnlyList<McpServerStatus> McpServers,
    string?                        ClaudeCodeVersion,
    string?                        Cwd,
    string?                        ApiKeySource) : StreamFrame("system");

/// <summary><c>system/status</c>: <c>requesting</c> while the model is being called, and so on.</summary>
public sealed record StatusFrame(string? Status) : StreamFrame("system");

/// <summary>Any other <c>system</c> subtype (compaction boundaries, hook output).</summary>
public sealed record SystemFrame(string? Subtype, string? Text) : StreamFrame("system");

/// <summary>A block inside an assistant or user message.</summary>
public abstract record ContentBlock(string Type);

public sealed record TextBlock(string Text) : ContentBlock("text");

public sealed record ThinkingBlock(string Text) : ContentBlock("thinking");

/// <summary>A tool call. <see cref="Input"/> is a detached clone, safe to keep.</summary>
public sealed record ToolUseBlock(string Id, string Name, JsonElement? Input) : ContentBlock("tool_use");

/// <summary>A tool's answer. Image results keep their base64 so the panel can show them.</summary>
public sealed record ToolResultBlock(
    string  ToolUseId,
    string  Text,
    bool    IsError,
    string? ImageBase64,
    string? ImageMediaType) : ContentBlock("tool_result")
{
    public bool HasImage => ImageBase64 != null;

    /// <summary>Approximate decoded size of the image, in bytes.</summary>
    public int ImageBytes => ImageBase64 == null ? 0 : ImageBase64.Length * 3 / 4;
}

public sealed record OtherBlock(string BlockType) : ContentBlock(BlockType);

/// <summary>A complete or partial assistant message. With partial messages on, it follows the stream events for the same blocks.</summary>
public sealed record AssistantFrame(
    string?                     MessageId,
    string?                     Model,
    IReadOnlyList<ContentBlock> Content,
    string?                     StopReason,
    string?                     Error,
    bool                        IsApiErrorMessage,
    string?                     Uuid,
    string?                     ParentToolUseId) : StreamFrame("assistant")
{
    /// <summary>All text blocks joined.</summary>
    public string Text => string.Join("\n", Content.OfType<TextBlock>().Select(t => t.Text));
}

/// <summary>A user turn: our own message echoed back (<see cref="IsReplay"/>) or tool results the CLI produced.</summary>
public sealed record UserFrame(
    bool                          IsReplay,
    string?                       Text,
    IReadOnlyList<ToolResultBlock> ToolResults,
    string?                       Uuid,
    string?                       ParentToolUseId) : StreamFrame("user");

/// <summary>A raw Anthropic streaming event, forwarded when <c>--include-partial-messages</c> is on.</summary>
public sealed record StreamEventFrame(
    string  EventType,
    int     Index,
    string? BlockType,
    string? ToolUseId,
    string? ToolName,
    string? DeltaType,
    string? Text,
    string? StopReason,
    string? MessageId,
    string? ParentToolUseId) : StreamFrame("stream_event");

/// <summary>End of a turn. <see cref="TotalCostUsd"/> is cumulative for the process.</summary>
public sealed record ResultFrame(
    string? Subtype,
    bool    IsError,
    double  TotalCostUsd,
    int     NumTurns,
    long    DurationMs,
    string? ResultText,
    string? TerminalReason,
    long    InputTokens,
    long    OutputTokens,
    long    CacheReadTokens,
    long    CacheCreationTokens,
    int     PermissionDenials) : StreamFrame("result");

/// <summary>The CLI asking us something; <c>can_use_tool</c> is the permission prompt.</summary>
public sealed record ControlRequestFrame(
    string       RequestId,
    string       Subtype,
    string?      ToolName,
    JsonElement? Input,
    JsonElement? PermissionSuggestions,
    string?      Title,
    string?      Description,
    string?      DecisionReason,
    string?      BlockedPath,
    string?      ToolUseId,
    bool         DefaultToNo,
    bool         SuppressAlwaysAllowRule,
    bool         RequiresUserInteraction) : StreamFrame("control_request")
{
    public bool IsPermissionRequest => Subtype == "can_use_tool";
}

/// <summary>The CLI's answer to one of our control requests (interrupt, set_permission_mode…).</summary>
public sealed record ControlResponseFrame(string? RequestId, string? Subtype, string? Error, JsonElement? Response) : StreamFrame("control_response");

/// <summary>The CLI withdrew a control request, typically a permission prompt for a cancelled tool.</summary>
public sealed record ControlCancelFrame(string RequestId) : StreamFrame("control_cancel_request");

public sealed record KeepAliveFrame() : StreamFrame("keep_alive");

/// <summary>A frame type this build does not know. Kept, not dropped, so Diagnostics can show it.</summary>
public sealed record UnknownFrame(string FrameType) : StreamFrame(FrameType);

// -------------------------------------------------------------------------
// Parser
// -------------------------------------------------------------------------

/// <summary>Decodes stream-json lines. Never throws.</summary>
public static class StreamJsonParser
{
    /// <summary>
    /// Parses one line. Returns false with an <paramref name="error"/> for blank or non-JSON
    /// lines (the CLI prints warnings to stdout occasionally) so the caller can show them raw.
    /// </summary>
    public static bool TryParse(string line, out StreamFrame? frame, out string? error)
    {
        frame = null;
        error = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            error = "blank line";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "not a JSON object";
                return false;
            }

            string type = Str(root, "type") ?? "";
            StreamFrame parsed;
            try
            {
                parsed = type switch
                {
                    "system"                 => ParseSystem(root),
                    "assistant"              => ParseAssistant(root),
                    "user"                   => ParseUser(root),
                    "stream_event"           => ParseStreamEvent(root),
                    "result"                 => ParseResult(root),
                    "control_request"        => ParseControlRequest(root),
                    "control_response"       => ParseControlResponse(root),
                    "control_cancel_request" => new ControlCancelFrame(Str(root, "request_id") ?? ""),
                    "keep_alive"             => new KeepAliveFrame(),
                    _                        => new UnknownFrame(type.Length == 0 ? "(untyped)" : type),
                };
            }
            catch (Exception ex)
            {
                // A shape we did not expect inside a known type: keep the line rather than lose it.
                parsed = new UnknownFrame(type + " (unparsed: " + ex.Message + ")");
            }

            frame = parsed with { SessionId = Str(root, "session_id"), Raw = line };
            return true;
        }
    }

    private static StreamFrame ParseSystem(JsonElement root)
    {
        string? subtype = Str(root, "subtype");
        switch (subtype)
        {
            case "init":
            {
                var servers = new List<McpServerStatus>();
                if (root.TryGetProperty("mcp_servers", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in list.EnumerateArray())
                        servers.Add(new McpServerStatus(Str(s, "name") ?? "?", Str(s, "status") ?? "?"));
                }

                return new InitFrame(Str(root, "model"), Str(root, "permissionMode"), StringArray(root, "tools"), servers,
                                     Str(root, "claude_code_version"), Str(root, "cwd"), Str(root, "apiKeySource"));
            }
            case "status":
                return new StatusFrame(Str(root, "status"));
            default:
                return new SystemFrame(subtype, Str(root, "message") ?? Str(root, "content"));
        }
    }

    private static StreamFrame ParseAssistant(JsonElement root)
    {
        var blocks = new List<ContentBlock>();
        string? messageId = null, model = null, stopReason = null;

        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            messageId  = Str(message, "id");
            model      = Str(message, "model");
            stopReason = Str(message, "stop_reason");

            if (message.TryGetProperty("content", out var content))
                blocks.AddRange(ParseBlocks(content));
        }

        return new AssistantFrame(messageId, model, blocks, stopReason, Str(root, "error"), Bool(root, "is_api_error_message"),
                                  Str(root, "uuid"), Str(root, "parent_tool_use_id"));
    }

    private static StreamFrame ParseUser(JsonElement root)
    {
        string? text = null;
        var results = new List<ToolResultBlock>();

        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                text = content.GetString();
            }
            else
            {
                var texts = new List<string>();
                foreach (var block in ParseBlocks(content))
                {
                    if (block is ToolResultBlock r) results.Add(r);
                    else if (block is TextBlock t) texts.Add(t.Text);
                }
                if (texts.Count > 0) text = string.Join("\n", texts);
            }
        }

        return new UserFrame(Bool(root, "isReplay"), text, results, Str(root, "uuid"), Str(root, "parent_tool_use_id"));
    }

    private static IEnumerable<ContentBlock> ParseBlocks(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            yield return new TextBlock(content.GetString() ?? "");
            yield break;
        }

        if (content.ValueKind != JsonValueKind.Array) yield break;

        foreach (var block in content.EnumerateArray())
        {
            string type = Str(block, "type") ?? "";
            switch (type)
            {
                case "text":
                    yield return new TextBlock(Str(block, "text") ?? "");
                    break;
                case "thinking":
                    yield return new ThinkingBlock(Str(block, "thinking") ?? "");
                    break;
                case "tool_use":
                    yield return new ToolUseBlock(Str(block, "id") ?? "", Str(block, "name") ?? "",
                                                  block.TryGetProperty("input", out var input) ? input.Clone() : null);
                    break;
                case "tool_result":
                    yield return ParseToolResult(block);
                    break;
                default:
                    yield return new OtherBlock(type.Length == 0 ? "(untyped)" : type);
                    break;
            }
        }
    }

    private static ToolResultBlock ParseToolResult(JsonElement block)
    {
        string toolUseId = Str(block, "tool_use_id") ?? "";
        bool isError = Bool(block, "is_error");
        string? imageBase64 = null, imageMediaType = null;
        var texts = new List<string>();

        if (block.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                texts.Add(content.GetString() ?? "");
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    switch (Str(part, "type"))
                    {
                        case "text":
                            texts.Add(Str(part, "text") ?? "");
                            break;
                        case "image":
                            if (part.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
                            {
                                imageBase64    ??= Str(source, "data");
                                imageMediaType ??= Str(source, "media_type");
                            }
                            break;
                    }
                }
            }
        }

        return new ToolResultBlock(toolUseId, string.Join("\n", texts), isError, imageBase64, imageMediaType);
    }

    private static StreamFrame ParseStreamEvent(JsonElement root)
    {
        string? eventType = null, blockType = null, toolUseId = null, toolName = null, deltaType = null, text = null, stopReason = null, messageId = null;
        int index = 0;

        if (root.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.Object)
        {
            eventType = Str(ev, "type");
            if (ev.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out int idx)) index = idx;

            if (ev.TryGetProperty("content_block", out var block) && block.ValueKind == JsonValueKind.Object)
            {
                blockType = Str(block, "type");
                toolUseId = Str(block, "id");
                toolName  = Str(block, "name");
                text      = Str(block, "text") ?? Str(block, "thinking");
            }

            if (ev.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                deltaType  = Str(delta, "type");
                text       = Str(delta, "text") ?? Str(delta, "partial_json") ?? Str(delta, "thinking");
                stopReason = Str(delta, "stop_reason");
            }

            if (ev.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                messageId = Str(message, "id");
        }

        return new StreamEventFrame(eventType ?? "", index, blockType, toolUseId, toolName, deltaType, text, stopReason, messageId,
                                    Str(root, "parent_tool_use_id"));
    }

    private static StreamFrame ParseResult(JsonElement root)
    {
        long input = 0, output = 0, cacheRead = 0, cacheCreate = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input       = Long(usage, "input_tokens");
            output      = Long(usage, "output_tokens");
            cacheRead   = Long(usage, "cache_read_input_tokens");
            cacheCreate = Long(usage, "cache_creation_input_tokens");
        }

        int denials = root.TryGetProperty("permission_denials", out var d) && d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : 0;

        return new ResultFrame(Str(root, "subtype"), Bool(root, "is_error"), Double(root, "total_cost_usd"), (int)Long(root, "num_turns"),
                               Long(root, "duration_ms"), Str(root, "result"), Str(root, "terminal_reason"),
                               input, output, cacheRead, cacheCreate, denials);
    }

    private static StreamFrame ParseControlRequest(JsonElement root)
    {
        string requestId = Str(root, "request_id") ?? "";
        if (!root.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object)
            return new ControlRequestFrame(requestId, "", null, null, null, null, null, null, null, null, false, false, false);

        return new ControlRequestFrame(
            requestId,
            Str(request, "subtype") ?? "",
            Str(request, "tool_name"),
            request.TryGetProperty("input", out var input) ? input.Clone() : null,
            request.TryGetProperty("permission_suggestions", out var suggestions) ? suggestions.Clone() : null,
            Str(request, "title") ?? Str(request, "display_name"),
            Str(request, "description"),
            Str(request, "decision_reason"),
            Str(request, "blocked_path"),
            Str(request, "tool_use_id"),
            Bool(request, "default_to_no"),
            Bool(request, "suppress_always_allow_rule"),
            Bool(request, "requires_user_interaction"));
    }

    private static StreamFrame ParseControlResponse(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            return new ControlResponseFrame(null, null, null, null);

        return new ControlResponseFrame(Str(response, "request_id"), Str(response, "subtype"), Str(response, "error"),
                                        response.TryGetProperty("response", out var inner) ? inner.Clone() : null);
    }

    // -------------------------------------------------------------------------

    private static string? Str(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long Long(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l
         : o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.Number ? (long)f.GetDouble() : 0;

    private static double Double(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static IReadOnlyList<string> StringArray(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var e in arr.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString() ?? "");
        return list;
    }
}

// -------------------------------------------------------------------------
// Writer
// -------------------------------------------------------------------------

/// <summary>Builds the lines we write to the CLI's stdin. One JSON object per line, no embedded newlines.</summary>
public static class StreamJsonWriter
{
    /// <summary>A user turn.</summary>
    public static string UserMessage(string text, string? sessionId = null)
    {
        var frame = new JsonObject
        {
            ["type"]    = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
            ["parent_tool_use_id"] = null,
        };
        if (!string.IsNullOrEmpty(sessionId)) frame["session_id"] = sessionId;
        return frame.ToJsonString();
    }

    /// <summary>Grants a <c>can_use_tool</c> request, echoing the input (optionally edited) and any permission updates to remember.</summary>
    public static string ControlResponseAllow(string requestId, JsonElement? updatedInput = null, JsonElement? updatedPermissions = null)
    {
        var response = new JsonObject { ["behavior"] = "allow" };
        if (updatedInput is { ValueKind: not JsonValueKind.Undefined } input) response["updatedInput"] = JsonNode.Parse(input.GetRawText());
        if (updatedPermissions is { ValueKind: JsonValueKind.Array } perms) response["updatedPermissions"] = JsonNode.Parse(perms.GetRawText());
        return ControlResponse(requestId, response);
    }

    /// <summary>Refuses a <c>can_use_tool</c> request with a message the model reads.</summary>
    public static string ControlResponseDeny(string requestId, string message)
        => ControlResponse(requestId, new JsonObject { ["behavior"] = "deny", ["message"] = message });

    private static string ControlResponse(string requestId, JsonObject payload) => new JsonObject
    {
        ["type"]     = "control_response",
        ["response"] = new JsonObject
        {
            ["subtype"]    = "success",
            ["request_id"] = requestId,
            ["response"]   = payload,
        },
    }.ToJsonString();

    /// <summary>A control request of ours; <paramref name="extra"/> fields are merged into the request object.</summary>
    public static string ControlRequest(string requestId, string subtype, JsonObject? extra = null)
    {
        var request = new JsonObject { ["subtype"] = subtype };
        if (extra != null)
        {
            foreach (var (key, value) in extra)
                request[key] = value?.DeepClone();
        }

        return new JsonObject
        {
            ["type"]       = "control_request",
            ["request_id"] = requestId,
            ["request"]    = request,
        }.ToJsonString();
    }

    /// <summary>Stops the current turn; the CLI answers with a <c>result</c>.</summary>
    public static string Interrupt(string requestId) => ControlRequest(requestId, "interrupt");

    /// <summary>Changes the permission mode for the rest of the session.</summary>
    public static string SetPermissionMode(string requestId, string mode)
        => ControlRequest(requestId, "set_permission_mode", new JsonObject { ["mode"] = mode });

    /// <summary>Changes the model for the rest of the session; null returns to the default.</summary>
    public static string SetModel(string requestId, string? model)
        => ControlRequest(requestId, "set_model", new JsonObject { ["model"] = model });
}
