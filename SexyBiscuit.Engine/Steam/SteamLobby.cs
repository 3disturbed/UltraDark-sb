#if STEAMWORKS
using Steamworks;
#endif

namespace SexyBiscuit.Engine.Steam;

/// <summary>
/// Static helpers for Steam matchmaking lobbies: create, join, find, leave, and invite.
/// All public API is safe to call when Steam is not initialised — calls are silently skipped.
/// </summary>
public static class SteamLobby
{
#if STEAMWORKS
    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private static CSteamID _currentLobby = CSteamID.Nil;
    public  static CSteamID CurrentLobby  => _currentLobby;

    // CallResult handles (must be kept alive to receive the callback)
    private static CallResult<LobbyCreated_t>?    _createResult;
    private static CallResult<LobbyEnter_t>?      _joinResult;
    private static CallResult<LobbyMatchList_t>?  _listResult;

    // Callback for rich presence join requests (kept as a field to prevent GC)
    private static Callback<GameRichPresenceJoinRequested_t>? _joinRequestCallback;

    // One-time setup of static callbacks
    private static bool _callbacksRegistered;

    private static void EnsureCallbacks()
    {
        if (_callbacksRegistered) return;
        _callbacksRegistered = true;

        _joinRequestCallback = Callback<GameRichPresenceJoinRequested_t>.Create(
            ev =>
            {
                // Parse the lobby ID from the connect string "+connect_lobby <id>"
                var connectStr = ev.m_rgchConnect;
                ulong lobbyId  = 0;

                const string prefix = "+connect_lobby ";
                int idx = connectStr.IndexOf(prefix, StringComparison.Ordinal);
                if (idx >= 0)
                    ulong.TryParse(connectStr.Substring(idx + prefix.Length).Trim(), out lobbyId);

                OnLobbyJoinRequested?.Invoke(lobbyId);
            });
    }
#endif

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------
    /// <summary>Fired when a lobby is created. (success, lobbyId)</summary>
    public static event Action<bool, ulong>? OnLobbyCreated;

    /// <summary>Fired when a lobby join attempt completes. (success)</summary>
    public static event Action<bool>? OnLobbyJoined;

    /// <summary>Fired when a lobby list request completes. (list of lobby IDs)</summary>
    public static event Action<List<ulong>>? OnLobbiesFound;

    /// <summary>Fired when a friend invites this user via rich presence.</summary>
    public static event Action<ulong>? OnLobbyJoinRequested;

    // -------------------------------------------------------------------------
    // Create
    // -------------------------------------------------------------------------
#if STEAMWORKS
    public static void CreateLobby(ELobbyType type = ELobbyType.k_ELobbyTypePublic, int maxPlayers = 4)
    {
        if (SteamManager.Instance?.IsInitialised != true) return;
        EnsureCallbacks();

        var call = SteamMatchmaking.CreateLobby(type, maxPlayers);
        _createResult = CallResult<LobbyCreated_t>.Create(
            (result, ioFail) =>
            {
                bool success = !ioFail && result.m_eResult == EResult.k_EResultOK;
                if (success)
                    _currentLobby = new CSteamID(result.m_ulSteamIDLobby);
                OnLobbyCreated?.Invoke(success, result.m_ulSteamIDLobby);
            });
        _createResult.Set(call);
    }
#else
    public static void CreateLobby(int maxPlayers = 4) { }
#endif

    // -------------------------------------------------------------------------
    // Join
    // -------------------------------------------------------------------------
#if STEAMWORKS
    public static void JoinLobby(CSteamID lobbyId)
    {
        if (SteamManager.Instance?.IsInitialised != true) return;
        EnsureCallbacks();

        var call = SteamMatchmaking.JoinLobby(lobbyId);
        _joinResult = CallResult<LobbyEnter_t>.Create(
            (result, ioFail) =>
            {
                bool success = !ioFail && (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse
                    == EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
                if (success)
                    _currentLobby = new CSteamID(result.m_ulSteamIDLobby);
                OnLobbyJoined?.Invoke(success);
            });
        _joinResult.Set(call);
    }
#else
    public static void JoinLobby(ulong lobbyId) { }
#endif

    // -------------------------------------------------------------------------
    // Leave
    // -------------------------------------------------------------------------
    public static void LeaveLobby()
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return;
        if (_currentLobby == CSteamID.Nil) return;
        SteamMatchmaking.LeaveLobby(_currentLobby);
        _currentLobby = CSteamID.Nil;
#endif
    }

    // -------------------------------------------------------------------------
    // Find
    // -------------------------------------------------------------------------
#if STEAMWORKS
    public static void FindLobbies(string? gameFilter = null)
    {
        if (SteamManager.Instance?.IsInitialised != true) return;
        EnsureCallbacks();

        if (!string.IsNullOrEmpty(gameFilter))
            SteamMatchmaking.AddRequestLobbyListStringFilter("game", gameFilter, ELobbyComparison.k_ELobbyComparisonEqual);

        var call = SteamMatchmaking.RequestLobbyList();
        _listResult = CallResult<LobbyMatchList_t>.Create(
            (result, ioFail) =>
            {
                if (ioFail) { OnLobbiesFound?.Invoke(new List<ulong>()); return; }

                int count = (int)Math.Min(result.m_nLobbiesMatching, 50u);
                var ids   = new List<ulong>(count);
                for (int i = 0; i < count; i++)
                    ids.Add((ulong)SteamMatchmaking.GetLobbyByIndex(i));

                OnLobbiesFound?.Invoke(ids);
            });
        _listResult.Set(call);
    }
#else
    public static void FindLobbies(string? gameFilter = null) { }
#endif

    // -------------------------------------------------------------------------
    // Lobby data
    // -------------------------------------------------------------------------
    public static void SetLobbyData(string key, string value)
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return;
        if (_currentLobby == CSteamID.Nil) return;
        SteamMatchmaking.SetLobbyData(_currentLobby, key, value);
#endif
    }

#if STEAMWORKS
    public static string GetLobbyData(CSteamID lobby, string key)
    {
        if (SteamManager.Instance?.IsInitialised != true) return string.Empty;
        return SteamMatchmaking.GetLobbyData(lobby, key);
    }
#else
    public static string GetLobbyData(ulong lobby, string key) => string.Empty;
#endif

    // -------------------------------------------------------------------------
    // Invite
    // -------------------------------------------------------------------------
#if STEAMWORKS
    public static void InviteFriend(CSteamID friendId)
    {
        if (SteamManager.Instance?.IsInitialised != true) return;
        if (_currentLobby == CSteamID.Nil) return;
        SteamMatchmaking.InviteUserToLobby(_currentLobby, friendId);
    }
#else
    public static void InviteFriend(ulong friendId) { }
#endif

    // -------------------------------------------------------------------------
    // Rich presence
    // -------------------------------------------------------------------------
    public static void SetRichPresence(string key, string value)
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return;
        SteamFriends.SetRichPresence(key, value);
#endif
    }
}
