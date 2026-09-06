using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.CookieJar;

/// <summary>
/// Repoints the asset references inside a cookie's scenes and prefabs when it is installed. A
/// cookie refers to its own files as <c>Assets/brick.png</c>; once installed that file lives at
/// <c>Assets/Cookies/double-jump/brick.png</c>, and every reference has to follow it.
/// </summary>
/// <remarks>
/// The rewrite walks the JSON rather than reflecting over component types, for three reasons: it
/// needs no graphics device, it works for a component type the project has not compiled yet, and
/// it reaches the material map paths nested inside a mesh renderer's <c>Materials</c> collection,
/// which a scan of top-level component properties misses. Only values the map already contains are
/// touched, so a false positive is not possible.
/// </remarks>
public static class CookiePathRewriter
{
    /// <summary>The property-name suffix that marks an asset reference, engine-wide.</summary>
    public const string PathSuffix = "Path";

    /// <summary>
    /// Rewrites in place and returns how many references changed. Anything that looks like an
    /// asset reference but is not in the map is added to <paramref name="unresolved"/>.
    /// </summary>
    public static int Rewrite(JsonNode? root, IReadOnlyDictionary<string, string> map, ICollection<string>? unresolved = null)
    {
        int rewritten = 0;
        if (root == null) return 0;

        var queue = new Queue<JsonNode>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            switch (queue.Dequeue())
            {
                case JsonObject obj:
                    // Materialise first: assigning to a property while enumerating invalidates it.
                    foreach (var (name, value) in obj.ToList())
                    {
                        if (value is JsonValue leaf && IsAssetReference(name) && leaf.TryGetValue(out string? text))
                        {
                            string? replacement = Lookup(map, text);
                            if (replacement != null)
                            {
                                obj[name] = replacement;
                                rewritten++;
                            }
                            else if (unresolved != null && LooksLikeAPath(text))
                            {
                                unresolved.Add(text!);
                            }
                        }
                        else if (value is JsonObject or JsonArray)
                        {
                            queue.Enqueue(value!);
                        }
                    }
                    break;

                case JsonArray array:
                    foreach (var item in array)
                        if (item is JsonObject or JsonArray) queue.Enqueue(item!);
                    break;
            }
        }

        return rewritten;
    }

    /// <summary>Rewrites a whole JSON document and returns the new text.</summary>
    public static string RewriteText(string json, IReadOnlyDictionary<string, string> map, ICollection<string>? unresolved = null)
    {
        var root = JsonNode.Parse(json) ?? throw new CookieException("the file is not valid JSON.");
        Rewrite(root, map, unresolved);
        return root.ToJsonString(CookieManifest.Json);
    }

    /// <summary>Turns an install map into the bake map: project-relative back to cookie-relative.</summary>
    public static IReadOnlyDictionary<string, string> Invert(IReadOnlyDictionary<string, string> map)
    {
        var inverted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in map) inverted[to] = from;
        return inverted;
    }

    /// <summary>True when a property name marks an asset reference, such as <c>AlbedoMapPath</c>.</summary>
    public static bool IsAssetReference(string propertyName)
        => propertyName.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase) && propertyName.Length > PathSuffix.Length;

    /// <summary>Compares the way the map is keyed: forward slashes, no leading <c>./</c>, case-insensitive.</summary>
    public static string Normalise(string path)
    {
        string text = path.Replace('\\', '/').Trim();
        while (text.StartsWith("./", StringComparison.Ordinal)) text = text[2..];
        return text;
    }

    private static string? Lookup(IReadOnlyDictionary<string, string> map, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return map.TryGetValue(Normalise(value), out string? replacement) ? replacement : null;
    }

    private static bool LooksLikeAPath(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Contains('.') && !value.Contains(' ');
}
