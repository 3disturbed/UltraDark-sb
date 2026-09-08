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

    [McpTool("list_component_types",
        "The component types that can be added, grouped by category (Rendering, Physics, Gameplay, Audio, Animation, " +
        "AI, UI, Scripting…). Names only by default; namesOnly=false adds a one-line description, required companions " +
        "and whether each comes from the engine or the project.", ReadOnly = true)]
    public McpToolResult ListComponentTypes(
        [McpParam("Only this category")] string? category = null,
        [McpParam("Case-insensitive substring of the type name")] string? search = null,
        [McpParam("Names grouped by category (default) or full JSON")] bool namesOnly = true)
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
            return McpToolResult.Text($"{types.Count} component type(s); * = from the project. describe_component_type gives properties.\n"
                                      + string.Join('\n', lines));
        }

        var list = new JsonArray();
        foreach (var type in types)
        {
            string summary = XmlDocs.Summary(type) ?? string.Empty;
            list.Add(new JsonObject
            {
                ["type"]     = type.Name,
                ["category"] = ComponentReflection.Category(type),
                ["source"]   = ComponentReflection.Source(type),
                ["summary"]  = summary.Length > 100 ? summary[..99] + "…" : summary,
                ["requires"] = new JsonArray(ComponentReflection.RequiredComponents(type).Select(r => (JsonNode)r.Name).ToArray()),
            });
        }

        return McpToolResult.Json(list, $"{list.Count} component type(s).");
    }

    [McpTool("describe_component_type",
        "The editable properties of a component type: name, type, enum values, default value, documentation, and whether " +
        "the property is saved in the scene file.", ReadOnly = true)]
    public McpToolResult DescribeComponentType([McpParam("Component type name")] string componentType)
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

    [McpTool("set_property",
        "Set one property on a component (or on the actor itself with componentType 'Actor'). Value formats: numbers, " +
        "booleans, strings, enum names, vectors as [x, y, z], rotations as [pitch, yaw, roll] degrees, colours as " +
        "'#RRGGBB', '#RRGGBBAA', a colour name or {r, g, b, a}. Use 'Transform3D' for Position/EulerAngles/Scale.",
        Mutating = true, Label = "Set {componentType}.{property} on {actor}")]
    public McpToolResult SetProperty(
        [McpParam("Actor id or name")] string actor,
        [McpParam("Component type name, or 'Actor'")] string componentType,
        [McpParam("Property name")] string property,
        [McpParam("New value")] JsonElement value)
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);

        object owner;
        Type   type;
        if (string.Equals(componentType, "Actor", StringComparison.OrdinalIgnoreCase))
        {
            owner = target;
            type  = target.GetType();
        }
        else
        {
            var component = FindComponent(target, componentType);
            if (component is MissingComponent)
                throw new McpToolException($"'{componentType}' on '{target.Name}' is a placeholder for an unresolved type; its properties cannot be edited until the type exists.");
            owner = component;
            type  = component.GetType();
        }

        var applied = SceneToolSupport.SetProperty(owner, type, property, value);
        return McpToolResult.Json(new JsonObject
        {
            ["actor"]     = target.Name,
            ["component"] = type.Name,
            ["property"]  = ComponentReflection.FindProperty(type, property)!.Name,
            ["value"]     = applied,
        });
    }

    [McpTool("get_property", "Read one property of a component (or of the actor itself with componentType 'Actor').", ReadOnly = true)]
    public McpToolResult GetProperty(
        [McpParam("Actor id or name")] string actor,
        [McpParam("Component type name, or 'Actor'")] string componentType,
        [McpParam("Property name")] string property)
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);

        object owner = string.Equals(componentType, "Actor", StringComparison.OrdinalIgnoreCase) ? target : FindComponent(target, componentType);
        var type = owner.GetType();

        var info = ComponentReflection.FindProperty(type, property)
            ?? throw new McpToolException($"'{type.Name}' has no editable property '{property}'.",
                ComponentReflection.Suggest(property, ComponentReflection.EditableProperties(type).Select(p => p.Name)));

        return McpToolResult.Json(new JsonObject
        {
            ["property"] = info.Name,
            ["type"]     = ValueConverter.Describe(info.PropertyType),
            ["value"]    = ValueConverter.ToJson(info.GetValue(owner)),
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
