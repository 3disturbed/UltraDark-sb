using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.CookieJar;

/// <summary>
/// Checks a cookie beyond what parsing its manifest proves: that the files it claims exist, that
/// what it declares matches what it ships, and that nothing it references points outside itself.
/// </summary>
/// <remarks>
/// Deep validation deliberately stops at parsing scenes and prefabs as JSON rather than
/// deserialising them through <see cref="Scene.SceneSerializer"/>. A cookie's own components are
/// not compiled when it is validated, so every one of them would come back as a missing component
/// and the check would report the cookie's whole point as an error.
/// </remarks>
public static class CookieValidator
{
    /// <summary>Everything wrong with <paramref name="cookie"/>, worst first.</summary>
    public static IReadOnlyList<CookieProblem> Validate(Cookie cookie, bool deep = false)
    {
        var problems = new List<CookieProblem>(cookie.Manifest.Validate(Path.GetFileName(cookie.Directory)));
        var files    = cookie.Files();
        var lookup   = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string id    = cookie.Id;

        void Error(string message)   => problems.Add(new CookieProblem(CookieSeverity.Error, id, message));
        void Warning(string message) => problems.Add(new CookieProblem(CookieSeverity.Warning, id, message));

        if (string.IsNullOrWhiteSpace(SafeRead(cookie.AgentPath)))
            Error($"{Cookie.AgentFileName} is empty; it is what an agent is told after installing.");

        // What it says it runs on has to match what it ships.
        bool hasCSharp = files.Any(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        bool hasScript = files.Any(f => f.EndsWith(".js", StringComparison.OrdinalIgnoreCase));

        if (cookie.Manifest.SupportsEngine(CookieManifest.EngineCSharp) && !hasCSharp)
            Warning("declares the 'csharp' engine but ships no .cs file.");
        if (cookie.Manifest.SupportsEngine(CookieManifest.EngineJavaScript) && !hasScript)
            Warning("declares the 'js' engine but ships no .js file.");
        if (hasCSharp && !cookie.Manifest.SupportsEngine(CookieManifest.EngineCSharp))
            Warning("ships C# but does not list 'csharp' in engines, so it will not be installed as code.");

        foreach (string prefab in cookie.Manifest.Provides.Prefabs)
            if (!lookup.Any(f => f.EndsWith("/" + prefab, StringComparison.OrdinalIgnoreCase) ||
                                 f.Equals(prefab, StringComparison.OrdinalIgnoreCase)))
                Error($"provides.prefabs names '{prefab}', which is not in the cookie.");

        foreach (string script in cookie.Manifest.Provides.Scripts)
            if (!lookup.Any(f => f.EndsWith("/" + script, StringComparison.OrdinalIgnoreCase) ||
                                 f.Equals(script, StringComparison.OrdinalIgnoreCase)))
                Error($"provides.scripts names '{script}', which is not in the cookie.");

        foreach (var rule in cookie.Manifest.Files ?? new List<CookieFileRule>())
            if (!rule.From.Contains('*') && !lookup.Contains(CookiePathRewriter.Normalise(rule.From)))
                Error($"files rule points at '{rule.From}', which is not in the cookie.");

        if (cookie.Manifest.EffectiveNamespace is { } expected && hasCSharp)
            foreach (string file in files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                string? declared = DeclaredNamespace(Path.Combine(cookie.Directory, file.Replace('/', Path.DirectorySeparatorChar)));
                if (declared != null && declared != expected)
                    Warning($"{file} declares namespace '{declared}'; this cookie's namespace is '{expected}'.");
            }

        if (!deep) return problems;

        foreach (string file in files.Where(IsSceneLike))
        {
            string absolute = Path.Combine(cookie.Directory, file.Replace('/', Path.DirectorySeparatorChar));
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(SafeRead(absolute) ?? "");
            }
            catch (Exception ex)
            {
                Error($"{file} is not valid JSON: {ex.Message}");
                continue;
            }

            var unresolved = new List<string>();
            CookiePathRewriter.Rewrite(root, EmptyMap, unresolved);

            foreach (string reference in unresolved.Distinct(StringComparer.OrdinalIgnoreCase))
                if (!lookup.Contains(CookiePathRewriter.Normalise(reference)))
                    Warning($"{file} references '{reference}', which the cookie does not ship. It has to already exist in the project.");
        }

        return problems;
    }

    /// <summary>Validates every cookie a catalogue found.</summary>
    public static IReadOnlyList<CookieProblem> ValidateAll(CookieCatalogue catalogue, bool deep = false)
    {
        var problems = new List<CookieProblem>(catalogue.Problems);
        foreach (var cookie in catalogue.All) problems.AddRange(Validate(cookie, deep));
        return problems;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static bool IsSceneLike(string file)
        => file.EndsWith(".scene", StringComparison.OrdinalIgnoreCase)
        || file.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

    private static string? SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch (Exception) { return null; }
    }

    /// <summary>The first namespace a C# file declares, file-scoped or braced.</summary>
    internal static string? DeclaredNamespace(string path)
    {
        foreach (string line in SafeRead(path)?.Split('\n') ?? Array.Empty<string>())
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("namespace ", StringComparison.Ordinal)) continue;

            string rest = trimmed["namespace ".Length..].Trim();
            int stop = rest.IndexOfAny(new[] { ';', '{', ' ' });
            return stop >= 0 ? rest[..stop] : rest;
        }

        return null;
    }
}
