using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;

namespace SexyBiscuit.Engine.Networking;

// ---------------------------------------------------------------------------
// WebSocketNetTransport.cs
// The shared wire: the only transport a browser can also speak.
// ---------------------------------------------------------------------------

/// <summary>
/// Binary WebSocket frames, client and server.
/// </summary>
/// <remarks>
/// This is the transport the two engines have in common. A browser cannot open a UDP socket or
/// listen on a port, so anything a browser must join — which is every web build, and every
/// mixed session — happens here. A native build keeps
/// <see cref="LiteNetLibTransport"/> for desktop-to-desktop play, and both carry exactly the
/// same frames, so a game does not know which one it is on.
///
/// Every <see cref="NetDelivery"/> collapses to reliable-ordered: TCP has no other setting. A
/// state update therefore cannot be dropped in favour of a fresher one the way it can over UDP;
/// a stalled connection queues them instead. That is a real difference, and it is why both
/// transports exist rather than one.
/// </remarks>
public sealed class WebSocketNetTransport : INetworkTransport
{
    /// <summary>A frame larger than this is treated as hostile and closes the connection.</summary>
    public int MaxFrameBytes { get; init; } = 256 * 1024;

    private sealed class Connection
    {
        public Connection(NetPeerHandle peer, WebSocket socket)
        {
            Peer   = peer;
            Socket = socket;
        }

        public NetPeerHandle           Peer   { get; }
        public WebSocket               Socket { get; }
        public CancellationTokenSource Cancel { get; } = new();

        // WebSocket forbids overlapping sends, so every frame for a peer funnels through one
        // pump task rather than being written from whichever thread produced it.
        public readonly ConcurrentQueue<byte[]> Outbound = new();
        public readonly SemaphoreSlim           Signal   = new(0);
    }

    private readonly ConcurrentDictionary<int, Connection> _connections = new();
    private readonly ConcurrentQueue<Action>               _events      = new();
    private readonly bool                                  _isServer;
    private readonly Uri?                                  _serverUri;
    private readonly int                                   _listenPort;
    private readonly int                                   _maxPeers;

    private HttpListener?            _listener;
    private CancellationTokenSource? _lifetime;
    private int                      _nextPeerId;

    private WebSocketNetTransport(bool isServer, Uri? serverUri, int listenPort, int maxPeers)
    {
        _isServer   = isServer;
        _serverUri  = serverUri;
        _listenPort = listenPort;
        _maxPeers   = maxPeers;
    }

    /// <summary>
    /// Listens for browser and native clients on <paramref name="port"/>, at
    /// <c>http://+:port/</c>.
    /// </summary>
    /// <remarks>
    /// This is the built-in host, for LAN play and for a game that ships its own listen server.
    /// A game deployed behind nginx does not use it: there the room server terminates the
    /// socket and the engine connects to it as a client.
    /// </remarks>
    public static WebSocketNetTransport Listen(int port, int maxPeers = 16)
        => new(isServer: true, serverUri: null, port, maxPeers);

    /// <summary>Connects to a <c>ws://</c> or <c>wss://</c> endpoint.</summary>
    public static WebSocketNetTransport Connect(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "ws" && uri.Scheme != "wss"))
            throw new ArgumentException($"'{url}' is not a ws:// or wss:// URL.", nameof(url));
        return new WebSocketNetTransport(isServer: false, uri, listenPort: 0, maxPeers: 1);
    }

    /// <summary>Connects to a host and port, with an optional room code in the query string.</summary>
    public static WebSocketNetTransport Connect(string host, int port, string? room = null, bool secure = false)
    {
        string query = string.IsNullOrEmpty(room) ? "" : $"?room={Uri.EscapeDataString(room)}";
        return Connect($"{(secure ? "wss" : "ws")}://{host}:{port}/ws{query}");
    }

    public string Name      => "websocket";
    public bool   IsRunning { get; private set; }

    public IReadOnlyCollection<NetPeerHandle> Peers => _connections.Values.Select(c => c.Peer).ToArray();

    public event Action<NetPeerHandle>? PeerConnected;
    public event Action<NetPeerHandle, NetDisconnectReason>? PeerDisconnected;
    public event Action<NetPeerHandle, ReadOnlyMemory<byte>>? FrameReceived;

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public void Start()
    {
        if (IsRunning) return;
        _lifetime = new CancellationTokenSource();
        IsRunning = true;

        if (_isServer) _ = AcceptLoopAsync(_lifetime.Token);
        else           _ = ConnectAsync(_lifetime.Token);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _lifetime?.Cancel();
        _listener?.Close();
        _listener = null;

        foreach (var connection in _connections.Values)
            Close(connection, NetDisconnectReason.ClosedByUs);
    }

    public void Poll()
    {
        // Every socket callback only enqueues; draining here is what keeps the game
        // single-threaded, as INetworkTransport promises.
        while (_events.TryDequeue(out var raise)) raise();
    }

    // -------------------------------------------------------------------------
    // Server
    // -------------------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken cancel)
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_listenPort}/");
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            // Binding "+" needs a URL reservation on Windows and root on some Linux setups.
            // Localhost always works and is what a listen server actually needs, so fall back
            // rather than failing to host at all.
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_listenPort}/");
                _listener.Start();
            }
            catch (Exception ex)
            {
                Fail($"could not listen on port {_listenPort}: {ex.Message}");
                return;
            }
        }

        while (!cancel.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancel.IsCancellationRequested || !IsRunning)
            {
                return;
            }
            catch (Exception ex)
            {
                Fail($"accept failed: {ex.Message}");
                return;
            }

            _ = HandleRequestAsync(context, cancel);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancel)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            // A plain GET on the same port answers the room probe every client makes before
            // opening a socket, so a listen server needs no second listener for it.
            context.Response.StatusCode  = 200;
            context.Response.ContentType = "application/json";
            byte[] body = System.Text.Encoding.UTF8.GetBytes(
                $"{{\"ok\":true,\"players\":{_connections.Count},\"max\":{_maxPeers}}}");
            await context.Response.OutputStream.WriteAsync(body, cancel).ConfigureAwait(false);
            context.Response.Close();
            return;
        }

        if (_connections.Count >= _maxPeers)
        {
            context.Response.StatusCode = 503;
            context.Response.Close();
            return;
        }

        HttpListenerWebSocketContext socketContext;
        try
        {
            socketContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        }
        catch (Exception)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        string address = context.Request.RemoteEndPoint?.ToString() ?? "websocket";
        await RunConnectionAsync(socketContext.WebSocket, address, cancel).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Client
    // -------------------------------------------------------------------------

    private async Task ConnectAsync(CancellationToken cancel)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(_serverUri!, cancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            Fail($"could not connect to {_serverUri}: {ex.Message}");
            return;
        }

        await RunConnectionAsync(socket, _serverUri!.ToString(), cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Adopts a socket someone else accepted — an ASP.NET host, or a test.
    /// </summary>
    /// <remarks>
    /// Returns only when the connection has finished, so a web host can await it to keep the
    /// request alive for the session's lifetime.
    /// </remarks>
    public Task AcceptAsync(WebSocket socket, string address = "websocket")
        => RunConnectionAsync(socket, address, _lifetime?.Token ?? CancellationToken.None);

    // -------------------------------------------------------------------------
    // One connection
    // -------------------------------------------------------------------------

    private async Task RunConnectionAsync(WebSocket socket, string address, CancellationToken cancel)
    {
        var peer       = new NetPeerHandle(Interlocked.Increment(ref _nextPeerId), address);
        var connection = new Connection(peer, socket);
        _connections[peer.Id] = connection;
        _events.Enqueue(() => PeerConnected?.Invoke(peer));

        var pump = Task.Run(() => SendPumpAsync(connection), CancellationToken.None);

        var buffer = new byte[16 * 1024];
        var frame  = new System.IO.MemoryStream();
        var reason = NetDisconnectReason.ClosedByPeer;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, connection.Cancel.Token);
            while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, linked.Token).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    reason = NetDisconnectReason.ClosedByPeer;
                    break;
                }

                // Text frames are not part of this protocol. Ignoring rather than closing keeps
                // a proxy's keepalive from killing a session.
                if (result.MessageType == WebSocketMessageType.Text) { frame.SetLength(0); continue; }

                frame.Write(buffer, 0, result.Count);
                if (frame.Length > MaxFrameBytes)
                {
                    reason = NetDisconnectReason.TransportError;
                    break;
                }
                if (!result.EndOfMessage) continue;

                byte[] payload = frame.ToArray();
                frame.SetLength(0);
                _events.Enqueue(() => FrameReceived?.Invoke(peer, payload));
            }
        }
        catch (OperationCanceledException) { reason = NetDisconnectReason.ClosedByUs; }
        catch (WebSocketException)         { reason = NetDisconnectReason.TransportError; }
        catch (Exception)                  { reason = NetDisconnectReason.TransportError; }

        Close(connection, reason);
        await pump.ConfigureAwait(false);
    }

    private async Task SendPumpAsync(Connection connection)
    {
        try
        {
            while (!connection.Cancel.IsCancellationRequested)
            {
                await connection.Signal.WaitAsync(connection.Cancel.Token).ConfigureAwait(false);
                while (connection.Outbound.TryDequeue(out var payload))
                {
                    if (connection.Socket.State != WebSocketState.Open) return;
                    await connection.Socket
                        .SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, connection.Cancel.Token)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException)         { }
        catch (Exception)                  { }
    }

    private void Close(Connection connection, NetDisconnectReason reason)
    {
        if (!_connections.TryRemove(connection.Peer.Id, out _)) return;

        connection.Cancel.Cancel();
        try
        {
            if (connection.Socket.State == WebSocketState.Open)
            {
                _ = connection.Socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }
        catch (Exception) { /* already gone; nothing to salvage */ }

        _events.Enqueue(() => PeerDisconnected?.Invoke(connection.Peer, reason));
    }

    private void Fail(string message)
    {
        Console.Error.WriteLine($"[WebSocketNetTransport] {message}");
        IsRunning = false;
    }

    // -------------------------------------------------------------------------
    // INetworkTransport
    // -------------------------------------------------------------------------

    public void Send(NetPeerHandle peer, ReadOnlySpan<byte> payload, NetDelivery delivery)
    {
        if (!_connections.TryGetValue(peer.Id, out var connection)) return;
        connection.Outbound.Enqueue(payload.ToArray());
        connection.Signal.Release();
    }

    public void Disconnect(NetPeerHandle peer, string reason = "")
    {
        if (_connections.TryGetValue(peer.Id, out var connection))
            Close(connection, NetDisconnectReason.ClosedByUs);
    }

    public void Dispose()
    {
        Stop();
        _lifetime?.Dispose();
    }
}
