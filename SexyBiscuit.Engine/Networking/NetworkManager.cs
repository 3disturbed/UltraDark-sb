using LiteNetLib;
using LiteNetLib.Utils;
using System.Net;
using System.Net.Sockets;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Networking;

// ---------------------------------------------------------------------------
// NetworkManager.cs
// Main networking façade using LiteNetLib.
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Internal packet type discriminator (1 byte prefix on every packet)
// ---------------------------------------------------------------------------
internal enum PacketType : byte
{
    StateUpdate     = 1,
    Rpc             = 2,
    SpawnObject     = 3,
    DespawnObject   = 4,
    ClientConnected = 5,
    Ping            = 6,
}

/// <summary>
/// Singleton façade over LiteNetLib that provides server/client transport,
/// drives the <see cref="ReplicationSystem"/> and <see cref="RpcSystem"/>,
/// and exposes connection lifecycle events.
/// </summary>
public class NetworkManager : IDisposable
{
    // -------------------------------------------------------------------------
    // Singleton
    // -------------------------------------------------------------------------

    /// <summary>The active NetworkManager, or <c>null</c> if not running.</summary>
    public static NetworkManager? Instance { get; private set; }

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    public bool IsServer  { get; private set; }
    public bool IsClient  { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>
    /// The local client ID as assigned by the server. Only valid on clients.
    /// Always -1 on a dedicated server.
    /// </summary>
    public int LocalClientId { get; private set; } = -1;

    /// <summary>Round-trip time in milliseconds. Populated on client only.</summary>
    public int Ping { get; private set; }

    // -------------------------------------------------------------------------
    // Sub-systems
    // -------------------------------------------------------------------------

    public ReplicationSystem Replication { get; } = new();
    public RpcSystem         Rpc         { get; } = new();

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /// <summary>Server-side: a client has connected. Argument is the client ID.</summary>
    public event Action<int>? OnClientConnected;

    /// <summary>Server-side: a client has disconnected. Argument is the client ID.</summary>
    public event Action<int>? OnClientDisconnected;

    /// <summary>Client-side: successfully connected to a server.</summary>
    public event Action? OnConnectedToServer;

    /// <summary>Client-side: disconnected from the server.</summary>
    public event Action? OnDisconnectedFromServer;

    // -------------------------------------------------------------------------
    // LiteNetLib internals
    // -------------------------------------------------------------------------

    private NetManager?              _netManager;
    private EventBasedNetListener?   _listener;

    // Server: clientId -> NetPeer
    private readonly Dictionary<int, NetPeer> _peers       = new();
    private readonly object                    _peersLock   = new();

    // Client: single connection to server
    private NetPeer? _serverPeer;

    // Monotonically increasing client ID counter (server-side)
    private int _nextClientId = 1;

    // Network ID counter (server-side)
    private uint _nextNetworkId = 1;

    // Pending inbound packets — accumulated during PollEvents, processed in Tick
    private readonly System.Collections.Concurrent.ConcurrentQueue<InboundPacket> _inboundQueue = new();

    // Ping probe timer (client only)
    private float _pingTimer;
    private const float PingInterval = 1f;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public NetworkManager()
    {
        if (Instance != null)
            throw new InvalidOperationException(
                "A NetworkManager is already running. Dispose the existing one first.");
        Instance = this;
    }

    // -------------------------------------------------------------------------
    // Server API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts a LiteNetLib server on <paramref name="port"/>.
    /// </summary>
    public void StartServer(int port, int maxClients = 16)
    {
        if (IsRunning) throw new InvalidOperationException("NetworkManager is already running.");

        IsServer = true;
        IsClient = false;

        _listener = new EventBasedNetListener();
        _netManager = new NetManager(_listener)
        {
            AutoRecycle         = true,
            UnconnectedMessagesEnabled = false,
        };

        _listener.ConnectionRequestEvent += request =>
        {
            if (_peers.Count < maxClients)
                request.AcceptIfKey("SexyBiscuit");
            else
                request.Reject();
        };

        _listener.PeerConnectedEvent += peer =>
        {
            int clientId = _nextClientId++;
            // Store mapping: we use peer.Id as the key since it's int
            lock (_peersLock)
            {
                _peers[clientId] = peer;
                // Attach the clientId to the peer's Tag for reverse lookup
                peer.Tag = clientId;
            }

            // Notify the new client of its assigned ID
            SendClientConnectedPacket(peer, clientId);

            Console.WriteLine($"[NetworkManager] Client {clientId} connected (peer {peer.Id}).");
            OnClientConnected?.Invoke(clientId);
        };

        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            int clientId = GetClientId(peer);
            lock (_peersLock) { _peers.Remove(clientId); }
            Console.WriteLine($"[NetworkManager] Client {clientId} disconnected: {info.Reason}.");
            OnClientDisconnected?.Invoke(clientId);
        };

        _listener.NetworkReceiveEvent += (peer, reader, channel, deliveryMethod) =>
        {
            int senderId = GetClientId(peer);
            EnqueueInbound(reader.GetRemainingBytes(), senderId: senderId);
        };

        _netManager.Start(port);
        IsRunning = true;

        Console.WriteLine($"[NetworkManager] Server started on port {port}.");
    }

    /// <summary>Stops the server and disconnects all clients.</summary>
    public void StopServer()
    {
        if (!IsServer || !IsRunning) return;
        _netManager?.Stop();
        IsRunning = false;
        IsServer  = false;
        lock (_peersLock) { _peers.Clear(); }
        Console.WriteLine("[NetworkManager] Server stopped.");
    }

    /// <summary>
    /// Kicks a connected client with an optional human-readable reason.
    /// </summary>
    public void KickClient(int clientId, string reason = "")
    {
        NetPeer? peer;
        lock (_peersLock) { _peers.TryGetValue(clientId, out peer); }
        if (peer == null)
        {
            Console.Error.WriteLine($"[NetworkManager] KickClient: no peer for clientId={clientId}.");
            return;
        }

        var writer = new NetDataWriter();
        writer.Put(reason);
        peer.Disconnect(writer);
        Console.WriteLine($"[NetworkManager] Kicked client {clientId}: {reason}");
    }

    /// <summary>Snapshot of connected client IDs at the time of the call.</summary>
    public IEnumerable<int> ConnectedClientIds
    {
        get
        {
            lock (_peersLock)
            {
                return _peers.Keys.ToArray();
            }
        }
    }

    // -------------------------------------------------------------------------
    // Client API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Connects to a server as a client.
    /// </summary>
    public void ConnectToServer(string address, int port)
    {
        if (IsRunning) throw new InvalidOperationException("NetworkManager is already running.");

        IsClient = true;
        IsServer = false;

        _listener = new EventBasedNetListener();
        _netManager = new NetManager(_listener)
        {
            AutoRecycle = true,
        };

        _listener.PeerConnectedEvent += peer =>
        {
            _serverPeer = peer;
            Console.WriteLine($"[NetworkManager] Connected to server {address}:{port}.");
            // LocalClientId is assigned by the server via ClientConnected packet
            OnConnectedToServer?.Invoke();
        };

        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            _serverPeer = null;
            Console.WriteLine($"[NetworkManager] Disconnected from server: {info.Reason}.");
            OnDisconnectedFromServer?.Invoke();
        };

        _listener.NetworkReceiveEvent += (peer, reader, channel, deliveryMethod) =>
        {
            EnqueueInbound(reader.GetRemainingBytes(), senderId: -1);
        };

        _netManager.Start();
        _netManager.Connect(address, port, "SexyBiscuit");
        IsRunning = true;

        Console.WriteLine($"[NetworkManager] Connecting to {address}:{port}...");
    }

    /// <summary>Gracefully disconnects from the server.</summary>
    public void Disconnect()
    {
        if (!IsClient || !IsRunning) return;
        _serverPeer?.Disconnect();
        _netManager?.Stop();
        IsRunning = false;
        IsClient  = false;
        _serverPeer = null;
        Console.WriteLine("[NetworkManager] Disconnected from server.");
    }

    // -------------------------------------------------------------------------
    // Tick — call from SBEngine.FixedUpdate
    // -------------------------------------------------------------------------

    /// <summary>
    /// Polls LiteNetLib for new events, drains the inbound packet queue,
    /// and drives the ReplicationSystem. Call this every fixed-update.
    /// </summary>
    public void Tick(float dt)
    {
        if (!IsRunning) return;

        // Let LiteNetLib deliver events (fires the listener callbacks above,
        // which enqueue packets into _inboundQueue)
        _netManager?.PollEvents();

        // Drain inbound queue
        while (_inboundQueue.TryDequeue(out var packet))
            DispatchInboundPacket(packet);

        // Drive replication
        Replication.Tick(dt, this);

        // Client-side ping probe
        if (IsClient)
        {
            _pingTimer += dt;
            if (_pingTimer >= PingInterval)
            {
                _pingTimer = 0f;
                SendPingProbe();
            }

            // Update RTT from LiteNetLib peer stat
            if (_serverPeer != null)
                Ping = _serverPeer.Ping;
        }
    }

    // -------------------------------------------------------------------------
    // Send helpers
    // -------------------------------------------------------------------------

    /// <summary>Sends a packet from the client to the server.</summary>
    public void SendToServer(byte[] data, DeliveryMethod method = DeliveryMethod.ReliableOrdered)
    {
        if (!IsClient || _serverPeer == null) return;
        var writer = new NetDataWriter();
        writer.Put(data);
        _serverPeer.Send(writer, method);
    }

    /// <summary>Sends a packet from the server to a specific client.</summary>
    public void SendToClient(int clientId, byte[] data,
                             DeliveryMethod method = DeliveryMethod.ReliableOrdered)
    {
        NetPeer? peer;
        lock (_peersLock) { _peers.TryGetValue(clientId, out peer); }
        if (peer == null) return;

        var writer = new NetDataWriter();
        writer.Put(data);
        peer.Send(writer, method);
    }

    /// <summary>
    /// Broadcasts a packet to all connected clients, optionally excluding one.
    /// </summary>
    public void SendToAll(byte[] data,
                          DeliveryMethod method = DeliveryMethod.ReliableOrdered,
                          int excludeClientId = -1)
    {
        List<NetPeer> peers;
        lock (_peersLock) { peers = new List<NetPeer>(_peers.Values); }

        var writer = new NetDataWriter();
        writer.Put(data);

        foreach (var peer in peers)
        {
            if (excludeClientId >= 0 && GetClientId(peer) == excludeClientId) continue;
            peer.Send(writer, method);
        }
    }

    // -------------------------------------------------------------------------
    // Internal — network ID allocation
    // -------------------------------------------------------------------------

    /// <summary>Allocates and returns the next unique NetworkId. Server-side only.</summary>
    internal uint AllocateNetworkId()
    {
        if (!IsServer)
            throw new InvalidOperationException("AllocateNetworkId must be called on the server.");
        return _nextNetworkId++;
    }

    // -------------------------------------------------------------------------
    // Internal — packet dispatch
    // -------------------------------------------------------------------------

    private void EnqueueInbound(byte[] data, int senderId)
    {
        _inboundQueue.Enqueue(new InboundPacket(data, senderId));
    }

    private void DispatchInboundPacket(InboundPacket packet)
    {
        if (packet.Data.Length == 0) return;

        using var ms = new System.IO.MemoryStream(packet.Data);
        using var br = new System.IO.BinaryReader(ms);

        var type = (PacketType)br.ReadByte();

        switch (type)
        {
            case PacketType.StateUpdate:
                HandleStateUpdate(br);
                break;

            case PacketType.Rpc:
                HandleRpc(br, packet.SenderId);
                break;

            case PacketType.SpawnObject:
                HandleSpawnObject(br);
                break;

            case PacketType.DespawnObject:
                HandleDespawnObject(br);
                break;

            case PacketType.ClientConnected:
                HandleClientConnected(br);
                break;

            case PacketType.Ping:
                HandlePingPacket(br, packet.SenderId);
                break;

            default:
                Console.Error.WriteLine($"[NetworkManager] Unknown packet type: {type}");
                break;
        }
    }

    // -- StateUpdate ----------------------------------------------------------

    private void HandleStateUpdate(System.IO.BinaryReader br)
    {
        uint networkId = br.ReadUInt32();
        int  len       = br.ReadInt32();
        byte[] state   = len > 0 ? br.ReadBytes(len) : Array.Empty<byte>();
        Replication.HandleIncomingStateUpdate(networkId, state);
    }

    // -- Rpc ------------------------------------------------------------------

    private void HandleRpc(System.IO.BinaryReader br, int senderId)
    {
        RpcSystem.DecodeRpcPacket(br,
            out uint    networkId,
            out bool    isServerRpc,
            out int?    targetClientId,
            out string  methodName,
            out byte[]  argBytes);

        if (isServerRpc)
        {
            // This is a client -> server RPC arriving at the server
            Rpc.HandleIncomingServerRpc(senderId, networkId, methodName, argBytes);
        }
        else
        {
            // This is a server -> client RPC arriving at a client
            Rpc.HandleIncomingClientRpc(networkId, methodName, argBytes, targetClientId);
        }
    }

    // -- SpawnObject ----------------------------------------------------------

    private void HandleSpawnObject(System.IO.BinaryReader br)
    {
        // Client-side: the server has told us to instantiate an actor.
        // The game layer is responsible for listening to this and creating the
        // appropriate Actor type. Here we parse the packet and raise an event
        // that the game can hook into.
        uint   networkId    = br.ReadUInt32();
        string actorName    = br.ReadString();
        int    ownerClientId = br.ReadInt32();
        int    stateLen     = br.ReadInt32();
        byte[] initialState = stateLen > 0 ? br.ReadBytes(stateLen) : Array.Empty<byte>();

        Console.WriteLine(
            $"[NetworkManager] SpawnObject: networkId={networkId} name='{actorName}' owner={ownerClientId}");

        // Raise event for the game layer to create the actor
        OnSpawnObject?.Invoke(networkId, actorName, ownerClientId, initialState);
    }

    /// <summary>
    /// Raised on clients when the server instructs them to spawn a networked actor.
    /// The game layer should create the appropriate Actor, attach a NetworkObject,
    /// set its NetworkId/OwnerClientId/IsOwner flags, apply the initialState via
    /// <see cref="NetworkObject.ApplyRemoteState"/>, and register it with
    /// <see cref="ReplicationSystem.RegisterObject"/>.
    /// </summary>
    public event Action<uint, string, int, byte[]>? OnSpawnObject;

    // -- DespawnObject --------------------------------------------------------

    private void HandleDespawnObject(System.IO.BinaryReader br)
    {
        uint networkId = br.ReadUInt32();
        Console.WriteLine($"[NetworkManager] DespawnObject: networkId={networkId}");
        OnDespawnObject?.Invoke(networkId);
    }

    /// <summary>
    /// Raised on clients when the server instructs them to destroy a networked actor.
    /// </summary>
    public event Action<uint>? OnDespawnObject;

    // -- ClientConnected (server -> client: "here is your client ID") ---------

    private void SendClientConnectedPacket(NetPeer peer, int clientId)
    {
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        bw.Write((byte)PacketType.ClientConnected);
        bw.Write(clientId);
        bw.Flush();

        var writer = new NetDataWriter();
        writer.Put(ms.ToArray());
        peer.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    private void HandleClientConnected(System.IO.BinaryReader br)
    {
        // Client-side: server is telling us our local client ID
        int assignedId = br.ReadInt32();
        LocalClientId = assignedId;
        Console.WriteLine($"[NetworkManager] Assigned LocalClientId={assignedId}");
    }

    // -- Ping -----------------------------------------------------------------

    private void SendPingProbe()
    {
        if (_serverPeer == null) return;
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        bw.Write((byte)PacketType.Ping);
        bw.Write(Environment.TickCount64); // echo timestamp
        bw.Flush();

        var writer = new NetDataWriter();
        writer.Put(ms.ToArray());
        _serverPeer.Send(writer, DeliveryMethod.Unreliable);
    }

    private void HandlePingPacket(System.IO.BinaryReader br, int senderId)
    {
        long timestamp = br.ReadInt64();

        if (IsServer)
        {
            // Echo back to the client that sent it
            NetPeer? peer;
            lock (_peersLock) { _peers.TryGetValue(senderId, out peer); }
            if (peer == null) return;

            using var ms = new System.IO.MemoryStream();
            using var bw = new System.IO.BinaryWriter(ms);
            bw.Write((byte)PacketType.Ping);
            bw.Write(timestamp);
            bw.Flush();

            var writer = new NetDataWriter();
            writer.Put(ms.ToArray());
            peer.Send(writer, DeliveryMethod.Unreliable);
        }
        else
        {
            // Client: compute RTT
            long now = Environment.TickCount64;
            Ping = (int)(now - timestamp);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static int GetClientId(NetPeer peer)
        => peer.Tag is int id ? id : -1;

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsServer) StopServer();
        if (IsClient) Disconnect();

        _netManager?.Stop();
        _netManager = null;

        if (Instance == this) Instance = null;

        GC.SuppressFinalize(this);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private readonly record struct InboundPacket(byte[] Data, int SenderId);
}
