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
    // Not readonly: ClearTypeCache replaces it, because JsonSerializerOptions caches type
    // metadata per Type and would pin a hot-reloaded assembly's previous generation.
    private static JsonSerializerOptions _options = BuildOptions();

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
        opts.Converters.Add(new Material3DJsonConverter());
        return opts;
    }

    /// <summary>
    /// The converters and settings a scene is written with. <see cref="Prefab"/> serialises a
    /// single actor and has to use the same set, or an enum, a vector or a material round-trips
    /// differently depending on which entry point wrote the file. A property rather than a
    /// cached copy, because <see cref="ClearTypeCache"/> replaces the instance on hot reload.
    /// </summary>
    internal static JsonSerializerOptions Options => _options;

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Serialises a <see cref="Core.Scene"/> to a JSON string.</summary>
    public static string Serialize(Core.Scene scene, SceneSerializerOptions? options = null)
    {
        var dto = BuildSceneDto(scene, options ?? SceneSerializerOptions.Default);
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
    public static void SaveToFile(Core.Scene scene, string path, SceneSerializerOptions? options = null)
    {
        string dir = Path.GetDirectoryName(path)!;
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Serialize(scene, options));
    }

    /// <summary>
    /// Forgets every resolved type name and rebuilds the JSON options. Call it after unloading
    /// or reloading game code: both caches hold <see cref="Type"/> objects from the previous
    /// assembly generation, which would otherwise be handed out for the new one's names.
    /// </summary>
    public static void ClearTypeCache()
    {
        lock (_typeCache)
        {
            _typeCache.Clear();
            _ambiguityCache.Clear();
        }

        _options = BuildOptions();
    }

    /// <summary>Reads <paramref name="path"/> and deserialises a scene from it.</summary>
    public static Core.Scene LoadFromFile(string path)
        => Deserialize(File.ReadAllText(path));

    // -------------------------------------------------------------------------
    // Serialisation helpers
    // -------------------------------------------------------------------------

    private static SceneDto BuildSceneDto(Core.Scene scene, SceneSerializerOptions options)
    {
        var layerDtos = new List<LayerDto>();
        foreach (var layer in scene.Layers)
        {
            var actorDtos = new List<ActorDto>();
            foreach (var actor in layer.Actors)
            {
                // Attached actors are written inside their parent's `children`, so the
                // layer lists only roots. Writing them at both levels would load the
                // subtree twice.
                if (actor.Parent != null) continue;
                actorDtos.Add(BuildActorDto(actor, options));
            }

            layerDtos.Add(new LayerDto
            {
                Name   = layer.Name,
                Order  = layer.Order,
                Actors = actorDtos,
            });
        }

        return new SceneDto { Name = scene.Name, Layers = layerDtos };
    }

    internal static ActorDto BuildActorDto(Actor actor) => BuildActorDto(actor, SceneSerializerOptions.Default);

    internal static ActorDto BuildActorDto(Actor actor, SceneSerializerOptions options)
    {
        var componentDtos = new List<ComponentDto>();

        foreach (var component in actor.GetAllComponents())
        {
            // Transforms are stored as flat fields on the actor, not as components; the
            // missing-class marker is written back as the actor's class below.
            if (component is Transform or Transform3D or MissingActorClass) continue;

            // A placeholder goes back out exactly as it came in.
            if (component is MissingComponent missing)
            {
                componentDtos.Add(new ComponentDto { Type = missing.TypeName, Properties = missing.Properties });
                continue;
            }

            var type = component.GetType();

            componentDtos.Add(new ComponentDto
            {
                Type       = TypeNameFor(type, options.TypeNames, typeof(Component)),
                Properties = CollectProperties(component, type, declaredBelow: null),
            });
        }

        var actorType = actor.GetType();
        string? className = null;
        Dictionary<string, JsonElement>? actorProperties = null;

        if (actor.GetComponent<MissingActorClass>() is { } missingClass)
        {
            className = missingClass.ClassName;
        }
        else if (actorType != typeof(Actor))
        {
            className       = TypeNameFor(actorType, options.TypeNames, typeof(Actor));
            actorProperties = CollectProperties(actor, actorType, declaredBelow: typeof(Actor));
            if (actorProperties.Count == 0) actorProperties = null;
        }

        var transform = actor.Transform;
        var dto = new ActorDto
        {
            Class      = className,
            Properties = actorProperties,
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

        if (actor.Children.Count > 0)
        {
            var children = new List<ActorDto>(actor.Children.Count);
            foreach (var child in actor.Children)
                children.Add(BuildActorDto(child, options));
            dto.Children = children;
        }

        return dto;
    }

    /// <summary>
    /// Reads every serialisable property of an object into a JSON bag. With
    /// <paramref name="declaredBelow"/> set, only properties declared on types deriving from
    /// it are taken — an actor subclass's own state, not <c>Name</c> and <c>Tag</c>, which
    /// the actor block already carries.
    /// </summary>
    private static Dictionary<string, JsonElement> CollectProperties(object target, Type type, Type? declaredBelow)
    {
        var props = new Dictionary<string, JsonElement>();

        foreach (var prop in GetSerializableProperties(type))
        {
            if (declaredBelow != null && (prop.DeclaringType == null || !declaredBelow.IsAssignableFrom(prop.DeclaringType) || prop.DeclaringType == declaredBelow))
                continue;

            try
            {
                object? value = prop.GetValue(target);
                if (value is null) continue;
                props[prop.Name] = SerializeValue(value, _options);
            }
            catch { /* Skip unreadable properties */ }
        }

        return props;
    }

    private static readonly Dictionary<string, bool> _ambiguityCache = new(StringComparer.Ordinal);

    /// <summary>
    /// The name a scene file records for a type. Short names keep files readable and
    /// hand-editable, and stay valid across rebuilds; a short name shared by two loaded
    /// classes in the same family falls back to the namespace-qualified form.
    /// </summary>
    internal static string TypeNameFor(Type type, TypeNameStyle style, Type family)
    {
        switch (style)
        {
            case TypeNameStyle.AssemblyQualified:
                return type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
            case TypeNameStyle.Full:
                return type.FullName ?? type.Name;
        }

        return IsShortNameAmbiguous(type, family) ? type.FullName ?? type.Name : type.Name;
    }

    private static bool IsShortNameAmbiguous(Type type, Type family)
    {
        string key = family.Name + ":" + type.Name;
        lock (_typeCache)
        {
            if (_ambiguityCache.TryGetValue(key, out bool cached)) return cached;
        }

        int matches = 0;
        foreach (var candidate in ReflectionUtil.AllLoadedTypes())
        {
            if (candidate.Name != type.Name || candidate.IsAbstract || !family.IsAssignableFrom(candidate)) continue;
            if (++matches > 1) break;
        }

        bool ambiguous = matches > 1;
        lock (_typeCache) _ambiguityCache[key] = ambiguous;
        return ambiguous;
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

                // A child is an actor in its own right: it needs its own place in a layer
                // to be started, updated and drawn. It goes in the parent's layer, which is
                // the one it was written under.
                foreach (var descendant in actor.Descendants())
                    scene.AddActor(descendant, layer.Name);
            }
        }

        return scene;
    }

    internal static Actor BuildActor(ActorDto dto)
    {
        var actor = ConstructActor(dto);

        actor.Name     = dto.Name ?? "Actor";
        actor.Tag      = dto.Tag  ?? "Untagged";
        actor.Layer    = dto.Layer;
        actor.IsActive = dto.Active;

        if (dto.Properties is { Count: > 0 } && actor.GetComponent<MissingActorClass>() == null)
            ApplyProperties(actor, actor.GetType(), dto.Properties, dto.Class ?? "Actor");

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

        // Attach components. A component the actor already has — added by a subclass
        // constructor, or the Transform3D the transform block created — is filled in rather
        // than duplicated; each existing instance is claimed once, so two genuine components
        // of one type still load as two.
        var claimed = new HashSet<Component>(ReferenceEqualityComparer.Instance);

        foreach (var compDto in dto.Components ?? Enumerable.Empty<ComponentDto>())
        {
            if (string.IsNullOrWhiteSpace(compDto.Type)) continue;

            Type? type = ResolveComponentType(compDto.Type);

            if (type is null)
            {
                // Keep the data. A placeholder writes the type and its properties back out
                // untouched, so a scene survives a failed build or a renamed class.
                Console.Error.WriteLine(
                    $"[SceneSerializer] Could not resolve component type '{compDto.Type}'. Kept as a MissingComponent.");
                var placeholder = actor.AddComponent<MissingComponent>();
                placeholder.TypeName   = compDto.Type;
                placeholder.Properties = compDto.Properties;
                claimed.Add(placeholder);
                continue;
            }

            Component? component = null;
            foreach (var existing in actor.GetAllComponents())
            {
                if (existing.GetType() != type || claimed.Contains(existing)) continue;
                component = existing;
                break;
            }

            if (component == null)
            {
                try
                {
                    component = actor.AddComponentByType(type);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SceneSerializer] Failed to create component '{compDto.Type}': {ex.Message}");
                    continue;
                }
            }

            claimed.Add(component);

            if (compDto.Properties is { Count: > 0 })
                ApplyProperties(component, type, compDto.Properties, compDto.Type);
        }

        // Children last: the parent's own transform has to be in place before a child is
        // attached to it. `keepWorldTransform: false` is the whole point — the file stores
        // a child's transform as its local offset, so rebasing it into the parent's space
        // would apply that offset twice.
        foreach (var childDto in dto.Children ?? Enumerable.Empty<ActorDto>())
            BuildActor(childDto).AttachTo(actor, keepWorldTransform: false);

        return actor;
    }

    /// <summary>
    /// Creates the actor a DTO describes: the named subclass when it resolves, otherwise a
    /// plain <see cref="Actor"/> carrying a <see cref="MissingActorClass"/> marker so the
    /// name is written back on save.
    /// </summary>
    private static Actor ConstructActor(ActorDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Class)) return new Actor();

        Type? type = ResolveActorType(dto.Class);
        if (type != null)
        {
            try
            {
                return (Actor)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[SceneSerializer] Could not construct actor class '{dto.Class}': {ex.GetBaseException().Message}. Loading as a plain Actor.");
            }
        }
        else
        {
            Console.Error.WriteLine(
                $"[SceneSerializer] Could not resolve actor class '{dto.Class}'. Loading as a plain Actor; the class name is kept.");
        }

        var fallback = new Actor();
        fallback.AddComponent<MissingActorClass>().ClassName = dto.Class;
        return fallback;
    }

    private static void ApplyProperties(object target, Type type, Dictionary<string, JsonElement> properties, string ownerName)
    {
        foreach (var (propName, element) in properties)
        {
            var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);

            // Say something. A misspelled or renamed property used to be skipped in silence,
            // so the component came up with default values and the scene just quietly
            // behaved wrong — the worst possible failure for a file people edit by hand.
            if (prop is null)
            {
                Console.Error.WriteLine($"[SceneSerializer] '{type.Name}' has no property '{propName}'. Ignored.");
                continue;
            }

            if (!prop.CanWrite && !IsFillableCollection(prop.PropertyType))
            {
                Console.Error.WriteLine($"[SceneSerializer] '{type.Name}.{propName}' is read-only. Ignored.");
                continue;
            }

            try
            {
                object? value = DeserializeValue(element, prop.PropertyType, _options);
                if (value is null) continue;

                if (prop.CanWrite)
                {
                    prop.SetValue(target, value);
                }
                else if (prop.GetValue(target) is System.Collections.IList list
                      && value is System.Collections.IEnumerable source)
                {
                    // Get-only list: replace its contents rather than the list.
                    list.Clear();
                    foreach (var item in source) list.Add(item);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SceneSerializer] Could not set '{propName}' on '{ownerName}': {ex.Message}");
            }
        }
    }

    // -------------------------------------------------------------------------
    // Component type resolution
    // -------------------------------------------------------------------------

    private static readonly Dictionary<string, Type?> _typeCache = new(StringComparer.Ordinal);

    /// <summary>Resolves a component type name written in a scene file.</summary>
    internal static Type? ResolveComponentType(string name) => ResolveType(name, typeof(Component));

    /// <summary>Resolves an actor class name written in a scene file's <c>class</c> field.</summary>
    internal static Type? ResolveActorType(string name) => ResolveType(name, typeof(Actor));

    /// <summary>
    /// Resolves a type name from a scene file to a concrete type deriving from
    /// <paramref name="mustDerive"/>.
    /// </summary>
    /// <remarks>
    /// Tries assembly-qualified, then full name in any loaded assembly, then the bare type
    /// name. The bare form is what makes scene files writable by hand: nobody should have
    /// to type "SexyBiscuit.Engine.Rendering.Camera3D, SexyBiscuit.Engine" to place a
    /// camera. Results are cached because a large scene asks for the same handful of types
    /// hundreds of times.
    ///
    /// The assembly qualifier is stripped before asking individual assemblies —
    /// <see cref="Assembly.GetType(string)"/> throws on a qualified name, and hot-reloaded
    /// game types live in load contexts <see cref="Type.GetType(string)"/> never searches.
    /// Retired assemblies are skipped, so a name never resolves to a previous generation.
    ///
    /// An ambiguous bare name — two classes with the same short name in different
    /// namespaces — resolves to the first match and logs, rather than failing the load.
    /// Write the full name to disambiguate.
    /// </remarks>
    internal static Type? ResolveType(string name, Type mustDerive)
    {
        string key = mustDerive.Name + ":" + name;
        lock (_typeCache)
        {
            if (_typeCache.TryGetValue(key, out var cached)) return cached;
        }

        Type? found = null;
        int comma   = name.IndexOf(',');
        string bare = comma >= 0 ? name[..comma].Trim() : name.Trim();

        if (comma >= 0)
        {
            try
            {
                found = Type.GetType(name, throwOnError: false);
            }
            catch (Exception ex) when (ex is FileLoadException or BadImageFormatException or ArgumentException or TypeLoadException)
            {
                found = null;
            }

            if (found != null && (!mustDerive.IsAssignableFrom(found) || ReflectionUtil.IsRetired(found.Assembly)))
                found = null;
        }

        if (found == null)
        {
            foreach (var assembly in ReflectionUtil.LoadedAssemblies())
            {
                Type? candidate;
                try
                {
                    candidate = assembly.GetType(bare, throwOnError: false);
                }
                catch (Exception ex) when (ex is FileLoadException or BadImageFormatException or ArgumentException or TypeLoadException)
                {
                    continue;
                }

                if (candidate == null || !mustDerive.IsAssignableFrom(candidate)) continue;
                found = candidate;
                break;
            }
        }

        if (found == null && !bare.Contains('.'))
        {
            var matches = new List<Type>();

            foreach (var candidate in ReflectionUtil.AllLoadedTypes())
            {
                if (!string.Equals(candidate.Name, bare, StringComparison.Ordinal)) continue;
                if (!mustDerive.IsAssignableFrom(candidate)) continue;
                if (candidate.IsAbstract) continue;
                matches.Add(candidate);
            }

            if (matches.Count > 1)
            {
                Console.Error.WriteLine(
                    $"[SceneSerializer] '{bare}' matches {matches.Count} {mustDerive.Name.ToLowerInvariant()} types " +
                    $"({string.Join(", ", matches.Select(t => t.FullName))}). Using the first; " +
                    "write the full name to disambiguate.");
            }

            found = matches.FirstOrDefault();
        }

        lock (_typeCache) _typeCache[key] = found;
        return found;
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
        // A public setter, not merely a setter: a private setter marks runtime state
        // (TriangleCount, IsGrounded) that has no business in a file people edit by hand
        // and no meaning on the next load. Get-only lists are the one exception, filled in
        // place. [SceneIgnore] opts a property out explicitly.
        return type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead
                     && ((p.CanWrite && p.SetMethod?.IsPublic == true) || IsFillableCollection(p.PropertyType))
                     && IsSerializableType(p.PropertyType)
                     && !p.IsDefined(typeof(SceneIgnoreAttribute), inherit: true));
    }

    /// <summary>
    /// True for a List&lt;T&gt; that can be populated through its own instance.
    /// </summary>
    /// <remarks>
    /// Components routinely expose a collection as get-only and expect callers to add to
    /// it — <c>public List&lt;Material3D&gt; Materials { get; } = new();</c>. Requiring a
    /// setter would skip exactly those.
    /// </remarks>
    private static bool IsFillableCollection(Type t)
        => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>);

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
        typeof(Rendering.Material3D),
    };

    /// <summary>
    /// True when a property's type can be written to JSON and read back faithfully.
    /// </summary>
    /// <remarks>
    /// Lists and arrays of a supported element type are included. They were excluded
    /// wholesale before, which silently dropped a MeshRenderer's Materials and a Spline's
    /// Points — the two most visible pieces of a scene's content. The general worry about
    /// collections was polymorphic or unconstructible elements; restricting to a supported
    /// element type rules that out.
    /// </remarks>
    private static bool IsSerializableType(Type t)
    {
        if (_serializableTypes.Contains(t)) return true;
        if (t.IsEnum) return true;

        // Nullable<T> where T is serialisable
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            return IsSerializableType(t.GetGenericArguments()[0]);

        if (t.IsArray)
            return t.GetElementType() is { } element && IsSerializableType(element);

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
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

/// <summary>How a scene file names component and actor types.</summary>
public enum TypeNameStyle
{
    /// <summary>The bare class name, qualified with its namespace only when another loaded class shares it. Hand-editable and stable across rebuilds.</summary>
    Short,

    /// <summary>The namespace-qualified name.</summary>
    Full,

    /// <summary>The assembly-qualified name, version and all. What older files contain.</summary>
    AssemblyQualified,
}

/// <summary>Options for <see cref="SceneSerializer.Serialize(Core.Scene, SceneSerializerOptions?)"/>.</summary>
public sealed class SceneSerializerOptions
{
    public static SceneSerializerOptions Default { get; } = new();

    public TypeNameStyle TypeNames { get; init; } = TypeNameStyle.Short;
}

/// <summary>
/// Writes a <see cref="Rendering.Material3D"/> as its portable properties.
/// </summary>
/// <remarks>
/// A material mixes plain values with GPU handles. Reflection over the whole type would
/// try to serialise <c>Texture2D</c> and <c>Effect</c>, which cannot round-trip, so the
/// type was excluded entirely and every material was lost on save. This writes the
/// scalars and the asset paths, and leaves rebuilding the GPU side to
/// <see cref="Rendering.Material3D.ResolveTextures(Assets.AssetManager)"/> once a device exists.
/// </remarks>
public sealed class Material3DJsonConverter : JsonConverter<Rendering.Material3D>
{
    public override Rendering.Material3D Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var material = new Rendering.Material3D();
        if (reader.TokenType != JsonTokenType.StartObject) return material;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            string name = reader.GetString() ?? "";
            reader.Read();

            switch (name)
            {
                case "albedoColor":       material.AlbedoColor       = ReadColour(ref reader); break;
                case "metallic":          material.Metallic          = reader.GetSingle(); break;
                case "roughness":         material.Roughness         = reader.GetSingle(); break;
                case "emissiveIntensity": material.EmissiveIntensity = reader.GetSingle(); break;
                case "albedoMap":         material.AlbedoMapPath     = reader.GetString(); break;
                case "normalMap":         material.NormalMapPath     = reader.GetString(); break;
                case "metallicMap":       material.MetallicMapPath   = reader.GetString(); break;
                case "roughnessMap":      material.RoughnessMapPath  = reader.GetString(); break;
                case "emissiveMap":       material.EmissiveMapPath   = reader.GetString(); break;
                case "shader":            material.ShaderPath        = reader.GetString(); break;
                default:                  reader.Skip(); break;
            }
        }

        return material;
    }

    private static Color ReadColour(ref Utf8JsonReader reader)
    {
        string hex = (reader.GetString() ?? "#FFFFFFFF").TrimStart('#');
        if (hex.Length < 6) return Color.White;

        byte Channel(int i) => Convert.ToByte(hex.Substring(i, 2), 16);
        return new Color(Channel(0), Channel(2), Channel(4), hex.Length >= 8 ? Channel(6) : (byte)255);
    }

    public override void Write(Utf8JsonWriter writer, Rendering.Material3D value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        var c = value.AlbedoColor;
        writer.WriteString("albedoColor", $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}");
        writer.WriteNumber("metallic",          value.Metallic);
        writer.WriteNumber("roughness",         value.Roughness);
        writer.WriteNumber("emissiveIntensity", value.EmissiveIntensity);

        WriteIfSet("albedoMap",    value.AlbedoMapPath);
        WriteIfSet("normalMap",    value.NormalMapPath);
        WriteIfSet("metallicMap",  value.MetallicMapPath);
        WriteIfSet("roughnessMap", value.RoughnessMapPath);
        WriteIfSet("emissiveMap",  value.EmissiveMapPath);
        WriteIfSet("shader",       value.ShaderPath);

        writer.WriteEndObject();

        void WriteIfSet(string name, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) writer.WriteString(name, path);
        }
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

    /// <summary>The actor subclass to construct. Omitted for a plain Actor.</summary>
    [JsonPropertyName("class")]
    public string? Class { get; set; }

    /// <summary>The subclass's own serialisable properties (not Name, Tag and the transform).</summary>
    [JsonPropertyName("properties")]
    public Dictionary<string, JsonElement>? Properties { get; set; }

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

    /// <summary>
    /// The actors attached to this one, nested. A child's transform blocks are its
    /// <em>local</em> offset from this actor, which is why loading attaches with the
    /// world transform not preserved: the file already says where the child sits in
    /// its parent's frame.
    /// </summary>
    [JsonPropertyName("children")]
    public List<ActorDto>? Children { get; set; }
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
