namespace SexyBiscuit.Engine.CookieJar;

/// <summary>A file an uninstall will leave alone, and why.</summary>
public sealed record KeptFile(string Path, string Reason);

/// <summary>What removing a cookie would do.</summary>
public sealed record CookieUninstallPlan(
    InstalledCookie       Cookie,
    IReadOnlyList<string> Remove,
    IReadOnlyList<KeptFile> Kept,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Dependents)
{
    /// <summary>True when nothing else installed needs this cookie.</summary>
    public bool IsApplicable => Dependents.Count == 0;
}

/// <summary>What an uninstall actually did.</summary>
public sealed record CookieUninstallOutcome(
    string                Id,
    IReadOnlyList<string> Removed,
    IReadOnlyList<KeptFile> Kept,
    IReadOnlyList<string> RemovedDirectories);

/// <summary>
/// Removes a cookie using the record the install wrote. A file is deleted only when it still
/// hashes to what was installed: anything the author has edited since is theirs, and is kept.
/// </summary>
public static class CookieUninstaller
{
    /// <summary>Works out what removing <paramref name="id"/> would do.</summary>
    public static CookieUninstallPlan Plan(string projectRoot, CookieLockFile installed, string id, bool force = false)
    {
        var record = installed.Find(id)
                     ?? throw new CookieException($"Cookie '{id}' is not installed in this project.",
                                                  "Call search_cookies with installed=true to see what is.");

        var remove  = new List<string>();
        var kept    = new List<KeptFile>();
        var missing = new List<string>();

        foreach (var file in record.Files)
        {
            string absolute = Path.Combine(projectRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(absolute))
            {
                missing.Add(file.Path);
                continue;
            }

            string? hash = CookieLockFile.HashFile(absolute);
            if (hash == file.Sha256 || force) remove.Add(file.Path);
            else kept.Add(new KeptFile(file.Path, "edited since it was installed"));
        }

        var dependents = force ? Array.Empty<string>() : installed.Dependents(id).ToArray();

        return new CookieUninstallPlan(record, remove, kept, missing, dependents);
    }

    /// <summary>Carries out an uninstall plan and saves the lock file.</summary>
    public static CookieUninstallOutcome Apply(string projectRoot, CookieLockFile installed, CookieUninstallPlan plan)
    {
        if (!plan.IsApplicable)
            throw new CookieException(
                $"Cookie '{plan.Cookie.Id}' is required by {string.Join(", ", plan.Dependents)}.",
                "Remove those first, or uninstall with force if you mean to break them.");

        var removed = new List<string>();

        foreach (string relative in plan.Remove)
        {
            string absolute = Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                File.Delete(absolute);
                removed.Add(relative);
            }
            catch (Exception ex)
            {
                plan = plan with { Kept = plan.Kept.Append(new KeptFile(relative, "could not be deleted: " + ex.Message)).ToList() };
            }
        }

        // Deepest first, and only when empty: a folder the author has put their own files in stays.
        // The walk continues into the parents, so removing the last cookie does not leave an empty
        // Source/Cookies behind, but it stops before the project's own top-level folders.
        var removedDirectories = new List<string>();
        foreach (string relative in plan.Cookie.Directories.OrderByDescending(d => d.Length))
        {
            string? current = relative;

            while (!string.IsNullOrEmpty(current) && current.Contains('/'))
            {
                string absolute = Path.Combine(projectRoot, current.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (!Directory.Exists(absolute) || Directory.EnumerateFileSystemEntries(absolute).Any()) break;

                    Directory.Delete(absolute);
                    removedDirectories.Add(current);
                }
                catch (Exception)
                {
                    break;   // a folder that will not go is not worth failing an uninstall over
                }

                int slash = current.LastIndexOf('/');
                current = slash > 0 ? current[..slash] : null;
            }
        }

        installed.Remove(plan.Cookie.Id);
        installed.Save(projectRoot);

        return new CookieUninstallOutcome(plan.Cookie.Id, removed, plan.Kept, removedDirectories);
    }
}
