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
        "The open scene. format 'compact' (default): a header (layers, dirty flag, checks for camera, light, player " +
        "start, game mode) then one text line per actor \u2014 id, name, class, layer, tag, components, position. 'json': " +
        "the same as JSON. 'view': every actor's editable component properties (rotations in degrees, colours as hex). " +
        "'file': the exact .scene JSON save_scene would write. Page with offset and limit. Ids change after undo, redo " +
        "or load.",
        Label = "Read the scene summary", ReadOnly = true)]
    public McpToolResult GetSceneSummary(
        [McpParam("'compact', 'json', 'view' or 'file'")] string format = "compact",
        [McpParam("Only actors in this layer")] string? layer = null,
        [McpParam("Skip this many actors")] int offset = 0,
        [McpParam("Actors per page")] int limit = 100,
        [McpParam("Include each actor's component type list (json format)")] bool includeComponents = true)
    {
        var scene = RequireScene(_host);

        switch (format.ToLowerInvariant())
        {
            case "compact":
                return McpToolResult.Text(SceneViews.SceneSummaryText(scene, _host, _undo, layer, offset, limit));

            case "file":
                return McpToolResult.Text(JsonNode.Parse(SceneSerializer.Serialize(scene))!.ToJsonString());

            case "json":
            {
                var summary = SceneViews.SceneSummary(scene, _host, _undo, includeComponents);
                var actors  = summary["actors"]!.AsArray();
                var kept    = actors.Where(a => layer == null || string.Equals(a?["layer"]?.GetValue<string>(), layer, StringComparison.OrdinalIgnoreCase)).ToList();
                var page    = kept.Skip(Math.Max(0, offset)).Take(Math.Max(1, limit)).ToList();

                foreach (var node in page) actors.Remove(node!);
                summary["actors"] = new JsonArray(page.Select(n => (JsonNode)n!.DeepClone()).ToArray());
                if (kept.Count > offset + limit) summary["nextOffset"] = offset + limit;
                return McpToolResult.Json(summary);
            }

            case "view":
                return SceneView(scene, layer, offset, limit);

            default:
                throw new McpToolException($"Unknown format '{format}'.", "Use 'compact', 'json', 'view' or 'file'.");
        }
    }

    /// <summary>Readable actor views, layer by layer, capped so a big scene cannot flood a turn.</summary>
    private McpToolResult SceneView(Core.Scene scene, string? layer, int offset, int limit)
    {
        var layers     = new JsonArray();
        int seen       = 0;
        int emitted    = 0;
        bool truncated = false;

        foreach (var l in scene.Layers)
        {
            if (layer != null && !string.Equals(l.Name, layer, StringComparison.OrdinalIgnoreCase)) continue;

            var actors = new JsonArray();
            foreach (var actor in l.Actors)
            {
                if (seen++ < offset) continue;
                if (emitted >= limit)
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
        if (truncated) result["nextOffset"] = offset + emitted;
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

        return McpToolResult.Text($"Created scene '{name}' from the {template} template. {IdsRegeneratedNote}\n"
                                  + SceneViews.SceneSummaryText(scene, _host, _undo, null, 0, 100));
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
            throw new McpToolException($"No scene file found for '{path}'.", "Call list_assets to see what exists, or save_scene to create one.");

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

        var result = McpToolResult.Text($"Loaded {_host.CurrentScenePath}. {IdsRegeneratedNote}\n"
                                        + SceneViews.SceneSummaryText(loaded, _host, _undo, null, 0, 100));
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
        "combine with AND.", ReadOnly = true)]
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
        "One actor, by id or exact name. detail 'full' (default): transform in degrees, every component with its " +
        "editable properties, bounds, selection; 'row': id, name, class, layer, tag, component types, position. " +
        "property reads a single value instead, keyed 'Type.Property' such as 'Light3D.Intensity' or 'Actor.Tag'.", ReadOnly = true)]
    public McpToolResult GetActor(
        [McpParam("Actor id or name")] string actor,
        [McpParam("'full' or 'row'")] string detail = "full",
        [McpParam("Read one 'ComponentType.Property' (or 'Actor.Property')")] string? property = null)
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);

        if (property != null) return ReadProperty(target, property);

        return detail.ToLowerInvariant() switch
        {
            "row"  => McpToolResult.Json(SceneViews.ActorRow(target)),
            "full" => McpToolResult.Json(SceneViews.ActorView(target, _host)),
            _      => throw new McpToolException($"Unknown detail '{detail}'.", "Use 'full' or 'row'."),
        };
    }

    /// <summary>One editable property of a component, or of the actor itself with the 'Actor.' prefix.</summary>
    private static McpToolResult ReadProperty(Actor target, string key)
    {
        int dot = key.LastIndexOf('.');
        if (dot <= 0)
            throw new McpToolException($"'{key}': use a 'ComponentType.Property' key, e.g. 'Light3D.Intensity'.");

        string owner        = key[..dot];
        string propertyName = key[(dot + 1)..];

        object target_ = string.Equals(owner, "Actor", StringComparison.OrdinalIgnoreCase) ? target : FindComponent(target, owner);
        var type = target_.GetType();

        var info = ComponentReflection.FindProperty(type, propertyName)
            ?? throw new McpToolException($"'{type.Name}' has no editable property '{propertyName}'.",
                ComponentReflection.Suggest(propertyName, ComponentReflection.EditableProperties(type).Select(p => p.Name)));

        return McpToolResult.Json(new JsonObject
        {
            ["property"] = $"{type.Name}.{info.Name}",
            ["type"]     = ValueConverter.Describe(info.PropertyType),
            ["value"]    = ValueConverter.ToJson(info.GetValue(target_)),
        });
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
