using System.Text.Json.Nodes;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scene;
using static SexyBiscuit.Engine.Mcp.SceneToolSupport;

namespace SexyBiscuit.Engine.Mcp.Tools;

/// <summary>Whole-scene tools: inspect, create, load, save, search.</summary>
public sealed class SceneTools
{
    private readonly IMcpSceneHost   _host;
    private readonly SceneUndoStack? _undo;

    public SceneTools(IMcpSceneHost host, SceneUndoStack? undo = null)
    {
        _host = host;
        _undo = undo;
    }

    [McpTool("get_scene_summary",
        "Compact list of every actor in the open scene — id, name, class, layer, tag, component types, position — plus " +
        "layer info and checks (main camera, light, player start, game mode). Ids change after undo, redo or load, so " +
        "re-query rather than remembering them.",
        Label = "Read the scene summary")]
    public McpToolResult GetSceneSummary(
        [McpParam("Include each actor's component type list")] bool includeComponents = true)
    {
        var scene = RequireScene(_host);
        return McpToolResult.Json(SceneViews.SceneSummary(scene, _host, _undo, includeComponents));
    }

    [McpTool("get_scene_json",
        "Full dump of the open scene. format 'view' gives readable actor views with editable component properties " +
        "(rotations in degrees, colours as hex); 'file' gives the exact .scene JSON that save_scene would write.",
        Label = "Dump the scene")]
    public McpToolResult GetSceneJson(
        [McpParam("'view' or 'file'")] string format = "view",
        [McpParam("Only actors in this layer")] string? layer = null,
        [McpParam("Cap on the actors returned in view format")] int maxActors = 200)
    {
        var scene = RequireScene(_host);

        if (string.Equals(format, "file", StringComparison.OrdinalIgnoreCase))
            return McpToolResult.Text(SceneSerializer.Serialize(scene));

        if (!string.Equals(format, "view", StringComparison.OrdinalIgnoreCase))
            throw new McpToolException($"Unknown format '{format}'.", "Use 'view' or 'file'.");

        var layers   = new JsonArray();
        int emitted  = 0;
        bool truncated = false;

        foreach (var l in scene.Layers)
        {
            if (layer != null && !string.Equals(l.Name, layer, StringComparison.OrdinalIgnoreCase)) continue;

            var actors = new JsonArray();
            foreach (var actor in l.Actors)
            {
                if (emitted >= maxActors)
                {
                    truncated = true;
                    break;
                }
                actors.Add(SceneViews.ActorView(actor, _host));
                emitted++;
            }

            layers.Add(new JsonObject { ["name"] = l.Name, ["order"] = l.Order, ["actors"] = actors });
        }

        var result = new JsonObject
        {
            ["name"]      = scene.Name,
            ["path"]      = _host.CurrentScenePath,
            ["layers"]    = layers,
            ["truncated"] = truncated,
        };
        return McpToolResult.Json(result);
    }

    [McpTool("new_scene",
        "Replace the open scene with a fresh one. template 'default3d' gives a sky, two lights, a floor, a few shapes, a " +
        "player start and a game mode; 'default2d' a 2D camera; 'empty' just the default layers. Unsaved changes are " +
        "lost (undo can bring the previous scene back).",
        Mutating = true, Destructive = true, Label = "New scene '{name}'")]
    public McpToolResult NewScene(
        [McpParam("Scene name")] string name = "Untitled",
        [McpParam("'default3d', 'default2d' or 'empty'")] string template = "default3d")
    {
        RefuseWhilePlaying(_host, "replace the scene");

        Core.Scene scene = template.ToLowerInvariant() switch
        {
            "default3d" or "3d" => SceneTemplates.CreateDefault3D(name),
            "default2d" or "2d" => SceneTemplates.CreateDefault2D(name),
            "empty"             => new Core.Scene(name),
            _ => throw new McpToolException($"Unknown template '{template}'.", "Use 'default3d', 'default2d' or 'empty'."),
        };

        _host.AdoptScene(scene);
        scene.FlushPendingActors();
        _host.CurrentScenePath = null;
        _host.SceneDirty       = true;

        var summary = SceneViews.SceneSummary(scene, _host, _undo, includeComponents: true);
        summary["note"] = IdsRegeneratedNote;
        return McpToolResult.Json(summary, $"Created scene '{name}' from the {template} template.");
    }

    [McpTool("load_scene",
        "Load a .scene file — path relative to the project root, extension optional — and make it the open scene. " +
        "Refused during play mode. Unsaved changes are lost (undo can bring them back).",
        Mutating = true, Destructive = true, Label = "Load scene {path}")]
    public McpToolResult LoadScene([McpParam("e.g. 'Scenes/Level1.scene' or 'Scenes/Level1'")] string path)
    {
        RefuseWhilePlaying(_host, "load a scene");

        string? full = FindSceneFile(path);
        if (full == null)
            throw new McpToolException($"No scene file found for '{path}'.", "Call list_scenes to see what exists, or save_scene to create one.");

        Core.Scene loaded;
        IReadOnlyList<string> warnings;
        using (var capture = new ConsoleErrorCapture())
        {
            try
            {
                loaded = SceneSerializer.LoadFromFile(full);
            }
            catch (Exception ex)
            {
                throw new McpToolException($"Could not load '{path}': {ex.Message}");
            }
            warnings = capture.Lines;
        }

        _host.AdoptScene(loaded);
        loaded.FlushPendingActors();
        _host.CurrentScenePath = MakeProjectRelative(_host, full);
        _host.SceneDirty       = false;

        var summary = SceneViews.SceneSummary(loaded, _host, _undo, includeComponents: true);
        summary["note"] = IdsRegeneratedNote;
        var result = McpToolResult.Json(summary, $"Loaded {_host.CurrentScenePath}.");
        foreach (var warning in warnings) result.WithWarning(warning);
        return result;
    }

    [McpTool("save_scene",
        "Save the open scene to disk. Omit path to save where it was loaded from or last saved; otherwise give a " +
        "project-relative path such as 'Scenes/Level1.scene'. Refused during play mode; refuses paths outside the project.",
        Label = "Save scene {path}")]
    public McpToolResult SaveScene([McpParam("Project-relative path; defaults to the scene's current path")] string? path = null)
    {
        RefuseWhilePlaying(_host, "save the scene");
        var scene = RequireScene(_host);

        string? target = string.IsNullOrWhiteSpace(path) ? _host.CurrentScenePath : path;
        if (string.IsNullOrWhiteSpace(target))
            throw new McpToolException("This scene has never been saved, so a path is required.", "Pass e.g. 'Scenes/" + SafeFileName(scene.Name) + ".scene'.");

        if (!Path.HasExtension(target)) target += ".scene";

        string full = ResolveInsideProject(_host, target, "save a scene");
        scene.FlushPendingActors();
        SceneSerializer.SaveToFile(scene, full);

        _host.CurrentScenePath = MakeProjectRelative(_host, full);
        _host.SceneDirty       = false;

        return McpToolResult.Json(new JsonObject
        {
            ["path"]       = _host.CurrentScenePath,
            ["bytes"]      = new FileInfo(full).Length,
            ["actorCount"] = scene.Layers.Sum(l => l.Actors.Count),
        }, $"Saved {_host.CurrentScenePath}.");
    }

    [McpTool("find_actors",
        "Filter the open scene's actors by name substring, tag, component type, layer or class. Filters are optional and " +
        "combine with AND.")]
    public McpToolResult FindActors(
        [McpParam("Case-insensitive substring of the name")] string? nameContains = null,
        [McpParam("Exact tag")] string? tag = null,
        [McpParam("Component type the actor must have, e.g. 'MeshRenderer'")] string? componentType = null,
        [McpParam("Layer name")] string? layer = null,
        [McpParam("Actor class, e.g. 'GameMode'")] string? @class = null)
    {
        var scene = RequireScene(_host);
        var rows  = new JsonArray();

        foreach (var actor in scene.Layers.SelectMany(l => l.Actors))
        {
            if (nameContains != null && !actor.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;
            if (tag != null && !string.Equals(actor.Tag, tag, StringComparison.OrdinalIgnoreCase)) continue;
            if (layer != null && !string.Equals(actor.Layer_?.Name, layer, StringComparison.OrdinalIgnoreCase)) continue;
            if (@class != null && !string.Equals(SceneViews.ClassName(actor), @class, StringComparison.OrdinalIgnoreCase)) continue;
            if (componentType != null && TryFindComponent(actor, componentType) == null) continue;

            rows.Add(SceneViews.ActorRow(actor));
        }

        return McpToolResult.Json(rows, $"{rows.Count} actor(s) matched.");
    }

    [McpTool("get_actor",
        "Everything about one actor: transform (rotation in degrees), components with their editable properties, " +
        "bounds and selection state. actor is an id from get_scene_summary or an exact name.")]
    public McpToolResult GetActor([McpParam("Actor id or name")] string actor)
    {
        var scene = RequireScene(_host);
        return McpToolResult.Json(SceneViews.ActorView(ActorRef.Resolve(scene, actor), _host));
    }

    // -------------------------------------------------------------------------

    private string? FindSceneFile(string path)
    {
        string root = _host.ProjectRoot ?? ProjectPaths.EffectiveRoot;

        foreach (var candidate in new[] { path, path + ".scene", path + ".json" })
        {
            string full = Path.IsPathRooted(candidate) ? candidate : Path.Combine(root, candidate);
            if (File.Exists(full)) return Path.GetFullPath(full);

            string underScenes = Path.Combine(root, "Scenes", candidate);
            if (File.Exists(underScenes)) return Path.GetFullPath(underScenes);
        }

        return null;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string clean = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "Scene" : clean;
    }
}
