using System.Text.Json;
using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>The JSON-RPC 2.0 error codes the server emits. MCP defines no others.</summary>
public static class JsonRpcErrorCodes
{
    public const int ParseError     = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams  = -32602;
    public const int InternalError  = -32603;
}

/// <summary>A JSON-RPC 2.0 error object.</summary>
public sealed class JsonRpcError
{
    public int       Code    { get; init; }
    public string    Message { get; init; } = "";
    public JsonNode? Data    { get; init; }

    public static JsonRpcError Parse(string detail)
        => new() { Code = JsonRpcErrorCodes.ParseError, Message = "Parse error: " + detail };

    public static JsonRpcError InvalidRequest(string detail)
        => new() { Code = JsonRpcErrorCodes.InvalidRequest, Message = "Invalid request: " + detail };

    public static JsonRpcError MethodNotFound(string method)
        => new() { Code = JsonRpcErrorCodes.MethodNotFound, Message = $"Method not found: {method}" };

    public static JsonRpcError InvalidParams(string detail)
        => new() { Code = JsonRpcErrorCodes.InvalidParams, Message = "Invalid params: " + detail };

    public static JsonRpcError Internal(string detail)
        => new() { Code = JsonRpcErrorCodes.InternalError, Message = "Internal error: " + detail };

    public JsonObject ToJsonObject()
    {
        var o = new JsonObject { ["code"] = Code, ["message"] = Message };
        if (Data != null) o["data"] = Data.DeepClone();
        return o;
    }
}

/// <summary>
/// An incoming JSON-RPC 2.0 request or notification. A notification has no <see cref="Id"/>.
/// </summary>
public sealed class JsonRpcRequest
{
    /// <summary>A string or number, or null for a notification.</summary>
    public JsonElement? Id     { get; init; }
    public string       Method { get; init; } = "";
    public JsonElement? Params { get; init; }

    public bool IsNotification => Id is null;

    /// <summary>A stable key for the id, used to correlate cancellations with running calls.</summary>
    public string IdKey => Id is { } id ? id.GetRawText() : "";

    /// <summary>
    /// Parses one message. Distinguishes text that is not JSON (-32700) from JSON that is not
    /// a request object (-32600), which is what a client needs to tell a broken pipe from a
    /// broken caller.
    /// </summary>
    /// <remarks>
    /// Batches (a JSON array of requests) are rejected: the 2025-06-18 revision of MCP
    /// removed them, and the clients this server exists for never send one.
    /// </remarks>
    public static bool TryParse(string body, out JsonRpcRequest? request, out JsonRpcError? error)
    {
        request = null;
        error   = null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            error = JsonRpcError.Parse(ex.Message);
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                error = JsonRpcError.InvalidRequest("JSON-RPC batching is not supported");
                return false;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = JsonRpcError.InvalidRequest("expected a JSON object");
                return false;
            }

            if (!root.TryGetProperty("jsonrpc", out var version)
                || version.ValueKind != JsonValueKind.String
                || version.GetString() != "2.0")
            {
                error = JsonRpcError.InvalidRequest("jsonrpc must be \"2.0\"");
                return false;
            }

            if (!root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
            {
                error = JsonRpcError.InvalidRequest("method must be a string");
                return false;
            }

            JsonElement? id = null;
            if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null)
            {
                if (idElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                {
                    error = JsonRpcError.InvalidRequest("id must be a string or a number");
                    return false;
                }

                // Clone: the document is disposed when this method returns.
                id = idElement.Clone();
            }

            JsonElement? parameters = root.TryGetProperty("params", out var p) ? p.Clone() : null;

            request = new JsonRpcRequest
            {
                Id     = id,
                Method = method.GetString()!,
                Params = parameters,
            };
            return true;
        }
    }
}

/// <summary>An outgoing JSON-RPC 2.0 response: exactly one of result or error.</summary>
public sealed class JsonRpcResponse
{
    public JsonElement?  Id     { get; init; }
    public JsonNode?     Result { get; init; }
    public JsonRpcError? Error  { get; init; }

    public static JsonRpcResponse Success(JsonElement? id, JsonNode result) => new() { Id = id, Result = result };
    public static JsonRpcResponse Failure(JsonElement? id, JsonRpcError error) => new() { Id = id, Error = error };

    public JsonObject ToJsonObject()
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0" };
        o["id"] = Id is { } id ? JsonValue.Create(id) : null;

        if (Error != null)
            o["error"] = Error.ToJsonObject();
        else
            o["result"] = Result?.DeepClone() ?? new JsonObject();

        return o;
    }

    /// <summary>Compact JSON. SSE data lines may not contain raw newlines, so never indent.</summary>
    public string ToJson() => ToJsonObject().ToJsonString(McpJson.Compact);
}

/// <summary>A server-to-client JSON-RPC notification (no id, no reply).</summary>
public sealed class JsonRpcNotification
{
    public string    Method { get; init; } = "";
    public JsonNode? Params { get; init; }

    public JsonObject ToJsonObject()
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = Method };
        if (Params != null) o["params"] = Params.DeepClone();
        return o;
    }

    public string ToJson() => ToJsonObject().ToJsonString(McpJson.Compact);
}
