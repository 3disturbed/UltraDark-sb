using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Gameplay;

/// <summary>
/// Match-wide state that every client needs — elapsed time, the player list, the current
/// phase. Modelled on Unreal's <c>AGameStateBase</c>.
/// </summary>
/// <remarks>
/// The split from <see cref="GameMode"/> is deliberate: the game mode holds the rules and
/// exists only on the server, while the game state holds the facts and is visible everywhere.
/// Anything a client's HUD needs to read belongs here, not on the mode.
/// </remarks>
public class GameState : Actor
{
    /// <summary>Seconds since the match entered <see cref="MatchState.InProgress"/>.</summary>
    public float ElapsedTime { get; internal set; }

    /// <summary>Current phase of the match.</summary>
    public MatchState MatchState { get; internal set; } = MatchState.WaitingToStart;

    /// <summary>Every player currently in the session, including bots.</summary>
    public List<PlayerState> Players { get; } = new();

    /// <summary>Raised when <see cref="MatchState"/> changes, with the new state.</summary>
    public SBEvent<MatchState> MatchStateChanged { get; } = new();

    public GameState() : base("GameState") { }

    /// <summary>Adds a player to <see cref="Players"/> if not already present.</summary>
    public void AddPlayer(PlayerState player)
    {
        if (!Players.Contains(player)) Players.Add(player);
    }

    /// <summary>Removes a player from <see cref="Players"/>.</summary>
    public void RemovePlayer(PlayerState player) => Players.Remove(player);

    /// <summary>Players sorted by score, highest first.</summary>
    public IEnumerable<PlayerState> GetScoreboard()
        => Players.OrderByDescending(p => p.Score);

    protected override void Update(float dt)
    {
        if (MatchState == MatchState.InProgress) ElapsedTime += dt;
    }
}

/// <summary>Phases a match moves through, in order.</summary>
public enum MatchState
{
    /// <summary>Players are still connecting; the mode has not started the match.</summary>
    WaitingToStart,

    /// <summary>Gameplay is live.</summary>
    InProgress,

    /// <summary>Gameplay is suspended but the match has not ended.</summary>
    Paused,

    /// <summary>The match finished; a winner may be known.</summary>
    Ended,
}
