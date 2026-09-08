using System.Text.Json;

namespace SexyBiscuit.Editor;

public class ProjectFile
{
    public string ProjectName { get; set; } = "Untitled";
    public string EngineVersion { get; set; } = SexyBiscuit.Engine.EngineInfo.Version;
    public string DefaultScene { get; set; } = "";
    public List<string> AssetDirectories { get; set; } = new() { "Assets" };
    public List<string> ScriptDirectories { get; set; } = new() { "Scripts" };
    public string BuildConfigPath { get; set; } = "BuildSettings.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ProjectFile Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ProjectFile>(json, JsonOptions) ?? new ProjectFile();
    }

    public static void Save(ProjectFile project, string path)
    {
        var json = JsonSerializer.Serialize(project, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static string CreateNew(string name, string directory, string? templatePath = null)
    {
        // Create directory structure
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "Assets"));
        Directory.CreateDirectory(Path.Combine(directory, "Assets", "Sprites"));
        Directory.CreateDirectory(Path.Combine(directory, "Assets", "Audio"));
        Directory.CreateDirectory(Path.Combine(directory, "Scripts"));
        Directory.CreateDirectory(Path.Combine(directory, "Scenes"));

        // Copy template if provided
        if (templatePath != null && Directory.Exists(templatePath))
        {
            CopyDirectory(templatePath, directory);
        }

        // Create project file — pick up DefaultScene from ProjectSettings.json if present
        var project = new ProjectFile { ProjectName = name };

        var settingsFile = Path.Combine(directory, "ProjectSettings.json");
        if (File.Exists(settingsFile))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsFile));
                if (doc.RootElement.TryGetProperty("StartScene", out var ss))
                    project.DefaultScene = ss.GetString() ?? "";
            }
            catch { }
        }

        var projectPath = Path.Combine(directory, name + ".sbproject");
        Save(project, projectPath);

        return projectPath;
    }

    private static void CopyDirectory(string source, string dest)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, file);
            var destPath = Path.Combine(dest, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            if (!File.Exists(destPath)) // Don't overwrite .sbproject
                File.Copy(file, destPath, false);
        }
    }
}
