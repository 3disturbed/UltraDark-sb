#if STEAMWORKS
using Steamworks;
#endif

namespace SexyBiscuit.Engine.Steam;

/// <summary>
/// Singleton wrapper around the Steamworks API lifecycle.
/// Call Init() early in engine startup, Update() every frame, and Shutdown() on exit.
/// </summary>
public sealed class SteamManager
{
    // -------------------------------------------------------------------------
    // Singleton
    // -------------------------------------------------------------------------
    public static SteamManager? Instance { get; private set; }

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    public bool IsInitialised { get; private set; }

#if STEAMWORKS
    private Callback<GameOverlayActivated_t>? _overlayCallback;
    private static bool _overlayActive;
    public static bool IsOverlayActive => _overlayActive;
#else
    public static bool IsOverlayActive => false;
#endif

    // -------------------------------------------------------------------------
    // Construction (private — use Init to create the singleton)
    // -------------------------------------------------------------------------
    private SteamManager() { }

    // -------------------------------------------------------------------------
    // Init / Shutdown
    // -------------------------------------------------------------------------
    /// <summary>
    /// Initialises the Steam API. Creates the singleton if it doesn't exist.
    /// Safe to call multiple times — subsequent calls are no-ops if already initialised.
    /// </summary>
    public static void Init(uint appId = 480)
    {
        if (Instance == null)
            Instance = new SteamManager();

        if (Instance.IsInitialised)
            return;

#if STEAMWORKS
        try
        {
            // steam_appid.txt must exist in the working directory, or the
            // environment variable SteamAppId must be set before Init is called.
            if (!SteamAPI.Init())
            {
                System.Console.Error.WriteLine($"[SteamManager] SteamAPI.Init() returned false. " +
                    "Ensure Steam is running and steam_appid.txt contains app {appId}.");
                return;
            }

            Instance.IsInitialised = true;

            // Register overlay callback as an instance field to prevent GC collection
            Instance._overlayCallback = Callback<GameOverlayActivated_t>.Create(
                ev => _overlayActive = ev.m_bActive != 0);

            System.Console.WriteLine($"[SteamManager] Initialised. AppId={SteamUtils.GetAppID()}, " +
                $"Name={SteamFriends.GetPersonaName()}");
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[SteamManager] Exception during Init: {ex.Message}");
        }
#else
        System.Console.WriteLine("[SteamManager] Built without STEAMWORKS — Steam disabled.");
#endif
    }

    /// <summary>Shuts down the Steam API and resets the singleton.</summary>
    public static void Shutdown()
    {
#if STEAMWORKS
        if (Instance?.IsInitialised == true)
        {
            SteamAPI.Shutdown();
            Instance.IsInitialised = false;
            System.Console.WriteLine("[SteamManager] Shutdown.");
        }
#endif
        Instance = null;
    }

    /// <summary>Pumps Steam callbacks. Must be called every frame.</summary>
    public void Update()
    {
#if STEAMWORKS
        if (!IsInitialised) return;
        SteamAPI.RunCallbacks();
#endif
    }

    // -------------------------------------------------------------------------
    // Utility properties
    // -------------------------------------------------------------------------
#if STEAMWORKS
    /// <summary>The Steam AppID of the running application.</summary>
    public static uint AppId =>
        Instance?.IsInitialised == true ? (uint)SteamUtils.GetAppID() : 0u;

    /// <summary>The current user's Steam display name.</summary>
    public static string PersonaName =>
        Instance?.IsInitialised == true ? SteamFriends.GetPersonaName() : string.Empty;

    /// <summary>Triggers Steam's built-in screenshot capture.</summary>
    public static void TriggerScreenshot()
    {
        if (Instance?.IsInitialised != true) return;
        SteamScreenshots.TriggerScreenshot();
    }
#else
    public static uint   AppId        => 0u;
    public static string PersonaName  => string.Empty;
    public static void   TriggerScreenshot() { }
#endif
}
