using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Steam;

namespace SexyBiscuit.Demo;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
#if STEAMWORKS
        SteamManager.Init(480);
#endif

        var config = new EngineConfig
        {
            WindowTitle = "Biscuit Chronicles",
            WindowWidth  = 1920,
            WindowHeight = 1080,
            StartScene   = "Scenes/MainMenu",
            ShowCursor   = true,
            VSync        = true,
        };

        SBEngine.Run(config);

#if STEAMWORKS
        SteamManager.Shutdown();
#endif
    }
}
