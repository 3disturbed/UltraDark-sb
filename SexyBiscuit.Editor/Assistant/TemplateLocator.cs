using System.Text.Json;

namespace SexyBiscuit.Editor.Assistant;

/// <summary>One project template: the folder plus what its template.json says about it.</summary>
public sealed record ProjectTemplate(string Name, string Description, string Category, string Path);

/// <summary>
/// Finds the repository's <c>Templates/</c> folder the way the Project Manager does: next to the
/// executable, in the working directory, in the open project, or up to six levels above the
/// editor binary (which lives four deep in <c>bin/</c>).
/// </summary>
public static class TemplateLocator
{
    public static string? FindRoot()
    {
        var candidates = new List<string>
        {
            AppDomain.CurrentDomain.BaseDirectory,
            Directory.GetCurrentDirectory(),
            EditorState.ProjectPath,
        };

        string walk = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var parent = Path.GetDirectoryName(walk);
            if (parent == null || parent == walk) break;
            walk = parent;
            candidates.Add(walk);
        }

        foreach (var candidate in candidates)
        {
            string templates = Path.Combine(candidate, "Templates");
            if (Directory.Exists(templates)) return templates;
        }

        return null;
    }

    public static IReadOnlyList<ProjectTemplate> List()
    {
        string? root = FindRoot();
        if (root == null) return Array.Empty<ProjectTemplate>();

        var result = new List<ProjectTemplate>();
        foreach (var dir in Directory.GetDirectories(root).OrderBy(Path.GetFileName))
        {
            string json = Path.Combine(dir, "template.json");
            if (!File.Exists(json)) continue;

            string name = Path.GetFileName(dir), description = "", category = "";
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString() ?? name;
                if (doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) description = d.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) category = c.GetString() ?? "";
            }
            catch (JsonException)
            {
                // A broken template.json still names a usable folder.
            }

            result.Add(new ProjectTemplate(name, description, category, dir));
        }

        return result;
    }

    public static ProjectTemplate? Find(string name)
        => List().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(Path.GetFileName(t.Path), name, StringComparison.OrdinalIgnoreCase));
}
