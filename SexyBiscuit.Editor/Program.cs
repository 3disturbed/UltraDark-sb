namespace SexyBiscuit.Editor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // STAThread is required for Windows Forms dialogs (FolderBrowserDialog, etc.)
        using var app = new EditorApp();
        app.Run();
    }
}
