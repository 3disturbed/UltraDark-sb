using SexyBiscuit.Editor.Assistant;
using SexyBiscuit.Editor.GameCode;

namespace SexyBiscuit.Editor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);
        foreach (var unknown in options.Unknown)
            Console.Error.WriteLine($"Ignoring unknown argument '{unknown}'.");

        // Headless modes never touch the window or the graphics device.
        if (options.IsHeadless)
            return AssistantSelfTest.Run(options);

        using var app = new EditorApp(options);
        app.Run();
        return 0;
    }
}
