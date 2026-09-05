using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Gameplay;

/// <summary>
/// Marks an actor as a valid spawn location. The game mode picks between all
/// enabled start points when spawning a player.
/// </summary>
public sealed class PlayerStart : Component
{
    /// <summary>Every enabled player start in the scene, in registration order.</summary>
    public static readonly List<PlayerStart> All = new();

    /// <summary>Only starts matching a controller's team are eligible; -1 means any team.</summary>
    public int TeamId { get; set; } = -1;

    /// <summary>Spawn position, taken from the actor's <see cref="Transform3D"/>.</summary>
    public Vector3 Position => Actor.GetComponent<Transform3D>()?.Position ?? Vector3.Zero;

    /// <summary>Spawn yaw in degrees, taken from the actor's <see cref="Transform3D"/>.</summary>
    public float Yaw => Actor.GetComponent<Transform3D>()?.EulerAngles.Y ?? 0f;

    public override void Awake()   => All.Add(this);
    public override void OnDestroy() => All.Remove(this);
}

/// <summary>
/// The rules of a match: who spawns, where, what happens on death, and when the match ends.
/// Modelled on Unreal's <c>AGameModeBase</c>.
/// </summary>
/// <remarks>
/// In a networked session only the server runs a game mode — clients see its consequences
/// through <see cref="GameState"/>. Set <see cref="PawnFactory"/> and
/// <see cref="PlayerControllerFactory"/> to control what gets spawned, or override
/// <see cref="SpawnDefaultPawnFor"/> for full control.
/// </remarks>
/// <example>
/// <code>
/// public class DeathmatchMode : GameMode
/// {
///     public DeathmatchMode()
///     {
///         PawnFactory             = () =&gt; new Character("Fighter");
///         PlayerControllerFactory = () =&gt; new MyPlayerController();
///         ScoreToWin              = 25;
///     }
///
///     public override void OnPlayerDied(PlayerController victim, PlayerController? killer)
///     {
///         killer?.PlayerState?.AddScore(1);
///         RestartPlayer(victim);
///     }
/// }
/// </code>
/// </example>
public class GameMode : Actor
{
    /// <summary>The game mode running in the active scene, or null if none was spawned.</summary>
    public static GameMode? Current { get; private set; }

    /// <summary>Creates the pawn a joining player possesses. Defaults to a plain <see cref="Character"/>.</summary>
    public Func<Pawn> PawnFactory { get; set; } = () => new Character();

    /// <summary>Creates the controller for a joining player.</summary>
    public Func<PlayerController> PlayerControllerFactory { get; set; } = () => new PlayerController();

    /// <summary>Creates the per-player state object.</summary>
    public Func<PlayerState> PlayerStateFactory { get; set; } = () => new PlayerState();

    /// <summary>Creates the match-wide state object.</summary>
    public Func<GameState> GameStateFactory { get; set; } = () => new GameState();

    /// <summary>Layer new gameplay actors are added to.</summary>
    public string SpawnLayer { get; set; } = "default";

    /// <summary>Seconds between a player dying and respawning through <see cref="RestartPlayer"/>.</summary>
    public float RespawnDelay { get; set; } = 3f;

    /// <summary>Score at which <see cref="EndMatch"/> fires automatically. Zero disables it.</summary>
    public int ScoreToWin { get; set; }

    /// <summary>Match length in seconds. Zero means no time limit.</summary>
    public float TimeLimit { get; set; }

    /// <summary>When true the mode spawns one local player as soon as the match starts.</summary>
    public bool AutoStartLocalPlayer { get; set; } = true;

    /// <summary>The match-wide state this mode drives.</summary>
    public GameState GameState { get; private set; } = null!;

    /// <summary>Controllers currently in the match.</summary>
    public List<PlayerController> Controllers { get; } = new();

    /// <summary>Raised when the match begins.</summary>
    public SBEvent MatchStarted { get; } = new();

    /// <summary>Raised when the match ends, carrying the winner or null for a draw.</summary>
    public SBEvent<PlayerState?> MatchEnded { get; } = new();

    private int _nextPlayerId;

    public GameMode() : base("GameMode") { }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    protected override void OnStart()
    {
        Current = this;

        // In an editor's edit-time scene nothing is played: no game state, no players. The
        // match starts when Play loads the scene afresh with PlayMode.IsActive set.
        if (!PlayMode.IsActive) return;

        GameState = GameStateFactory();
        Scene?.AddActor(GameState, SpawnLayer);

        OnPreStart();
        StartMatch();
    }

    /// <summary>Called after the game state exists but before the match starts.</summary>
    protected virtual void OnPreStart() { }

    /// <summary>Moves the match into <see cref="MatchState.InProgress"/> and spawns the local player.</summary>
    public void StartMatch()
    {
        if (GameState.MatchState == MatchState.InProgress) return;

        SetMatchState(MatchState.InProgress);
        GameState.ElapsedTime = 0f;

        if (AutoStartLocalPlayer) SpawnPlayer(0);

        OnMatchStart();
        MatchStarted.Broadcast();
    }

    /// <summary>Called once when the match begins.</summary>
    protected virtual void OnMatchStart() { }

    /// <summary>Ends the match and broadcasts the winner.</summary>
    public void EndMatch(PlayerState? winner = null)
    {
        if (GameState.MatchState == MatchState.Ended) return;

        SetMatchState(MatchState.Ended);
        OnMatchEnd(winner);
        MatchEnded.Broadcast(winner);
    }

    /// <summary>Called once when the match ends.</summary>
    protected virtual void OnMatchEnd(PlayerState? winner) { }

    private void SetMatchState(MatchState state)
    {
        GameState.MatchState = state;
        GameState.MatchStateChanged.Broadcast(state);
    }

    // -------------------------------------------------------------------------
    // Player lifecycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a controller, player state and pawn for a local player and possesses the pawn.
    /// </summary>
    /// <param name="playerIndex">Local player slot — 0 for the first player, 1 for split-screen player two.</param>
    public virtual PlayerController SpawnPlayer(int playerIndex = 0)
    {
        var controller = PlayerControllerFactory();
        controller.PlayerIndex = playerIndex;

        var state = PlayerStateFactory();
        state.PlayerId   = _nextPlayerId++;
        state.PlayerName = $"Player {state.PlayerId + 1}";
        controller.PlayerState = state;

        Scene?.AddActor(state,      SpawnLayer);
        Scene?.AddActor(controller, SpawnLayer);

        Controllers.Add(controller);
        GameState.AddPlayer(state);

        var pawn = SpawnDefaultPawnFor(controller);
        if (pawn != null) controller.Possess(pawn);

        OnPlayerJoined(controller);
        return controller;
    }

    /// <summary>
    /// Creates the pawn for <paramref name="controller"/> and places it at a chosen
    /// <see cref="PlayerStart"/>. Override to spawn different pawns per team or class.
    /// </summary>
    public virtual Pawn? SpawnDefaultPawnFor(PlayerController controller)
    {
        var pawn = PawnFactory();
        var t3d  = pawn.GetComponent<Transform3D>() ?? pawn.AddComponent<Transform3D>();

        var start = ChoosePlayerStart(controller);
        if (start != null)
        {
            t3d.Position     = start.Position;
            t3d.EulerAngles  = new Vector3(0f, start.Yaw, 0f);
            pawn.ControlRotation = new Vector3(0f, start.Yaw, 0f);
        }

        Scene?.AddActor(pawn, SpawnLayer);
        return pawn;
    }

    /// <summary>
    /// Picks a spawn point for <paramref name="controller"/>. The default picks the
    /// team-matching start furthest from any other player, falling back to the first start.
    /// </summary>
    protected virtual PlayerStart? ChoosePlayerStart(PlayerController controller)
    {
        int team = controller.PlayerState?.TeamId ?? -1;

        var eligible = PlayerStart.All
            .Where(s => s.Actor.IsActive && (s.TeamId < 0 || team < 0 || s.TeamId == team))
            .ToList();

        if (eligible.Count == 0) return null;
        if (eligible.Count == 1) return eligible[0];

        var occupied = Controllers
            .Select(c => c.ControlledPawn?.GetComponent<Transform3D>()?.Position)
            .Where(p => p.HasValue)
            .Select(p => p!.Value)
            .ToList();

        if (occupied.Count == 0) return eligible[0];

        return eligible
            .OrderByDescending(s => occupied.Min(o => Vector3.DistanceSquared(o, s.Position)))
            .First();
    }

    /// <summary>
    /// Destroys the controller's current pawn and spawns a fresh one after
    /// <see cref="RespawnDelay"/> seconds.
    /// </summary>
    public virtual void RestartPlayer(PlayerController controller)
    {
        controller.ControlledPawn?.Destroy();
        controller.UnPossess();

        TimerManager.Instance.SetTimer(RespawnDelay, () =>
        {
            if (GameState.MatchState != MatchState.InProgress) return;
            var pawn = SpawnDefaultPawnFor(controller);
            if (pawn != null) controller.Possess(pawn);
        });
    }

    /// <summary>Removes a player from the match and destroys their actors.</summary>
    public virtual void RemovePlayer(PlayerController controller)
    {
        controller.ControlledPawn?.Destroy();
        controller.UnPossess();
        Controllers.Remove(controller);

        if (controller.PlayerState != null)
        {
            GameState.RemovePlayer(controller.PlayerState);
            controller.PlayerState.Destroy();
        }

        controller.Destroy();
    }

    /// <summary>Called after a player joins and has been given a pawn.</summary>
    protected virtual void OnPlayerJoined(PlayerController controller) { }

    /// <summary>
    /// Called by gameplay code when a player dies. The default awards the killer a point
    /// and restarts the victim.
    /// </summary>
    public virtual void OnPlayerDied(PlayerController victim, PlayerController? killer)
    {
        killer?.PlayerState?.AddScore(1);
        RestartPlayer(victim);
    }

    // -------------------------------------------------------------------------
    // Win conditions
    // -------------------------------------------------------------------------

    protected override void Update(float dt)
    {
        if (GameState.MatchState != MatchState.InProgress) return;

        if (TimeLimit > 0f && GameState.ElapsedTime >= TimeLimit)
        {
            EndMatch(GameState.GetScoreboard().FirstOrDefault());
            return;
        }

        if (ScoreToWin > 0)
        {
            var leader = GameState.GetScoreboard().FirstOrDefault();
            if (leader != null && leader.Score >= ScoreToWin) EndMatch(leader);
        }
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
