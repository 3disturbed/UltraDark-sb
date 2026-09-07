using System.Collections.Concurrent;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SexyBiscuit.Engine.Networking;

// ---------------------------------------------------------------------------
// LiteNetLibTransport.cs
// UDP, for desktop talking to desktop.
// ---------------------------------------------------------------------------

/// <summary>
/// The fast transport: UDP with real unreliable and reliable channels.
/// </summary>
/// <remarks>
/// A browser cannot open one, which is the whole reason
/// <see cref="WebSocketNetTransport"/> exists beside it. Prefer this one whenever both ends are
/// native: a dropped state update genuinely is better than a re-sent stale one, and only UDP
/// can express that.
/// </remarks>
public sealed class LiteNetLibTransport : INetworkTransport
{
    /// <summary>The connection key. A peer that sends anything else is not one of ours.</summary>
    public const string ConnectionKey = "SexyBiscuit";

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager            _net;
    private readonly bool                  _isServer;
    private readonly string?               _address;
    private readonly int                   _port;
    private readonly int                   _maxPeers;

    private readonly ConcurrentDictionary<int, NetPeerHandle> _peers = new();

    private LiteNetLibTransport(bool isServer, string? address, int port, int maxPeers)
    {
        _isServer = isServer;
        _address  = address;
        _port     = port;
        _maxPeers = maxPeers;

        _net = new NetManager(_listener)
        {
            AutoRecycle                = true,
            UnconnectedMessagesEnabled = false,
        };

        _listener.ConnectionRequestEvent += request =>
        {
            if (_peers.Count < _maxPeers) request.AcceptIfKey(ConnectionKey);
            else                          request.Reject();
        };

        _listener.PeerConnectedEvent += peer =>
        {
            var handle = new NetPeerHandle(peer.Id, peer.ToString() ?? "udp");
            peer.Tag = handle;
            _peers[peer.Id] = handle;
            _events.Enqueue(() => PeerConnected?.Invoke(handle));
        };

        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (!_peers.TryRemove(peer.Id, out var handle)) return;
            var reason = info.Reason switch
            {
                DisconnectReason.Timeout or DisconnectReason.ConnectionFailed => NetDisconnectReason.Timeout,
                DisconnectReason.DisconnectPeerCalled                          => NetDisconnectReason.ClosedByUs,
                DisconnectReason.RemoteConnectionClose                         => NetDisconnectReason.ClosedByPeer,
                _                                                              => NetDisconnectReason.TransportError,
            };
            _events.Enqueue(() => PeerDisconnected?.Invoke(handle, reason));
        };

        _listener.NetworkReceiveEvent += (peer, reader, _, _) =>
        {
            // The reader is recycled the moment this returns, so the bytes are copied. The
            // alternative -- handing the span straight on -- worked until the first frame that
            // was not consumed synchronously, and then delivered another packet's contents.
            byte[] frame = reader.GetRemainingBytes();
            if (peer.Tag is NetPeerHandle handle)
                _events.Enqueue(() => FrameReceived?.Invoke(handle, frame));
        };
    }

    /// <summary>Listens on a port for incoming UDP connections.</summary>
    public static LiteNetLibTransport Listen(int port, int maxPeers = 16)
        => new(isServer: true, address: null, port, maxPeers);

    /// <summary>Connects to a server.</summary>
    public static LiteNetLibTransport Connect(string address, int port)
        => new(isServer: false, address, port, maxPeers: 1);

    private readonly ConcurrentQueue<Action> _events = new();

    public string Name      => "udp";
    public bool   IsRunning { get; private set; }

    public IReadOnlyCollection<NetPeerHandle> Peers => _peers.Values.ToArray();

    public event Action<NetPeerHandle>? PeerConnected;
    public event Action<NetPeerHandle, NetDisconnectReason>? PeerDisconnected;
    public event Action<NetPeerHandle, ReadOnlyMemory<byte>>? FrameReceived;

    public void Start()
    {
        if (IsRunning) return;

        if (_isServer)
        {
            if (!_net.Start(_port))
                throw new InvalidOperationException($"could not bind UDP port {_port}");
        }
        else
        {
            _net.Start();
            _net.Connect(_address!, _port, ConnectionKey);
        }

        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _net.Stop();
        _peers.Clear();
    }

    public void Poll()
    {
        if (!IsRunning) return;

        // PollEvents fires the listener callbacks above, which only enqueue. Draining after
        // is what keeps every handler on the caller's thread, as the interface promises.
        _net.PollEvents();
        while (_events.TryDequeue(out var raise)) raise();
    }

    public void Send(NetPeerHandle peer, ReadOnlySpan<byte> payload, NetDelivery delivery)
    {
        var target = _net.GetPeerById(peer.Id);
        if (target is null || target.ConnectionState != ConnectionState.Connected) return;

        var writer = new NetDataWriter();
        writer.Put(payload.ToArray());
        target.Send(writer, Method(delivery));
    }

    public void Disconnect(NetPeerHandle peer, string reason = "")
    {
        var target = _net.GetPeerById(peer.Id);
        if (target is null) return;

        if (string.IsNullOrEmpty(reason))
        {
            target.Disconnect();
            return;
        }

        var writer = new NetDataWriter();
        writer.Put(reason);
        target.Disconnect(writer);
    }

    /// <summary>Round-trip time to a peer in milliseconds, or 0 when it is not connected.</summary>
    public int PingTo(NetPeerHandle peer) => _net.GetPeerById(peer.Id)?.Ping ?? 0;

    private static DeliveryMethod Method(NetDelivery delivery) => delivery switch
    {
        NetDelivery.Unreliable        => DeliveryMethod.Unreliable,
        NetDelivery.ReliableUnordered => DeliveryMethod.ReliableUnordered,
        _                             => DeliveryMethod.ReliableOrdered,
    };

    public void Dispose() => Stop();
}
