using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace SexyBiscuit.Engine.Mcp;

public sealed class McpHttpTransportOptions
{
    public string   Host                  { get; init; } = "127.0.0.1";
    public int      Port                  { get; init; } = 7331;

    /// <summary>Ports tried above <see cref="Port"/> when it is taken — a second editor, usually.</summary>
    public int      PortSearchRange       { get; init; } = 10;
    public string   Path                  { get; init; } = "/mcp/";
    public string?  BearerToken           { get; init; }

    /// <summary>A call that takes longer than this answers over SSE with keepalives.</summary>
    public TimeSpan SseSwitchDelay        { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan KeepAliveInterval     { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Refuse a browser Origin that is not localhost (DNS-rebinding guard).</summary>
    public bool     RequireLoopbackOrigin { get; init; } = true;
    public long     MaxBodyBytes          { get; init; } = 16 * 1024 * 1024;
}

/// <summary>
/// MCP's Streamable HTTP transport on <see cref="HttpListener"/>: JSON-RPC over POST, an
/// optional server-to-client SSE stream over GET, no sessions. Every request runs on a
/// thread-pool task; the game thread is only ever reached through the dispatcher.
/// </summary>
/// <remarks>
/// A slow call (a build, a question to the user) switches its response to
/// <c>text/event-stream</c> and writes a comment every few seconds, so neither the client's
/// HTTP timeout nor an idle proxy gives up on it. Fast calls answer as plain JSON.
/// </remarks>
public sealed class McpHttpTransport : IAsyncDisposable
{
    private readonly McpServer               _server;
    private readonly McpHttpTransportOptions _options;
    private readonly Action<string, bool>?   _log;
    private readonly ConcurrentDictionary<SseStream, byte> _streams = new();

    private HttpListener?           _listener;
    private CancellationTokenSource _stopCts = new();
    private Task?                   _acceptLoop;
    private int                     _inFlight;

    public McpHttpTransport(McpServer server, McpHttpTransportOptions? options = null, Action<string, bool>? log = null)
    {
        _server  = server;
        _options = options ?? new McpHttpTransportOptions();
        _log     = log;
        _server.NotificationReady += Broadcast;
    }

    /// <summary>The URL actually bound; the port may differ from the configured one.</summary>
    public Uri? BoundUrl { get; private set; }

    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>Open GET notification streams.</summary>
    public int OpenStreams => _streams.Count;

    /// <summary>Requests currently being handled.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /// <summary>Binds the first free port in range and starts accepting.</summary>
    public void Start()
    {
        if (_listener != null) throw new InvalidOperationException("The transport is already started.");
        if (!HttpListener.IsSupported) throw new PlatformNotSupportedException("HttpListener is not supported here.");

        string path = _options.Path.StartsWith('/') ? _options.Path : "/" + _options.Path;
        if (!path.EndsWith('/')) path += "/";

        Exception? last = null;
        int range = Math.Max(1, _options.PortSearchRange);

        for (int port = _options.Port; port < _options.Port + range; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://{_options.Host}:{port}{path}");

            try
            {
                listener.Start();
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                last = ex;
                listener.Close();
                continue;
            }

            _listener   = listener;
            _stopCts    = new CancellationTokenSource();
            BoundUrl    = new Uri($"http://{_options.Host}:{port}{path}");
            _acceptLoop = Task.Run(AcceptLoopAsync);
            _log?.Invoke($"[mcp] listening on {BoundUrl}", false);
            return;
        }

        throw new InvalidOperationException(
            $"No free port between {_options.Port} and {_options.Port + range - 1} ({last?.Message}).", last);
    }

    /// <summary>Stops accepting, cancels in-flight calls, closes streams, waits briefly for handlers.</summary>
    public async Task StopAsync(TimeSpan? drainTimeout = null)
    {
        var listener = _listener;
        if (listener == null) return;

        _stopCts.Cancel();
        foreach (var stream in _streams.Keys) stream.Complete();

        try { listener.Stop(); } catch (ObjectDisposedException) { }

        var deadline = DateTime.UtcNow + (drainTimeout ?? TimeSpan.FromSeconds(2));
        while (Volatile.Read(ref _inFlight) > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20).ConfigureAwait(false);

        if (_acceptLoop != null)
            await Task.WhenAny(_acceptLoop, Task.Delay(500)).ConfigureAwait(false);

        try { listener.Close(); } catch (ObjectDisposedException) { }

        _listener = null;
        BoundUrl  = null;
        _log?.Invoke("[mcp] stopped", false);
    }

    public async ValueTask DisposeAsync()
    {
        _server.NotificationReady -= Broadcast;
        await StopAsync().ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Accept loop
    // -------------------------------------------------------------------------

    private async Task AcceptLoopAsync()
    {
        var listener = _listener!;
        var token    = _stopCts.Token;

        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                if (token.IsCancellationRequested) break;
                _log?.Invoke($"[mcp] accept failed: {ex.Message}", true);
                await Task.Delay(50).ConfigureAwait(false);
                continue;
            }

            Interlocked.Increment(ref _inFlight);
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[mcp] request failed: {ex.GetType().Name}: {ex.Message}", true);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            });
        }
    }

    // -------------------------------------------------------------------------
    // Request handling
    // -------------------------------------------------------------------------

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request  = context.Request;
        var response = context.Response;

        try
        {
            if (!Authorised(request))
            {
                response.AddHeader("WWW-Authenticate", "Bearer");
                await WriteTextAsync(response, 401, "Unauthorized: a bearer token is required.").ConfigureAwait(false);
                return;
            }

            if (!OriginAllowed(request))
            {
                await WriteTextAsync(response, 403, "Forbidden: only localhost origins may use this server.").ConfigureAwait(false);
                return;
            }

            switch (request.HttpMethod)
            {
                case "POST":
                    await HandlePostAsync(request, response).ConfigureAwait(false);
                    break;

                case "GET":
                    await HandleGetAsync(request, response).ConfigureAwait(false);
                    break;

                case "DELETE":
                    // No sessions to end; acknowledge so a well-behaved client can shut down cleanly.
                    response.StatusCode      = 200;
                    response.ContentLength64 = 0;
                    break;

                default:
                    response.AddHeader("Allow", "GET, POST, DELETE");
                    await WriteTextAsync(response, 405, "Method not allowed.").ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            try { response.Close(); } catch (Exception) { /* client already gone */ }
        }
    }

    private async Task HandlePostAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        string? body = await ReadBodyAsync(request, response).ConfigureAwait(false);
        if (body == null) return;

        if (!JsonRpcRequest.TryParse(body, out var rpc, out var parseError))
        {
            await WriteJsonAsync(response, 400, JsonRpcResponse.Failure(null, parseError!).ToJson()).ConfigureAwait(false);
            return;
        }

        if (rpc!.IsNotification)
        {
            _server.HandleNotification(rpc);
            response.StatusCode      = 202;
            response.ContentLength64 = 0;
            return;
        }

        bool acceptsSse = (request.Headers["Accept"] ?? "").Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

        var cts      = CancellationTokenSource.CreateLinkedTokenSource(_stopCts.Token);
        var tracking = _server.TrackRequest(rpc.IdKey, cts);

        var callContext = new McpCallContext
        {
            RequestId     = rpc.Id,
            ClientName    = request.Headers[McpServer.ClientHeader],
            Cancellation  = cts.Token,
            Dispatcher    = _server.Tools.Dispatcher,
            ProgressToken = ExtractProgressToken(rpc.Params),
        };

        var work = _server.HandleRequestAsync(rpc, callContext);

        try
        {
            if (acceptsSse)
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                var delay = Task.Delay(_options.SseSwitchDelay, delayCts.Token);
                await Task.WhenAny(work, delay).ConfigureAwait(false);
                delayCts.Cancel();
            }

            if (work.IsCompleted || !acceptsSse)
            {
                var completed = await work.ConfigureAwait(false);
                await WriteJsonAsync(response, 200, completed.ToJson()).ConfigureAwait(false);
                return;
            }

            await StreamResponseAsync(response, work, callContext, cts).ConfigureAwait(false);
        }
        finally
        {
            tracking.Dispose();

            // The tool may still be running after a client disconnect; only dispose its token
            // source once it has actually finished.
            if (work.IsCompleted)
                cts.Dispose();
            else
                _ = work.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
        }
    }

    private async Task StreamResponseAsync(HttpListenerResponse response, Task<JsonRpcResponse> work,
                                           McpCallContext callContext, CancellationTokenSource cts)
    {
        BeginEventStream(response);
        var writer = new SseWriter(response.OutputStream);

        callContext.ProgressSink = (progress, total, message) =>
        {
            var parameters = new JsonObject
            {
                ["progressToken"] = callContext.ProgressToken is { } token ? JsonValue.Create(token) : null,
                ["progress"]      = progress,
            };
            if (total.HasValue) parameters["total"] = total.Value;
            if (message != null) parameters["message"] = message;

            var notification = new JsonRpcNotification { Method = "notifications/progress", Params = parameters };
            _ = writer.TryWriteEventAsync("message", notification.ToJson());
        };

        try
        {
            while (!work.IsCompleted)
            {
                await Task.WhenAny(work, Task.Delay(_options.KeepAliveInterval)).ConfigureAwait(false);
                if (!work.IsCompleted)
                    await writer.WriteCommentAsync("keepalive").ConfigureAwait(false);
            }

            var completed = await work.ConfigureAwait(false);
            await writer.WriteEventAsync("message", completed.ToJson()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away mid-wait. Withdraw the work it was waiting on.
            cts.Cancel();
        }
    }

    private async Task HandleGetAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        bool acceptsSse = (request.Headers["Accept"] ?? "").Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
        if (!acceptsSse)
        {
            response.AddHeader("Allow", "GET, POST, DELETE");
            await WriteTextAsync(response, 405,
                "SexyBiscuit MCP server. POST JSON-RPC here, or GET with Accept: text/event-stream for notifications.").ConfigureAwait(false);
            return;
        }

        BeginEventStream(response);
        var stream = new SseStream(response.OutputStream);
        _streams.TryAdd(stream, 0);

        try
        {
            await stream.Writer.WriteCommentAsync("connected").ConfigureAwait(false);

            while (!_stopCts.IsCancellationRequested)
            {
                string? frame = await stream.NextAsync(_options.KeepAliveInterval, _stopCts.Token).ConfigureAwait(false);
                if (stream.IsCompleted && frame == null) break;

                if (frame == null)
                    await stream.Writer.WriteCommentAsync("keepalive").ConfigureAwait(false);
                else
                    await stream.Writer.WriteEventAsync("message", frame).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            // Client disconnected or the server is stopping.
        }
        finally
        {
            _streams.TryRemove(stream, out _);
        }
    }

    private void Broadcast(JsonRpcNotification notification)
    {
        string json = notification.ToJson();
        foreach (var stream in _streams.Keys)
            stream.Post(json);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private bool Authorised(HttpListenerRequest request)
    {
        if (string.IsNullOrEmpty(_options.BearerToken)) return true;

        string? header = request.Headers["Authorization"];
        if (header == null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(header[7..].Trim(), _options.BearerToken, StringComparison.Ordinal);
    }

    private bool OriginAllowed(HttpListenerRequest request)
    {
        if (!_options.RequireLoopbackOrigin) return true;

        string? origin = request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin) || origin == "null") return true;

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static JsonElement? ExtractProgressToken(JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } p) return null;
        if (!p.TryGetProperty("_meta", out var meta) || meta.ValueKind != JsonValueKind.Object) return null;
        return meta.TryGetProperty("progressToken", out var token) ? token : null;
    }

    private async Task<string?> ReadBodyAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.ContentLength64 > _options.MaxBodyBytes)
        {
            await WriteTextAsync(response, 413, "Request body too large.").ConfigureAwait(false);
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        long total = 0;
        int read;

        while ((read = await request.InputStream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > _options.MaxBodyBytes)
            {
                await WriteTextAsync(response, 413, "Request body too large.").ConfigureAwait(false);
                return null;
            }
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void BeginEventStream(HttpListenerResponse response)
    {
        response.StatusCode  = 200;
        response.ContentType = "text/event-stream";
        response.AddHeader("Cache-Control", "no-cache");
        response.SendChunked = true;
        response.KeepAlive   = true;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode      = status;
        response.ContentType     = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(HttpListenerResponse response, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode      = status;
        response.ContentType     = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>Serialises SSE frames onto one output stream.</summary>
    private sealed class SseWriter
    {
        private readonly Stream        _stream;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public SseWriter(Stream stream) => _stream = stream;

        public Task WriteCommentAsync(string comment) => WriteRawAsync($": {comment}\n\n");

        /// <summary>Data must be a single line: the JSON is always compact.</summary>
        public Task WriteEventAsync(string eventName, string data) => WriteRawAsync($"event: {eventName}\ndata: {data}\n\n");

        public async Task TryWriteEventAsync(string eventName, string data)
        {
            try
            {
                await WriteEventAsync(eventName, data).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException)
            {
                // Progress is best-effort; the final result write reports a dead client.
            }
        }

        private async Task WriteRawAsync(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(bytes).ConfigureAwait(false);
                await _stream.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>One open GET stream: a channel of pending notification frames plus its writer.</summary>
    private sealed class SseStream
    {
        private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();
        private Task<string>? _pendingRead;

        public SseStream(Stream output) => Writer = new SseWriter(output);

        public SseWriter Writer { get; }

        public bool IsCompleted { get; private set; }

        public void Post(string frame) => _channel.Writer.TryWrite(frame);

        public void Complete()
        {
            IsCompleted = true;
            _channel.Writer.TryComplete();
        }

        /// <summary>The next frame, or null after <paramref name="timeout"/> (time to send a keepalive).</summary>
        public async Task<string?> NextAsync(TimeSpan timeout, CancellationToken token)
        {
            _pendingRead ??= ReadOneAsync();

            var winner = await Task.WhenAny(_pendingRead, Task.Delay(timeout, token)).ConfigureAwait(false);
            if (winner != _pendingRead) return null;

            var read = _pendingRead;
            _pendingRead = null;

            try
            {
                return await read.ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                IsCompleted = true;
                return null;
            }
        }

        private async Task<string> ReadOneAsync()
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                if (_channel.Reader.TryRead(out var frame)) return frame;
            }

            throw new ChannelClosedException();
        }
    }
}
