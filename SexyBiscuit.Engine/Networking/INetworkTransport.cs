namespace SexyBiscuit.Engine.Networking;

// ---------------------------------------------------------------------------
// INetworkTransport.cs
// The seam between the engine and the socket.
// ---------------------------------------------------------------------------

/// <summary>Why a connection ended.</summary>
public enum NetDisconnectReason
{
    ClosedByPeer,
    ClosedByUs,
    Timeout,
    TransportError,
}

/// <summary>
/// A connected remote. Identity only — no transport library's types leak through it, so
/// gameplay code never learns whether it is talking over UDP, a WebSocket, or a function call
/// in the same process.
/// </summary>
public sealed class NetPeerHandle
{
    public NetPeerHandle(int id, string address)
    {
        Id      = id;
        Address = address;
    }

    /// <summary>Transport-local peer id. Not the game's client id, which the manager assigns.</summary>
    public int Id { get; }

    /// <summary>For logs and bans. Never parsed for behaviour.</summary>
    public string Address { get; }

    /// <summary>Whatever the host wants to hang off this peer — the manager keeps its client id here.</summary>
    public object? Tag { get; set; }

    public override string ToString() => $"peer#{Id} ({Address})";
}

/// <summary>
/// A transport moves opaque byte frames between peers. It says nothing about what is in them.
/// </summary>
/// <remarks>
/// Implementations are <em>polled</em>, never callback-driven from their own threads:
/// <see cref="Poll"/> drains whatever arrived and raises the events on the caller's thread.
/// That is what keeps the simulation single-threaded, and it is what lets a transport be
/// swapped — for a WebSocket so a browser can join, or for an in-memory pair in a test —
/// without any of the engine noticing.
/// </remarks>
public interface INetworkTransport : IDisposable
{
    /// <summary>True once <see cref="Start"/> has run and before <see cref="Stop"/>.</summary>
    bool IsRunning { get; }

    /// <summary>A short name for logs and diagnostics: "udp", "websocket", "loopback".</summary>
    string Name { get; }

    void Start();

    void Stop();

    /// <summary>
    /// Delivers everything that has arrived since the last call, raising the events below
    /// synchronously on the calling thread. Called once per engine tick.
    /// </summary>
    void Poll();

    /// <summary>Sends one frame to one peer.</summary>
    void Send(NetPeerHandle peer, ReadOnlySpan<byte> payload, NetDelivery delivery);

    /// <summary>Closes one connection without stopping the transport.</summary>
    void Disconnect(NetPeerHandle peer, string reason = "");

    /// <summary>The peers currently connected, as of the call.</summary>
    IReadOnlyCollection<NetPeerHandle> Peers { get; }

    event Action<NetPeerHandle>? PeerConnected;

    event Action<NetPeerHandle, NetDisconnectReason>? PeerDisconnected;

    /// <summary>
    /// The payload is only valid for the duration of the handler. Copy it to keep it.
    /// </summary>
    event Action<NetPeerHandle, ReadOnlyMemory<byte>>? FrameReceived;
}
