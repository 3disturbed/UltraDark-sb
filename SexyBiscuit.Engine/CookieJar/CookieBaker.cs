using System.Text.RegularExpressions;

namespace SexyBiscuit.Engine.CookieJar;

/// <summary>What to bake out of a project, and where to put it.</summary>
public sealed record BakeRequest(
    CookieProjectContext  Project,
    string                Id,
    string                Name,
    string                Summary,
    string                AgentInstructions,
    string                DestinationJar,
    IReadOnlyList<string> Files,
    IReadOnlyList<string>? Tags        = null,
    IReadOnlyList<string>? Engines     = null,
    IReadOnlyList<string>? Requires    = null,
    IReadOnlyList<string>? NextSteps   = null,
    string                 Version     = "1.0.0",
    string?                Description = null,
    bool                   Overwrite   = false);

/// <summary>What baking would write.</summary>
public sealed record BakePlan(
    BakeRequest                          Request,
    CookieManifest                       Manifest,
    string                               Directory,
    IReadOnlyList<PlannedFile>           Files,
    IReadOnlyList<CookieConflict>        Conflicts,
    IReadOnlyDictionary<string, string>  PathMap)
{
    public bool IsApplicable => !Conflicts.Any(c => c.Blocking);
    public CookieConflict? FirstBlocker => Conflicts.FirstOrDefault(c => c.Blocking);
}

/// <summary>What baking did.</summary>
public sealed record BakeOutcome(
    string                       Directory,
    CookieManifest               Manifest,
    IReadOnlyList<string>        Files,
    IReadOnlyList<string>        Renamespaced,
    IReadOnlyList<CookieProblem> Validation);

/// <summary>
/// Turns part of a project into a cookie: the reverse of an install, down to inverting the asset
/// path rewriting so the new cookie refers to its own copies rather than to the project it came
/// from.
/// </summary>
public static class CookieBaker
{
    /// <summary>Works out what baking would write, without writing it.</summary>
    public static BakePlan Plan(BakeRequest request)
    {
        var conflicts = new List<CookieConflict>();
        var planned   = new List<PlannedFile>();
        var pathMap   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!CookieManifest.IsValidId(request.Id))
            conflicts.Add(new CookieConflict(CookieConflictKind.UnmappedFile, request.Id,
                                             "id must be kebab-case, 2 to 48 characters."));

        string directory = Path.Combine(request.DestinationJar, request.Id);
        if (Directory.Exists(directory) && !request.Overwrite && Directory.EnumerateFileSystemEntries(directory).Any())
            conflicts.Add(new CookieConflict(CookieConflictKind.AlreadyInstalled, request.Id,
                                             $"'{directory}' already exists. Bake with overwrite to replace it."));

        var manifest = new CookieManifest
        {
            Id          = request.Id,
            Name        = request.Name,
            Version     = request.Version,
            Summary     = request.Summary,
            Description = request.Description,
            Tags        = request.Tags?.ToList()    ?? new List<string>(),
            Engines     = request.Engines?.ToList() ?? new List<string>(),
            Requires    = request.Requires?.ToList()  ?? new List<string>(),
            NextSteps   = request.NextSteps?.ToList() ?? new List<string>(),
        };

        foreach (string selected in request.Files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string relative = CookiePathRewriter.Normalise(selected);
            string absolute = Path.Combine(request.Project.Root, relative.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(absolute))
            {
                conflicts.Add(new CookieConflict(CookieConflictKind.UnmappedFile, relative, "is not in the project.", Blocking: false));
                continue;
            }

            string? inCookie = CookieRelative(relative, request.Project);
            if (inCookie == null)
            {
                conflicts.Add(new CookieConflict(CookieConflictKind.UnmappedFile, relative,
                    "is not under Source, Scripts, Scenes, Assets or Config, so a cookie has nowhere to put it.",
                    Blocking: false));
                continue;
            }

            planned.Add(new PlannedFile(absolute, inCookie, SafeLength(absolute), PlannedFileAction.Create));
            pathMap[relative] = inCookie;
        }

        if (manifest.Engines.Count == 0)
            manifest.Engines = Guess(planned);

        DeriveProvides(manifest, planned);

        return new BakePlan(request, manifest, directory, planned, conflicts, pathMap);
    }

    /// <summary>Writes the cookie and validates it.</summary>
    public static BakeOutcome Apply(BakePlan plan)
    {
        if (!plan.IsApplicable)
            throw new CookieException($"Cannot bake '{plan.Request.Id}': {plan.FirstBlocker?.Detail}");

        Directory.CreateDirectory(plan.Directory);

        var written      = new List<string>();
        var renamespaced = new List<string>();
        string wanted    = plan.Manifest.EffectiveNamespace;

        foreach (var file in plan.Files)
        {
            string destination = Path.Combine(plan.Directory, file.DestinationRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (IsSceneLike(file.DestinationRelative))
            {
                // A baked scene must point at the cookie's own copies, not at where they were.
                string json = File.ReadAllText(file.SourceAbsolute);
                File.WriteAllText(destination, CookiePathRewriter.RewriteText(json, plan.PathMap));
            }
            else if (file.DestinationRelative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                string source = File.ReadAllText(file.SourceAbsolute);
                string moved  = MoveNamespace(source, wanted, out bool changed);
                File.WriteAllText(destination, moved);
                if (changed) renamespaced.Add(file.DestinationRelative);
            }
            else
            {
                File.Copy(file.SourceAbsolute, destination, overwrite: true);
            }

            written.Add(file.DestinationRelative);
        }

        plan.Manifest.Namespace ??= null;   // written only when it differs from the derived one
        File.WriteAllText(Path.Combine(plan.Directory, Cookie.ManifestFileName), plan.Manifest.ToJson());
        File.WriteAllText(Path.Combine(plan.Directory, Cookie.AgentFileName), AgentFile(plan));
        File.WriteAllText(Path.Combine(plan.Directory, "README.md"), ReadmeFile(plan));

        var problems = new List<CookieProblem>();
        var cookie   = Cookie.Load(plan.Directory, "(baking)", problems);
        if (cookie != null) problems.AddRange(CookieValidator.Validate(cookie, deep: true));

        return new BakeOutcome(plan.Directory, plan.Manifest, written, renamespaced, problems);
    }

    // -------------------------------------------------------------------------
    // Placing files
    // -------------------------------------------------------------------------

    /// <summary>Where a project-relative file lands inside a cookie, or null when it has no home.</summary>
    internal static string? CookieRelative(string relative, CookieProjectContext project)
    {
        foreach (var (projectFolder, cookieFolder) in new[]
                 {
                     (project.SourceDirectory, "Source"),
                     (project.ScriptDirectory, "Scripts"),
                     (project.SceneDirectory,  "Scenes"),
                     (project.AssetDirectory,  "Assets"),
                     (project.ConfigDirectory, "Config"),
                 })
        {
            string prefix = projectFolder.TrimEnd('/') + "/";
            if (!relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            string rest = relative[prefix.Length..];

            // Files another cookie installed keep their Cookies/<id>/ folder in the project; a new
            // cookie should not inherit it.
            if (rest.StartsWith("Cookies/", StringComparison.OrdinalIgnoreCase))
            {
                int slash = rest.IndexOf('/', "Cookies/".Length);
                rest = slash >= 0 ? rest[(slash + 1)..] : Path.GetFileName(rest);
            }

            return cookieFolder + "/" + rest;
        }

        return null;
    }

    private static List<string> Guess(IReadOnlyList<PlannedFile> files)
    {
        var engines = new List<string>();
        if (files.Any(f => f.DestinationRelative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            engines.Add(CookieManifest.EngineCSharp);
        if (files.Any(f => f.DestinationRelative.EndsWith(".js", StringComparison.OrdinalIgnoreCase)))
            engines.Add(CookieManifest.EngineJavaScript);
        return engines;
    }

    private static readonly Regex ClassDeclaration = new(
        @"class\s+(?<name>\w+)\s*:\s*(?<bases>[^\r\n{]+)", RegexOptions.Compiled);

    private static readonly string[] ActorBases =
        { "Actor", "Pawn", "Character", "GameMode", "PlayerController", "Controller", "GameState", "PlayerState" };

    /// <summary>
    /// Fills in what the cookie provides by reading its C#. Best effort by design: it is a
    /// starting point the author corrects, and the manifest is what everything else trusts.
    /// </summary>
    private static void DeriveProvides(CookieManifest manifest, IReadOnlyList<PlannedFile> files)
    {
        foreach (var file in files)
        {
            string relative = file.DestinationRelative;

            if (relative.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                manifest.Provides.Scripts.Add(Path.GetFileName(relative));
                continue;
            }

            if (relative.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                manifest.Provides.Prefabs.Add(Path.GetFileName(relative));
                continue;
            }

            if (!relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;

            string text;
            try { text = File.ReadAllText(file.SourceAbsolute); } catch (Exception) { continue; }

            foreach (Match match in ClassDeclaration.Matches(text))
            {
                string name  = match.Groups["name"].Value;
                string bases = match.Groups["bases"].Value;

                if (bases.Contains("Component"))                            manifest.Provides.Components.Add(name);
                else if (ActorBases.Any(b => Regex.IsMatch(bases, $@"\b{b}\b"))) manifest.Provides.ActorClasses.Add(name);
            }
        }

        Distinct(manifest.Provides.Components);
        Distinct(manifest.Provides.ActorClasses);
        Distinct(manifest.Provides.Scripts);
        Distinct(manifest.Provides.Prefabs);
    }

    private static void Distinct(List<string> values)
    {
        var seen = values.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
        values.Clear();
        values.AddRange(seen);
    }

    /// <summary>
    /// Puts a copied file in the cookie's namespace. One line, and reported in the result: a
    /// cookie author who writes the right namespace to begin with never sees this happen.
    /// </summary>
    internal static string MoveNamespace(string source, string wanted, out bool changed)
    {
        changed = false;
        var lines = source.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("namespace ", StringComparison.Ordinal)) continue;

            string rest = trimmed["namespace ".Length..].Trim();
            int stop = rest.IndexOfAny(new[] { ';', '{', ' ' });
            string declared = stop >= 0 ? rest[..stop] : rest;
            if (declared == wanted) return source;

            string indent = lines[i][..(lines[i].Length - trimmed.Length)];
            string tail   = stop >= 0 ? rest[stop..] : ";";
            lines[i] = indent + "namespace " + wanted + tail;
            changed  = true;
            return string.Join('\n', lines);
        }

        return source;
    }

    private static bool IsSceneLike(string relative)
        => relative.EndsWith(".scene", StringComparison.OrdinalIgnoreCase)
        || relative.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch (Exception) { return 0; }
    }

    // -------------------------------------------------------------------------
    // Generated files
    // -------------------------------------------------------------------------

    private static string AgentFile(BakePlan plan)
    {
        var m = plan.Manifest;
        var text = new System.Text.StringBuilder();

        text.AppendLine($"# {m.Name}").AppendLine();
        text.AppendLine(plan.Request.AgentInstructions.Trim()).AppendLine();

        if (m.Provides.Components.Count > 0)
            text.AppendLine("Components: " + string.Join(", ", m.Provides.Components)).AppendLine();
        if (m.Provides.ActorClasses.Count > 0)
            text.AppendLine("Actor classes: " + string.Join(", ", m.Provides.ActorClasses)).AppendLine();
        if (m.SupportsEngine(CookieManifest.EngineCSharp))
            text.AppendLine($"C# namespace: `{m.EffectiveNamespace}`").AppendLine();

        if (m.NextSteps.Count > 0)
        {
            text.AppendLine("After installing:").AppendLine();
            foreach (string step in m.NextSteps) text.AppendLine("- " + step);
        }

        return text.ToString();
    }

    private static string ReadmeFile(BakePlan plan)
        => $"# {plan.Manifest.Name}\n\n{plan.Manifest.Summary}\n\n"
         + $"Baked from `{Path.GetFileName(plan.Request.Project.Root)}` on {DateTime.UtcNow:yyyy-MM-dd}.\n\n"
         + "Install it with `install_cookie`, or from the editor's Cookie Jar panel.\n";
}
