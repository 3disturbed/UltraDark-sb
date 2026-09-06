using System.Text.Json;
using System.Text.Json.Nodes;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.Scene;
using static SexyBiscuit.Engine.Mcp.SceneToolSupport;

namespace SexyBiscuit.Engine.Mcp.Tools;

/// <summary>
/// Many edits in one call.
/// </summary>
/// <remarks>
/// Every tool call is a round trip that resends the whole conversation, so a scene built one
/// actor per call costs its size squared. A batch is one call, one undo step and one flush; the
/// operations reuse the existing tools through <see cref="McpToolRegistry.InvokeInline"/> rather
/// than reimplementing them, and an op can refer to an actor an earlier op spawned as
/// <c>"$n"</c>.
/// </remarks>
public sealed class BatchTools
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "spawn_actor", "spawn_primitive", "place_actor", "duplicate_actor",
        "set_transform", "translate", "rotate", "look_at",
        "set_properties", "set_property", "add_component", "remove_component", "set_material",
        "rename_actor", "set_actor", "move_to_layer", "destroy_actor",
    };

    private static readonly string[] ActorArguments = { "actor", "targetActor", "lookAtActor", "target" };

    private readonly IMcpSceneHost   _host;
    private readonly McpToolRegistry _registry;

    public BatchTools(IMcpSceneHost host, McpToolRegistry registry)
    {
        _host     = host;
        _registry = registry;
    }

    [McpTool("apply_scene_edits",
        "Run several scene edits in one call and one undo step. ops is a JSON array; each op is a tool's arguments plus " +
        "\"op\": spawn_actor, spawn_primitive, place_actor, duplicate_actor, set_transform, translate, rotate, look_at, " +
        "set_properties, set_property, add_component, remove_component, set_material, rename_actor, set_actor, " +
        "move_to_layer or destroy_actor. An actor argument may be \"$n\": the id spawned by op n (0-based).",
        Mutating = true, Label = "Apply scene edits")]
    public McpToolResult ApplySceneEdits(
        McpCallContext context,
        [McpParam("e.g. [{\"op\":\"spawn_primitive\",\"shape\":\"Cube\",\"position\":[0,0,0]},{\"op\":\"set_properties\",\"actor\":\"$0\",\"properties\":{\"Transform3D.Scale\":[2,1,2]}}]")] JsonElement ops,
        [McpParam("Stop at the first failure")] bool stopOnError = false)
    {
        RequireScene(_host);
        if (ops.ValueKind != JsonValueKind.Array)
            throw new McpToolException("ops must be a JSON array of operations.");

        var rows    = new JsonArray();
        var ids     = new List<long?>();
        int ok = 0, failed = 0;

        int index = 0;
        foreach (var op in ops.EnumerateArray())
        {
            int i = index++;
            string? name = op.ValueKind == JsonValueKind.Object && op.TryGetProperty("op", out var opName) && opName.ValueKind == JsonValueKind.String
                ? opName.GetString()
                : null;

            McpToolResult result;
            if (name == null || !Allowed.Contains(name))
            {
                result = McpToolResult.Error(name == null ? "each op needs an \"op\" name" : $"'{name}' cannot be part of a batch");
            }
            else
            {
                result = _registry.InvokeInline(name, Substitute(op, ids, i), context);
            }

            long? id = result.IsError ? null : ReadId(result);
            ids.Add(id);

            var row = new JsonObject { ["i"] = i, ["op"] = name ?? "?" };
            if (result.IsError)
            {
                failed++;
                row["ok"]    = false;
                row["error"] = result.FirstText;
            }
            else
            {
                ok++;
                if (id != null) row["id"] = id;
                if (result.StructuredContent?["name"] is JsonValue actorName) row["name"] = actorName.DeepClone();
            }
            rows.Add(row);

            if (result.IsError && stopOnError) break;
        }

        _host.ActiveScene?.FlushPendingActors();

        var summary = McpToolResult.Json(rows, $"{ok} ok, {failed} failed");
        if (ok == 0 && failed > 0) summary.IsError = true;
        return summary;
    }

    [McpTool("spawn_many",
        "Spawn one primitive shape (Cube, Sphere, Plane, Quad, Cylinder, Cone) or one palette preset at several positions, " +
        "or on a grid. Returns ids, names and positions only.",
        Mutating = true, Label = "Spawn many {what}")]
    public McpToolResult SpawnMany(
        McpCallContext context,
        [McpParam("A primitive shape or a preset name")] string what,
        [McpParam("[[x,y,z], …]")] float[][]? positions = null,
        [McpParam("[countX, countZ] laid out from origin, spacing apart")] int[]? grid = null,
        [McpParam("Grid spacing in world units")] float spacing = 2f,
        [McpParam("Grid origin [x, y, z]")] float[]? origin = null,
        [McpParam("Name prefix; copies are numbered")] string? name = null,
        [McpParam("[x, y, z] size multipliers (primitives)")] float[]? scale = null,
        [McpParam("Albedo colour (primitives), '#RRGGBB' or a name")] string? color = null,
        [McpParam("Layer name")] string? layer = null,
        [McpParam("Tag")] string? tag = null)
    {
        RequireScene(_host);

        bool primitive = Enum.TryParse<MeshPrimitive>(what, ignoreCase: true, out var shape) && shape != MeshPrimitive.None;
        if (!primitive && ActorPresets.Find(what) == null)
            throw new McpToolException($"'{what}' is neither a primitive shape nor a preset.", "Use Cube, Sphere, Plane, Quad, Cylinder, Cone, or call list_actor_presets.");

        var places = new List<float[]>();
        if (positions != null) places.AddRange(positions);
        if (grid != null)
        {
            if (grid.Length != 2) throw new McpToolException("grid must be [countX, countZ].");
            float step = spacing > 0f ? spacing : 2f;
            float ox = origin is { Length: >= 1 } ? origin[0] : 0f;
            float oy = origin is { Length: >= 2 } ? origin[1] : 0f;
            float oz = origin is { Length: >= 3 } ? origin[2] : 0f;
            for (int z = 0; z < grid[1]; z++)
                for (int x = 0; x < grid[0]; x++)
                    places.Add(new[] { ox + x * step, oy, oz + z * step });
        }
        if (places.Count == 0) throw new McpToolException("Give positions or a grid.");
        if (places.Count > 500) throw new McpToolException("At most 500 actors per call.");

        string prefix = string.IsNullOrWhiteSpace(name) ? (primitive ? shape.ToString() : what) : name;
        var spawned = new JsonArray();
        var errors  = new List<string>();

        for (int i = 0; i < places.Count; i++)
        {
            var args = new JsonObject { ["position"] = new JsonArray(places[i].Select(v => (JsonNode)v).ToArray()) };
            args[primitive ? "shape" : "preset"] = what;
            args["name"] = $"{prefix} {i + 1}";
            if (layer != null) args["layer"] = layer;
            if (primitive)
            {
                if (scale != null) args["scale"] = new JsonArray(scale.Select(v => (JsonNode)v).ToArray());
                if (color != null) args["color"] = color;
                if (tag != null)   args["tag"]   = tag;
            }

            var result = _registry.InvokeInline(primitive ? "spawn_primitive" : "place_actor", JsonSerializer.SerializeToElement(args), context);
            if (result.IsError) { errors.Add($"{i}: {result.FirstText}"); continue; }

            if (!primitive && tag != null && ReadId(result) is { } placedId)
                _registry.InvokeInline("set_actor", JsonSerializer.SerializeToElement(new JsonObject { ["actor"] = placedId.ToString(), ["tag"] = tag }), context);

            var stub = result.StructuredContent as JsonObject;
            spawned.Add(new JsonObject
            {
                ["id"]       = stub?["id"]?.DeepClone(),
                ["name"]     = stub?["name"]?.DeepClone(),
                ["position"] = stub?["position"]?.DeepClone(),
            });
        }

        _host.ActiveScene?.FlushPendingActors();

        var summary = McpToolResult.Json(new JsonObject { ["spawned"] = spawned }, $"Spawned {spawned.Count} x {what}.");
        foreach (var error in errors) summary.WithWarning(error);
        if (spawned.Count == 0) summary.IsError = true;
        return summary;
    }

    // -------------------------------------------------------------------------

    /// <summary>The op's arguments without "op", with "$n" actor references resolved to ids.</summary>
    private static JsonElement Substitute(JsonElement op, List<long?> ids, int index)
    {
        var args = new JsonObject();
        foreach (var property in op.EnumerateObject())
        {
            if (property.Name == "op") continue;

            if (ActorArguments.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString() is { } text && text.StartsWith('$') && int.TryParse(text.AsSpan(1), out int reference))
            {
                if (reference < 0 || reference >= ids.Count)
                    throw new McpToolException($"op {index}: '{text}' refers to an op that has not run yet.");
                if (ids[reference] is not { } id)
                    throw new McpToolException($"op {index}: '{text}' refers to op {reference}, which failed or spawned nothing.");
                args[property.Name] = id.ToString();
                continue;
            }

            args[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
        return JsonSerializer.SerializeToElement(args);
    }

    /// <summary>The id a spawn echoed. Actor ids are unsigned, so the node may hold a uint, not a long.</summary>
    private static long? ReadId(McpToolResult result)
    {
        if (result.StructuredContent?["id"] is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var asLong)) return asLong;
        if (value.TryGetValue<uint>(out var asUint)) return asUint;
        if (value.TryGetValue<int>(out var asInt)) return asInt;
        return long.TryParse(value.ToJsonString(), out var parsed) ? parsed : null;
    }
}
