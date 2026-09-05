using System.Text.RegularExpressions;

namespace SexyBiscuit.Engine.Mcp.ClaudeCode;

/// <summary>Somewhere a <c>claude</c> binary might be, and why we looked there.</summary>
public sealed record ClaudeCandidate(string Path, string Source);

/// <summary>
/// What the locator needs to know about the machine, injected so the search order is testable
/// without a file system.
/// </summary>
public sealed class LocatorEnvironment
{
    /// <summary>A path the user configured; always tried first.</summary>
    public string? Override { get; init; }

    public string? PathVariable  { get; init; }
    public string? HomeDirectory { get; init; }
    public bool    IsWindows     { get; init; }
    public bool    IsMacOS       { get; init; }
    public string? LocalAppData  { get; init; }
    public string? AppData       { get; init; }
    public string? ProgramFiles  { get; init; }

    public Func<string, bool>                FileExists      { get; init; } = _ => false;
    public Func<string, IEnumerable<string>> ListDirectories { get; init; } = _ => Array.Empty<string>();

    /// <summary>The real machine.</summary>
    public static LocatorEnvironment Current(string? overridePath = null) => new()
    {
        Override      = overridePath,
        PathVariable  = Environment.GetEnvironmentVariable("PATH"),
        HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        IsWindows     = OperatingSystem.IsWindows(),
        IsMacOS       = OperatingSystem.IsMacOS(),
        LocalAppData  = Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        AppData       = Environment.GetEnvironmentVariable("APPDATA"),
        ProgramFiles  = Environment.GetEnvironmentVariable("ProgramFiles"),
        FileExists    = File.Exists,
        ListDirectories = dir =>
        {
            try { return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToArray() : Array.Empty<string>(); }
            catch (Exception) { return Array.Empty<string>(); }
        },
    };
}

/// <summary>
/// Finds <c>claude</c> binaries in the order a user would expect: their override, PATH, the
/// native installer's locations, package managers, and finally the copy bundled with the Claude
/// desktop app. Every candidate is returned; the caller runs <c>--version</c> on each, because
/// a shim on PATH can exist and still not work.
/// </summary>
public static class ClaudeCodeLocator
{
    public static IReadOnlyList<ClaudeCandidate> Candidates(LocatorEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);

        var results = new List<ClaudeCandidate>();
        var seen    = new HashSet<string>(env.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        void Add(string? path, string source)
        {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) return;
            results.Add(new ClaudeCandidate(path, source));
        }

        if (!string.IsNullOrWhiteSpace(env.Override))
        {
            Add(env.Override, "settings");
        }

        // PATH, in order. Windows: the native .exe before an npm .cmd shim.
        string[] names = env.IsWindows ? new[] { "claude.exe", "claude.cmd" } : new[] { "claude" };
        char separator = env.IsWindows ? ';' : ':';
        foreach (var dir in (env.PathVariable ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                string candidate = Join(env, dir.Trim('"'), name);
                if (env.FileExists(candidate)) Add(candidate, "PATH");
            }
        }

        string? home = env.HomeDirectory;
        if (env.IsWindows)
        {
            if (env.LocalAppData != null)
            {
                Add(Existing(env, Join(env, env.LocalAppData, "Programs", "claude", "claude.exe")), "native installer");
                Add(Existing(env, Join(env, env.LocalAppData, "Programs", "Claude", "claude.exe")), "native installer");
            }
            if (home != null)
            {
                Add(Existing(env, Join(env, home, ".local", "bin", "claude.exe")), "native installer");
                Add(Existing(env, Join(env, home, ".claude", "local", "claude.exe")), "local install");
            }
            if (env.AppData != null)
                Add(Existing(env, Join(env, env.AppData, "npm", "claude.cmd")), "npm");
        }
        else
        {
            if (home != null)
            {
                Add(Existing(env, Join(env, home, ".local", "bin", "claude")), "native installer");
                Add(Existing(env, Join(env, home, ".claude", "local", "claude")), "local install");
            }
            Add(Existing(env, "/usr/local/bin/claude"), "/usr/local/bin");
            Add(Existing(env, "/opt/homebrew/bin/claude"), "homebrew");
        }

        // The Claude desktop app ships a full binary per version; take the newest.
        foreach (var bundle in DesktopBundles(env))
            Add(bundle, "Claude desktop app");

        return results;
    }

    /// <summary>Binaries bundled with the desktop app, newest version first.</summary>
    public static IReadOnlyList<string> DesktopBundles(LocatorEnvironment env)
    {
        var roots = new List<string>();
        if (env.IsMacOS && env.HomeDirectory != null)
            roots.Add(Join(env, env.HomeDirectory, "Library", "Application Support", "Claude", "claude-code"));
        if (env.IsWindows)
        {
            if (env.LocalAppData != null) roots.Add(Join(env, env.LocalAppData, "AnthropicClaude", "claude-code"));
            if (env.AppData != null)      roots.Add(Join(env, env.AppData, "Claude", "claude-code"));
        }

        var found = new List<(Version version, string path)>();
        foreach (var root in roots)
        {
            foreach (var dir in env.ListDirectories(root))
            {
                string name = System.IO.Path.GetFileName(dir);
                if (!Version.TryParse(name, out var version)) continue;

                string binary = env.IsWindows
                    ? Join(env, dir, "claude.exe")
                    : Join(env, dir, "claude.app", "Contents", "MacOS", "claude");
                if (env.FileExists(binary)) found.Add((version, binary));
            }
        }

        return found.OrderByDescending(f => f.version).Select(f => f.path).ToList();
    }

    /// <summary>Parses <c>claude --version</c> output such as <c>2.1.260 (Claude Code)</c>.</summary>
    public static Version? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = Regex.Match(output, @"(\d+)\.(\d+)\.(\d+)");
        return match.Success && Version.TryParse(match.Value, out var v) ? v : null;
    }

    /// <summary>The one-liners a user can run to get a working binary, per platform.</summary>
    public static string InstallInstructions(bool isWindows) => isWindows
        ? "Install Claude Code with: irm https://claude.ai/install.ps1 | iex   (or: npm install -g @anthropic-ai/claude-code), then run `claude` once and sign in."
        : "Install Claude Code with: curl -fsSL https://claude.ai/install.sh | bash   (or: npm install -g @anthropic-ai/claude-code), then run `claude` once and sign in.";

    private static string? Existing(LocatorEnvironment env, string path) => env.FileExists(path) ? path : null;

    // Joins with the target platform's separator, not the host's, so the search order is testable anywhere.
    private static string Join(LocatorEnvironment env, params string[] parts)
    {
        char separator = env.IsWindows ? '\\' : '/';
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (sb.Length > 0 && sb[^1] != separator) sb.Append(separator);
            sb.Append(sb.Length == 0 ? part : part.TrimStart('\\', '/'));
        }
        return sb.ToString();
    }
}
