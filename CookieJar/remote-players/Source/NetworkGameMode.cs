using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Networking;

namespace Cookies.RemotePlayers;

/// <summary>
/// A game mode that knows about clients. Use this instead of <see cref="GameMode"/>, or subclass
/// it, and a machine that connects becomes a spawned, possessed player on every machine.
/// </summary>
/// <remarks>
/// The engine had every piece of this except the join: the transport raises a connection event, the
/// game mode can spawn and possess a player, and nothing joined the two. This is that wire.
/// </remarks>
public class NetworkGameMode : GameMode
{
    /// <summary>Client id to the controller playing for it.</summary>
    private readonly Dictionary<int, PlayerController> _byClient = new();

    public NetworkGameMode() => PlayerControllerFactory = () => new NetPlayerController();

    /// <summary>The controller a client is playing through, or null.</summary>
    public PlayerController? FindController(int clientId)
        => _byClient.TryGetValue(clientId, out var controller) ? controller : null;

    /// <summary>Which client owns a controller, or -1 for a local one.</summary>
    public static int OwnerOf(PlayerController controller)
        => controller is NetPlayerController net ? net.OwningClientId : -1;

    /// <summary>
    /// Spawns and possesses a player for a client that has just connected. Server only: a client
    /// learns about the new player from the spawn packet.
    /// </summary>
    public virtual PlayerController? SpawnRemotePlayer(int clientId)
    {
        if (NetworkManager.Instance is not { IsServer: true }) return null;
        if (_byClient.ContainsKey(clientId)) return _byClient[clientId];

        int index = NextPlayerIndex();
        var controller = SpawnPlayer(index);

        if (controller is NetPlayerController net) net.OwningClientId = clientId;
        _byClient[clientId] = controller;

        if (controller.ControlledPawn is { } pawn)
        {
            var netObj = pawn.GetComponent<NetworkObject>() ?? pawn.AddComponent<NetworkObject>();
            netObj.AssignOwner(clientId);
            netObj.SpawnKey      = PawnSpawnKey;

            if (pawn.GetComponent<NetTransform>() == null) pawn.AddComponent<NetTransform>();

            NetworkObject.Spawn(pawn);
        }

        return controller;
    }

    /// <summary>Removes the player a client was using, when it disconnects.</summary>
    public virtual void RemoveRemotePlayer(int clientId)
    {
        if (!_byClient.Remove(clientId, out var controller)) return;

        if (controller.ControlledPawn is { } pawn) NetworkObject.Despawn(pawn);
        RemovePlayer(controller);
    }

    /// <summary>
    /// What a client looks the pawn up by when it is told to spawn one. Override when a game has
    /// more than one kind of player pawn.
    /// </summary>
    public virtual string PawnSpawnKey => "player-pawn";

    /// <summary>The lowest player index nobody is using.</summary>
    private int NextPlayerIndex()
    {
        var used = Controllers.Select(c => c.PlayerIndex).ToHashSet();
        for (int index = 0; index < 64; index++)
            if (used.Add(index)) return index;

        return Controllers.Count;
    }
}
