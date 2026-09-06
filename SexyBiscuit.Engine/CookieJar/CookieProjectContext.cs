using SexyBiscuit.Engine.Code;

namespace SexyBiscuit.Engine.CookieJar;

/// <summary>
/// The little a cookie install needs to know about the project it is going into. A record rather
/// than a reference to the editor's project types, so the planner stays testable and the engine
/// keeps knowing nothing about the editor.
/// </summary>
public sealed record CookieProjectContext(
    string Root,
    string RootNamespace,
    string SourceDirectory = "Source",
    string ScriptDirectory = "Scripts",
    string SceneDirectory  = "Scenes",
    string AssetDirectory  = "Assets",
    string ConfigDirectory = "Config",
    bool   HasCodeProject  = false,
    string EngineVersion   = "1.0.0")
{
    /// <summary>Probes <paramref name="root"/> for a C# project and takes the conventional folders.</summary>
    public static CookieProjectContext ForRoot(string root)
    {
        string full    = Path.GetFullPath(root);
        var    project = CodeProject.Find(full);

        return new CookieProjectContext(
            Root:           full,
            RootNamespace:  project?.RootNamespace ?? CodeProjectGenerator.SanitiseIdentifier(Path.GetFileName(full)),
            HasCodeProject: project != null);
    }

    /// <summary>Where a cookie's files land by default, keyed by the folder they came from.</summary>
    /// <remarks>
    /// C# is namespaced by the cookie's Pascal id so a folder listing matches the namespace, while
    /// everything else keeps the kebab id, which is what a scene's asset path will read as.
    /// </remarks>
    public IReadOnlyDictionary<string, string> DefaultMap(CookieManifest manifest) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Source"]  = $"{SourceDirectory}/Cookies/{manifest.PascalId}",
            ["Scripts"] = $"{ScriptDirectory}/Cookies/{manifest.Id}",
            ["Scenes"]  = $"{SceneDirectory}/Cookies/{manifest.Id}",
            ["Assets"]  = $"{AssetDirectory}/Cookies/{manifest.Id}",
            ["Config"]  = $"{ConfigDirectory}/Cookies/{manifest.Id}",
        };
}
