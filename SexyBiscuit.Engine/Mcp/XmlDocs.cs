using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>
/// One-line summaries from the XML documentation files that ship next to the assemblies
/// (<c>GenerateDocumentationFile</c> is on for the engine and for generated game projects), so
/// tool catalogues and type descriptions carry the same words as IntelliSense.
/// </summary>
public static class XmlDocs
{
    private static readonly ConcurrentDictionary<Assembly, Lazy<Dictionary<string, string>>> _index = new();
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Tags       = new(@"<see\s+cref=""[A-Z]:([^""]+)""\s*/>|<see\s+cref=""([^""]+)""\s*/>|<paramref\s+name=""([^""]+)""\s*/>|<[^>]+>", RegexOptions.Compiled);

    public static string? Summary(Type type)
        => type.FullName == null ? null : Lookup(type.Assembly, "T:" + type.FullName);

    public static string? Summary(PropertyInfo property)
    {
        var owner = property.DeclaringType;
        if (owner?.FullName == null) return null;
        return Lookup(owner.Assembly, $"P:{owner.FullName}.{property.Name}");
    }

    /// <summary>Drops the cached index for an assembly, e.g. when game code is unloaded.</summary>
    public static void Forget(Assembly assembly) => _index.TryRemove(assembly, out _);

    private static string? Lookup(Assembly assembly, string key)
    {
        var index = _index.GetOrAdd(assembly, a => new Lazy<Dictionary<string, string>>(() => Load(a))).Value;
        return index.GetValueOrDefault(key);
    }

    private static Dictionary<string, string> Load(Assembly assembly)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        string? location = assembly.IsDynamic ? null : assembly.Location;
        if (string.IsNullOrEmpty(location)) return result;

        string xmlPath = Path.ChangeExtension(location, ".xml");
        if (!File.Exists(xmlPath)) return result;

        try
        {
            var doc = XDocument.Load(xmlPath);
            foreach (var member in doc.Descendants("member"))
            {
                string? name = member.Attribute("name")?.Value;
                var summary  = member.Element("summary");
                if (name == null || summary == null) continue;

                string text = Clean(string.Concat(summary.Nodes().Select(n => n.ToString())));
                if (text.Length > 0) result[name] = text;
            }
        }
        catch (Exception)
        {
            // A malformed doc file costs nothing but descriptions.
        }

        return result;
    }

    private static string Clean(string raw)
    {
        // <see cref="T:Foo.Bar"/> → Bar; other tags dropped; whitespace collapsed.
        string text = Tags.Replace(raw, m =>
        {
            string cref = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (string.IsNullOrEmpty(cref)) return " ";
            int dot = cref.LastIndexOf('.');
            return dot >= 0 ? cref[(dot + 1)..] : cref;
        });

        text = System.Net.WebUtility.HtmlDecode(text);
        return Whitespace.Replace(text, " ").Trim();
    }
}
