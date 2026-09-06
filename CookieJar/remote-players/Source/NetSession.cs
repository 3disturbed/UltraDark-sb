using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Gameplay;

namespace Cookies.RemotePlayers;

/// <summary>How this machine takes part in a session.</summary>
public enum SessionMode
{
    /// <summary>No networking at all. The default, so a scene carrying this component still runs alone.</summary>
    Offline,

    /// <summary>Hosts and plays at the same time.</summary>
    ListenServer,

    /// <summary>Hosts without a local player.</summary>
    DedicatedServer,

    /// <summary>Joins somebody else's server.</summary>
    Client,
}

/// <summary>Who decides where a player's pawn is.</summary>
public enum NetAuthority
{
    /// <summary>
    /// The owner moves their own pawn and the server accepts it. Feels right, and is trivially
    /// cheatable: co-op netcode, not competitive netcode.
    /// </summary>
    ClientAuthoritative,

    /// <summary>
    /// The server moves every pawn from the inputs it is sent. Honest, and without prediction your
    /// own character lags by a round trip.
    /// </summary>
    ServerAuthoritative,
}

/// <summary>
/// The turn-on for networked play. Put one on an actor in the start scene and choose a mode.
/// </summary>
/// <remarks>
/// The transport is pumped by a game-instance subsystem this component asks for, which ticks before
/// the scene updates -- so packets have landed by the time gameplay reads them, and the engine's
/// own loop needed no change.
/// </remarks>
public sealed class NetSession : Component
{
    /// <summary>What this machine does. Offline means nothing starts.</summary>
    public SessionMode Mode { get; set; } = SessionMode.Offline;

    /// <summary>The port to host on, or to connect to.</summary>
    public int Port { get; set; } = 9050;

    /// <summary>The server to join, when <see cref="Mode"/> is <see cref="SessionMode.Client"/>.</summary>
    public string Address { get; set; } = "127.0.0.1";

    /// <summary>How many clients a server accepts.</summary>
    public int MaxPlayers { get; set; } = 8;

    /// <summary>The name a LAN browser shows for this server.</summary>
    public string ServerName { get; set; } = "SexyBiscuit game";

    /// <summary>Whether a server announces itself on the local network.</summary>
    public bool LanBroadcast { get; set; } = true;

    /// <summary>Who moves a pawn. See the remarks on <see cref="NetAuthority"/>.</summary>
    public NetAuthority Authority { get; set; } = NetAuthority.ClientAuthoritative;

    /// <summary>How far behind the newest update a remote pawn is drawn, in seconds.</summary>
    public float InterpolationDelay { get; set; } = 0.1f;

    /// <summary>Whether to start as soon as the scene does.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>The subsystem doing the work, once it exists.</summary>
    public NetSessionSubsystem? Subsystem { get; private set; }

    /// <summary>The settings the rest of the cookie reads, without needing this actor.</summary>
    public static NetSession? Active { get; private set; }

    public override void Start()
    {
        Active    = this;
        Subsystem = GameInstance.Current?.GetSubsystem<NetSessionSubsystem>();

        if (Subsystem == null)
        {
            Console.Error.WriteLine("[remote-players] no GameInstance, so there is nothing to pump the transport.");
            return;
        }

        if (GameMode.Current is not NetworkGameMode)
            Console.Error.WriteLine(
                "[remote-players] the scene's game mode is not a NetworkGameMode, so connecting clients will not "
              + "be spawned. Use NetworkGameMode, or subclass it.");

        if (AutoStart && Mode != SessionMode.Offline) Subsystem.Start(this);
    }

    public override void OnDestroy()
    {
        if (Active == this) Active = null;
        Subsystem?.Stop();
    }

    /// <summary>Starts the session by hand, when <see cref="AutoStart"/> is off.</summary>
    public void StartNow() => Subsystem?.Start(this);

    /// <summary>Leaves the session and returns to offline.</summary>
    public void Leave() => Subsystem?.Stop();
}
