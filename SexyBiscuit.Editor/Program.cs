using SexyBiscuit.Editor.GameCode;

namespace SexyBiscuit.Editor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);
        foreach (var unknown in options.Unknown)
            Console.Error.WriteLine($"Ignoring unknown argument '{unknown}'.");

        using var app = new EditorApp(options);
        app.Run();
    }
}
