using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Networking;

namespace Cookies.RemotePlayers;

/// <summary>
/// Owns the transport for as long as the game runs, pumps it, and turns a connection into a player.
/// </summary>
/// <remarks>
/// Game-instance subsystems tick before the scene updates, so a packet that arrived this frame has
/// been applied by the time any actor reads it. That is why nothing in the engine's own loop had to
/// change to make this work.
/// </remarks>
public sealed class NetSessionSubsystem : GameInstanceSubsystem
{
    private NetworkManager? _manager;
    private NetSession?     _session;

    /// <summary>The live transport, or null when offline.</summary>
    public NetworkManager? Manager => _manager;

    /// <summary>True while hosting.</summary>
    public bool IsServer => _manager?.IsServer == true;

    /// <summary>This machine's client id, or -1 when offline.</summary>
    public int LocalClientId => _manager?.LocalClientId ?? -1;

    /// <summary>Starts hosting or connecting, per <paramref name="session"/>.</summary>
    public void Start(NetSession session)
    {
        Stop();
        _session = session;

        // The constructor refuses a second instance and only Dispose clears it, so a hot reload
        // that re-runs Start must reuse whatever is already there.
        _manager = NetworkManager.Instance ?? new NetworkManager();

        _manager.OnClientConnected    += OnClientConnected;
        _manager.OnClientDisconnected += OnClientDisconnected;

        switch (session.Mode)
        {
            case SessionMode.ListenServer:
            case SessionMode.DedicatedServer:
                _manager.StartServer(session.Port, session.MaxPlayers);
                if (session.LanBroadcast) Announce(session);
                break;

            case SessionMode.Client:
                _manager.ConnectToServer(session.Address, session.Port);
                break;
        }
    }

    /// <summary>Leaves the session and lets the transport go.</summary>
    public void Stop()
    {
        if (_manager == null) return;

        _manager.OnClientConnected    -= OnClientConnected;
        _manager.OnClientDisconnected -= OnClientDisconnected;

        try
        {
            LanDiscovery.StopBroadcast();
            _manager.Disconnect();
            _manager.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[remote-players] leaving the session: " + ex.Message);
        }

        _manager = null;
        _session = null;
    }

    public override void Tick(float dt) => _manager?.Tick(dt);

    public override void Deinitialize() => Stop();

    private void OnClientConnected(int clientId)
    {
        if (GameMode.Current is NetworkGameMode mode) mode.SpawnRemotePlayer(clientId);
        else Console.Error.WriteLine($"[remote-players] client {clientId} connected, but the game mode cannot spawn it.");
    }

    private void OnClientDisconnected(int clientId)
    {
        if (GameMode.Current is NetworkGameMode mode) mode.RemoveRemotePlayer(clientId);
    }

    private static void Announce(NetSession session)
    {
        try
        {
            LanDiscovery.StartBroadcast(session.Port, new LanServerInfo
            {
                ServerName  = session.ServerName,
                Port        = session.Port,
                MaxPlayers  = session.MaxPlayers,
                PlayerCount = GameMode.Current?.Controllers.Count ?? 0,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[remote-players] LAN announce failed: " + ex.Message);
        }
    }
}
