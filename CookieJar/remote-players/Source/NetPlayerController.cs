using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Input;
using SexyBiscuit.Engine.Networking;

namespace Cookies.RemotePlayers;

/// <summary>
/// A player controller that works the same whether the person is at this machine or another one.
/// </summary>
/// <remarks>
/// The owner's actions are sent to the server every tick and applied there through the virtual
/// input layer, which the action map reads before it looks at a device. So
/// <see cref="PlayerController.OnPlayerTick"/> is written once and runs unchanged for a local
/// player, a remote one, and anything else that pushes the same actions.
/// </remarks>
public class NetPlayerController : PlayerController
{
    /// <summary>The client this controller plays for, or -1 when it is local.</summary>
    public int OwningClientId { get; set; } = -1;

    /// <summary>The actions whose values are sent. Add to it for a game with more.</summary>
    public List<string> ReplicatedAxes { get; set; } = new() { "MoveX", "MoveY", "CameraX", "CameraY" };

    /// <summary>The actions whose held state is sent.</summary>
    public List<string> ReplicatedButtons { get; set; } = new() { "Jump", "Attack", "Dodge", "Interact" };

    /// <summary>How often input goes out, in times per second.</summary>
    public float SendRate { get; set; } = 30f;

    /// <summary>True when the person playing this controller is at this machine.</summary>
    public bool IsLocallyOwned
    {
        get
        {
            var manager = NetworkManager.Instance;
            if (manager == null) return true;                       // offline: everybody is local
            if (OwningClientId < 0) return manager.IsServer;        // a listen server's own player
            return manager.LocalClientId == OwningClientId;
        }
    }

    private float _sendTimer;

    protected override void Update(float dt)
    {
        base.Update(dt);

        if (!IsLocallyOwned || NetworkManager.Instance == null) return;
        if (ControlledPawn?.GetComponent<NetworkObject>() is not { } netObj) return;

        _sendTimer += dt;
        float interval = 1f / MathF.Max(1f, SendRate);
        if (_sendTimer < interval) return;

        _sendTimer = 0f;
        RpcSystem.CallServerRpc(netObj, nameof(SubmitInput), Pack());
    }

    /// <summary>
    /// Carries one frame of the owner's input to the server, which pushes it into their virtual
    /// input so the pawn is driven by the same code a local player uses.
    /// </summary>
    [ServerRpc(requireOwnership: true)]
    public void SubmitInput(string packed)
    {
        var player = Input?.GetPlayer(PlayerIndex);
        if (player == null) return;

        foreach (string entry in packed.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = entry.IndexOf('=');
            if (split <= 0) continue;

            string action = entry[..split];
            string value  = entry[(split + 1)..];

            if (value is "1" or "0")
            {
                if (value == "1") player.PressVirtual(action);
                else              player.ReleaseVirtual(action);
            }
            else if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out float axis))
            {
                player.SetVirtualAxis(action, axis);
            }
        }
    }

    /// <summary>One frame of input as text: cheap, readable in a packet dump, and easy to extend.</summary>
    private string Pack()
    {
        var player = Player;
        if (player == null) return "";

        var text = new System.Text.StringBuilder();

        foreach (string action in ReplicatedAxes)
            text.Append(action).Append('=')
                .Append(player.GetAxis(action).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                .Append(';');

        foreach (string action in ReplicatedButtons)
            text.Append(action).Append('=').Append(player.IsHeld(action) ? '1' : '0').Append(';');

        return text.ToString();
    }
}
