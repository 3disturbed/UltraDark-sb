namespace SexyBiscuit.Engine.Code;

/// <summary>
/// Finds <c>node</c> and <c>npm</c> for the html5 test and lint runs, wherever node was
/// installed: PATH first, then Homebrew, the system prefix, the newest nvm version, and on
/// Windows the Program Files install.
/// </summary>
public static class NodeLocator
{
    /// <summary>The npm launcher, or null when node is not installed.</summary>
    public static string? FindNpm() => ProcessRunner.FindOnPath("npm", ExtraDirectories());

    /// <summary>The node binary, or null when node is not installed.</summary>
    public static string? FindNode() => ProcessRunner.FindOnPath("node", ExtraDirectories());

    private static string[] ExtraDirectories()
    {
        var extra = new List<string> { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" };

        string nvm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nvm", "versions", "node");
        if (Directory.Exists(nvm))
        {
            extra.AddRange(Directory.GetDirectories(nvm)
                .Select(d => (Path: d, Version: Version.TryParse(Path.GetFileName(d).TrimStart('v'), out var v) ? v : new Version(0, 0)))
                .OrderByDescending(d => d.Version)
                .Select(d => Path.Combine(d.Path, "bin")));
        }

        if (OperatingSystem.IsWindows())
        {
            extra.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));
            extra.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        }

        return extra.ToArray();
    }
}
