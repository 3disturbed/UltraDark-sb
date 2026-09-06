using System.Text;
using System.Text.Json.Nodes;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scene;
using SexyBiscuit.Engine.Scripting;

namespace SexyBiscuit.Engine.Mcp.Tools;

/// <summary>Documents Claude can read on demand, and the guidance sent with <c>initialize</c>.</summary>
public sealed class SceneResources
{
    private readonly IMcpSceneHost   _host;
    private readonly SceneUndoStack? _undo;
    private readonly McpToolRegistry? _registry;

    public SceneResources(IMcpSceneHost host, SceneUndoStack? undo = null, McpToolRegistry? registry = null)
    {
        _host     = host;
        _undo     = undo;
        _registry = registry;
    }

    /// <summary>What every client is told when it connects.</summary>
    public const string Instructions =
        "SexyBiscuit editor. Call get_context first; get_scene_summary lists actors one per line and get_actor inspects " +
        "one (ids change after undo, redo or load — re-query rather than remembering them); apply_scene_edits runs " +
        "several edits in one call. Units: 3D positions in " +
        "world units (metres), 2D in pixels; rotations in degrees [pitch, yaw, roll]; colours '#RRGGBB' or '#RRGGBBAA' " +
        "or a colour name. A renderable 3D scene needs a light or a Skybox, something with a MeshRenderer, and a Camera " +
        "tagged MainCamera3D (place_actor 'Camera'); Play also needs a Player Start and a Game Mode. Every change shows " +
        "in the editor immediately: use capture_viewport to look, undo to revert, save_scene to persist. Prefer " +
        "spawn_primitive for quick geometry, place_actor for lights, cameras and gameplay actors, set_material for " +
        "colours, and the C# tools (create_code_project, build_project, reload_game_code) for behaviour.";

    [McpResource("sexybiscuit://guide", "Workflow guide", "text/markdown", Description = "How to work in the SexyBiscuit editor through these tools.")]
    public string Guide() => "# SexyBiscuit MCP guide\n\n" + Instructions + "\n";

    [McpResource("sexybiscuit://scene/current", "Current scene (view)", "application/json", Description = "Readable actor views with editable properties.")]
    public string CurrentSceneView()
    {
        var scene = _host.ActiveScene;
        if (scene == null) return "{}";

        var layers = new JsonArray();
        foreach (var layer in scene.Layers)
        {
            var actors = new JsonArray();
            foreach (var actor in layer.Actors) actors.Add(SceneViews.ActorView(actor, _host));
            layers.Add(new JsonObject { ["name"] = layer.Name, ["order"] = layer.Order, ["actors"] = actors });
        }

        return McpJson.ToText(new JsonObject { ["name"] = scene.Name, ["path"] = _host.CurrentScenePath, ["layers"] = layers });
    }

    [McpResource("sexybiscuit://scene/current.scene", "Current scene (file format)", "application/json", Description = "The exact JSON save_scene writes.")]
    public string CurrentSceneFile()
        => _host.ActiveScene is { } scene ? SceneSerializer.Serialize(scene) : "{}";

    [McpResource("sexybiscuit://components", "Component catalogue", "text/markdown", Description = "Every component type with its editable properties.")]
    public string ComponentCatalogue()
    {
        var sb = new StringBuilder("# Components\n\n");

        foreach (var group in ReflectionUtil.FindComponentTypes().GroupBy(ComponentReflection.Category).OrderBy(g => g.Key))
        {
            sb.Append("## ").Append(group.Key).AppendLine().AppendLine();
            foreach (var type in group.OrderBy(t => t.Name))
            {
                sb.Append("### ").Append(type.Name);
                if (ComponentReflection.Source(type) == "project") sb.Append(" (project)");
                sb.AppendLine();

                string? summary = XmlDocs.Summary(type);
                if (summary != null) sb.AppendLine().AppendLine(summary);

                var requires = ComponentReflection.RequiredComponents(type).Select(r => r.Name).ToList();
                if (requires.Count > 0) sb.AppendLine().Append("Requires: ").AppendLine(string.Join(", ", requires));

                var properties = ComponentReflection.EditableProperties(type).ToList();
                if (properties.Count > 0)
                {
                    sb.AppendLine().AppendLine("| Property | Type | Notes |").AppendLine("|---|---|---|");
                    foreach (var p in properties)
                        sb.Append("| ").Append(p.Name).Append(" | ").Append(ValueConverter.Describe(p.PropertyType))
                          .Append(" | ").Append(XmlDocs.Summary(p) ?? "").AppendLine(" |");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    [McpResource("sexybiscuit://actor-presets", "Actor presets", "application/json", Description = "The Place Actors palette.")]
    public string Presets()
    {
        var list = new JsonArray();
        foreach (var preset in ActorPresets.All)
            list.Add(new JsonObject { ["name"] = preset.Name, ["category"] = preset.Category, ["description"] = preset.Description });
        return McpJson.ToText(list);
    }

    [McpResource("sexybiscuit://scripting/api.d.ts", "JavaScript scripting API", "text/plain", Description = "TypeScript definitions for the Jint script bridge (2D scripting).")]
    public string ScriptingApi() => TypeScriptDefinitions.Generate();

    [McpResource("sexybiscuit://tools", "Tool catalogue", "text/markdown", Description = "Every registered tool with its parameters.")]
    public string Tools() => _registry?.DescribeMarkdown() ?? "";

    [McpPrompt("build_level", "Build a level in the open scene from a short brief.")]
    public string BuildLevel([McpParam("What the level should contain and feel like")] string brief)
        => $"Build this level in the open SexyBiscuit scene: {brief}\n\n" +
           "Start with get_context, then place actors with apply_scene_edits (spawn_primitive, place_actor and " +
           "set_material, check your work with capture_viewport, and save with save_scene when it looks right. " +
           "Tell me what you built and what you would add next.";
}
