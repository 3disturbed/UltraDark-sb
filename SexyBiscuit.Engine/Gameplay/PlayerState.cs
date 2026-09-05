using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Gameplay;

/// <summary>
/// Per-player state that every machine in a session needs to see — name, score, ping.
/// Modelled on Unreal's <c>APlayerState</c>.
/// </summary>
/// <remarks>
/// Lives separately from the pawn so it survives death and respawn, and separately from the
/// controller so remote clients can read it for players they do not own. Mark fields you add
/// with <c>[Replicate]</c> to have the replication system carry them.
/// </remarks>
public class PlayerState : Actor
{
    /// <summary>Display name shown in scoreboards and nameplates.</summary>
    public string PlayerName { get; set; } = "Player";

    /// <summary>Stable identifier for this player within the session.</summary>
    public int PlayerId { get; set; }

    /// <summary>Score in whatever unit the game mode uses.</summary>
    public int Score { get; set; }

    /// <summary>Team index; -1 means unassigned or free-for-all.</summary>
    public int TeamId { get; set; } = -1;

    /// <summary>Round-trip latency in milliseconds. Zero for the local player on a listen server.</summary>
    public int PingMs { get; set; }

    /// <summary>True when this state belongs to a bot rather than a human.</summary>
    public bool IsBot { get; set; }

    /// <summary>Raised whenever <see cref="Score"/> changes through <see cref="AddScore"/>.</summary>
    public SBEvent<int> ScoreChanged { get; } = new();

    public PlayerState() : base("PlayerState") { }

    /// <summary>Adds to the score and broadcasts <see cref="ScoreChanged"/> with the new total.</summary>
    public void AddScore(int delta)
    {
        Score += delta;
        ScoreChanged.Broadcast(Score);
    }
}
