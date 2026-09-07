using System.Text.Json;
using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.Networking;

// ---------------------------------------------------------------------------
// NetworkManager.cs
// The façade: sessions, connections, packet dispatch, replication and RPC.
// ---------------------------------------------------------------------------

/// <summary>Which wire a session runs on.</summary>
public enum NetTransportKind
{
    /// <summary>
    /// Binary WebSocket. The only one a browser can speak, so it is the default: a session
    /// started this way can be joined from a web build and from a desktop build at once.
    /// </summary>
    WebSocket,

    /// <summary>
    /// UDP via LiteNetLib. Faster, and the only transport that can genuinely drop a stale
    /// state update — but desktop-to-desktop only.
    /// </summary>
    Udp,

    /// <summary>
    /// Wired to itself in memory. A single-player run of a multiplayer game, and every test.
    /// </summary>
    Loopback,
}

/// <summary>
/// The engine's networking façade: it owns the transport, assigns client ids, dispatches
/// frames, and drives the <see cref="ReplicationSystem"/> and <see cref="RpcSystem"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is sent or received until <see cref="Tick"/> is pumped.</b> The manager is not
/// wired into the game loop, because a game that is not networked should pay nothing for the
/// fact that it could be.
/// </para>
/// <para>
/// The frames are defined by <see cref="NetProtocol"/> and mirrored by the browser engine, so a
/// script written against <c>Network.*</c> behaves the same in both. The transport is chosen at
/// <see cref="StartServer"/> time and never leaks past this class.
/// </para>
/// </remarks>
public sealed class NetworkManager : IDisposable
{
    // -------------------------------------------------------------------------
    // Singleton
    // -------------------------------------------------------------------------

    /// <summary>The running NetworkManager, or <c>null</c> when there is none.</summary>
    public static NetworkManager? Instance { get; private set; }

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    public bool IsServer  { get; private set; }
    public bool IsClient  { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>True when this process is both ends of the session — a listen server or solo play.</summary>
    public bool IsHost => IsServer && IsClient;

    /// <summary>True once the server has accepted us, or immediately when we are the server.</summary>
    public bool IsConnected => IsRunning && (IsServer || LocalClientId >= 0);

    /// <summary>
    /// The local client id the server assigned. Always <c>0</c> on the server itself, and
    /// <c>-1</c> on a client until <see cref="NetMessage.Welcome"/> arrives.
    /// </summary>
    public int LocalClientId { get; private set; } = -1;

    /// <summary>Round-trip time in milliseconds. Populated on clients.</summary>
    public int Ping { get; private set; }

    /// <summary>The name this peer introduced itself with. Set it before connecting.</summary>
    public string PlayerName { get; set; } = "Player";

    /// <summary>The room code this session belongs to, when it was started with one.</summary>
    public string Room { get; private set; } = "";

    /// <summary>Which transport the session is running on.</summary>
    public NetTransportKind Transport { get; private set; } = NetTransportKind.Loopback;

    /// <summary>Every player in the session, local one included, by client id.</summary>
    public IReadOnlyDictionary<int, string> Players => _players;

    // -------------------------------------------------------------------------
    // Sub-systems
    // -------------------------------------------------------------------------

    public ReplicationSystem Replication { get; } = new();
    public RpcSystem         Rpc         { get; } = new();

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /// <summary>Server-side: a client has joined. The argument is its client id.</summary>
    public event Action<int>? OnClientConnected;

    /// <summary>Server-side: a client has left.</summary>
    public event Action<int>? OnClientDisconnected;

    /// <summary>Client-side: the server accepted us and <see cref="LocalClientId"/> is set.</summary>
    public event Action? OnConnectedToServer;

    /// <summary>Client-side: the session ended. The argument is the reason, when the server gave one.</summary>
    public event Action<string>? OnDisconnectedFromServer;

    /// <summary>
    /// Clients: the server wants a networked actor built. The game supplies the actor — the
    /// engine sends a name, not a prefab.
    /// </summary>
    public event Action<uint, string, int, byte[]>? OnSpawnObject;

    /// <summary>Clients: the server wants a networked actor destroyed.</summary>
    public event Action<uint>? OnDespawnObject;

    /// <summary>
    /// A message from another peer: <c>(senderClientId, type, payload)</c>. This is the channel
    /// <c>Network.sendToAll</c> reaches; the engine never looks inside the payload.
    /// </summary>
    public event Action<int, string, JsonNode?>? OnMessage;

    /// <summary>Another player joined or left the session.</summary>
    public event Action<int, string>? OnPlayerJoined;
    public event Action<int>?         OnPlayerLeft;

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private INetworkTransport? _transport;

    // Server: our client id per peer, and back again.
    private readonly Dictionary<int, NetPeerHandle> _clients = new();
    private readonly Dictionary<int, string>        _players = new();

    // Client: the one peer that is the server.
    private NetPeerHandle? _serverPeer;

    private int  _nextClientId  = 1;
    private uint _nextNetworkId = 1;
    private int  _maxClients    = 16;

    private float       _pingTimer;
    private const float PingInterval = 1f;

    /// <summary>A loopback session's other half, kept so both ends can be pumped from one Tick.</summary>
    private NetworkManager? _loopbackPeer;
    private bool            _isLoopbackClientHalf;

    public NetworkManager()
    {
        if (Instance != null)
            throw new InvalidOperationException(
                "A NetworkManager is already running. Dispose the existing one first.");
        Instance = this;
    }

    /// <summary>For the second half of a loopback pair, which must not claim the singleton.</summary>
    private NetworkManager(bool _) { }

    // -------------------------------------------------------------------------
    // Starting a session
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts a server on <paramref name="port"/>.
    /// </summary>
    /// <param name="port">The port to bind.</param>
    /// <param name="maxClients">How many peers may connect.</param>
    /// <param name="transport">
    /// Which wire to use. <see cref="NetTransportKind.WebSocket"/> by default, because it is the
    /// only one a browser can join and a native client speaks it too. Choose
    /// <see cref="NetTransportKind.Udp"/> when every player is on a desktop build and dropping a
    /// stale update matters more than reaching the web.
    /// </param>
    /// <param name="room">A room code to report in the handshake. Cosmetic to the engine.</param>
    public void StartServer(int port, int maxClients = 16,
                            NetTransportKind transport = NetTransportKind.WebSocket,
                            string room = "")
    {
        RequireStopped();

        _maxClients = maxClients;
        Transport   = transport;
        Room        = room;

        _transport = transport switch
        {
            NetTransportKind.Udp => LiteNetLibTransport.Listen(port, maxClients),
            NetTransportKind.WebSocket => WebSocketNetTransport.Listen(port, maxClients),
            _ => throw new ArgumentException(
                "A loopback session is started with StartSolo, which makes both ends at once.", nameof(transport)),
        };

        WireTransport();
        _transport.Start();

        IsServer      = true;
        IsClient      = false;
        IsRunning     = true;
        LocalClientId = 0;
        _players[0]   = PlayerName;

        Log($"server listening on {transport.ToString().ToLowerInvariant()} port {port}");
    }

    /// <summary>Connects to a server over UDP.</summary>
    public void ConnectToServer(string address, int port)
    {
        RequireStopped();

        Transport  = NetTransportKind.Udp;
        _transport = LiteNetLibTransport.Connect(address, port);
        StartAsClient($"{address}:{port}");
    }

    /// <summary>
    /// Connects to a <c>ws://</c> or <c>wss://</c> server — a room server, or another player's
    /// listen server.
    /// </summary>
    public void ConnectToUrl(string url, string room = "")
    {
        RequireStopped();

        Room       = room;
        Transport  = NetTransportKind.WebSocket;
        _transport = WebSocketNetTransport.Connect(url);
        StartAsClient(url);
    }

    /// <summary>
    /// Starts a session with no socket at all: this process is both server and client.
    /// </summary>
    /// <remarks>
    /// A multiplayer game played alone should not be a different game. Every frame still goes
    /// through the same encode, dispatch and replication path a real one does, so solo play
    /// exercises the netcode instead of bypassing it — which is what stops "works alone, breaks
    /// in a lobby".
    /// </remarks>
    public void StartSolo()
    {
        RequireStopped();

        var (serverEnd, clientEnd) = LoopbackTransport.CreatePair();

        Transport = NetTransportKind.Loopback;
        _transport = serverEnd;
        WireTransport();

        // The client half is a second manager that does not claim the singleton: the game keeps
        // talking to this one, which is both ends at once.
        var client = new NetworkManager(false)
        {
            Transport             = NetTransportKind.Loopback,
            PlayerName            = PlayerName,
            _transport            = clientEnd,
            _isLoopbackClientHalf = true,
        };
        client.WireTransport();
        client.ForwardTo(this);

        _loopbackPeer = client;

        IsServer      = true;
        IsClient      = true;
        IsRunning     = true;
        LocalClientId = 0;
        _players[0]   = PlayerName;

        client.IsClient  = true;
        client.IsRunning = true;

        serverEnd.Start();
        clientEnd.Start();

        Log("solo session started (loopback)");
    }

    private void StartAsClient(string label)
    {
        WireTransport();
        _transport!.Start();

        IsClient      = true;
        IsServer      = false;
        IsRunning     = true;
        LocalClientId = -1;

        Log($"connecting to {label}…");
    }

    private void RequireStopped()
    {
        if (IsRunning)
            throw new InvalidOperationException(
                "NetworkManager is already running. Call StopServer() or Disconnect() first.");
    }

    // -------------------------------------------------------------------------
    // Ending a session
    // -------------------------------------------------------------------------

    /// <summary>Stops the server and drops every client.</summary>
    public void StopServer()
    {
        if (!IsServer) return;

        foreach (var peer in _clients.Values.ToArray())
            _transport?.Disconnect(peer, "server closed");

        Teardown();
        Log("server stopped");
    }

    /// <summary>Leaves the session.</summary>
    public void Disconnect()
    {
        if (!IsRunning) return;

        if (IsServer) { StopServer(); return; }
        Teardown();
        Log("disconnected");
    }

    private void Teardown()
    {
        _loopbackPeer?.Teardown();
        _loopbackPeer = null;

        _transport?.Stop();
        _transport?.Dispose();
        _transport = null;

        _clients.Clear();
        _players.Clear();
        _serverPeer   = null;
        IsRunning     = false;
        IsServer      = false;
        IsClient      = false;
        LocalClientId = -1;
        Ping          = 0;
    }

    /// <summary>Disconnects one client with a reason it will see.</summary>
    public void KickClient(int clientId, string reason = "")
    {
        if (!IsServer || !_clients.TryGetValue(clientId, out var peer)) return;

        // The reason rides in a frame of its own: a WebSocket close code carries no text a
        // browser can read back, and a UDP disconnect payload is not visible to the browser
        // engine at all. One frame works on every transport.
        Send(peer, new NetWriter(NetMessage.Kick).String(reason).ToArray(), NetDelivery.ReliableOrdered);
        _transport?.Disconnect(peer, reason);
        Log($"kicked client {clientId}: {reason}");
    }

    /// <summary>The connected client ids, as of the call. Server only.</summary>
    public IEnumerable<int> ConnectedClientIds => _clients.Keys.ToArray();

    // -------------------------------------------------------------------------
    // The pump
    // -------------------------------------------------------------------------

    /// <summary>
    /// Polls the transport, dispatches everything that arrived, and drives replication. Call it
    /// once per frame; nothing moves without it.
    /// </summary>
    public void Tick(float dt)
    {
        if (!IsRunning) return;

        _transport?.Poll();
        _loopbackPeer?._transport?.Poll();

        Replication.Tick(dt, this);

        if (IsClient && !IsHost)
        {
            _pingTimer += dt;
            if (_pingTimer >= PingInterval)
            {
                _pingTimer = 0f;
                SendToServer(new NetWriter(NetMessage.Ping)
                    .Double(Now()).ToArray(), NetDelivery.Unreliable);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Transport wiring
    // -------------------------------------------------------------------------

    private void WireTransport()
    {
        var transport = _transport!;
        transport.PeerConnected    += OnPeerConnected;
        transport.PeerDisconnected += OnPeerDisconnected;
        transport.FrameReceived    += OnFrameReceived;
    }

    /// <summary>
    /// Points the loopback client half's events at the manager the game actually holds, so solo
    /// play raises the same events a real client does.
    /// </summary>
    private void ForwardTo(NetworkManager host)
    {
        OnMessage                += (sender, type, payload) => host.OnMessage?.Invoke(sender, type, payload);
        OnSpawnObject            += (id, name, owner, state) => host.OnSpawnObject?.Invoke(id, name, owner, state);
        OnDespawnObject          += id => host.OnDespawnObject?.Invoke(id);
        OnConnectedToServer      += () => host.OnConnectedToServer?.Invoke();
        OnDisconnectedFromServer += reason => host.OnDisconnectedFromServer?.Invoke(reason);
    }

    private void OnPeerConnected(NetPeerHandle peer)
    {
        if (IsServer && !_isLoopbackClientHalf)
        {
            // The server waits for Hello before assigning an id: a peer that connects and never
            // introduces itself is a port scan, and should not take a seat.
            return;
        }

        // Client: introduce ourselves.
        _serverPeer = peer;
        var hello = new JsonObject
        {
            ["proto"] = NetProtocolVersion.Current,
            ["name"]  = PlayerName,
            ["room"]  = Room,
        };
        Send(peer, NetProtocol.EncodeJson(NetMessage.Hello, hello), NetDelivery.ReliableOrdered);
    }

    private void OnPeerDisconnected(NetPeerHandle peer, NetDisconnectReason reason)
    {
        if (IsServer && peer.Tag is int clientId && !_isLoopbackClientHalf)
        {
            _clients.Remove(clientId);
            _players.Remove(clientId);
            Broadcast(new NetWriter(NetMessage.PeerLeft).Int(clientId).ToArray(),
                      NetDelivery.ReliableOrdered);
            OnClientDisconnected?.Invoke(clientId);
            OnPlayerLeft?.Invoke(clientId);
            Log($"client {clientId} left ({reason})");
            return;
        }

        if (ReferenceEquals(peer, _serverPeer) || _serverPeer == null)
        {
            _serverPeer = null;
            OnDisconnectedFromServer?.Invoke(_kickReason ?? reason.ToString());
            _kickReason = null;
        }
    }

    private string? _kickReason;

    private void OnFrameReceived(NetPeerHandle peer, ReadOnlyMemory<byte> payload)
    {
        try
        {
            Dispatch(peer, payload.Span);
        }
        catch (NetProtocolException ex)
        {
            // A bad frame is a peer, not a bug: log it and drop the peer rather than taking the
            // session down with an unhandled exception on the game thread.
            Console.Error.WriteLine($"[NetworkManager] bad frame from {peer}: {ex.Message}");
            if (IsServer) _transport?.Disconnect(peer, "protocol error");
        }
    }

    // -------------------------------------------------------------------------
    // Dispatch
    // -------------------------------------------------------------------------

    private void Dispatch(NetPeerHandle peer, ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0) return;

        var reader = new NetReader(frame);
        byte id    = reader.Byte();
        int sender = peer.Tag is int clientId ? clientId : -1;

        switch (id)
        {
            case NetMessage.Hello:    HandleHello(peer, ref reader);      break;
            case NetMessage.Welcome:  HandleWelcome(ref reader);          break;
            case NetMessage.Spawn:    HandleSpawn(ref reader);            break;
            case NetMessage.Despawn:  OnDespawnObject?.Invoke(reader.UInt()); break;
            case NetMessage.State:    HandleState(ref reader);            break;
            case NetMessage.Rpc:      HandleRpc(ref reader, sender);      break;
            case NetMessage.Message:  HandleMessage(peer, ref reader, sender); break;
            case NetMessage.Ping:     HandlePing(peer, ref reader);       break;
            case NetMessage.Pong:     HandlePong(ref reader);             break;
            case NetMessage.PeerJoined: HandlePeerJoined(ref reader);     break;
            case NetMessage.PeerLeft:   HandlePeerLeft(ref reader);       break;
            case NetMessage.Kick:     _kickReason = reader.String();      break;
            default:
                Console.Error.WriteLine($"[NetworkManager] unknown message id 0x{id:X2} from {peer}");
                break;
        }
    }

    private void HandleHello(NetPeerHandle peer, ref NetReader reader)
    {
        if (!IsServer) return;

        var hello = NetProtocol.DecodeJson(ref reader);
        int proto = hello?["proto"]?.GetValue<int>() ?? 0;

        if (proto != NetProtocolVersion.Current)
        {
            // Refusing beats mis-decoding: a client one version behind would otherwise read
            // every later frame at the wrong offsets and fail somewhere unrelated.
            Send(peer, new NetWriter(NetMessage.Kick)
                .String($"protocol {proto} does not match the server's {NetProtocolVersion.Current}").ToArray(),
                NetDelivery.ReliableOrdered);
            _transport?.Disconnect(peer, "protocol mismatch");
            return;
        }

        if (_clients.Count >= _maxClients)
        {
            Send(peer, new NetWriter(NetMessage.Kick).String("server is full").ToArray(),
                 NetDelivery.ReliableOrdered);
            _transport?.Disconnect(peer, "full");
            return;
        }

        int clientId = _nextClientId++;
        string name  = Truncate(hello?["name"]?.GetValue<string>() ?? $"Player {clientId}", 32);

        peer.Tag            = clientId;
        _clients[clientId]  = peer;
        _players[clientId]  = name;

        var welcome = new JsonObject
        {
            ["proto"]    = NetProtocolVersion.Current,
            ["clientId"] = clientId,
            ["room"]     = Room,
            ["players"]  = new JsonArray(_players
                .Select(p => (JsonNode)new JsonObject { ["id"] = p.Key, ["name"] = p.Value })
                .ToArray()),
        };
        Send(peer, NetProtocol.EncodeJson(NetMessage.Welcome, welcome), NetDelivery.ReliableOrdered);

        // Everyone already in gets told; the newcomer learnt the roster from Welcome.
        Broadcast(new NetWriter(NetMessage.PeerJoined).Int(clientId).String(name).ToArray(),
                  NetDelivery.ReliableOrdered, exceptClientId: clientId);

        OnClientConnected?.Invoke(clientId);
        OnPlayerJoined?.Invoke(clientId, name);
        Log($"client {clientId} '{name}' joined");
    }

    private void HandleWelcome(ref NetReader reader)
    {
        var welcome   = NetProtocol.DecodeJson(ref reader);
        LocalClientId = welcome?["clientId"]?.GetValue<int>() ?? -1;
        Room          = welcome?["room"]?.GetValue<string>() ?? Room;

        _players.Clear();
        if (welcome?["players"] is JsonArray roster)
        {
            foreach (var entry in roster)
            {
                int id = entry?["id"]?.GetValue<int>() ?? -1;
                if (id >= 0) _players[id] = entry?["name"]?.GetValue<string>() ?? $"Player {id}";
            }
        }

        OnConnectedToServer?.Invoke();
        Log($"joined as client {LocalClientId}");
    }

    private void HandleSpawn(ref NetReader reader)
    {
        uint   networkId = reader.UInt();
        string actorName = reader.String();
        int    owner     = reader.Int();
        byte[] state     = reader.Bytes();
        OnSpawnObject?.Invoke(networkId, actorName, owner, state);
    }

    private void HandleState(ref NetReader reader)
    {
        uint   networkId = reader.UInt();
        byte[] state     = reader.Bytes();
        Replication.HandleIncomingStateUpdate(networkId, state);
    }

    private void HandleRpc(ref NetReader reader, int senderClientId)
    {
        uint   networkId = reader.UInt();
        bool   toServer  = reader.Bool();
        int    target    = reader.Int();
        string method    = reader.String();
        byte[] args      = reader.Bytes();

        if (toServer) Rpc.HandleIncomingServerRpc(senderClientId, networkId, method, args);
        else          Rpc.HandleIncomingClientRpc(networkId, method, args, target < 0 ? null : target);
    }

    private void HandleMessage(NetPeerHandle peer, ref NetReader reader, int senderClientId)
    {
        int    declared = reader.Int();
        string type     = reader.String();
        string json     = reader.String();

        // The server stamps the sender itself. Trusting the client's own number would let any
        // peer post as any other, which is the cheapest possible way to cheat.
        int sender = IsServer ? senderClientId : declared;

        JsonNode? payload = null;
        if (json.Length > 0)
        {
            try { payload = JsonNode.Parse(json); }
            catch (JsonException) { payload = null; }
        }

        OnMessage?.Invoke(sender, type, payload);

        // A server relays to everyone else, so a script's sendToAll reaches every peer without
        // the game writing a relay of its own.
        if (IsServer)
        {
            Broadcast(new NetWriter(NetMessage.Message).Int(sender).String(type).String(json).ToArray(),
                      NetDelivery.ReliableOrdered, exceptClientId: sender);
        }
    }

    private void HandlePing(NetPeerHandle peer, ref NetReader reader)
    {
        double clientTime = reader.Double();
        if (!IsServer) return;
        Send(peer, new NetWriter(NetMessage.Pong).Double(clientTime).Double(Now()).ToArray(),
             NetDelivery.Unreliable);
    }

    private void HandlePong(ref NetReader reader)
    {
        double sentAt = reader.Double();
        _ = reader.Double();                 // server clock, for a future clock sync
        Ping = (int)Math.Max(0, Now() - sentAt);
    }

    private void HandlePeerJoined(ref NetReader reader)
    {
        int    id   = reader.Int();
        string name = reader.String();
        _players[id] = name;
        OnPlayerJoined?.Invoke(id, name);
    }

    private void HandlePeerLeft(ref NetReader reader)
    {
        int id = reader.Int();
        _players.Remove(id);
        OnPlayerLeft?.Invoke(id);
    }

    // -------------------------------------------------------------------------
    // Sending
    // -------------------------------------------------------------------------

    /// <summary>Sends a raw frame to the server. No-op unless this peer is a client.</summary>
    public void SendToServer(byte[] frame, NetDelivery delivery = NetDelivery.ReliableOrdered)
    {
        if (_serverPeer != null) { Send(_serverPeer, frame, delivery); return; }

        // The host half of a loopback session has no server peer -- it *is* the server -- so
        // the frame goes over the loopback client's socket instead.
        _loopbackPeer?.SendUpstream(frame, delivery);
    }

    private void SendUpstream(byte[] frame, NetDelivery delivery)
    {
        if (_serverPeer != null) Send(_serverPeer, frame, delivery);
    }

    /// <summary>Sends a raw frame to one client. Server only.</summary>
    public void SendToClient(int clientId, byte[] frame, NetDelivery delivery = NetDelivery.ReliableOrdered)
    {
        if (_clients.TryGetValue(clientId, out var peer)) Send(peer, frame, delivery);
    }

    /// <summary>Sends a raw frame to every client. Server only.</summary>
    public void SendToAll(byte[] frame, NetDelivery delivery = NetDelivery.ReliableOrdered,
                          int excludeClientId = -1)
        => Broadcast(frame, delivery, excludeClientId);

    private void Broadcast(byte[] frame, NetDelivery delivery, int exceptClientId = -1)
    {
        foreach (var (clientId, peer) in _clients)
        {
            if (clientId == exceptClientId) continue;
            Send(peer, frame, delivery);
        }
    }

    private void Send(NetPeerHandle peer, byte[] frame, NetDelivery delivery)
        => _transport?.Send(peer, frame, delivery);

    // -------------------------------------------------------------------------
    // The script channel
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a named message to every other peer. This is what a game script's
    /// <c>Network.sendToAll(type, data)</c> reaches.
    /// </summary>
    /// <remarks>
    /// A client sends it to the server, which relays it on. The engine does not interpret the
    /// payload; it only guarantees that the sender id the receiver sees is the one the server
    /// assigned, not one the sender chose.
    /// </remarks>
    public void SendMessageToAll(string type, JsonNode? payload = null)
    {
        if (!IsRunning) return;

        string json  = payload?.ToJsonString() ?? "";
        int    sender = LocalClientId;
        byte[] frame  = new NetWriter(NetMessage.Message).Int(sender).String(type).String(json).ToArray();

        if (IsServer) Broadcast(frame, NetDelivery.ReliableOrdered);
        else          SendToServer(frame);
    }

    /// <summary>Sends a named message to one peer. Server only; a client's goes via the server.</summary>
    public void SendMessageTo(int clientId, string type, JsonNode? payload = null)
    {
        if (!IsRunning) return;
        byte[] frame = new NetWriter(NetMessage.Message)
            .Int(LocalClientId).String(type).String(payload?.ToJsonString() ?? "").ToArray();

        if (IsServer) SendToClient(clientId, frame);
        else          SendToServer(frame);
    }

    // -------------------------------------------------------------------------
    // Spawning
    // -------------------------------------------------------------------------

    /// <summary>Tells every client to build a networked actor. Server only.</summary>
    internal void BroadcastSpawn(uint networkId, string actorName, int ownerClientId, byte[] state)
        => Broadcast(new NetWriter(NetMessage.Spawn)
            .UInt(networkId).String(actorName).Int(ownerClientId).Bytes(state).ToArray(),
            NetDelivery.ReliableOrdered);

    /// <summary>Tells every client to destroy a networked actor. Server only.</summary>
    internal void BroadcastDespawn(uint networkId)
        => Broadcast(new NetWriter(NetMessage.Despawn).UInt(networkId).ToArray(),
                     NetDelivery.ReliableOrdered);

    /// <summary>Allocates the next network id. Server only.</summary>
    internal uint AllocateNetworkId()
    {
        if (!IsServer)
            throw new InvalidOperationException("Network ids are allocated by the server.");
        return _nextNetworkId++;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static double Now() => (double)Environment.TickCount64;

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private void Log(string message)
    {
        if (_isLoopbackClientHalf) return;   // one line per event, not two
        Console.WriteLine($"[NetworkManager] {message}");
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Teardown();
        if (Instance == this) Instance = null;
    }
}
