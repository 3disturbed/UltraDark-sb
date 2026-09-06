namespace SexyBiscuit.Engine.CookieJar;

/// <summary>
/// Carries out a plan. Files are staged and rewritten in the project's own scratch folder first,
/// then moved into place, so a failure half way leaves the project as it was rather than half
/// installed.
/// </summary>
public static class CookieInstaller
{
    /// <summary>Where files are assembled before they are moved into the project.</summary>
    public const string StagingFolder = ".sexybiscuit/cookie-staging";

    /// <summary>File types whose asset references are repointed on the way in.</summary>
    private static readonly string[] RewritableExtensions = { ".scene", ".prefab" };

    /// <summary>
    /// Applies <paramref name="plan"/> and records it in <paramref name="installed"/>, which is
    /// saved on success. Throws <see cref="CookieException"/> when the plan is blocked.
    /// </summary>
    public static CookieInstallOutcome Apply(
        CookieInstallPlan    plan,
        CookieProjectContext project,
        CookieLockFile       installed,
        CookieJarSource?     jar = null)
    {
        if (!plan.IsApplicable)
            throw new CookieException(
                $"Cookie '{plan.Cookie.Id}' cannot be installed: {plan.FirstBlocker?.Detail}",
                "Call the planner first and read its conflicts, or install with overwrite where that is what you mean.");

        string staging = Path.Combine(project.Root, StagingFolder.Replace('/', Path.DirectorySeparatorChar), plan.Cookie.Id);
        var written    = new List<PlannedFile>();
        var unresolved = new List<string>();
        var backups    = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashes     = new Dictionary<string, string>(StringComparer.Ordinal);
        int rewrites   = 0;

        try
        {
            ResetDirectory(staging);

            // 1. Stage every file, rewriting the ones that carry asset references.
            foreach (var file in plan.Writable)
            {
                string staged = Path.Combine(staging, file.DestinationRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);

                if (IsRewritable(file.DestinationRelative))
                {
                    string json = File.ReadAllText(file.SourceAbsolute);
                    File.WriteAllText(staged, CookiePathRewriter.RewriteText(json, plan.PathMap, unresolved));
                    rewrites++;
                }
                else
                {
                    File.Copy(file.SourceAbsolute, staged, overwrite: true);
                }

                hashes[file.DestinationRelative] = CookieLockFile.HashFile(staged) ?? "";
            }

            // 2. Move into place, keeping a copy of anything replaced.
            foreach (var file in plan.Writable)
            {
                string staged   = Path.Combine(staging, file.DestinationRelative.Replace('/', Path.DirectorySeparatorChar));
                string absolute = Path.Combine(project.Root, file.DestinationRelative.Replace('/', Path.DirectorySeparatorChar));

                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

                if (File.Exists(absolute))
                {
                    string backup = Path.Combine(staging, ".replaced", file.DestinationRelative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(absolute, backup, overwrite: true);
                    backups[absolute] = backup;
                }

                File.Copy(staged, absolute, overwrite: true);
                written.Add(file);
            }
        }
        catch (Exception ex)
        {
            RollBack(written, backups, project.Root);
            SafeDelete(staging);
            throw new CookieException($"Installing '{plan.Cookie.Id}' failed and was undone: {ex.Message}", ex);
        }

        SafeDelete(staging);

        // 3. Record it, last, so a project never claims a cookie it does not have.
        var record = new InstalledCookie
        {
            Id           = plan.Cookie.Id,
            Version      = plan.Cookie.Manifest.Version,
            Name         = plan.Cookie.Name,
            Jar          = plan.Cookie.JarName,
            SourceCommit = jar?.PinnedCommit,
            InstalledUtc = DateTime.UtcNow,
            Namespace    = plan.Cookie.Manifest.EffectiveNamespace,
            Engines      = plan.Cookie.Manifest.Engines.ToList(),
            Provides     = plan.Cookie.Manifest.Provides,
            Requires     = plan.Cookie.Manifest.Requires.ToList(),
            Files        = plan.Files
                               .Where(f => f.Action != PlannedFileAction.Blocked)
                               .Select(f => new InstalledFile(f.DestinationRelative,
                                                              hashes.TryGetValue(f.DestinationRelative, out string? h)
                                                                  ? h
                                                                  : CookieLockFile.HashFile(Path.Combine(project.Root, f.DestinationRelative)) ?? ""))
                               .ToList(),
            Directories  = plan.Directories.ToList(),
        };

        installed.Set(record);
        installed.Save(project.Root);

        return new CookieInstallOutcome(plan.Cookie, written, unresolved.Distinct(StringComparer.Ordinal).ToList(), rewrites, record);
    }

    private static bool IsRewritable(string relative)
        => RewritableExtensions.Any(e => relative.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static void RollBack(List<PlannedFile> written, Dictionary<string, string> backups, string root)
    {
        foreach (var file in written)
        {
            string absolute = Path.Combine(root, file.DestinationRelative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (backups.TryGetValue(absolute, out string? backup) && File.Exists(backup))
                    File.Copy(backup, absolute, overwrite: true);
                else if (File.Exists(absolute))
                    File.Delete(absolute);
            }
            catch (Exception)
            {
                // Best effort: one file that will not roll back must not hide the original failure.
            }
        }
    }

    private static void ResetDirectory(string path)
    {
        SafeDelete(path);
        Directory.CreateDirectory(path);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // Scratch space; a leftover folder is harmless.
        }
    }
}
