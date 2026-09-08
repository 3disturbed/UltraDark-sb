namespace SexyBiscuit.Engine.Code;

/// <summary>Where the engine's source lives, when the editor runs from a checkout.</summary>
public sealed record EngineRepo(string Root, string EngineCsproj, string EditorCsproj, string TestsCsproj, string Solution, bool IsGitCheckout);

/// <summary>
/// Finds the SexyBiscuit repository: an explicit path, <c>SEXYBISCUIT_REPO</c>, or a walk up from
/// the running binary (which sits four levels deep in <c>bin/</c>).
/// </summary>
public static class EngineRepoLocator
{
    public const string EnvironmentVariable = "SEXYBISCUIT_REPO";

    public static EngineRepo? Find(string? explicitRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot) && Probe(explicitRoot) is { } explicitRepo)
            return explicitRepo;

        string? fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv) && Probe(fromEnv) is { } envRepo)
            return envRepo;

        foreach (var start in new[] { AppContext.BaseDirectory, SafeAssemblyDirectory() })
        {
            if (start == null) continue;
            if (WalkUp(start) is { } repo) return repo;
        }

        return null;
    }

    /// <summary>Checks one directory for the solution and the engine project.</summary>
    public static EngineRepo? Probe(string root)
    {
        try
        {
            string full     = Path.GetFullPath(root);
            string solution = Path.Combine(full, "SexyBiscuit.sln");
            string engine   = Path.Combine(full, "SexyBiscuit.Engine", "SexyBiscuit.Engine.csproj");
            string editor   = Path.Combine(full, "SexyBiscuit.Editor", "SexyBiscuit.Editor.csproj");
            string tests    = Path.Combine(full, "SexyBiscuit.Tests", "SexyBiscuit.Tests.csproj");

            if (!File.Exists(solution) || !File.Exists(engine)) return null;

            return new EngineRepo(full, engine, editor, tests, solution, IsGitCheckout(full));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="root"/> is a git checkout. In a linked worktree — which is what
    /// <c>git worktree add</c> and Claude Code's own <c>--worktree</c> produce — <c>.git</c> is a
    /// file pointing at the real folder, not a directory, so testing only for a directory reports
    /// every worktree as "not a checkout": the engine-repo report loses its branch and commit, and
    /// GitInfo, which follows the pointer correctly, disagrees with the locator that found it.
    /// </summary>
    private static bool IsGitCheckout(string root)
    {
        string dotGit = Path.Combine(root, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    private static EngineRepo? WalkUp(string start)
    {
        string? dir = start;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (Probe(dir) is { } repo) return repo;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        return null;
    }

    private static string? SafeAssemblyDirectory()
    {
        try
        {
            string location = typeof(EngineRepoLocator).Assembly.Location;
            return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
