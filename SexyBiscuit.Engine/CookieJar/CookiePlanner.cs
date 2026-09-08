using System.Text.RegularExpressions;

namespace SexyBiscuit.Engine.CookieJar;

/// <summary>
/// Works out exactly what installing a cookie would do. Pure: it reads the cookie folder and the
/// project's existing files, and writes nothing. Everything that can refuse an install refuses it
/// here, before a byte has moved.
/// </summary>
public static class CookiePlanner
{
    /// <summary>Files at the cookie's root that describe it rather than belong to a project.</summary>
    private static readonly HashSet<string> NotInstalled =
        new(StringComparer.OrdinalIgnoreCase) { Cookie.ManifestFileName, Cookie.AgentFileName, "README.md", ".gitignore", ".DS_Store" };

    /// <summary>Plans an install of one cookie into one project.</summary>
    public static CookieInstallPlan Plan(
        Cookie                 cookie,
        CookieProjectContext   project,
        CookieLockFile         installed,
        CookieInstallOptions?  options = null,
        CookieJarSource?       jar     = null)
    {
        options ??= new CookieInstallOptions();

        var conflicts = new List<CookieConflict>();
        var files     = new List<PlannedFile>();
        var pathMap   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        CheckJar(cookie, jar, conflicts);
        CheckEngineVersion(cookie, project, conflicts);
        CheckAlreadyInstalled(cookie, installed, options, conflicts);
        CheckTypeNames(cookie, installed, conflicts);

        var map = BuildDestinationMap(cookie, project, conflicts);

        foreach (string relative in cookie.Files())
        {
            if (IsCookieMetadata(relative)) continue;

            string? destination = Destination(relative, map);
            if (destination == null)
            {
                conflicts.Add(new CookieConflict(CookieConflictKind.UnmappedFile, relative,
                                                 "no rule says where this file goes, so it will not be installed.",
                                                 Blocking: false));
                continue;
            }

            string source = Path.Combine(cookie.Directory, relative.Replace('/', Path.DirectorySeparatorChar));
            long   bytes  = SafeLength(source);

            if (!CookiePathSafety.Explain(project.Root, destination, out string? reason))
            {
                files.Add(new PlannedFile(source, destination, bytes, PlannedFileAction.Blocked, reason));
                conflicts.Add(new CookieConflict(CookieConflictKind.PathEscape, destination, reason!));
                continue;
            }

            string absolute = Path.Combine(project.Root, destination.Replace('/', Path.DirectorySeparatorChar));
            var (action, why) = Compare(source, absolute, options.Overwrite);

            if (action == PlannedFileAction.Overwrite && !options.Overwrite)
                conflicts.Add(new CookieConflict(CookieConflictKind.FileExists, destination,
                                                 "a different file is already there; install with overwrite to replace it."));

            files.Add(new PlannedFile(source, destination, bytes, action, why));
            pathMap[CookiePathRewriter.Normalise(relative)] = destination;
        }

        var directories = files
            .Where(f => f.Action != PlannedFileAction.Blocked)
            .Select(f => Path.GetDirectoryName(f.DestinationRelative)?.Replace('\\', '/'))
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(d => d.Length)
            .ToList();

        bool requiresBuild = cookie.Manifest.SupportsEngine(CookieManifest.EngineCSharp)
                          && files.Any(f => f.Action is PlannedFileAction.Create or PlannedFileAction.Overwrite
                                         && f.DestinationRelative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

        return new CookieInstallPlan(cookie, files, conflicts, pathMap, directories, requiresBuild);
    }

    // -------------------------------------------------------------------------
    // Checks
    // -------------------------------------------------------------------------

    private static void CheckJar(Cookie cookie, CookieJarSource? jar, List<CookieConflict> conflicts)
    {
        if (jar == null) return;

        if (!jar.IsInstallable)
            conflicts.Add(new CookieConflict(CookieConflictKind.UntrustedJar, jar.Name,
                jar.Enabled
                    ? $"jar '{jar.Name}' has not been trusted. Installing a cookie compiles and runs its code, so a "
                    + "cloned jar has to be approved in the editor's Cookie Jar panel first."
                    : $"jar '{jar.Name}' is disabled."));
    }

    private static void CheckEngineVersion(Cookie cookie, CookieProjectContext project, List<CookieConflict> conflicts)
    {
        var range = cookie.Manifest.EngineVersion;
        if (range == null) return;

        var engine = CookieVersion.Parse(project.ResolvedEngineVersion);

        if (range.Min is { } min && CookieVersion.TryParse(min, out var minimum) && engine.CompareTo(minimum) < 0)
            conflicts.Add(new CookieConflict(CookieConflictKind.EngineVersion, cookie.Id,
                                             $"needs engine {min} or newer; this is {project.ResolvedEngineVersion}."));

        if (range.Max is { } max && CookieVersion.TryParse(max, out var maximum) && engine.CompareTo(maximum) > 0)
            conflicts.Add(new CookieConflict(CookieConflictKind.EngineVersion, cookie.Id,
                                             $"declares support up to engine {max}; this is {project.ResolvedEngineVersion}.",
                                             Blocking: false));
    }

    private static void CheckAlreadyInstalled(
        Cookie cookie, CookieLockFile installed, CookieInstallOptions options, List<CookieConflict> conflicts)
    {
        var existing = installed.Find(cookie.Id);
        if (existing == null) return;

        var have = CookieVersion.Parse(existing.Version);
        var want = cookie.Version;
        int order = want.CompareTo(have);

        if (order == 0)
            conflicts.Add(new CookieConflict(CookieConflictKind.AlreadyInstalled, cookie.Id,
                                             $"version {have} is already installed.", Blocking: !options.Overwrite));
        else if (order < 0)
            conflicts.Add(new CookieConflict(CookieConflictKind.VersionDowngrade, cookie.Id,
                                             $"version {have} is installed and {want} is older.", Blocking: !options.Overwrite));
    }

    private static void CheckTypeNames(Cookie cookie, CookieLockFile installed, List<CookieConflict> conflicts)
    {
        var mine = cookie.Manifest.Provides.TypeNames.ToHashSet(StringComparer.Ordinal);
        if (mine.Count == 0) return;

        foreach (var other in installed.Cookies)
        {
            if (string.Equals(other.Id, cookie.Id, StringComparison.Ordinal)) continue;

            foreach (string clash in other.Provides.TypeNames.Where(mine.Contains))
                conflicts.Add(new CookieConflict(CookieConflictKind.TypeNameCollision, clash,
                    $"cookie '{other.Id}' already provides a type called '{clash}'. A scene names component types by "
                  + "their short name, so two would be ambiguous."));
        }
    }

    // -------------------------------------------------------------------------
    // Where each file goes
    // -------------------------------------------------------------------------

    private static bool IsCookieMetadata(string relative)
        => !relative.Contains('/') && NotInstalled.Contains(relative);

    /// <summary>
    /// The rules that place a file, most specific first: the manifest's own rules, then the
    /// convention that a cookie's top-level folders mirror a project's.
    /// </summary>
    private static List<PlacementRule> BuildDestinationMap(
        Cookie cookie, CookieProjectContext project, List<CookieConflict> conflicts)
    {
        var rules = new List<PlacementRule>();

        foreach (var rule in cookie.Manifest.Files ?? new List<CookieFileRule>())
        {
            try
            {
                rules.Add(PlacementRule.FromGlob(rule.From, rule.To));
            }
            catch (Exception ex)
            {
                conflicts.Add(new CookieConflict(CookieConflictKind.UnmappedFile, rule.From,
                                                 "files rule is unusable: " + ex.Message));
            }
        }

        foreach (var (folder, destination) in project.DefaultMap(cookie.Manifest))
            rules.Add(PlacementRule.ForFolder(folder, destination));

        return rules;
    }

    private static string? Destination(string relative, List<PlacementRule> rules)
    {
        foreach (var rule in rules)
            if (rule.TryMap(relative, out string? destination)) return destination;

        return null;
    }

    private static (PlannedFileAction Action, string? Reason) Compare(string source, string destination, bool overwrite)
    {
        if (!File.Exists(destination)) return (PlannedFileAction.Create, null);

        string? existing = CookieLockFile.HashFile(destination);
        string? incoming = CookieLockFile.HashFile(source);

        if (existing != null && existing == incoming)
            return (PlannedFileAction.SkipIdentical, "already there, byte for byte.");

        return (PlannedFileAction.Overwrite, overwrite ? "replacing a different file." : "a different file is already there.");
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception) { return 0; }
    }

    // -------------------------------------------------------------------------
    // Placement
    // -------------------------------------------------------------------------

    /// <summary>One rule mapping a cookie-relative path to a project-relative one.</summary>
    private sealed class PlacementRule
    {
        private readonly Regex   _match;
        private readonly string  _destination;
        private readonly bool    _destinationIsFolder;
        private readonly string  _stripPrefix;

        private PlacementRule(Regex match, string destination, bool destinationIsFolder, string stripPrefix)
        {
            _match               = match;
            _destination         = destination.TrimEnd('/');
            _destinationIsFolder = destinationIsFolder;
            _stripPrefix         = stripPrefix;
        }

        /// <summary>Everything under <paramref name="folder"/> lands under <paramref name="destination"/>.</summary>
        public static PlacementRule ForFolder(string folder, string destination)
            => new(new Regex("^" + Regex.Escape(folder + "/") + "(?<rest>.+)$", RegexOptions.IgnoreCase),
                   destination, destinationIsFolder: true, stripPrefix: folder + "/");

        /// <summary>A manifest rule, whose <c>from</c> may be a literal path, a folder, or a glob.</summary>
        public static PlacementRule FromGlob(string from, string to)
        {
            string pattern = CookiePathRewriter.Normalise(from);
            if (pattern.Length == 0) throw new ArgumentException("'from' is empty.");

            bool folderDestination = to.EndsWith('/') || to.EndsWith('\\') || !Path.HasExtension(to);
            string prefix = pattern.Split('*')[0];
            int lastSlash = prefix.LastIndexOf('/');
            prefix = lastSlash >= 0 ? prefix[..(lastSlash + 1)] : "";

            if (!pattern.Contains('*') && !Path.HasExtension(pattern))
                pattern = pattern.TrimEnd('/') + "/**";

            return new PlacementRule(new Regex("^" + GlobToRegex(pattern) + "$", RegexOptions.IgnoreCase),
                                     to, folderDestination, prefix);
        }

        public bool TryMap(string relative, out string? destination)
        {
            destination = null;
            var match = _match.Match(relative);
            if (!match.Success) return false;

            if (!_destinationIsFolder)
            {
                destination = _destination;
                return true;
            }

            string rest = match.Groups["rest"].Success
                ? match.Groups["rest"].Value
                : relative.StartsWith(_stripPrefix, StringComparison.OrdinalIgnoreCase)
                    ? relative[_stripPrefix.Length..]
                    : Path.GetFileName(relative);

            destination = _destination.Length == 0 ? rest : _destination + "/" + rest;
            return true;
        }

        /// <summary>Glob to regex: <c>**</c> crosses folders, <c>*</c> does not.</summary>
        private static string GlobToRegex(string glob)
        {
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < glob.Length; i++)
            {
                char c = glob[i];
                if (c == '*')
                {
                    bool doubled = i + 1 < glob.Length && glob[i + 1] == '*';
                    if (doubled)
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/') i++;
                        builder.Append("(?:.*/)?");
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }
                }
                else if (c == '?') builder.Append("[^/]");
                else builder.Append(Regex.Escape(c.ToString()));
            }

            return builder.ToString();
        }
    }
}
