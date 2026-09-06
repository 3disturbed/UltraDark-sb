using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Input;

namespace Cookies.CouchCoop;

/// <summary>
/// Lets people join by pressing a button, and gives each of them their own devices. Put one on the
/// actor that carries the <see cref="GameMode"/>.
/// </summary>
/// <remarks>
/// Spawning a second player has always worked -- the game mode sets the index, picks a different
/// player start and possesses a pawn. What was missing was input: without dividing the devices up,
/// every pawn reads the same keyboard and the same pad zero.
/// </remarks>
public sealed class LocalPlayers : Component
{
    /// <summary>How many people can play, from 1 to 4. Four is the engine's gamepad limit.</summary>
    public int MaxPlayers { get; set; } = 4;

    /// <summary>Whether a free pad joins when it presses <see cref="JoinAction"/>.</summary>
    public bool JoinByPress { get; set; } = true;

    /// <summary>The action a free pad presses to join.</summary>
    public string JoinAction { get; set; } = "Jump";

    /// <summary>Whether player one keeps the keyboard and mouse.</summary>
    public bool KeyboardIsPlayerOne { get; set; } = true;

    /// <summary>Whether a player's slot is freed when their pad is unplugged.</summary>
    public bool DropOnDisconnect { get; set; } = true;

    /// <summary>Ignores joins for this long after the match starts, so a menu press does not carry over.</summary>
    public float JoinGracePeriod { get; set; } = 0.5f;

    /// <summary>Whether to tint each pawn by player index as it spawns.</summary>
    public bool TintPawns { get; set; } = true;

    /// <summary>How many people are playing right now.</summary>
    public int PlayerCount => _claimedPads.Count + (KeyboardIsPlayerOne ? 1 : 0);

    /// <summary>Raised when somebody joins, with their controller.</summary>
    public event Action<PlayerController>? PlayerJoined;

    /// <summary>Raised when somebody's pad is unplugged, with their controller.</summary>
    public event Action<PlayerController>? PlayerLeft;

    private readonly Dictionary<int, int> _claimedPads = new();   // pad index -> player index
    private float _elapsed;

    public override void Start()
    {
        // Player one is spawned by the game mode; take the keyboard and mouse for them, and pad
        // zero as well, so one person alone can use either.
        if (EngineHost.Current?.Input is not { } input) return;

        var first = input.GetPlayer(0);
        first.Devices = KeyboardIsPlayerOne ? InputDeviceKind.All : InputDeviceKind.Gamepad;
        first.GamepadIndex = 0;
    }

    public override void Update(float dt)
    {
        _elapsed += dt;

        if (EngineHost.Current?.Input is not { } input) return;
        if (GameMode.Current is not { } mode) return;

        if (JoinByPress && _elapsed >= JoinGracePeriod) WatchForJoins(input, mode);
        if (DropOnDisconnect) WatchForDrops(input, mode);
    }

    private void WatchForJoins(InputManager input, GameMode mode)
    {
        for (int pad = 0; pad < 4; pad++)
        {
            if (_claimedPads.ContainsKey(pad)) continue;
            if (PlayerCount >= MaxPlayers) return;
            if (!input.IsGamepadConnected(pad)) continue;

            // Pad zero belongs to player one when they have no keyboard, so it is never free.
            if (pad == 0 && !KeyboardIsPlayerOne) continue;

            // A free pad has no player of its own yet, so ask it directly.
            var probe = input.GetPlayer(NextFreeIndex());
            probe.Devices      = InputDeviceKind.Gamepad;
            probe.GamepadIndex = pad;

            if (!probe.IsPressed(JoinAction) && !probe.AnyInputThisFrame()) continue;

            Join(mode, probe.PlayerIndex, pad);
        }
    }

    private void WatchForDrops(InputManager input, GameMode mode)
    {
        foreach (var (pad, playerIndex) in _claimedPads.ToList())
        {
            if (input.IsGamepadConnected(pad)) continue;

            _claimedPads.Remove(pad);

            var controller = mode.Controllers.FirstOrDefault(c => c.PlayerIndex == playerIndex);
            if (controller == null) continue;

            PlayerLeft?.Invoke(controller);
            mode.RemovePlayer(controller);
        }
    }

    private void Join(GameMode mode, int playerIndex, int pad)
    {
        var controller = mode.SpawnPlayer(playerIndex);
        _claimedPads[pad] = playerIndex;

        if (EngineHost.Current?.Input is { } input)
        {
            var player = input.GetPlayer(playerIndex);
            player.Devices      = InputDeviceKind.Gamepad;   // only player one gets the shared keyboard
            player.GamepadIndex = pad;
        }

        if (TintPawns && controller.ControlledPawn is { } pawn)
            (pawn.GetComponent<PlayerColours>() ?? pawn.AddComponent<PlayerColours>()).PlayerIndex = playerIndex;

        PlayerJoined?.Invoke(controller);
    }

    /// <summary>The lowest player index nobody is using.</summary>
    private int NextFreeIndex()
    {
        for (int index = 1; index < 4; index++)
            if (!_claimedPads.ContainsValue(index)) return index;

        return 3;
    }
}
