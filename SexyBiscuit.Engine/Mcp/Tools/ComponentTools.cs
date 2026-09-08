using System.Text.Json;
using System.Text.Json.Nodes;
using SexyBiscuit.Engine.Core;
using static SexyBiscuit.Engine.Mcp.SceneToolSupport;

namespace SexyBiscuit.Engine.Mcp.Tools;

/// <summary>Adding, removing, describing and editing components.</summary>
public sealed class ComponentTools
{
    private readonly IMcpSceneHost _host;

    public ComponentTools(IMcpSceneHost host) => _host = host;

    [McpTool("add_component",
        "Add a component to an actor by type name (short names such as 'Light3D' work). Companion components the type " +
        "requires are added automatically and reported. properties sets initial values by property name.",
        Mutating = true, Label = "Add {componentType} to {actor}")]
    public McpToolResult AddComponent(
        [McpParam("Actor id or name")] string actor,
        [McpParam("Component type name")] string componentType,
        [McpParam("Initial property values, e.g. {\"Intensity\": 2, \"Color\": \"#FFEEDD\"}")] JsonElement? properties = null)
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);
        var type   = ResolveComponentTypeOrThrow(componentType);

        var before = target.GetAllComponents().ToList();
        var added  = target.AddComponent(type);

        var alsoAdded = new JsonArray();
        foreach (var c in target.GetAllComponents())
            if (!before.Contains(c) && !ReferenceEquals(c, added)) alsoAdded.Add(c.GetType().Name);

        var applied = new List<string>();
        var failed  = new List<string>();
        ApplyPropertyBag(target, properties, added, applied, failed);

        var view = SceneViews.ComponentView(added);
        view["actor"]     = new JsonObject { ["id"] = target.Id, ["name"] = target.Name };
        view["alsoAdded"] = alsoAdded;

        var result = McpToolResult.Json(view, $"Added {added.GetType().Name} to '{target.Name}'.");
        foreach (var f in failed) result.WithWarning(f);
        return result;
    }

    [McpTool("remove_component",
        "Remove the first component of a type from an actor. The 2D Transform cannot be removed; remove a Transform3D " +
        "only if nothing else on the actor needs it.",
        Mutating = true, Label = "Remove {componentType} from {actor}")]
    public McpToolResult RemoveComponent(
        [McpParam("Actor id or name")] string actor,
        [McpParam("Component type name")] string componentType)
    {
        var scene     = RequireScene(_host);
        var target    = ActorRef.Resolve(scene, actor);
        var component = FindComponent(target, componentType);

        if (component is Transform)
            throw new McpToolException("Every actor has exactly one 2D Transform; it cannot be removed.");

        var dependants = target.GetAllComponents()
            .Where(c => !ReferenceEquals(c, component)
                     && ComponentReflection.RequiredComponents(c.GetType()).Any(r => r.IsInstanceOfType(component)))
            .Select(c => c.GetType().Name)
            .ToList();

        if (!target.RemoveComponent(component))
            throw new McpToolException($"Could not remove {SceneViews.ComponentTypeName(component)} from '{target.Name}'.");

        var result = McpToolResult.Json(new JsonObject
        {
            ["removed"]   = SceneViews.ComponentTypeName(component),
            ["remaining"] = new JsonArray(target.GetAllComponents().Where(c => c is not Transform).Select(c => (JsonNode)SceneViews.ComponentTypeName(c)).ToArray()),
        }, $"Removed {SceneViews.ComponentTypeName(component)} from '{target.Name}'.");

        if (dependants.Count > 0)
            result.WithWarning($"{string.Join(", ", dependants)} on this actor require the removed component and may misbehave.");

        return result;
    }

    [McpTool("describe_components",
        "The component types that can be added. Without type: names grouped by category (Rendering, Physics, Gameplay, " +
        "Audio, Animation, AI, UI, Scripting\u2026), or with namesOnly=false a one-line description, required companions and " +
        "engine/project source for each. With type: that one type's editable properties \u2014 name, type, enum values, " +
        "default, documentation, and whether it is saved in the scene file.", ReadOnly = true)]
    public McpToolResult DescribeComponents(
        [McpParam("One component type to describe in full")] string? type = null,
        [McpParam("Only this category")] string? category = null,
        [McpParam("Case-insensitive substring of the type name")] string? search = null,
        [McpParam("Names grouped by category (default) or full JSON")] bool namesOnly = true)
        => type != null ? DescribeOne(type) : ListTypes(category, search, namesOnly);

    private static McpToolResult ListTypes(string? category, string? search, bool namesOnly)
    {
        var types = ReflectionUtil.FindComponentTypes()
            .Where(t => category == null || string.Equals(ComponentReflection.Category(t), category, StringComparison.OrdinalIgnoreCase))
            .Where(t => search == null || t.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => ComponentReflection.Category(t)).ThenBy(t => t.Name)
            .ToList();

        if (namesOnly)
        {
            var lines = types.GroupBy(ComponentReflection.Category)
                             .Select(g => $"{g.Key}: {string.Join(", ", g.Select(t => ComponentReflection.Source(t) == "project" ? t.Name + "*" : t.Name))}");
            return McpToolResult.Text($"{types.Count} component type(s); * = from the project. Pass type for one type's properties.\n"
                                      + string.Join('\n', lines));
        }

        var list = new JsonArray();
        foreach (var t in types)
        {
            string summary = XmlDocs.Summary(t) ?? string.Empty;
            list.Add(new JsonObject
            {
                ["type"]     = t.Name,
                ["category"] = ComponentReflection.Category(t),
                ["source"]   = ComponentReflection.Source(t),
                ["summary"]  = summary.Length > 100 ? summary[..99] + "\u2026" : summary,
                ["requires"] = new JsonArray(ComponentReflection.RequiredComponents(t).Select(r => (JsonNode)r.Name).ToArray()),
            });
        }

        return McpToolResult.Json(list, $"{list.Count} component type(s).");
    }

    private static McpToolResult DescribeOne(string componentType)
    {
        var type = ResolveComponentTypeOrThrow(componentType);

        object? sample = null;
        try
        {
            sample = Activator.CreateInstance(type);
        }
        catch (Exception)
        {
            // Defaults are then omitted; everything else still describes the type.
        }

        var properties = new JsonArray();
        foreach (var property in ComponentReflection.EditableProperties(type))
        {
            var entry = new JsonObject
            {
                ["name"]       = property.Name,
                ["type"]       = ValueConverter.Describe(property.PropertyType),
                ["summary"]    = XmlDocs.Summary(property),
                ["serialised"] = ComponentReflection.IsSerialised(property),
            };

            var enumType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (enumType.IsEnum)
                entry["enumValues"] = new JsonArray(Enum.GetNames(enumType).Select(n => (JsonNode)n).ToArray());

            if (sample != null)
            {
                try
                {
                    entry["default"] = ValueConverter.ToJson(property.GetValue(sample));
                }
                catch (Exception)
                {
                }
            }

            properties.Add(entry);
        }

        if (sample is Component disposable) disposable.OnDestroy();

        return McpToolResult.Json(new JsonObject
        {
            ["type"]       = type.Name,
            ["fullName"]   = type.FullName,
            ["category"]   = ComponentReflection.Category(type),
            ["source"]     = ComponentReflection.Source(type),
            ["summary"]    = XmlDocs.Summary(type),
            ["requires"]   = new JsonArray(ComponentReflection.RequiredComponents(type).Select(r => (JsonNode)r.Name).ToArray()),
            ["properties"] = properties,
        });
    }

    [McpTool("set_properties",
        "Set several properties on one actor at once. Keys are 'ComponentType.Property' (or 'Actor.Property'), e.g. " +
        "{\"Light3D.Intensity\": 2, \"Transform3D.Position\": [0, 3, 0]}. Valid entries are applied even if others fail.",
        Mutating = true, Label = "Edit {actor}")]
    public McpToolResult SetProperties(
        [McpParam("Actor id or name")] string actor,
        [McpParam("Object of 'Type.Property' keys to values")] JsonElement properties)
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);

        if (properties.ValueKind != JsonValueKind.Object)
            throw new McpToolException("properties must be a JSON object of 'Type.Property' keys.");

        var applied = new List<string>();
        var failed  = new List<string>();
        ApplyPropertyBag(target, properties, null, applied, failed);

        var result = McpToolResult.Json(new JsonObject
        {
            ["applied"] = new JsonArray(applied.Select(a => (JsonNode)a).ToArray()),
            ["failed"]  = new JsonArray(failed.Select(f => (JsonNode)f).ToArray()),
        }, $"{applied.Count} applied, {failed.Count} failed on '{target.Name}'.");

        if (failed.Count > 0) result.IsError = true;
        return result;
    }
}
