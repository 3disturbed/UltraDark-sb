using System.Collections.Concurrent;

namespace SexyBiscuit.Engine.Networking;

// ---------------------------------------------------------------------------
// LoopbackTransport.cs
// A pair of transports wired to each other in memory.
// ---------------------------------------------------------------------------

/// <summary>
/// Two transports joined in the same process, with no socket between them.
/// </summary>
/// <remarks>
/// This is what makes networking testable and what makes a single-player run of a multiplayer
/// game work: the game hosts and joins itself, every packet goes through the same encode and
/// decode path as a real one, and nothing binds a port. Frames are queued and delivered on
/// <see cref="Poll"/> rather than handed over directly — delivering inline would let a handler
/// run inside the sender's own send call, which no real transport ever does and which hides
/// re-entrancy bugs until the first LAN test.
/// </remarks>
public sealed class LoopbackTransport : INetworkTransport
{
    private readonly ConcurrentQueue<byte[]> _inbox = new();
    private readonly NetPeerHandle           _self;
    private LoopbackTransport?               _other;
    private bool                             _connected;

    private LoopbackTransport(int id, string address) => _self = new NetPeerHandle(id, address);

    /// <summary>Builds a connected pair: the server end first, then the client end.</summary>
    public static (LoopbackTransport Server, LoopbackTransport Client) CreatePair()
    {
        var server = new LoopbackTransport(1, "loopback/server");
        var client = new LoopbackTransport(2, "loopback/client");
        server._other = client;
        client._other = server;
        return (server, client);
    }

    public string Name      => "loopback";
    public bool   IsRunning { get; private set; }

    public event Action<NetPeerHandle>? PeerConnected;
    public event Action<NetPeerHandle, NetDisconnectReason>? PeerDisconnected;
    public event Action<NetPeerHandle, ReadOnlyMemory<byte>>? FrameReceived;

    /// <summary>The peer at the far end, once both ends have started.</summary>
    public IReadOnlyCollection<NetPeerHandle> Peers
        => _connected && _other != null ? new[] { _other._self } : Array.Empty<NetPeerHandle>();

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;

        // The connection exists once both ends are up. Announcing it from the second Start
        // means each end raises PeerConnected exactly once, in its own Poll.
        if (_other is { IsRunning: true })
        {
            _connected        = true;
            _other._connected = true;
            _pendingConnect        = true;
            _other._pendingConnect = true;
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        if (_connected && _other != null)
        {
            _connected = false;
            _other._connected = false;
            _other._pendingDisconnect = NetDisconnectReason.ClosedByPeer;
        }
    }

    private bool                 _pendingConnect;
    private NetDisconnectReason? _pendingDisconnect;

    public void Poll()
    {
        if (_pendingConnect)
        {
            _pendingConnect = false;
            if (_other != null) PeerConnected?.Invoke(_other._self);
        }

        while (_inbox.TryDequeue(out var frame))
        {
            if (_other != null) FrameReceived?.Invoke(_other._self, frame);
        }

        if (_pendingDisconnect is { } reason)
        {
            _pendingDisconnect = null;
            if (_other != null) PeerDisconnected?.Invoke(_other._self, reason);
        }
    }

    public void Send(NetPeerHandle peer, ReadOnlySpan<byte> payload, NetDelivery delivery)
    {
        if (!IsRunning || !_connected || _other == null) return;
        _other._inbox.Enqueue(payload.ToArray());
    }

    public void Disconnect(NetPeerHandle peer, string reason = "") => Stop();

    public void Dispose() => Stop();
}
