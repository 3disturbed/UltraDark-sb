using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Scene;

/// <summary>
/// Serialises and deserialises <see cref="Core.Scene"/> objects to/from JSON.
/// Supports all primitive, enum, Vector2, Color, bool, int, float, and string
/// component properties via reflection. Skips Texture2D and other non-portable types.
/// </summary>
public static class SceneSerializer
{
    // -------------------------------------------------------------------------
    // JSON options (shared, thread-safe)
    // -------------------------------------------------------------------------
    private static readonly JsonSerializerOptions _options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented          = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        opts.Converters.Add(new Vector2JsonConverter());
        opts.Converters.Add(new ColorJsonConverter());
        return opts;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Serialises a <see cref="Core.Scene"/> to a JSON string.</summary>
    public static string Serialize(Core.Scene scene)
    {
        var dto = BuildSceneDto(scene);
        return JsonSerializer.Serialize(dto, _options);
    }

    /// <summary>Deserialises a <see cref="Core.Scene"/> from a JSON string.</summary>
    public static Core.Scene Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<SceneDto>(json, _options)
                  ?? throw new JsonException("Failed to deserialise scene JSON.");
        return BuildScene(dto);
    }

    /// <summary>Serialises a scene and writes it to <paramref name="path"/>.</summary>
    public static void SaveToFile(Core.Scene scene, string path)
    {
        string dir = Path.GetDirectoryName(path)!;
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Serialize(scene));
    }

    /// <summary>Reads <paramref name="path"/> and deserialises a scene from it.</summary>
    public static Core.Scene LoadFromFile(string path)
        => Deserialize(File.ReadAllText(path));

    // -------------------------------------------------------------------------
    // Serialisation helpers
    // -------------------------------------------------------------------------

    private static SceneDto BuildSceneDto(Core.Scene scene)
    {
        var layerDtos = new List<LayerDto>();
        foreach (var layer in scene.Layers)
        {
            var actorDtos = new List<ActorDto>();
            foreach (var actor in layer.Actors)
                actorDtos.Add(BuildActorDto(actor));

            layerDtos.Add(new LayerDto
            {
                Name   = layer.Name,
                Order  = layer.Order,
                Actors = actorDtos,
            });
        }

        return new SceneDto { Name = scene.Name, Layers = layerDtos };
    }

    internal static ActorDto BuildActorDto(Actor actor)
    {
        var componentDtos = new List<ComponentDto>();

        foreach (var component in actor.GetAllComponents())
        {
            // Skip the built-in Transform — it is stored as flat fields on the actor
            if (component is Transform) continue;

            var type = component.GetType();
            var props = new Dictionary<string, JsonElement>();

            foreach (var prop in GetSerializableProperties(type))
            {
                try
                {
                    object? value = prop.GetValue(component);
                    if (value is null) continue;
                    var element = SerializeValue(value, _options);
                    props[prop.Name] = element;
                }
                catch { /* Skip unreadable properties */ }
            }

            componentDtos.Add(new ComponentDto
            {
                Type       = type.AssemblyQualifiedName ?? type.FullName ?? type.Name,
                Properties = props,
            });
        }

        var transform = actor.Transform;
        return new ActorDto
        {
            Name       = actor.Name,
            Tag        = actor.Tag,
            Layer      = actor.Layer,
            Active     = actor.IsActive,
            Position   = new[] { transform.LocalPosition.X, transform.LocalPosition.Y },
            Rotation   = transform.LocalRotation,
            Scale      = new[] { transform.LocalScale.X, transform.LocalScale.Y },
            Components = componentDtos,
        };
    }

    // -------------------------------------------------------------------------
    // Deserialisation helpers
    // -------------------------------------------------------------------------

    private static Core.Scene BuildScene(SceneDto dto)
    {
        var scene = new Core.Scene(dto.Name ?? "Scene");

        foreach (var layerDto in dto.Layers ?? Enumerable.Empty<LayerDto>())
        {
            var layer = scene.GetOrCreateLayer(layerDto.Name ?? "default", layerDto.Order);

            foreach (var actorDto in layerDto.Actors ?? Enumerable.Empty<ActorDto>())
            {
                var actor = BuildActor(actorDto);
                scene.AddActor(actor, layer.Name);
            }
        }

        return scene;
    }

    internal static Actor BuildActor(ActorDto dto)
    {
        var actor = new Actor
        {
            Name     = dto.Name     ?? "Actor",
            Tag      = dto.Tag      ?? "Untagged",
            Layer    = dto.Layer,
            IsActive = dto.Active,
        };

        // Apply transform
        var pos = dto.Position;
        var scl = dto.Scale;
        actor.Transform.LocalPosition = (pos != null && pos.Length >= 2)
            ? new Vector2(pos[0], pos[1])
            : Vector2.Zero;
        actor.Transform.LocalRotation = dto.Rotation;
        actor.Transform.LocalScale = (scl != null && scl.Length >= 2)
            ? new Vector2(scl[0], scl[1])
            : Vector2.One;

        // Attach components
        foreach (var compDto in dto.Components ?? Enumerable.Empty<ComponentDto>())
        {
            if (string.IsNullOrWhiteSpace(compDto.Type)) continue;

            Type? type = Type.GetType(compDto.Type);
            if (type is null)
            {
                // Fallback: search loaded assemblies by full name
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = asm.GetType(compDto.Type);
                    if (type != null) break;
                }
            }

            if (type is null)
            {
                Console.Error.WriteLine($"[SceneSerializer] Could not resolve component type '{compDto.Type}'. Skipping.");
                continue;
            }

            if (!typeof(Component).IsAssignableFrom(type))
            {
                Console.Error.WriteLine($"[SceneSerializer] Type '{compDto.Type}' is not a Component. Skipping.");
                continue;
            }

            Component component;
            try
            {
                component = actor.AddComponentByType(type);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SceneSerializer] Failed to create component '{compDto.Type}': {ex.Message}");
                continue;
            }

            // Apply serialised properties
            if (compDto.Properties is { Count: > 0 })
            {
                foreach (var (propName, element) in compDto.Properties)
                {
                    var prop = type.GetProperty(propName,
                        BindingFlags.Public | BindingFlags.Instance);

                    if (prop is null || !prop.CanWrite) continue;

                    try
                    {
                        object? value = DeserializeValue(element, prop.PropertyType, _options);
                        if (value is not null)
                            prop.SetValue(component, value);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[SceneSerializer] Could not set '{propName}' on '{compDto.Type}': {ex.Message}");
                    }
                }
            }
        }

        return actor;
    }

    // -------------------------------------------------------------------------
    // Reflection utilities
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns public, instance, settable, and readable properties that can
    /// be round-tripped through JSON (primitives, string, bool, enum, Vector2, Color).
    /// Intentionally excludes Texture2D, SpriteBatch, and other non-portable XNA types.
    /// </summary>
    internal static IEnumerable<PropertyInfo> GetSerializableProperties(Type type)
    {
        return type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && IsSerializableType(p.PropertyType));
    }

    private static readonly HashSet<Type> _serializableTypes = new()
    {
        typeof(bool),
        typeof(byte),
        typeof(sbyte),
        typeof(short),
        typeof(ushort),
        typeof(int),
        typeof(uint),
        typeof(long),
        typeof(ulong),
        typeof(float),
        typeof(double),
        typeof(decimal),
        typeof(string),
        typeof(Vector2),
        typeof(Color),
    };

    private static bool IsSerializableType(Type t)
    {
        if (_serializableTypes.Contains(t)) return true;
        if (t.IsEnum) return true;
        // Nullable<T> where T is serialisable
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            return IsSerializableType(t.GetGenericArguments()[0]);
        return false;
    }

    private static JsonElement SerializeValue(object value, JsonSerializerOptions opts)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, value.GetType(), opts));
        return doc.RootElement.Clone();
    }

    private static object? DeserializeValue(JsonElement element, Type targetType, JsonSerializerOptions opts)
    {
        // Unwrap nullable
        Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return JsonSerializer.Deserialize(element.GetRawText(), underlying, opts);
    }
}

// =============================================================================
// Data Transfer Objects
// =============================================================================

internal sealed class SceneDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("layers")]
    public List<LayerDto>? Layers { get; set; }
}

internal sealed class LayerDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("actors")]
    public List<ActorDto>? Actors { get; set; }
}

internal sealed class ActorDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("layer")]
    public int Layer { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;

    [JsonPropertyName("position")]
    public float[]? Position { get; set; }

    [JsonPropertyName("rotation")]
    public float Rotation { get; set; }

    [JsonPropertyName("scale")]
    public float[]? Scale { get; set; }

    [JsonPropertyName("components")]
    public List<ComponentDto>? Components { get; set; }
}

internal sealed class ComponentDto
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("properties")]
    public Dictionary<string, JsonElement>? Properties { get; set; }
}

// =============================================================================
// Custom JSON Converters
// =============================================================================

/// <summary>Serialises <see cref="Vector2"/> as a two-element JSON array: [x, y].</summary>
public sealed class Vector2JsonConverter : JsonConverter<Vector2>
{
    public override Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected JSON array for Vector2.");

        reader.Read(); float x = reader.GetSingle();
        reader.Read(); float y = reader.GetSingle();
        reader.Read(); // EndArray

        return new Vector2(x, y);
    }

    public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteEndArray();
    }
}

/// <summary>
/// Serialises <see cref="Color"/> as an HTML hex colour string: "#RRGGBBAA".
/// Accepts "#RRGGBB" (opaque), "#RRGGBBAA", and "#RGB" on read.
/// </summary>
public sealed class ColorJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string hex = reader.GetString() ?? "#FFFFFFFF";
        hex = hex.TrimStart('#');

        return hex.Length switch
        {
            3 => new Color(
                    (byte)HexByte(hex[0], hex[0]),
                    (byte)HexByte(hex[1], hex[1]),
                    (byte)HexByte(hex[2], hex[2]),
                    (byte)255),
            6 => new Color(
                    (byte)HexByte(hex[0], hex[1]),
                    (byte)HexByte(hex[2], hex[3]),
                    (byte)HexByte(hex[4], hex[5]),
                    (byte)255),
            8 => new Color(
                    (byte)HexByte(hex[0], hex[1]),
                    (byte)HexByte(hex[2], hex[3]),
                    (byte)HexByte(hex[4], hex[5]),
                    (byte)HexByte(hex[6], hex[7])),
            _ => Color.White,
        };
    }

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
        => writer.WriteStringValue($"#{value.R:X2}{value.G:X2}{value.B:X2}{value.A:X2}");

    private static byte HexByte(char hi, char lo)
        => Convert.ToByte($"{hi}{lo}", 16);
}
