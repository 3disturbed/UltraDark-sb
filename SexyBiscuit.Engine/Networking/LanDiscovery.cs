using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SexyBiscuit.Engine.Networking;

// ---------------------------------------------------------------------------
// LanDiscovery.cs
// UDP broadcast-based LAN server discovery.
// ---------------------------------------------------------------------------

/// <summary>
/// Provides UDP-broadcast-based LAN server advertisement and discovery.
/// Broadcast: the server periodically sends a JSON-encoded
/// <see cref="LanServerInfo"/> to the LAN broadcast address.
/// Discover: a client listens for those broadcasts and invokes a callback for
/// each unique server found within the timeout window.
/// All operations run on dedicated background threads to avoid blocking the game loop.
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    // -------------------------------------------------------------------------
    // Broadcast state (static — one broadcast per process)
    // -------------------------------------------------------------------------

    private static UdpClient?        _broadcastClient;
    private static CancellationTokenSource? _broadcastCts;
    private static Thread?           _broadcastThread;
    private static readonly object   _broadcastLock = new();

    // -------------------------------------------------------------------------
    // Discovery state (static — one discovery scan per process at a time)
    // -------------------------------------------------------------------------

    private static UdpClient?        _discoverClient;
    private static CancellationTokenSource? _discoverCts;
    private static Thread?           _discoverThread;
    private static readonly object   _discoverLock = new();

    // -------------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------------

    private const string BroadcastAddress = "255.255.255.255";
    private const float  BroadcastIntervalSeconds = 2f;

    // -------------------------------------------------------------------------
    // Public API — Broadcast
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts broadcasting <paramref name="info"/> on the LAN every 2 seconds
    /// so that clients running <see cref="Discover"/> can find this server.
    /// Call <see cref="StopBroadcast"/> when the server shuts down.
    /// </summary>
    /// <param name="port">UDP port to broadcast on (must match the Discover port).</param>
    /// <param name="info">Server metadata to include in the broadcast.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if a broadcast is already running. Call <see cref="StopBroadcast"/> first.
    /// </exception>
    public static void StartBroadcast(int port, LanServerInfo info)
    {
        lock (_broadcastLock)
        {
            if (_broadcastThread != null && _broadcastThread.IsAlive)
                throw new InvalidOperationException(
                    "LanDiscovery broadcast is already running. Call StopBroadcast() first.");

            _broadcastCts = new CancellationTokenSource();
            var cts   = _broadcastCts;
            var token = cts.Token;

            _broadcastThread = new Thread(() => BroadcastLoop(port, info, token))
            {
                IsBackground = true,
                Name         = "LanDiscovery.BroadcastThread",
            };
            _broadcastThread.Start();
        }

        Console.WriteLine($"[LanDiscovery] Broadcasting '{info.ServerName}' on port {port}.");
    }

    /// <summary>
    /// Stops the LAN broadcast. No-op if no broadcast is running.
    /// </summary>
    public static void StopBroadcast()
    {
        lock (_broadcastLock)
        {
            _broadcastCts?.Cancel();
            _broadcastCts?.Dispose();
            _broadcastCts = null;

            try { _broadcastClient?.Close(); } catch { /* intentionally silent */ }
            _broadcastClient = null;

            _broadcastThread = null;
        }

        Console.WriteLine("[LanDiscovery] Broadcast stopped.");
    }

    // -------------------------------------------------------------------------
    // Public API — Discovery
    // -------------------------------------------------------------------------

    /// <summary>
    /// Listens on <paramref name="port"/> for broadcast packets from servers
    /// running <see cref="StartBroadcast"/>. Invokes <paramref name="onFound"/>
    /// for each unique server (deduped by Address:Port) found within
    /// <paramref name="timeoutSeconds"/>. Runs on a background thread.
    /// </summary>
    /// <param name="port">UDP port to listen on.</param>
    /// <param name="onFound">
    /// Callback invoked (on the discovery background thread) for each new server.
    /// </param>
    /// <param name="timeoutSeconds">
    /// How long to listen. After the timeout the discovery is stopped automatically.
    /// </param>
    public static void Discover(int port, Action<LanServerInfo> onFound,
                                 float timeoutSeconds = 3f)
    {
        lock (_discoverLock)
        {
            if (_discoverThread != null && _discoverThread.IsAlive)
                StopDiscovery(); // Restart if already running

            _discoverCts = new CancellationTokenSource();
            var token = _discoverCts.Token;

            _discoverThread = new Thread(() => DiscoverLoop(port, onFound, timeoutSeconds, token))
            {
                IsBackground = true,
                Name         = "LanDiscovery.DiscoverThread",
            };
            _discoverThread.Start();
        }

        Console.WriteLine($"[LanDiscovery] Discovering servers on port {port} " +
                          $"(timeout {timeoutSeconds:F1}s)...");
    }

    /// <summary>
    /// Cancels an in-progress discovery scan. No-op if discovery is not running.
    /// </summary>
    public static void StopDiscovery()
    {
        lock (_discoverLock)
        {
            _discoverCts?.Cancel();
            _discoverCts?.Dispose();
            _discoverCts = null;

            try { _discoverClient?.Close(); } catch { /* intentionally silent */ }
            _discoverClient = null;

            _discoverThread = null;
        }

        Console.WriteLine("[LanDiscovery] Discovery stopped.");
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    private bool _disposed;

    /// <summary>
    /// Stops both broadcast and discovery and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopBroadcast();
        StopDiscovery();

        GC.SuppressFinalize(this);
    }

    // -------------------------------------------------------------------------
    // Broadcast loop (runs on background thread)
    // -------------------------------------------------------------------------

    private static void BroadcastLoop(int port, LanServerInfo info, CancellationToken token)
    {
        UdpClient? client = null;
        try
        {
            client = new UdpClient
            {
                EnableBroadcast = true,
                ExclusiveAddressUse = false,
            };

            lock (_broadcastLock) { _broadcastClient = client; }

            var endpoint  = new IPEndPoint(IPAddress.Broadcast, port);
            int intervalMs = (int)(BroadcastIntervalSeconds * 1000);

            // Populate the local IP address if not already set
            if (string.IsNullOrEmpty(info.Address))
                info.Address = GetLocalIPAddress();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(info, LanJsonContext.Default.LanServerInfo);
                    client.Send(payload, payload.Length, endpoint);
                }
                catch (SocketException ex) when (!token.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"[LanDiscovery] Broadcast send error: {ex.Message}");
                }

                // Sleep in small chunks so cancellation is responsive
                int slept = 0;
                while (slept < intervalMs && !token.IsCancellationRequested)
                {
                    int chunk = Math.Min(100, intervalMs - slept);
                    Thread.Sleep(chunk);
                    slept += chunk;
                }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LanDiscovery] Broadcast thread error: {ex}");
        }
        finally
        {
            try { client?.Close(); } catch { /* intentionally silent */ }
        }
    }

    // -------------------------------------------------------------------------
    // Discovery loop (runs on background thread)
    // -------------------------------------------------------------------------

    private static void DiscoverLoop(int port, Action<LanServerInfo> onFound,
                                     float timeoutSeconds, CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        UdpClient? client = null;

        try
        {
            client = new UdpClient(port)
            {
                EnableBroadcast     = true,
                ExclusiveAddressUse = false,
            };
            client.Client.ReceiveTimeout = 500; // 500 ms receive timeout so we can check cancellation

            lock (_discoverLock) { _discoverClient = client; }

            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            while (!token.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                try
                {
                    var remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = client.Receive(ref remoteEndpoint);

                    LanServerInfo? info = null;
                    try
                    {
                        info = JsonSerializer.Deserialize(data, LanJsonContext.Default.LanServerInfo);
                    }
                    catch (JsonException ex)
                    {
                        Console.Error.WriteLine($"[LanDiscovery] Malformed broadcast packet: {ex.Message}");
                        continue;
                    }

                    if (info == null) continue;

                    // Prefer the actual source IP if the broadcast carries the loopback or empty address
                    if (string.IsNullOrEmpty(info.Address) ||
                        info.Address == "127.0.0.1" ||
                        info.Address == "::1")
                    {
                        info.Address = remoteEndpoint.Address.ToString();
                    }

                    // Dedup by Address:Port
                    string key = $"{info.Address}:{info.Port}";
                    if (seen.Add(key))
                    {
                        Console.WriteLine($"[LanDiscovery] Found server: {info.ServerName} @ {key}");
                        onFound(info);
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    // Receive timeout — loop back and check deadline / cancellation
                }
                catch (SocketException ex) when (token.IsCancellationRequested ||
                                                  ex.SocketErrorCode == SocketError.Interrupted)
                {
                    break; // Client was closed
                }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LanDiscovery] Discover thread error: {ex}");
        }
        finally
        {
            try { client?.Close(); } catch { /* intentionally silent */ }
        }

        Console.WriteLine("[LanDiscovery] Discovery scan complete.");
    }

    // -------------------------------------------------------------------------
    // Network utility
    // -------------------------------------------------------------------------

    private static string GetLocalIPAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            var ep = socket.LocalEndPoint as IPEndPoint;
            return ep?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}

// ---------------------------------------------------------------------------
// LanServerInfo
// ---------------------------------------------------------------------------

/// <summary>
/// Metadata about a server that is broadcast over the LAN.
/// All string properties are JSON-serialisable by the source-generated
/// <see cref="LanJsonContext"/> to avoid reflection at runtime.
/// </summary>
public sealed class LanServerInfo
{
    /// <summary>Human-readable server/lobby name.</summary>
    public string ServerName  { get; set; } = string.Empty;

    /// <summary>
    /// IPv4 address of the server. If empty, <see cref="LanDiscovery"/> will
    /// automatically populate it with the sender's IP as seen by the receiver.
    /// </summary>
    public string Address     { get; set; } = string.Empty;

    /// <summary>Port the server is listening on for game connections.</summary>
    public int    Port        { get; set; }

    /// <summary>Current number of connected players.</summary>
    public int    PlayerCount { get; set; }

    /// <summary>Maximum players the server accepts.</summary>
    public int    MaxPlayers  { get; set; }

    /// <summary>Game/engine version string for client compatibility checks.</summary>
    public string GameVersion { get; set; } = string.Empty;

    /// <summary>
    /// Arbitrary key/value pairs the game can use for matchmaking metadata,
    /// e.g. map name, game mode, etc.
    /// </summary>
    public Dictionary<string, string> CustomData { get; init; } = new();
}

// ---------------------------------------------------------------------------
// Source-generated JSON serialisation context
// Avoids runtime reflection for LanServerInfo; keeps AOT / NativeAOT friendly.
// ---------------------------------------------------------------------------

[JsonSerializable(typeof(LanServerInfo))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(
    WriteIndented              = false,
    PropertyNamingPolicy       = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class LanJsonContext : JsonSerializerContext { }
