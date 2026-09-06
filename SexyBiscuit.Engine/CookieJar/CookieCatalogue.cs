namespace SexyBiscuit.Engine.CookieJar;

/// <summary>A cookie one jar supplied and another jar's copy hid.</summary>
public sealed record ShadowedCookie(string Id, string WinningJar, string ShadowedJar);

/// <summary>A search result and how well it matched, highest first.</summary>
public sealed record CookieSearchHit(Cookie Cookie, int Score);

/// <summary>
/// Every cookie the enabled jars supply, merged and de-duplicated. Built by reading manifests
/// only, so scanning a large jar costs a few kilobytes rather than a compile.
/// </summary>
public sealed class CookieCatalogue
{
    private readonly Dictionary<string, Cookie> _byId;

    private CookieCatalogue(
        IReadOnlyList<CookieJarSource> jars,
        Dictionary<string, Cookie>     byId,
        IReadOnlyList<CookieProblem>   problems,
        IReadOnlyList<ShadowedCookie>  shadowed)
    {
        Jars     = jars;
        _byId    = byId;
        Problems = problems;
        Shadowed = shadowed;
        All      = byId.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>The jars this catalogue was built from, in the order they were searched.</summary>
    public IReadOnlyList<CookieJarSource> Jars { get; }

    /// <summary>Every usable cookie, ordered by id.</summary>
    public IReadOnlyList<Cookie> All { get; }

    /// <summary>Everything wrong that was found while scanning. Never thrown.</summary>
    public IReadOnlyList<CookieProblem> Problems { get; }

    /// <summary>Cookies hidden because an earlier jar supplied the same id.</summary>
    public IReadOnlyList<ShadowedCookie> Shadowed { get; }

    /// <summary>An empty catalogue, for a host with no jars configured yet.</summary>
    public static CookieCatalogue Empty { get; } =
        new(Array.Empty<CookieJarSource>(), new Dictionary<string, Cookie>(StringComparer.Ordinal),
            Array.Empty<CookieProblem>(), Array.Empty<ShadowedCookie>());

    /// <summary>
    /// Reads every enabled jar in order. The first jar to supply an id wins, so a jar listed
    /// before the builtin one deliberately overrides it; the loser is recorded rather than lost.
    /// </summary>
    public static CookieCatalogue Scan(IEnumerable<CookieJarSource> jars)
    {
        var ordered  = jars.ToList();
        var byId     = new Dictionary<string, Cookie>(StringComparer.Ordinal);
        var problems = new List<CookieProblem>();
        var shadowed = new List<ShadowedCookie>();

        foreach (var jar in ordered)
        {
            if (!jar.Enabled) continue;

            if (!Directory.Exists(jar.Path))
            {
                problems.Add(new CookieProblem(CookieSeverity.Warning, jar.Name,
                                               $"jar folder is missing: {jar.Path}"));
                continue;
            }

            IEnumerable<string> folders;
            try
            {
                folders = Directory.EnumerateDirectories(jar.Path).OrderBy(d => d, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                problems.Add(new CookieProblem(CookieSeverity.Warning, jar.Name, "jar folder is unreadable: " + ex.Message));
                continue;
            }

            foreach (string folder in folders)
            {
                var cookie = Cookie.Load(folder, jar.Name, problems);
                if (cookie == null) continue;

                if (byId.TryGetValue(cookie.Id, out var winner))
                    shadowed.Add(new ShadowedCookie(cookie.Id, winner.JarName, jar.Name));
                else
                    byId[cookie.Id] = cookie;
            }
        }

        return new CookieCatalogue(ordered, byId, problems, shadowed);
    }

    /// <summary>The cookie with this id, or null.</summary>
    public Cookie? Find(string? id)
        => id != null && _byId.TryGetValue(id, out var cookie) ? cookie : null;

    /// <summary>The jar with this name, or null.</summary>
    public CookieJarSource? Jar(string? name)
        => Jars.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The jar a cookie came from.</summary>
    public CookieJarSource? JarOf(Cookie cookie) => Jar(cookie.JarName);

    /// <summary>
    /// Ranked search over id, name, tags and summary. An empty query matches everything, so the
    /// tag and engine filters alone are a useful call.
    /// </summary>
    public IReadOnlyList<CookieSearchHit> Search(
        string?                   query  = null,
        IReadOnlyList<string>?    tags   = null,
        string?                   engine = null,
        int                       limit  = 20)
    {
        string? needle = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var hits = new List<CookieSearchHit>();

        foreach (var cookie in All)
        {
            if (engine != null && !cookie.Manifest.SupportsEngine(engine)) continue;

            if (tags is { Count: > 0 } &&
                !tags.All(t => cookie.Manifest.Tags.Any(have => string.Equals(have, t, StringComparison.OrdinalIgnoreCase))))
                continue;

            int score = needle == null ? 1 : Score(cookie, needle);
            if (score > 0) hits.Add(new CookieSearchHit(cookie, score));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Cookie.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    private static int Score(Cookie cookie, string needle)
    {
        var m = cookie.Manifest;

        if (string.Equals(m.Id, needle, StringComparison.OrdinalIgnoreCase))            return 100;
        if (m.Id.StartsWith(needle, StringComparison.OrdinalIgnoreCase))                return 80;
        if (string.Equals(m.Name, needle, StringComparison.OrdinalIgnoreCase))          return 75;
        if (m.Tags.Any(t => string.Equals(t, needle, StringComparison.OrdinalIgnoreCase))) return 60;
        if (Contains(m.Name, needle))                                                   return 50;
        if (m.Provides.TypeNames.Any(t => Contains(t, needle)))                         return 45;
        if (Contains(m.Summary, needle))                                                return 30;
        if (m.Id.Contains(needle, StringComparison.OrdinalIgnoreCase))                  return 20;
        if (Contains(m.Description, needle))                                            return 10;
        return 0;
    }

    private static bool Contains(string? haystack, string needle)
        => haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
