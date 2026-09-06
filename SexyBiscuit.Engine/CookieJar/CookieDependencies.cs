namespace SexyBiscuit.Engine.CookieJar;

/// <summary>What a set of requested cookies expands to, once their requirements are followed.</summary>
public sealed record DependencyResolution(
    IReadOnlyList<Cookie>               Ordered,
    IReadOnlyList<string>               Missing,
    IReadOnlyList<IReadOnlyList<string>> Cycles,
    IReadOnlyList<string>               AlreadyInstalled);

/// <summary>Expands cookie requirements into an install order.</summary>
public static class CookieDependencies
{
    /// <summary>
    /// Every cookie needed to install <paramref name="ids"/>, dependencies before dependents.
    /// </summary>
    /// <remarks>
    /// The walk is iterative rather than recursive. A manifest is data from a jar the user may have
    /// only just added, and a chain a few thousand deep should be a reported cycle, not a crashed
    /// editor.
    /// </remarks>
    public static DependencyResolution Resolve(
        CookieCatalogue           catalogue,
        IEnumerable<string>       ids,
        CookieLockFile?           installed          = null,
        bool                      includeDependencies = true)
    {
        var ordered          = new List<Cookie>();
        var visited          = new HashSet<string>(StringComparer.Ordinal);   // fully expanded
        var missing          = new List<string>();
        var cycles           = new List<IReadOnlyList<string>>();
        var alreadyInstalled = new List<string>();

        foreach (string root in ids.Distinct(StringComparer.Ordinal))
        {
            if (visited.Contains(root)) continue;

            // Depth-first, keeping the path so a cycle can name itself.
            var stack = new Stack<(string Id, bool Expanded)>();
            var path  = new List<string>();
            var onPath = new HashSet<string>(StringComparer.Ordinal);
            stack.Push((root, false));

            while (stack.Count > 0)
            {
                var (id, expanded) = stack.Pop();

                if (expanded)
                {
                    // Children are done; this cookie can be placed.
                    onPath.Remove(id);
                    path.RemoveAt(path.Count - 1);

                    if (visited.Add(id) && catalogue.Find(id) is { } resolved)
                    {
                        ordered.Add(resolved);
                        if (installed?.Find(id) != null) alreadyInstalled.Add(id);
                    }
                    continue;
                }

                if (visited.Contains(id)) continue;

                if (onPath.Contains(id))
                {
                    int start = path.IndexOf(id);
                    cycles.Add(path.Skip(start).Append(id).ToList());
                    continue;
                }

                var cookie = catalogue.Find(id);
                if (cookie == null)
                {
                    missing.Add(id);
                    visited.Add(id);
                    continue;
                }

                onPath.Add(id);
                path.Add(id);
                stack.Push((id, true));

                if (!includeDependencies) continue;

                // Reversed, so the manifest's order survives the stack.
                foreach (string required in Enumerable.Reverse(cookie.Manifest.Requires))
                    if (!visited.Contains(required))
                        stack.Push((required, false));
            }
        }

        return new DependencyResolution(ordered, missing.Distinct(StringComparer.Ordinal).ToList(),
                                        cycles, alreadyInstalled);
    }
}
