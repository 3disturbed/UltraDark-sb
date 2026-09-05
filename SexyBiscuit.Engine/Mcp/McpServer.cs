using System.Text.Json;
using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>What the server says about itself in <c>initialize</c>.</summary>
public sealed record McpServerInfo(string Name, string Version, string? Instructions);

/// <summary>A client the server has heard from. Tracked by name; the transport is stateless.</summary>
public sealed class McpClientInfo
{
    public string   Name         { get; internal set; } = "unknown";
    public string   Version      { get; internal set; } = "";
    public DateTime LastSeenUtc  { get; internal set; }
    public int      RequestCount { get; internal set; }

    /// <summary>True for the editor's own Claude Code process (it sends <c>X-SexyBiscuit-Client: embedded</c>).</summary>
    public bool IsEmbedded { get; internal set; }
}

/// <summary>
/// The protocol layer: routes JSON-RPC methods to the registries, negotiates the protocol
/// version and capabilities, and maps every failure to a JSON-RPC error. Transport-agnostic and
/// stateless per request, so a client that reconnects after an idle timeout just carries on.
/// </summary>
public sealed class McpServer
{
    /// <summary>Newest first. The client's version is echoed when supported.</summary>
    public static readonly string[] SupportedProtocolVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    /// <summary>The header the embedded Claude Code session sends so the panel can tell drivers apart.</summary>
    public const string ClientHeader = "X-SexyBiscuit-Client";

    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpClientInfo>           _clients  = new(StringComparer.Ordinal);

    public McpServer(McpToolRegistry tools, McpResourceRegistry resources, McpServerInfo info)
    {
        Tools        = tools;
        Resources    = resources;
        Info         = info;
        Instructions = info.Instructions;

        tools.ToolsChanged += () => NotificationReady?.Invoke(new JsonRpcNotification { Method = "notifications/tools/list_changed" });
        resources.Changed  += () =>
        {
            NotificationReady?.Invoke(new JsonRpcNotification { Method = "notifications/resources/list_changed" });
            NotificationReady?.Invoke(new JsonRpcNotification { Method = "notifications/prompts/list_changed" });
        };
    }

    public McpToolRegistry     Tools     { get; }
    public McpResourceRegistry Resources { get; }
    public McpServerInfo       Info      { get; }

    /// <summary>Sent in <c>initialize</c>; the Assistant supplies the external-mode guidance.</summary>
    public string? Instructions { get; set; }

    /// <summary>When the last response was produced. The restart tool waits for its own result to leave.</summary>
    public DateTime LastResponseUtc { get; private set; }

    public long RequestsServed { get; private set; }

    public IReadOnlyList<McpClientInfo> Clients
    {
        get { lock (_lock) return _clients.Values.OrderByDescending(c => c.LastSeenUtc).ToArray(); }
    }

    /// <summary>Raised on the request thread whenever a client is seen.</summary>
    public event Action<McpClientInfo>? ClientActivity;

    /// <summary>Server-to-client notifications; the transport fans them out to open streams.</summary>
    public event Action<JsonRpcNotification>? NotificationReady;

    public Action<string, bool>? Log { get; set; }

    // -------------------------------------------------------------------------
    // Requests
    // -------------------------------------------------------------------------

    /// <summary>Handles one request. Never throws: every failure becomes a JSON-RPC error.</summary>
    public async Task<JsonRpcResponse> HandleRequestAsync(JsonRpcRequest request, McpCallContext context)
    {
        JsonRpcResponse response;
        try
        {
            NoteActivity(context.ClientName);
            response = await DispatchAsync(request, context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            response = JsonRpcResponse.Failure(request.Id, JsonRpcError.Internal("request cancelled"));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[mcp] {request.Method} failed: {ex}", true);
            response = JsonRpcResponse.Failure(request.Id, JsonRpcError.Internal($"{ex.GetType().Name}: {ex.Message}"));
        }

        lock (_lock)
        {
            LastResponseUtc = DateTime.UtcNow;
            RequestsServed++;
        }

        return response;
    }

    private async Task<JsonRpcResponse> DispatchAsync(JsonRpcRequest request, McpCallContext context)
    {
        var id = request.Id;
        var p  = request.Params;

        switch (request.Method)
        {
            case "initialize":
                return JsonRpcResponse.Success(id, Initialize(p, context));

            case "ping":
                return JsonRpcResponse.Success(id, new JsonObject());

            case "tools/list":
                return JsonRpcResponse.Success(id, new JsonObject { ["tools"] = Tools.DescribeForToolsList() });

            case "tools/call":
            {
                string? name = GetString(p, "name");
                if (string.IsNullOrEmpty(name))
                    return JsonRpcResponse.Failure(id, JsonRpcError.InvalidParams("tools/call requires 'name'"));

                if (Tools.Find(name) == null)
                    return JsonRpcResponse.Failure(id, JsonRpcError.InvalidParams($"Unknown tool '{name}'. Call tools/list."));

                JsonElement? arguments = p is { ValueKind: JsonValueKind.Object } po && po.TryGetProperty("arguments", out var a) ? a : null;
                var result = await Tools.InvokeAsync(name, arguments, context).ConfigureAwait(false);
                return JsonRpcResponse.Success(id, result.ToJsonNode());
            }

            case "resources/list":
            {
                var list = new JsonArray();
                foreach (var r in Resources.Resources)
                {
                    var entry = new JsonObject { ["uri"] = r.Uri, ["name"] = r.Name, ["mimeType"] = r.MimeType };
                    if (r.Description != null) entry["description"] = r.Description;
                    list.Add(entry);
                }
                return JsonRpcResponse.Success(id, new JsonObject { ["resources"] = list });
            }

            case "resources/read":
            {
                string? uri = GetString(p, "uri");
                if (string.IsNullOrEmpty(uri))
                    return JsonRpcResponse.Failure(id, JsonRpcError.InvalidParams("resources/read requires 'uri'"));

                var descriptor = Resources.FindResource(uri);
                if (descriptor == null)
                    return JsonRpcResponse.Failure(id, JsonRpcError.InvalidParams($"Unknown resource '{uri}'. Call resources/list."));

                string text = await descriptor.Read(context).ConfigureAwait(false);
                var contents = new JsonArray(new JsonObject { ["uri"] = uri, ["mimeType"] = descriptor.MimeType, ["text"] = text });
                return JsonRpcResponse.Success(id, new JsonObject { ["contents"] = contents });
            }

            case "resources/templates/list":
                return JsonRpcResponse.Success(id, new JsonObject { ["resourceTemplates"] = new JsonArray() });

            case "prompts/list":
            {
                var list = new JsonArray();
                foreach (var prompt in Resources.Prompts)
                {
                    var args = new JsonArray();
                    foreach (var arg in prompt.Arguments)
                        args.Add(new JsonObject { ["name"] = arg.Name, ["description"] = arg.Description, ["required"] = arg.Required });

                    list.Add(new JsonObject { ["name"] = prompt.Name, ["description"] = prompt.Description, ["arguments"] = args });
                }
                return JsonRpcResponse.Success(id, new JsonObject { ["prompts"] = list });
            }

            case "prompts/get":
            {
                string? name = GetString(p, "name");
                if (string.IsNullOrEmpty(name))
                    return JsonRpcResponse.Failure(id, JsonRpcError.InvalidParams("prompts/get requires 'name'"));

                var prompt = Resources.FindPrompt(name);
                if (prompt == null)
                    return JsonRpcResponse.Failure(id, JsonRpcError.InvalidParams($"Unknown prompt '{name}'."));

                var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (p is { ValueKind: JsonValueKind.Object } pp && pp.TryGetProperty("arguments", out var argObj) && argObj.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in argObj.EnumerateObject())
                        arguments[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.GetRawText();
                }

                IReadOnlyList<McpPromptMessage> messages;
                try
                {
                    messages = await prompt.Get(arguments, context).ConfigureAwait(false);
                }
                catch (McpToolException ex)
                {
                    return JsonRpcResponse.Failure(id, JsonRpcError.InvalidParams(ex.FullMessage));
                }

                var list = new JsonArray();
                foreach (var m in messages)
                    list.Add(new JsonObject { ["role"] = m.Role, ["content"] = new JsonObject { ["type"] = "text", ["text"] = m.Text } });

                return JsonRpcResponse.Success(id, new JsonObject { ["description"] = prompt.Description, ["messages"] = list });
            }

            case "logging/setLevel":
                return JsonRpcResponse.Success(id, new JsonObject());

            case "completion/complete":
                return JsonRpcResponse.Success(id, new JsonObject
                {
                    ["completion"] = new JsonObject { ["values"] = new JsonArray(), ["hasMore"] = false },
                });

            default:
                return JsonRpcResponse.Failure(id, JsonRpcError.MethodNotFound(request.Method));
        }
    }

    private JsonObject Initialize(JsonElement? p, McpCallContext context)
    {
        string requested = GetString(p, "protocolVersion") ?? SupportedProtocolVersions[0];
        string version   = SupportedProtocolVersions.Contains(requested) ? requested : SupportedProtocolVersions[0];

        if (p is { ValueKind: JsonValueKind.Object } po && po.TryGetProperty("clientInfo", out var ci) && ci.ValueKind == JsonValueKind.Object)
        {
            string name    = ci.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "unknown" : "unknown";
            string cversion = ci.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            NoteClient(name, cversion, context.ClientName == "embedded");
        }

        var result = new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"]     = new JsonObject { ["listChanged"] = true },
                ["resources"] = new JsonObject { ["subscribe"] = false, ["listChanged"] = true },
                ["prompts"]   = new JsonObject { ["listChanged"] = true },
                ["logging"]   = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject { ["name"] = Info.Name, ["version"] = Info.Version },
        };

        if (!string.IsNullOrEmpty(Instructions)) result["instructions"] = Instructions;
        return result;
    }

    // -------------------------------------------------------------------------
    // Notifications and cancellation
    // -------------------------------------------------------------------------

    /// <summary>Handles a client notification. <c>initialized</c> is a no-op; <c>cancelled</c> cancels a running call.</summary>
    public void HandleNotification(JsonRpcRequest notification)
    {
        switch (notification.Method)
        {
            case "notifications/cancelled":
            {
                if (notification.Params is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("requestId", out var rid))
                    TryCancel(rid.GetRawText());
                break;
            }
        }
    }

    /// <summary>Cancels the in-flight request with this id key. Returns false when none is running.</summary>
    public bool TryCancel(string requestIdKey)
    {
        CancellationTokenSource? cts;
        lock (_lock) _inFlight.TryGetValue(requestIdKey, out cts);

        if (cts == null) return false;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        return true;
    }

    /// <summary>Registers a request's cancellation source for the duration of the returned scope.</summary>
    public IDisposable TrackRequest(string requestIdKey, CancellationTokenSource cts)
    {
        if (string.IsNullOrEmpty(requestIdKey)) return NoopScope.Instance;

        lock (_lock) _inFlight[requestIdKey] = cts;
        return new TrackingScope(this, requestIdKey);
    }

    private sealed class TrackingScope : IDisposable
    {
        private McpServer? _owner;
        private readonly string _key;

        public TrackingScope(McpServer owner, string key)
        {
            _owner = owner;
            _key   = key;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner == null) return;
            lock (owner._lock) owner._inFlight.Remove(_key);
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }

    // -------------------------------------------------------------------------
    // Clients
    // -------------------------------------------------------------------------

    /// <summary>Records a client from <c>initialize</c>.</summary>
    public void NoteClient(string name, string version, bool embedded)
    {
        McpClientInfo info;
        lock (_lock)
        {
            string key = embedded ? "embedded" : name;
            if (!_clients.TryGetValue(key, out info!))
            {
                info = new McpClientInfo { IsEmbedded = embedded };
                _clients[key] = info;
            }

            info.Name        = name;
            info.Version     = version;
            info.LastSeenUtc = DateTime.UtcNow;
            info.RequestCount++;
        }

        ClientActivity?.Invoke(info);
    }

    // Later requests carry no clientInfo. The embedded client identifies itself with a header;
    // anything else is attributed to the most recently initialised external client.
    private void NoteActivity(string? clientHeader)
    {
        McpClientInfo? info;
        lock (_lock)
        {
            if (clientHeader == "embedded")
                _clients.TryGetValue("embedded", out info);
            else
                info = _clients.Values.Where(c => !c.IsEmbedded).OrderByDescending(c => c.LastSeenUtc).FirstOrDefault();

            if (info == null) return;
            info.LastSeenUtc = DateTime.UtcNow;
            info.RequestCount++;
        }

        ClientActivity?.Invoke(info);
    }

    private static string? GetString(JsonElement? p, string property)
        => p is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
