using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SexyBiscuit.Engine.Scene;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>
/// The JSON options every MCP payload is written with: camelCase, nulls omitted, enums as
/// names, and the engine's own converters for vectors, quaternions, colours and materials so
/// a component property looks the same to Claude as it does in a scene file.
/// </summary>
public static class McpJson
{
    public static JsonSerializerOptions Compact  { get; } = Build(indented: false);
    public static JsonSerializerOptions Indented { get; } = Build(indented: true);

    private static JsonSerializerOptions Build(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented               = indented,
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            NumberHandling              = JsonNumberHandling.AllowReadingFromString,
        };

        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new Vector2JsonConverter());
        options.Converters.Add(new Vector3JsonConverter());
        options.Converters.Add(new Vector4JsonConverter());
        options.Converters.Add(new QuaternionJsonConverter());
        options.Converters.Add(new ColorJsonConverter());
        options.Converters.Add(new Material3DJsonConverter());
        return options;
    }

    /// <summary>
    /// Converts any value to a node tree. A node that already has a parent is cloned, because
    /// a <see cref="JsonNode"/> can only sit in one tree.
    /// </summary>
    public static JsonNode? ToNode(object? value)
    {
        if (value is null) return null;
        if (value is JsonNode node) return node.Parent is null ? node : node.DeepClone();
        if (value is JsonElement element) return JsonNode.Parse(element.GetRawText());
        return JsonSerializer.SerializeToNode(value, Compact);
    }

    public static string Serialize(object? value, bool indented = false)
        => JsonSerializer.Serialize(value, indented ? Indented : Compact);

    /// <summary>An indented rendering of a detached element, for panels.</summary>
    public static string PrettyPrint(JsonElement element) => JsonSerializer.Serialize(element, Indented);

    public static string ToText(JsonNode? node, bool indented = false)
        => node?.ToJsonString(indented ? Indented : Compact) ?? "null";
}
