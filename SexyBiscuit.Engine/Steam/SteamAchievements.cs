#if STEAMWORKS
using Steamworks;
#endif

namespace SexyBiscuit.Engine.Steam;

/// <summary>
/// Static helpers for Steam achievements, stat tracking, and the OnStatsReceived callback.
/// </summary>
public static class SteamAchievements
{
    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /// <summary>Fired when Steam has delivered the current user's stats to the local client.</summary>
    public static event Action? OnStatsReceived;

#if STEAMWORKS
    // Callback field — stored statically to survive GC
    private static Callback<UserStatsReceived_t>? _statsReceivedCallback;
    private static bool _callbackRegistered;

    private static void EnsureCallback()
    {
        if (_callbackRegistered) return;
        _callbackRegistered = true;
        _statsReceivedCallback = Callback<UserStatsReceived_t>.Create(
            _ => OnStatsReceived?.Invoke());
    }
#endif

    // -------------------------------------------------------------------------
    // Achievements
    // -------------------------------------------------------------------------

    /// <summary>Unlocks a Steam achievement and immediately stores stats to the server.</summary>
    public static void Unlock(string achievementId)
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return;
        SteamUserStats.SetAchievement(achievementId);
        SteamUserStats.StoreStats();
#endif
    }

    /// <summary>Reports achievement progress to Steam (used for achievements with progress bars).</summary>
    public static void SetProgress(string achievementId, int current, int max)
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return;
        SteamUserStats.IndicateAchievementProgress(achievementId, (uint)current, (uint)max);
#endif
    }

    // -------------------------------------------------------------------------
    // Stats — integer
    // -------------------------------------------------------------------------

    /// <summary>Sets an integer stat and stores it to the Steam backend.</summary>
    public static void SetStat(string statName, int value)
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return;
        SteamUserStats.SetStat(statName, value);
        SteamUserStats.StoreStats();
#endif
    }

    /// <summary>Returns the current value of an integer stat, or 0 if unavailable.</summary>
    public static int GetStatInt(string statName)
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return 0;
        SteamUserStats.GetStat(statName, out int v);
        return v;
#else
        return 0;
#endif
    }

    // -------------------------------------------------------------------------
    // Stats — float
    // -------------------------------------------------------------------------

    /// <summary>Sets a float stat and stores it to the Steam backend.</summary>
    public static void SetStat(string statName, float value)
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return;
        SteamUserStats.SetStat(statName, value);
        SteamUserStats.StoreStats();
#endif
    }

    /// <summary>Returns the current value of a float stat, or 0 if unavailable.</summary>
    public static float GetStatFloat(string statName)
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return 0f;
        SteamUserStats.GetStat(statName, out float v);
        return v;
#else
        return 0f;
#endif
    }

    // -------------------------------------------------------------------------
    // Request current stats from Steam
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asynchronously requests the current user's stats from Steam.
    /// Subscribe to <see cref="OnStatsReceived"/> to be notified when the data arrives.
    /// </summary>
    public static void RequestStats()
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return;
        EnsureCallback();
        SteamUserStats.RequestCurrentStats();
#endif
    }
}
