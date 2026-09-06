using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Mcp;
using SexyBiscuit.Engine.Rendering;
using Xunit;

namespace SexyBiscuit.Tests;

// -------------------------------------------------------------------------
// Shared fixtures
// -------------------------------------------------------------------------

/// <summary>Tools with one of every parameter shape the schema builder must handle.</summary>
public sealed class McpTestTools
{
    public int NothingCalls;

    [McpTool("echo", "Echoes text.")]
    public string Echo([McpParam("Text to echo")] string text) => text;

    [McpTool("add", "Adds two integers.")]
    public object Add([McpParam("First")] int a, [McpParam("Second")] int b = 1) => new { sum = a + b };

    [McpTool("fail", "Always fails.")]
    public string Fail() => throw new McpToolException("Deliberate failure.", "Try echo instead.");

    [McpTool("crash", "Throws something unexpected.")]
    public string Crash() => throw new InvalidOperationException("boom");

    [McpTool("slow", "Waits for a while.", MainThread = false)]
    public async Task<string> Slow([McpParam("Milliseconds")] int ms, CancellationToken cancellation)
    {
        await Task.Delay(ms, cancellation);
        return "done";
    }

    [McpTool("light", "Takes an enum.")]
    public string Light([McpParam("Light type")] LightType type) => type.ToString();

    [McpTool("move", "Takes a vector and an optional colour.")]
    public object Move([McpParam("Position")] Vector3 position, [McpParam("Tint")] Color? tint = null)
        => new { position, tint };

    [McpTool("mutate", "A mutating tool.", Mutating = true, Label = "Mutate {name}")]
    public string Mutate([McpParam("Name")] string name) => "mutated " + name;

    [McpTool("image", "Returns an image.")]
    public McpToolResult Image() => McpToolResult.Image(new byte[] { 1, 2, 3 }, "a picture");

    [McpTool("nothing", "Returns nothing.")]
    public void Nothing() => NothingCalls++;
}

public sealed class McpExtraTools
{
    [McpTool("extra", "Registered after start-up.")]
    public string Extra() => "extra";
}

public sealed class McpTestResources
{
    [McpResource("test://doc", "Doc", "text/plain", Description = "A document.")]
    public string Doc() => "hello resource";

    [McpPrompt("greet", "Greets someone.")]
    public string Greet([McpParam("Who to greet")] string name) => $"Say hello to {name}.";
}

/// <summary>A live server on a free loopback port, with short SSE timings so tests stay fast.</summary>
internal sealed class McpTestServer : IAsyncDisposable
{
    public McpToolRegistry     Registry  { get; }
    public McpResourceRegistry Resources { get; }
    public McpServer           Server    { get; }
    public McpHttpTransport    Transport { get; }
    public HttpClient          Client    { get; }

    public Uri Url => Transport.BoundUrl!;

    public McpTestServer(string? bearerToken = null)
    {
        Registry  = new McpToolRegistry(InlineMcpDispatcher.Instance);
        Resources = new McpResourceRegistry(InlineMcpDispatcher.Instance);
        Registry.RegisterInstance(new McpTestTools());
        Resources.RegisterInstance(new McpTestResources());

        Server = new McpServer(Registry, Resources, new McpServerInfo("test-server", "0.0.1", "Test instructions."));

        // HttpListener will not pick an ephemeral port for us, so ask the OS for one and let
        // the transport walk upwards if someone grabs it in between.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        Transport = new McpHttpTransport(Server, new McpHttpTransportOptions
        {
            Port              = port,
            PortSearchRange   = 20,
            BearerToken       = bearerToken,
            SseSwitchDelay    = TimeSpan.FromMilliseconds(200),
            KeepAliveInterval = TimeSpan.FromMilliseconds(150),
        });
        Transport.Start();

        Client = new HttpClient { BaseAddress = Url, Timeout = TimeSpan.FromSeconds(30) };
        Client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        Client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (bearerToken != null)
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public static string Request(string method, object? parameters = null, object? id = null)
    {
        var message = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id != null) message["id"] = id;
        if (parameters != null) message["params"] = parameters;
        return JsonSerializer.Serialize(message);
    }

    public Task<HttpResponseMessage> PostRawAsync(string body)
        => Client.PostAsync("", new StringContent(body, Encoding.UTF8, "application/json"));

    /// <summary>Posts a request and returns the JSON-RPC response, whether it came as JSON or SSE.</summary>
    public async Task<JsonDocument> CallAsync(string method, object? parameters = null, object? id = null)
    {
        var response = await PostRawAsync(Request(method, parameters, id ?? 1));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ParseAsync(response);
    }

    public static async Task<JsonDocument> ParseAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();

        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            string? data = body.Split('\n').LastOrDefault(l => l.StartsWith("data: ", StringComparison.Ordinal));
            Assert.NotNull(data);
            return JsonDocument.Parse(data!["data: ".Length..]);
        }

        return JsonDocument.Parse(body);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Transport.DisposeAsync();
    }
}

// -------------------------------------------------------------------------
// Protocol over real HTTP
// -------------------------------------------------------------------------

public class McpProtocolTests
{
    private static object InitParams(string version) => new
    {
        protocolVersion = version,
        capabilities    = new { },
        clientInfo      = new { name = "xunit", version = "1.0" },
    };

    [Fact]
    public async Task InitializeEchoesASupportedProtocolVersion()
    {
        await using var server = new McpTestServer();
        using var doc = await server.CallAsync("initialize", InitParams("2025-03-26"));

        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("2025-03-26", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("test-server", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("Test instructions.", result.GetProperty("instructions").GetString());
        Assert.True(result.GetProperty("capabilities").GetProperty("tools").GetProperty("listChanged").GetBoolean());

        // The client that introduced itself is now known to the server.
        Assert.Contains(server.Server.Clients, c => c.Name == "xunit" && !c.IsEmbedded);
    }

    [Fact]
    public async Task InitializeFallsBackToTheLatestVersionForAnUnknownOne()
    {
        await using var server = new McpTestServer();
        using var doc = await server.CallAsync("initialize", InitParams("1999-01-01"));

        Assert.Equal(McpServer.SupportedProtocolVersions[0],
            doc.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task TheInitializedNotificationReturns202WithNoBody()
    {
        await using var server = new McpTestServer();
        var response = await server.PostRawAsync(McpTestServer.Request("notifications/initialized"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ToolsListDescribesEveryToolWithAnObjectSchema()
    {
        await using var server = new McpTestServer();
        using var doc = await server.CallAsync("tools/list");

        var tools = doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "echo");

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrEmpty(tool.GetProperty("description").GetString()));
            Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
        }

        var echo = tools.Single(t => t.GetProperty("name").GetString() == "echo");
        Assert.Contains("text", echo.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.True(echo.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
    }

    [Fact]
    public async Task ToolsCallReturnsTextContentOnSuccess()
    {
        await using var server = new McpTestServer();
        using var doc = await server.CallAsync("tools/call", new { name = "echo", arguments = new { text = "hi" } });

        var result = doc.RootElement.GetProperty("result");
        var block  = result.GetProperty("content")[0];
        Assert.Equal("text", block.GetProperty("type").GetString());
        Assert.Equal("hi", block.GetProperty("text").GetString());
        Assert.False(result.TryGetProperty("isError", out _));
    }

    [Fact]
    public async Task ObjectResultsAreSentOnceAsCompactTextLedByTheSummary()
    {
        // Claude Code forwards structuredContent and drops the text when both are present, so the
        // summary line and every warning never reached the model, and the wire carried the
        // payload twice. One compact text copy now.
        await using var server = new McpTestServer();
        using var doc = await server.CallAsync("tools/call", new { name = "add", arguments = new { a = 2, b = 3 } });

        var result = doc.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("structuredContent", out _));
        Assert.Equal("{\"sum\":5}", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task StructuredContentIsEmittedOnlyWhenTheRegistryOptsIn()
    {
        await using var server = new McpTestServer();
        server.Registry.EmitStructuredContent = true;
        using var doc = await server.CallAsync("tools/call", new { name = "add", arguments = new { a = 2, b = 3 } });

        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(5, result.GetProperty("structuredContent").GetProperty("sum").GetInt32());
    }

    [Fact]
    public async Task AHandlerExceptionIsAnErrorResultNotAProtocolError()
    {
        await using var server = new McpTestServer();

        using var failed = await server.CallAsync("tools/call", new { name = "fail", arguments = new { } });
        var result = failed.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("Deliberate failure.", text);
        Assert.Contains("Try echo instead.", text);
        Assert.False(failed.RootElement.TryGetProperty("error", out _));

        // An unexpected exception is still a result — Claude can read it and change course.
        using var crashed = await server.CallAsync("tools/call", new { name = "crash", arguments = new { } });
        var crash = crashed.RootElement.GetProperty("result");
        Assert.True(crash.GetProperty("isError").GetBoolean());
        Assert.Contains("InvalidOperationException", crash.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task AMissingRequiredArgumentIsAnErrorResult()
    {
        await using var server = new McpTestServer();
        using var doc = await server.CallAsync("tools/call", new { name = "add", arguments = new { } });

        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("'a'", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task CallingAnUnknownToolIsAnInvalidParamsError()
    {
        await using var server = new McpTestServer();
        using var doc = await server.CallAsync("tools/call", new { name = "nope", arguments = new { } });

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task MalformedJsonIsAParseError()
    {
        await using var server = new McpTestServer();
        var response = await server.PostRawAsync("{not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonRpcErrorCodes.ParseError, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ABatchArrayIsAnInvalidRequest()
    {
        await using var server = new McpTestServer();
        var response = await server.PostRawAsync("[]");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonRpcErrorCodes.InvalidRequest, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task AnUnknownMethodIsMethodNotFound()
    {
        await using var server = new McpTestServer();
        using var doc = await server.CallAsync("frobnicate");

        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task PingReturnsAnEmptyObject()
    {
        await using var server = new McpTestServer();
        using var doc = await server.CallAsync("ping");

        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("result").ValueKind);
        Assert.Empty(doc.RootElement.GetProperty("result").EnumerateObject());
    }

    [Fact]
    public async Task AFastToolAnswersAsPlainJson()
    {
        await using var server = new McpTestServer();
        var response = await server.PostRawAsync(McpTestServer.Request("tools/call", new { name = "echo", arguments = new { text = "x" } }, 1));

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ASlowToolSwitchesToServerSentEventsWithKeepalives()
    {
        await using var server = new McpTestServer();
        var response = await server.PostRawAsync(McpTestServer.Request("tools/call", new { name = "slow", arguments = new { ms = 700 } }, 1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains(": keepalive", body);
        Assert.Contains("event: message", body);

        using var doc = await McpTestServer.ParseAsync(response);
        Assert.Equal("done", doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task AClientWithoutEventStreamAcceptGetsPlainJsonEvenForSlowTools()
    {
        await using var server = new McpTestServer();
        server.Client.DefaultRequestHeaders.Accept.Clear();
        server.Client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await server.PostRawAsync(McpTestServer.Request("tools/call", new { name = "slow", arguments = new { ms = 400 } }, 1));

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var doc = await McpTestServer.ParseAsync(response);
        Assert.Equal("done", doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task GetOpensAStreamThatReceivesToolsListChanged()
    {
        await using var server = new McpTestServer();

        using var request = new HttpRequestMessage(HttpMethod.Get, "");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await server.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));

        // Wait for the greeting so we know the stream is registered before changing the tools.
        string? line;
        do
        {
            line = await reader.ReadLineAsync(cts.Token);
        } while (line != null && !line.StartsWith(": connected", StringComparison.Ordinal));
        Assert.NotNull(line);
        Assert.Equal(1, server.Transport.OpenStreams);

        server.Registry.RegisterInstance(new McpExtraTools());

        string? data = null;
        while ((line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            data = line["data: ".Length..];
            if (data.Contains("tools/list_changed")) break;
        }

        Assert.NotNull(data);
        using var doc = JsonDocument.Parse(data!);
        Assert.Equal("notifications/tools/list_changed", doc.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task GetWithoutEventStreamAcceptIs405()
    {
        await using var server = new McpTestServer();

        using var request = new HttpRequestMessage(HttpMethod.Get, "");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await server.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task RequestsWithoutTheBearerTokenAreRejectedWhenOneIsConfigured()
    {
        await using var server = new McpTestServer(bearerToken: "secret");

        server.Client.DefaultRequestHeaders.Authorization = null;
        var denied = await server.PostRawAsync(McpTestServer.Request("ping", null, 1));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        server.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        var allowed = await server.PostRawAsync(McpTestServer.Request("ping", null, 1));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task ANonLoopbackOriginIsRefused()
    {
        await using var server = new McpTestServer();

        server.Client.DefaultRequestHeaders.Add("Origin", "http://evil.example");
        var refused = await server.PostRawAsync(McpTestServer.Request("ping", null, 1));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        server.Client.DefaultRequestHeaders.Remove("Origin");
        server.Client.DefaultRequestHeaders.Add("Origin", "http://localhost:3000");
        var allowed = await server.PostRawAsync(McpTestServer.Request("ping", null, 1));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task ResourcesListAndReadRoundTrip()
    {
        await using var server = new McpTestServer();

        using var list = await server.CallAsync("resources/list");
        var resources = list.RootElement.GetProperty("result").GetProperty("resources").EnumerateArray().ToList();
        Assert.Contains(resources, r => r.GetProperty("uri").GetString() == "test://doc");

        using var read = await server.CallAsync("resources/read", new { uri = "test://doc" });
        var content = read.RootElement.GetProperty("result").GetProperty("contents")[0];
        Assert.Equal("hello resource", content.GetProperty("text").GetString());
        Assert.Equal("text/plain", content.GetProperty("mimeType").GetString());

        using var missing = await server.CallAsync("resources/read", new { uri = "test://nope" });
        Assert.Equal(JsonRpcErrorCodes.InvalidParams, missing.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task PromptsListAndGetRoundTrip()
    {
        await using var server = new McpTestServer();

        using var list = await server.CallAsync("prompts/list");
        var prompt = list.RootElement.GetProperty("result").GetProperty("prompts").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "greet");
        Assert.True(prompt.GetProperty("arguments")[0].GetProperty("required").GetBoolean());

        using var get = await server.CallAsync("prompts/get", new { name = "greet", arguments = new { name = "Bob" } });
        var message = get.RootElement.GetProperty("result").GetProperty("messages")[0];
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("Say hello to Bob.", message.GetProperty("content").GetProperty("text").GetString());
    }

    [Fact]
    public async Task ACancelledNotificationStopsARunningTool()
    {
        await using var server = new McpTestServer();

        var pending = server.PostRawAsync(McpTestServer.Request("tools/call", new { name = "slow", arguments = new { ms = 10000 } }, 77));
        await Task.Delay(300);

        var cancel = await server.PostRawAsync(McpTestServer.Request("notifications/cancelled", new { requestId = 77 }));
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);

        var response = await pending;
        using var doc = await McpTestServer.ParseAsync(response);
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("Cancelled", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task StopFailsInFlightCallsAndClosesStreams()
    {
        var server = new McpTestServer();

        var pending = server.PostRawAsync(McpTestServer.Request("tools/call", new { name = "slow", arguments = new { ms = 10000 } }, 5));
        await Task.Delay(300);

        var stopped = server.Transport.StopAsync();
        var finished = await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(stopped, finished);
        Assert.False(server.Transport.IsRunning);

        // The client sees either a cancelled result or a connection closed before the final
        // event (the listener may shut the socket before the cancelled result is written).
        // Either way it never hangs.
        try
        {
            var response = await pending;
            string body  = await response.Content.ReadAsStringAsync();
            string? data = body.Split('\n').LastOrDefault(l => l.StartsWith("data: ", StringComparison.Ordinal));

            if (data != null)
            {
                using var doc = JsonDocument.Parse(data["data: ".Length..]);
                Assert.True(doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
        }

        server.Client.Dispose();
    }
}

// -------------------------------------------------------------------------
// Registry, schema and binding
// -------------------------------------------------------------------------

public class McpRegistryTests
{
    private static McpToolRegistry NewRegistry(out McpTestTools tools)
    {
        var registry = new McpToolRegistry(InlineMcpDispatcher.Instance);
        tools = new McpTestTools();
        registry.RegisterInstance(tools);
        return registry;
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void SchemaMarksParametersWithoutDefaultsAsRequired()
    {
        var registry = NewRegistry(out _);
        var schema   = registry.Find("add")!.InputSchema;

        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("a", required);
        Assert.DoesNotContain("b", required);
        Assert.Equal(1, schema["properties"]!["b"]!["default"]!.GetValue<int>());
    }

    [Fact]
    public void SchemaMapsEachSupportedParameterType()
    {
        var registry = NewRegistry(out _);

        Assert.Equal("string",  registry.Find("echo")!.InputSchema["properties"]!["text"]!["type"]!.GetValue<string>());
        Assert.Equal("integer", registry.Find("add")!.InputSchema["properties"]!["a"]!["type"]!.GetValue<string>());

        var light = registry.Find("light")!.InputSchema["properties"]!["type"]!;
        Assert.Equal("string", light["type"]!.GetValue<string>());
        Assert.Contains("Directional", light["enum"]!.AsArray().Select(n => n!.GetValue<string>()));

        var move = registry.Find("move")!.InputSchema;
        Assert.Equal("array", move["properties"]!["position"]!["type"]!.GetValue<string>());
        Assert.Equal(3, move["properties"]!["position"]!["minItems"]!.GetValue<int>());
        Assert.Equal("string", move["properties"]!["tint"]!["type"]!.GetValue<string>());

        // A nullable colour is optional even though it has no default.
        Assert.DoesNotContain("tint", move["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void SchemaCarriesDescriptions()
    {
        var registry = NewRegistry(out _);
        Assert.Contains("Text to echo", registry.Find("echo")!.InputSchema["properties"]!["text"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void InjectedParametersAreHiddenFromTheSchema()
    {
        var registry = NewRegistry(out _);
        var properties = registry.Find("slow")!.InputSchema["properties"]!.AsObject();

        Assert.Single(properties);
        Assert.True(properties.ContainsKey("ms"));
    }

    [Fact]
    public async Task ArgumentsBindByNameCaseInsensitively()
    {
        var registry = NewRegistry(out _);

        var exact = await registry.InvokeAsync("echo", Args(new { text = "a" }), McpCallContext.None);
        var upper = await registry.InvokeAsync("echo", Args(new Dictionary<string, string> { ["TEXT"] = "b" }), McpCallContext.None);

        Assert.Equal("a", exact.FirstText);
        Assert.Equal("b", upper.FirstText);
    }

    [Fact]
    public async Task ArgumentsConvertVectorsEnumsAndColours()
    {
        var registry = NewRegistry(out _);

        var moved = await registry.InvokeAsync("move", Args(new { position = new[] { 1, 2, 3 }, tint = "#FF0000" }), McpCallContext.None);
        Assert.False(moved.IsError, moved.FirstText);
        Assert.Contains("\"tint\":\"#FF0000FF\"", moved.FirstText);

        var lit = await registry.InvokeAsync("light", Args(new { type = "spot" }), McpCallContext.None);
        Assert.Equal("Spot", lit.FirstText);

        var bad = await registry.InvokeAsync("light", Args(new { type = "Laser" }), McpCallContext.None);
        Assert.True(bad.IsError);
        Assert.Contains("Directional", bad.FirstText);
    }

    [Fact]
    public async Task MutatingToolsRunTheBeforeMutationHook()
    {
        var registry = NewRegistry(out _);
        int snapshots = 0;
        registry.BeforeMutation = (_, _, _) => snapshots++;

        await registry.InvokeAsync("mutate", Args(new { name = "Cube" }), McpCallContext.None);
        await registry.InvokeAsync("echo", Args(new { text = "x" }), McpCallContext.None);

        Assert.Equal(1, snapshots);
    }

    [Fact]
    public async Task EveryToolRunsTheAfterInvokeHook()
    {
        var registry = NewRegistry(out _);
        var seen = new List<string>();
        registry.AfterInvoke = (d, _, _) => seen.Add(d.Name);

        await registry.InvokeAsync("echo", Args(new { text = "x" }), McpCallContext.None);
        await registry.InvokeAsync("mutate", Args(new { name = "y" }), McpCallContext.None);
        await registry.InvokeAsync("slow", Args(new { ms = 1 }), McpCallContext.None);

        Assert.Equal(new[] { "echo", "mutate", "slow" }, seen);
    }

    [Fact]
    public void RegisteringAToolRaisesToolsChangedOnceWhenSuspended()
    {
        var registry = new McpToolRegistry(InlineMcpDispatcher.Instance);
        int changes = 0;
        registry.ToolsChanged += () => changes++;

        registry.RegisterInstance(new McpTestTools());
        Assert.Equal(1, changes);

        using (registry.SuspendNotifications())
        {
            registry.RegisterInstance(new McpExtraTools());
            registry.Unregister("extra");
            Assert.Equal(1, changes);
        }

        Assert.Equal(2, changes);
    }

    [Fact]
    public void UnregisteringARegistrationRemovesAllItsTools()
    {
        var registry = new McpToolRegistry(InlineMcpDispatcher.Instance);
        var first  = registry.RegisterInstance(new McpTestTools());
        var second = registry.RegisterInstance(new McpExtraTools(), new McpRegistrationOptions { NamePrefix = "game_", Source = "project" });

        Assert.NotNull(registry.Find("game_extra"));
        Assert.Equal("project", registry.Find("game_extra")!.Source);

        Assert.Equal(first.ToolNames.Count, registry.Unregister(first));
        Assert.Null(registry.Find("echo"));
        Assert.NotNull(registry.Find("game_extra"));
        Assert.Equal(1, registry.Unregister(second));
    }

    [Fact]
    public void AToolNameCollisionIsRejected()
    {
        var registry = new McpToolRegistry(InlineMcpDispatcher.Instance);
        registry.RegisterInstance(new McpTestTools());

        Assert.Throws<InvalidOperationException>(() => registry.RegisterInstance(new McpTestTools()));
    }

    [Fact]
    public async Task ReturnValuesAreNormalisedIntoResults()
    {
        var registry = NewRegistry(out var tools);

        var text = await registry.InvokeAsync("echo", Args(new { text = "plain" }), McpCallContext.None);
        Assert.Equal("plain", text.FirstText);
        Assert.Null(text.StructuredContent);

        var json = await registry.InvokeAsync("add", Args(new { a = 1 }), McpCallContext.None);
        Assert.Equal(2, json.StructuredContent!["sum"]!.GetValue<int>());

        var nothing = await registry.InvokeAsync("nothing", null, McpCallContext.None);
        Assert.Equal("OK", nothing.FirstText);
        Assert.Equal(1, tools.NothingCalls);

        var image = await registry.InvokeAsync("image", null, McpCallContext.None);
        Assert.Contains(image.Content, c => c is McpImageContent { MimeType: "image/png" });
        Assert.Equal("image", image.ToJsonNode()["content"]![1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheActivityLogRecordsEveryCall()
    {
        var registry = NewRegistry(out _);
        var log = new ActivityLog();
        registry.Activity = log;

        await registry.InvokeAsync("mutate", Args(new { name = "Cube" }), new McpCallContext { ClientName = "tester" });
        await registry.InvokeAsync("fail", null, McpCallContext.None);

        var entries = log.Snapshot();
        Assert.Equal(2, entries.Count);

        Assert.Equal("mutate", entries[0].Tool);
        Assert.Equal("Mutate Cube", entries[0].Label);
        Assert.True(entries[0].Mutating);
        Assert.Equal(ActivityState.Succeeded, entries[0].State);
        Assert.Equal("tester", entries[0].Client);

        Assert.Equal(ActivityState.Failed, entries[1].State);
        Assert.Contains("Deliberate failure", entries[1].Summary);
    }

    [Fact]
    public void DescribeMarkdownListsEveryToolWithItsParameters()
    {
        var registry = NewRegistry(out _);
        string table = registry.DescribeMarkdown();

        Assert.Contains("| `add` |", table);
        Assert.Contains("`b`: integer (default 1)", table);
        Assert.DoesNotContain("cancellation", table);
    }
}

// -------------------------------------------------------------------------
// Value conversion
// -------------------------------------------------------------------------

public class ValueConverterTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData("\"#FF0000\"", 255, 0, 0, 255)]
    [InlineData("\"#00FF0080\"", 0, 255, 0, 128)]
    [InlineData("\"CornflowerBlue\"", 100, 149, 237, 255)]
    [InlineData("{\"R\":10,\"G\":20,\"B\":30,\"A\":40}", 10, 20, 30, 40)]
    [InlineData("[10, 20, 30]", 10, 20, 30, 255)]
    [InlineData("[1.0, 0.5, 0.0]", 255, 128, 0, 255)]
    public void ColoursReadFromHexNamesObjectsAndArrays(string json, int r, int g, int b, int a)
    {
        var colour = (Color)ValueConverter.FromJson(Json(json), typeof(Color))!;
        Assert.Equal(new Color(r, g, b, a), colour);
    }

    [Fact]
    public void AnUnknownColourNameIsAnActionableError()
    {
        var ex = Assert.Throws<McpToolException>(() => ValueConverter.FromJson(Json("\"Purpleish\""), typeof(Color)));
        Assert.Contains("#RRGGBB", ex.Message);
    }

    [Fact]
    public void VectorsReadFromArraysAndObjects()
    {
        Assert.Equal(new Vector3(1, 2, 3), (Vector3)ValueConverter.FromJson(Json("[1, 2, 3]"), typeof(Vector3))!);
        Assert.Equal(new Vector3(1, 2, 3), (Vector3)ValueConverter.FromJson(Json("{\"x\":1,\"Y\":2,\"z\":3}"), typeof(Vector3))!);
        Assert.Equal(new Vector2(4, 5), (Vector2)ValueConverter.FromJson(Json("[4, 5]"), typeof(Vector2))!);

        var ex = Assert.Throws<McpToolException>(() => ValueConverter.FromJson(Json("[1, 2]"), typeof(Vector3)));
        Assert.Contains("3-element", ex.Message);
    }

    [Fact]
    public void RotationsReadFromEulerDegreesAndFromQuaternions()
    {
        var fromEuler = (Quaternion)ValueConverter.FromJson(Json("[0, 90, 0]"), typeof(Quaternion))!;
        var expected  = Quaternion.CreateFromYawPitchRoll(MathHelper.ToRadians(90f), 0f, 0f);
        Assert.Equal(expected.Y, fromEuler.Y, 4);
        Assert.Equal(expected.W, fromEuler.W, 4);

        var fromXyzw = (Quaternion)ValueConverter.FromJson(Json("[0, 0, 0, 1]"), typeof(Quaternion))!;
        Assert.Equal(Quaternion.Identity, fromXyzw);

        var fromNamed = (Quaternion)ValueConverter.FromJson(Json("{\"yaw\": 90}"), typeof(Quaternion))!;
        Assert.Equal(expected.Y, fromNamed.Y, 4);
    }

    [Fact]
    public void EnumsReadByNameCaseInsensitivelyAndRejectUnknownNames()
    {
        Assert.Equal(LightType.Point, ValueConverter.FromJson(Json("\"point\""), typeof(LightType)));

        var ex = Assert.Throws<McpToolException>(() => ValueConverter.FromJson(Json("\"Laser\""), typeof(LightType)));
        Assert.Contains("Directional, Point, Spot", ex.Message);
    }

    [Fact]
    public void NumbersRejectFractionsForIntegers()
    {
        Assert.Equal(3, ValueConverter.FromJson(Json("3.0"), typeof(int)));
        Assert.Equal(2.5f, (float)ValueConverter.FromJson(Json("2.5"), typeof(float))!);
        Assert.Throws<McpToolException>(() => ValueConverter.FromJson(Json("2.5"), typeof(int)));
    }

    [Fact]
    public void NullableTargetsAcceptNull()
    {
        Assert.Null(ValueConverter.FromJson(Json("null"), typeof(Color?)));
        Assert.Null(ValueConverter.FromJson(Json("null"), typeof(string)));
        Assert.Throws<McpToolException>(() => ValueConverter.FromJson(Json("null"), typeof(int)));
    }

    [Fact]
    public void ValuesWriteInTheirReadableForms()
    {
        Assert.Equal("\"#FF0000FF\"", ValueConverter.ToJson(Color.Red)!.ToJsonString());
        Assert.Equal("[1,2,3]", ValueConverter.ToJson(new Vector3(1, 2, 3))!.ToJsonString());
        Assert.Equal("\"Spot\"", ValueConverter.ToJson(LightType.Spot)!.ToJsonString());
        Assert.Equal("0.3333", ValueConverter.ToJson(1f / 3f)!.ToJsonString());

        // 45, not 90: exactly 90 degrees of yaw sits in the gimbal-lock region where the
        // quaternion-to-Euler asin loses precision, as the serializer tests already note.
        var rotation = Quaternion.CreateFromYawPitchRoll(MathHelper.ToRadians(45f), 0f, 0f);
        var euler = ValueConverter.ToJson(rotation)!.AsArray();
        Assert.Equal(45f, euler[1]!.GetValue<float>(), 2);
    }
}

// -------------------------------------------------------------------------
// The queued dispatcher
// -------------------------------------------------------------------------

public class QueuedDispatcherTests
{
    /// <summary>Queues work from a thread-pool thread and hands back the pending task itself.</summary>
    private static async Task<Task<T>> EnqueueAsync<T>(QueuedMcpDispatcher dispatcher, Func<T> work, CancellationToken cancellation = default)
    {
        Task<T>? queued = null;
        await Task.Run(() => { queued = dispatcher.InvokeAsync(work, cancellation); });
        return queued!;
    }

    [Fact]
    public async Task WorkQueuedOffThreadRunsOnlyWhenDrained()
    {
        var dispatcher = new QueuedMcpDispatcher();
        dispatcher.BindMainThread();

        var task = await EnqueueAsync(dispatcher, () => 42);
        await Task.Delay(50);
        Assert.False(task.IsCompleted);
        Assert.Equal(1, dispatcher.PendingCount);

        Assert.Equal(1, dispatcher.Drain());
        Assert.Equal(42, await task);
    }

    [Fact]
    public async Task ItemsQueuedDuringADrainWaitForTheNextOne()
    {
        var dispatcher = new QueuedMcpDispatcher();
        dispatcher.BindMainThread();

        Task<int>? second = null;
        var first = await EnqueueAsync(dispatcher, () =>
        {
            // While the first item runs, another thread queues a second one.
            using var queued = new ManualResetEventSlim();
            Task.Run(() =>
            {
                second = dispatcher.InvokeAsync(() => 2);
                queued.Set();
            });
            queued.Wait();
            return 1;
        });

        Assert.Equal(1, dispatcher.Drain());
        Assert.Equal(1, await first);
        Assert.NotNull(second);
        Assert.False(second!.IsCompleted);

        Assert.Equal(1, dispatcher.Drain());
        Assert.Equal(2, await second);
    }

    [Fact]
    public async Task WorkFromTheMainThreadRunsInline()
    {
        var dispatcher = new QueuedMcpDispatcher();
        dispatcher.BindMainThread();

        var task = dispatcher.InvokeAsync(() => "now");
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal("now", await task);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public async Task ShutdownFailsPendingWorkWithCancellation()
    {
        var dispatcher = new QueuedMcpDispatcher();
        dispatcher.BindMainThread();

        var task = await EnqueueAsync(dispatcher, () => 1);
        dispatcher.Shutdown();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        var late = await EnqueueAsync(dispatcher, () => 2);
        Assert.True(late.IsCanceled);
    }

    [Fact]
    public async Task APreCancelledItemIsSkipped()
    {
        var dispatcher = new QueuedMcpDispatcher();
        dispatcher.BindMainThread();

        using var cts = new CancellationTokenSource();
        bool ran = false;
        var task = await EnqueueAsync(dispatcher, () => { ran = true; return 1; }, cts.Token);
        cts.Cancel();

        dispatcher.Drain();

        Assert.False(ran);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task AThrowingWorkItemFaultsItsTaskAndTheDrainContinues()
    {
        var dispatcher = new QueuedMcpDispatcher();
        dispatcher.BindMainThread();

        var bad  = await EnqueueAsync<int>(dispatcher, () => throw new InvalidOperationException("nope"));
        var good = await EnqueueAsync(dispatcher, () => 7);

        Assert.Equal(2, dispatcher.Drain());
        await Assert.ThrowsAsync<InvalidOperationException>(() => bad);
        Assert.Equal(7, await good);
    }
}
