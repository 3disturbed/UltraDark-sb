using System.Reflection;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scene;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>
/// The one rule for "a property a person may edit": public getter and setter, a value type
/// the converter understands, not the plumbing every component shares. Views, <c>set_properties</c>
/// and <c>describe_components</c> all use it, so what Claude can change is exactly what the
/// Details panel can.
/// </summary>
public static class ComponentReflection
{
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal) { "Actor", "Enabled" };

    public static IEnumerable<PropertyInfo> EditableProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
               .Where(p => p.CanRead && p.CanWrite
                        && p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true
                        && p.GetIndexParameters().Length == 0
                        && !Skipped.Contains(p.Name)
                        && ValueConverter.IsSupportedType(p.PropertyType))
               .OrderBy(p => p.Name, StringComparer.Ordinal);

    /// <summary>Finds an editable property by name, exactly first and then ignoring case.</summary>
    public static PropertyInfo? FindProperty(Type type, string name)
    {
        var candidates = EditableProperties(type).ToList();
        return candidates.FirstOrDefault(p => p.Name == name)
            ?? candidates.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the property is written to scene files (not <c>[SceneIgnore]</c>, public setter).</summary>
    public static bool IsSerialised(PropertyInfo property)
        => SceneSerializer.GetSerializableProperties(property.DeclaringType ?? property.ReflectedType!)
                          .Any(p => p.Name == property.Name);

    /// <summary>The component's area — Rendering, Physics, Gameplay… — or "Project" for game code.</summary>
    public static string Category(Type type)
    {
        if (type.Assembly != typeof(Component).Assembly) return "Project";

        string ns = type.Namespace ?? "";
        int dot = ns.LastIndexOf('.');
        return dot >= 0 ? ns[(dot + 1)..] : ns;
    }

    /// <summary><c>engine</c> for the engine assembly, <c>project</c> for anything else.</summary>
    public static string Source(Type type) => type.Assembly == typeof(Component).Assembly ? "engine" : "project";

    public static IEnumerable<Type> RequiredComponents(Type type)
        => type.GetCustomAttributes(typeof(RequireComponentAttribute), true)
               .Cast<RequireComponentAttribute>()
               .Select(a => a.RequiredType);

    /// <summary>
    /// A "did you mean" hint: candidates that contain the wanted text or are within a couple
    /// of edits of it, ignoring case.
    /// </summary>
    public static string Suggest(string wanted, IEnumerable<string> candidates, int max = 5)
    {
        var scored = candidates
            .Distinct()
            .Select(c => (name: c, score: Score(wanted, c)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.name, StringComparer.Ordinal)
            .Take(max)
            .Select(x => x.name)
            .ToList();

        return scored.Count == 0 ? "" : $"Did you mean {string.Join(", ", scored)}?";
    }

    private static int Score(string wanted, string candidate)
    {
        if (string.Equals(wanted, candidate, StringComparison.OrdinalIgnoreCase)) return 100;
        if (candidate.Contains(wanted, StringComparison.OrdinalIgnoreCase)) return 60 - Math.Abs(candidate.Length - wanted.Length);
        if (wanted.Contains(candidate, StringComparison.OrdinalIgnoreCase)) return 40;

        int distance = Levenshtein(wanted.ToLowerInvariant(), candidate.ToLowerInvariant());
        return distance <= Math.Max(2, wanted.Length / 4) ? 30 - distance : 0;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        for (int j = 1; j <= b.Length; j++)
        {
            int cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
        }

        return d[a.Length, b.Length];
    }
}
