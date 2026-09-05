using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Scene;

/// <summary>
/// Serialises and deserialises <see cref="Core.Scene"/> objects to/from JSON.
/// </summary>
/// <remarks>
/// <para>
/// Component state is discovered by reflection over public readable/writable properties whose
/// type round-trips through JSON: primitives, string, enum, <see cref="Vector2"/>,
/// <see cref="Vector3"/>, <see cref="Vector4"/>, <see cref="Quaternion"/> and
/// <see cref="Color"/>. GPU-side types such as <c>Texture2D</c> and <c>Effect</c> are skipped
/// deliberately — a scene file records which asset to load, not the loaded asset.
/// </para>
/// <para>
/// Both <see cref="Transform"/> and <see cref="Transform3D"/> are stored as flat fields on the
/// actor rather than as components, so a scene file reads naturally and a 2D scene does not
/// carry empty 3D data. An actor with a <see cref="Transform3D"/> writes the <c>position3</c>,
/// <c>rotation3</c> and <c>scale3</c> fields; one without writes only the 2D fields.
/// </para>
/// </remarks>
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
        // Enums as names, not ordinals. A scene file is meant to be read and hand-edited,
        // and "Directional" survives someone reordering the enum where 0 does not.
        opts.Converters.Add(new JsonStringEnumConverter());
        opts.Converters.Add(new Vector2JsonConverter());
        opts.Converters.Add(new Vector3JsonConverter());
        opts.Converters.Add(new Vector4JsonConverter());
        opts.Converters.Add(new QuaternionJsonConverter());
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
            // Transforms are stored as flat fields on the actor, not as components.
            if (component is Transform or Transform3D) continue;

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
        var dto = new ActorDto
        {
            Name       = actor.Name,
            Tag        = actor.Tag,
            Layer      = actor.Layer,
            Active     = actor.IsActive,
            LifeSpan   = actor.LifeSpan > 0f ? actor.LifeSpan : null,
            Position   = new[] { transform.LocalPosition.X, transform.LocalPosition.Y },
            Rotation   = transform.LocalRotation,
            Scale      = new[] { transform.LocalScale.X, transform.LocalScale.Y },
            Components = componentDtos,
        };

        var t3d = actor.GetComponent<Transform3D>();
        if (t3d != null)
        {
            dto.Position3 = new[] { t3d.LocalPosition.X, t3d.LocalPosition.Y, t3d.LocalPosition.Z };
            dto.Rotation3 = new[] { t3d.LocalRotation.X, t3d.LocalRotation.Y, t3d.LocalRotation.Z, t3d.LocalRotation.W };
            dto.Scale3    = new[] { t3d.LocalScale.X, t3d.LocalScale.Y, t3d.LocalScale.Z };
        }

        return dto;
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

        actor.LifeSpan = dto.LifeSpan ?? 0f;

        // The readable object form overrides the arrays when present.
        if (dto.TransformObject is { } t2)
        {
            actor.Transform.LocalPosition = new Vector2(t2.X, t2.Y);
            actor.Transform.LocalRotation = t2.Rotation;
            actor.Transform.LocalScale    = new Vector2(t2.ScaleX, t2.ScaleY);
        }

        if (dto.Transform3DObject is { } t3)
        {
            var t3d = actor.GetComponent<Transform3D>() ?? actor.AddComponent<Transform3D>();
            t3d.LocalPosition    = new Vector3(t3.X, t3.Y, t3.Z);
            t3d.LocalEulerAngles = new Vector3(t3.RotX, t3.RotY, t3.RotZ);
            t3d.LocalScale       = new Vector3(t3.ScaleX, t3.ScaleY, t3.ScaleZ);
        }

        // Restore the 3D transform only when the file carries one, so a 2D actor
        // does not pick up an unnecessary component on load.
        if (dto.Position3 != null || dto.Rotation3 != null || dto.Scale3 != null)
        {
            var t3d = actor.GetComponent<Transform3D>() ?? actor.AddComponent<Transform3D>();

            if (dto.Position3 is { Length: >= 3 })
                t3d.LocalPosition = new Vector3(dto.Position3[0], dto.Position3[1], dto.Position3[2]);

            if (dto.Rotation3 is { Length: >= 4 })
                t3d.LocalRotation = new Quaternion(dto.Rotation3[0], dto.Rotation3[1], dto.Rotation3[2], dto.Rotation3[3]);

            if (dto.Scale3 is { Length: >= 3 })
                t3d.LocalScale = new Vector3(dto.Scale3[0], dto.Scale3[1], dto.Scale3[2]);
        }

        // Attach components
        foreach (var compDto in dto.Components ?? Enumerable.Empty<ComponentDto>())
        {
            if (string.IsNullOrWhiteSpace(compDto.Type)) continue;

            Type? type = ResolveComponentType(compDto.Type);

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

                    // Say something. A misspelled or renamed property used to be skipped
                    // in silence, so the component came up with default values and the
                    // scene just quietly behaved wrong — the worst possible failure for a
                    // file people edit by hand.
                    if (prop is null)
                    {
                        Console.Error.WriteLine(
                            $"[SceneSerializer] '{type.Name}' has no property '{propName}'. Ignored.");
                        continue;
                    }

                    if (!prop.CanWrite)
                    {
                        Console.Error.WriteLine(
                            $"[SceneSerializer] '{type.Name}.{propName}' is read-only. Ignored.");
                        continue;
                    }

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
    // Component type resolution
    // -------------------------------------------------------------------------

    private static readonly Dictionary<string, Type?> _typeCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves a component type name written in a scene file.
    /// </summary>
    /// <remarks>
    /// Tries assembly-qualified, then full name in any loaded assembly, then the bare type
    /// name. The bare form is what makes scene files writable by hand: nobody should have
    /// to type "SexyBiscuit.Engine.Rendering.Camera3D, SexyBiscuit.Engine" to place a
    /// camera. Results are cached because a large scene asks for the same handful of types
    /// hundreds of times.
    ///
    /// An ambiguous bare name — two components with the same short name in different
    /// namespaces — resolves to the first match and logs, rather than failing the load.
    /// Write the full name to disambiguate.
    /// </remarks>
    internal static Type? ResolveComponentType(string name)
    {
        if (_typeCache.TryGetValue(name, out var cached)) return cached;

        Type? found = Type.GetType(name);

        if (found == null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                found = assembly.GetType(name, throwOnError: false);
                if (found != null) break;
            }
        }

        if (found == null && !name.Contains('.'))
        {
            var matches = new List<Type>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var candidate in SafeGetTypes(assembly))
                {
                    if (!string.Equals(candidate.Name, name, StringComparison.Ordinal)) continue;
                    if (!typeof(Component).IsAssignableFrom(candidate)) continue;
                    if (candidate.IsAbstract) continue;
                    matches.Add(candidate);
                }
            }

            if (matches.Count > 1)
            {
                Console.Error.WriteLine(
                    $"[SceneSerializer] '{name}' matches {matches.Count} component types " +
                    $"({string.Join(", ", matches.Select(t => t.FullName))}). Using the first; " +
                    "write the full name to disambiguate.");
            }

            found = matches.FirstOrDefault();
        }

        _typeCache[name] = found;
        return found;
    }

    /// <summary>Types from an assembly, tolerating ones that fail to load.</summary>
    /// <remarks>
    /// An assembly referencing something absent — Steamworks.NET without the native
    /// library, say — throws from GetTypes. The partial list on the exception is still
    /// usable, and one unrelated assembly should not fail a scene load.
    /// </remarks>
    private static IEnumerable<Type> SafeGetTypes(System.Reflection.Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
        catch { return Array.Empty<Type>(); }
    }

    // -------------------------------------------------------------------------
    // Reflection utilities
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns public, instance, settable and readable properties whose type can be
    /// round-tripped through JSON.
    /// </summary>
    /// <remarks>
    /// GPU and runtime types (<c>Texture2D</c>, <c>Effect</c>, <c>SpriteBatch</c>) are
    /// excluded on purpose: a scene file names the asset to load, and the loader rebuilds
    /// the GPU resource. Collections are excluded too — the general case cannot be
    /// reconstructed safely, so components that need one should expose a serialisable
    /// summary property instead.
    /// </remarks>
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
        typeof(Vector3),
        typeof(Vector4),
        typeof(Quaternion),
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

    /// <summary>Local 3D position as [x, y, z]. Absent for actors with no 3D transform.</summary>
    [JsonPropertyName("position3")]
    public float[]? Position3 { get; set; }

    /// <summary>Local 3D rotation as the quaternion [x, y, z, w].</summary>
    [JsonPropertyName("rotation3")]
    public float[]? Rotation3 { get; set; }

    /// <summary>Local 3D scale as [x, y, z].</summary>
    [JsonPropertyName("scale3")]
    public float[]? Scale3 { get; set; }

    /// <summary>Seconds the actor lives after spawning. Absent when it lives indefinitely.</summary>
    [JsonPropertyName("lifeSpan")]
    public float? LifeSpan { get; set; }

    /// <summary>
    /// Hand-authoring form of the 2D transform: <c>{ "x": 0, "y": 0, "rotation": 0,
    /// "scaleX": 1, "scaleY": 1 }</c>. Read only — saving always writes the flat arrays.
    /// </summary>
    [JsonPropertyName("transform")]
    public Transform2Dto? TransformObject { get; set; }

    /// <summary>
    /// Hand-authoring form of the 3D transform, with rotation in Euler degrees:
    /// <c>{ "x": 0, "y": 5, "z": 10, "rotX": -45, ... }</c>. Read only.
    /// </summary>
    [JsonPropertyName("transform3d")]
    public Transform3Dto? Transform3DObject { get; set; }

    [JsonPropertyName("components")]
    public List<ComponentDto>? Components { get; set; }
}

/// <summary>
/// The readable object form of a 2D transform, for scene files written by hand.
/// </summary>
/// <remarks>
/// The canonical on-disk form is flat arrays, which is compact and what saving produces.
/// Accepting this shape too costs little and makes a hand-edited file far easier to read:
/// <c>"rotation": 90</c> beats a bare number in the third slot of an array.
/// </remarks>
internal sealed class Transform2Dto
{
    [JsonPropertyName("x")]        public float X { get; set; }
    [JsonPropertyName("y")]        public float Y { get; set; }
    [JsonPropertyName("rotation")] public float Rotation { get; set; }
    [JsonPropertyName("scaleX")]   public float ScaleX { get; set; } = 1f;
    [JsonPropertyName("scaleY")]   public float ScaleY { get; set; } = 1f;
}

/// <summary>
/// The readable object form of a 3D transform, with rotation as Euler degrees.
/// </summary>
/// <remarks>
/// Euler is offered on read only. Saving writes a quaternion, because Euler round-trips
/// are lossy near the poles and depend on rotation order — fine for a human typing
/// "rotX: -45" once, not fine for a value re-saved on every edit.
/// </remarks>
internal sealed class Transform3Dto
{
    [JsonPropertyName("x")]      public float X { get; set; }
    [JsonPropertyName("y")]      public float Y { get; set; }
    [JsonPropertyName("z")]      public float Z { get; set; }
    [JsonPropertyName("rotX")]   public float RotX { get; set; }
    [JsonPropertyName("rotY")]   public float RotY { get; set; }
    [JsonPropertyName("rotZ")]   public float RotZ { get; set; }
    [JsonPropertyName("scaleX")] public float ScaleX { get; set; } = 1f;
    [JsonPropertyName("scaleY")] public float ScaleY { get; set; } = 1f;
    [JsonPropertyName("scaleZ")] public float ScaleZ { get; set; } = 1f;
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

/// <summary>Serialises <see cref="Vector3"/> as a three-element JSON array: [x, y, z].</summary>
public sealed class Vector3JsonConverter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected JSON array for Vector3.");

        reader.Read(); float x = reader.GetSingle();
        reader.Read(); float y = reader.GetSingle();
        reader.Read(); float z = reader.GetSingle();
        reader.Read(); // EndArray

        return new Vector3(x, y, z);
    }

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }
}

/// <summary>Serialises <see cref="Vector4"/> as a four-element JSON array: [x, y, z, w].</summary>
public sealed class Vector4JsonConverter : JsonConverter<Vector4>
{
    public override Vector4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected JSON array for Vector4.");

        reader.Read(); float x = reader.GetSingle();
        reader.Read(); float y = reader.GetSingle();
        reader.Read(); float z = reader.GetSingle();
        reader.Read(); float w = reader.GetSingle();
        reader.Read(); // EndArray

        return new Vector4(x, y, z, w);
    }

    public override void Write(Utf8JsonWriter writer, Vector4 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteNumberValue(value.W);
        writer.WriteEndArray();
    }
}

/// <summary>
/// Serialises <see cref="Quaternion"/> as a four-element JSON array: [x, y, z, w].
/// </summary>
/// <remarks>
/// Stored as raw components rather than Euler angles: Euler round-trips are lossy near the
/// poles and depend on rotation order, which would make a saved rotation drift each time a
/// scene is loaded and re-saved.
/// </remarks>
public sealed class QuaternionJsonConverter : JsonConverter<Quaternion>
{
    public override Quaternion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected JSON array for Quaternion.");

        reader.Read(); float x = reader.GetSingle();
        reader.Read(); float y = reader.GetSingle();
        reader.Read(); float z = reader.GetSingle();
        reader.Read(); float w = reader.GetSingle();
        reader.Read(); // EndArray

        return new Quaternion(x, y, z, w);
    }

    public override void Write(Utf8JsonWriter writer, Quaternion value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteNumberValue(value.W);
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
        // Accept { "R": 255, "G": 128, "B": 0, "A": 255 } as well as a hex string. Both
        // turn up in hand-written files, and channel names are the more obvious of the two
        // to someone reading a scene for the first time.
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            byte r = 255, g = 255, b = 255, a = 255;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                string channel = reader.GetString() ?? "";
                reader.Read();
                byte value = (byte)Math.Clamp(reader.GetInt32(), 0, 255);

                switch (channel.ToUpperInvariant())
                {
                    case "R": r = value; break;
                    case "G": g = value; break;
                    case "B": b = value; break;
                    case "A": a = value; break;
                }
            }

            return new Color(r, g, b, a);
        }

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
