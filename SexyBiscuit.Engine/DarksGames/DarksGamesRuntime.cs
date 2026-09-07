using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.DarksGames;

// ---------------------------------------------------------------------------
// DarksGamesRuntime.cs
// The façade a game -- and a game script -- actually talks to.
// ---------------------------------------------------------------------------

/// <summary>
/// The engine's Darks Games layer: identity, presence, cloud saves and achievements, wired to
/// the game loop.
/// </summary>
/// <remarks>
/// <para>
/// Everything the hub offers is asynchronous and everything a game loop does is not, so this
/// class is the seam between them. Requests go out on the thread pool; their results come back
/// through a queue that <see cref="Tick"/> drains on the game thread, and only then does any
/// event fire. A script handler therefore never runs mid-frame from a socket callback, which is
/// the same guarantee <c>NetworkManager</c> makes.
/// </para>
/// <para>
/// It is also the class that makes being signed out uneventful. Every call is safe when nobody
/// has signed in, and the counterpart in the browser engine behaves identically, so one script
/// covers both.
/// </para>
/// </remarks>
public sealed class DarksGamesRuntime : IDisposable
{
    /// <summary>The running instance, or null.</summary>
    public static DarksGamesRuntime? Instance { get; private set; }

    private readonly ConcurrentQueue<Action> _completions = new();
    private readonly DarksGamesAccount       _account;

    /// <param name="game">The catalogue slug. Also the token audience the hub enforces.</param>
    /// <param name="account">Injected by tests; a real one is built when null.</param>
    public DarksGamesRuntime(string game, DarksGamesAccount? account = null)
    {
        Game      = game;
        _account  = account ?? new DarksGamesAccount(game);
        Instance  = this;

        _account.UserChanged += user => _completions.Enqueue(() => UserChanged?.Invoke(user));
    }

    /// <summary>The catalogue slug this runtime speaks for.</summary>
    public string Game { get; }

    /// <summary>The signed-in player. Never null.</summary>
    public DarksGamesUser User => _account.User;

    public bool SignedIn => _account.SignedIn;

    /// <summary>The player signed in or out.</summary>
    public event Action<DarksGamesUser>? UserChanged;

    /// <summary>A cloud save arrived, or null when there was none.</summary>
    public event Action<DarksGamesSave?>? SaveLoaded;

    /// <summary>
    /// A write was refused because the save moved since this copy was read. The argument is the
    /// server's copy, so the game can merge or ask the player.
    /// </summary>
    public event Action<DarksGamesSave>? SaveConflict;

    /// <summary>An achievement was reported. False when the hub refused it.</summary>
    public event Action<string, bool>? AchievementReported;

    // -------------------------------------------------------------------------
    // Sign-in
    // -------------------------------------------------------------------------

    /// <summary>Adopts a token a launcher or a sign-in flow produced.</summary>
    public bool SignIn(string? token) => _account.SignIn(token);

    /// <summary>Signs in from <c>DG_ACCESS_TOKEN</c>, or from a file the launcher wrote.</summary>
    public bool SignInFromEnvironment(string? tokenFile = null) => _account.SignInFromEnvironment(tokenFile);

    public void SignOut() => _account.SignOut();

    // -------------------------------------------------------------------------
    // The pump
    // -------------------------------------------------------------------------

    /// <summary>
    /// Raises whatever finished since the last call. Pump it once a frame, beside
    /// <c>NetworkManager.Tick</c>; nothing is delivered without it.
    /// </summary>
    public void Tick(float dt)
    {
        while (_completions.TryDequeue(out var raise))
        {
            try { raise(); }
            catch (Exception ex) { Console.Error.WriteLine($"[DarksGames] handler threw: {ex.Message}"); }
        }

        // Presence is republished on a timer rather than on every change: the hub's entry lasts
        // 90 seconds, and a game whose room fills and empties every few seconds would otherwise
        // spend a request on each one.
        if (_presence == null) return;
        _presenceTimer += dt;
        if (_presenceTimer < PresenceInterval) return;
        _presenceTimer = 0f;
        SendPresence(_presence);
    }

    // -------------------------------------------------------------------------
    // Presence
    // -------------------------------------------------------------------------

    private const float PresenceInterval = 45f;

    private sealed record PresenceState(string State, string Detail, string? JoinCode, bool Joinable,
                                        int? Players, int? Max);

    private PresenceState? _presence;
    private float          _presenceTimer;

    /// <summary>
    /// Publishes what the player is doing, so friends see it and can join.
    /// </summary>
    /// <remarks>
    /// A join code is what puts a Join button on this player's row in every friend's overlay, so
    /// pass one whenever the room can take another player. The value is remembered and
    /// re-published before the hub's entry expires, so a game only calls this when something
    /// actually changes.
    /// </remarks>
    public void Presence(string state, string detail = "", string? joinCode = null,
                         bool joinable = true, int? players = null, int? max = null)
    {
        var next = new PresenceState(state, detail, joinCode, joinable, players, max);
        if (next == _presence) return;

        _presence      = next;
        _presenceTimer = 0f;
        SendPresence(next);
    }

    /// <summary>Stops showing the player as in this game.</summary>
    public void ClearPresence()
    {
        _presence = null;
        Fire(_account.ClearPresenceAsync());
    }

    private void SendPresence(PresenceState presence)
        => Fire(_account.PublishPresenceAsync(presence.State, presence.Detail, presence.JoinCode,
                                              presence.Joinable, presence.Players, presence.Max));

    /// <summary>
    /// Publishes presence derived from a running session, so a networked game needs no presence
    /// code of its own.
    /// </summary>
    public void PublishFrom(Networking.NetworkManager? network)
    {
        if (network is not { IsRunning: true })
        {
            ClearPresence();
            return;
        }

        string room = network.Room;
        Presence(room.Length > 0 ? "in a room" : "playing",
                 room.Length > 0 ? $"Room {room}" : string.Empty,
                 room.Length > 0 ? room : null,
                 players: network.Players.Count);
    }

    // -------------------------------------------------------------------------
    // Cloud saves
    // -------------------------------------------------------------------------

    /// <summary>Asks for the cloud save. It arrives on <see cref="SaveLoaded"/>.</summary>
    public void LoadSave()
    {
        _ = Task.Run(async () =>
        {
            var save = await _account.LoadSaveAsync().ConfigureAwait(false);
            _completions.Enqueue(() => SaveLoaded?.Invoke(save));
        });
    }

    /// <summary>
    /// Writes the cloud save.
    /// </summary>
    /// <param name="data">Anything JSON can express.</param>
    /// <param name="version">The game's own save version.</param>
    /// <param name="baseUpdatedAt">
    /// The <c>UpdatedAt</c> of the copy this edit was based on. Passing it turns a lost update
    /// into a <see cref="SaveConflict"/> the game can resolve, rather than one device silently
    /// overwriting the other.
    /// </param>
    public void WriteSave(JsonNode? data, int version = 1, string? baseUpdatedAt = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _account.WriteSaveAsync(data, version, baseUpdatedAt).ConfigureAwait(false);
            }
            catch (DarksGamesSaveConflict conflict)
            {
                _completions.Enqueue(() => SaveConflict?.Invoke(conflict.Server));
            }
        });
    }

    // -------------------------------------------------------------------------
    // Achievements
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reports an achievement from the client.
    /// </summary>
    /// <remarks>
    /// Only accepted for games the catalogue marks <c>clientAchievements: true</c>. A game with a
    /// server unlocks over s2s instead (<see cref="DarksGamesServer"/>), because a self-reported
    /// unlock is a claim, not a fact.
    /// </remarks>
    public void Achievement(string key, int? increment = null)
    {
        _ = Task.Run(async () =>
        {
            bool ok = await _account.ReportAchievementAsync(key, increment).ConfigureAwait(false);
            _completions.Enqueue(() => AchievementReported?.Invoke(key, ok));
        });
    }

    private static void Fire(Task task)
    {
        // Deliberately not awaited: presence is best-effort, and a game loop must never wait on
        // the hub. The client already swallows its own failures.
        _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Dispose()
    {
        _account.Dispose();
        if (Instance == this) Instance = null;
    }
}
