using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scene;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>Helpers the scene tools share: lookups that fail with useful messages, path safety, property setting.</summary>
internal static class SceneToolSupport
{
    public const string IdsRegeneratedNote = "Actor ids were regenerated; call get_scene_summary before referring to actors by id.";

    public static Core.Scene RequireScene(IMcpSceneHost host)
        => host.ActiveScene ?? throw new McpToolException("No scene is open.", "Call new_scene or load_scene first.");

    public static void RefuseWhilePlaying(IMcpSceneHost host, string action)
    {
        if (host.IsPlaying)
            throw new McpToolException($"Cannot {action} while the scene is in play mode.", "Call stop first.");
    }

    /// <summary>
    /// The layer to use for a requested name: an existing layer matched exactly or ignoring
    /// case (templates say "Default", new scenes say "default"), else the name as given, which
    /// the scene creates on demand.
    /// </summary>
    public static string ResolveLayerName(Core.Scene scene, string? requested)
    {
        string wanted = string.IsNullOrWhiteSpace(requested) ? "default" : requested.Trim();

        if (scene.GetLayer(wanted) != null) return wanted;

        var loose = scene.Layers.FirstOrDefault(l => string.Equals(l.Name, wanted, StringComparison.OrdinalIgnoreCase));
        return loose?.Name ?? wanted;
    }

    public static Transform3D EnsureTransform3D(Actor actor)
        => actor.GetComponent<Transform3D>() ?? actor.AddComponent<Transform3D>();

    public static Vector3 ToVector3(float[] values, string name)
    {
        if (values.Length != 3)
            throw new McpToolException($"'{name}' must have 3 elements [x, y, z] for a 3D actor; got {values.Length}.");
        return new Vector3(values[0], values[1], values[2]);
    }

    public static Vector2 ToVector2(float[] values, string name)
    {
        if (values.Length != 2)
            throw new McpToolException($"'{name}' must have 2 elements [x, y] for a 2D actor; got {values.Length}.");
        return new Vector2(values[0], values[1]);
    }

    /// <summary>A project-relative path made absolute against the host's root, refusing to leave it.</summary>
    public static string ResolveInsideProject(IMcpSceneHost host, string path, string purpose)
    {
        string root = Path.GetFullPath(host.ProjectRoot ?? ProjectPaths.EffectiveRoot);
        string full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));

        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!full.StartsWith(rootPrefix, comparison))
            throw new McpToolException($"Refusing to {purpose} outside the project root ({root}).", "Use a path relative to the project, such as 'Scenes/Level1.scene'.");

        return full;
    }

    public static string MakeProjectRelative(IMcpSceneHost host, string fullPath)
    {
        string root = Path.GetFullPath(host.ProjectRoot ?? ProjectPaths.EffectiveRoot);
        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }

    // -------------------------------------------------------------------------
    // Components
    // -------------------------------------------------------------------------

    public static Type ResolveComponentTypeOrThrow(string name)
    {
        var type = SceneSerializer.ResolveComponentType(name);
        if (type != null) return type;

        string hint = ComponentReflection.Suggest(name, ReflectionUtil.FindComponentTypes().Select(t => t.Name));
        throw new McpToolException($"Unknown component type '{name}'.",
            (hint.Length > 0 ? hint + " " : "") + "Call list_component_types for the full list.");
    }

    /// <summary>The actor's component of a type named by short or full name, ignoring case.</summary>
    public static Component FindComponent(Actor actor, string typeName)
    {
        var component = TryFindComponent(actor, typeName);
        if (component != null) return component;

        var names = actor.GetAllComponents().Select(SceneViews.ComponentTypeName).ToList();
        string hint = ComponentReflection.Suggest(typeName, names);
        throw new McpToolException(
            $"No component '{typeName}' on '{actor.Name}'. It has: {string.Join(", ", names)}.",
            hint.Length > 0 ? hint : null);
    }

    public static Component? TryFindComponent(Actor actor, string typeName)
    {
        string wanted = typeName.Trim();
        var components = actor.GetAllComponents();

        return components.FirstOrDefault(c => SceneViews.ComponentTypeName(c) == wanted)
            ?? components.FirstOrDefault(c => c.GetType().FullName == wanted)
            ?? components.FirstOrDefault(c => string.Equals(SceneViews.ComponentTypeName(c), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Sets one editable property from JSON and returns the value as it now reads.</summary>
    public static JsonNode? SetProperty(object target, Type type, string propertyName, JsonElement value)
    {
        var property = ComponentReflection.FindProperty(type, propertyName);
        if (property == null)
        {
            var names = ComponentReflection.EditableProperties(type).Select(p => p.Name).ToList();
            string hint = ComponentReflection.Suggest(propertyName, names);
            throw new McpToolException(
                $"'{type.Name}' has no editable property '{propertyName}'. Editable: {string.Join(", ", names)}.",
                hint.Length > 0 ? hint : null);
        }

        if (!ValueConverter.TryFromJson(value, property.PropertyType, out var converted, out var error))
            throw new McpToolException($"{type.Name}.{property.Name}: {error}");

        try
        {
            property.SetValue(target, converted);
        }
        catch (Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException { InnerException: { } ie } ? ie : ex;
            throw new McpToolException($"{type.Name}.{property.Name} rejected the value: {inner.Message}");
        }

        return ValueConverter.ToJson(property.GetValue(target));
    }

    /// <summary>
    /// Applies a bag keyed by <c>"Type.Property"</c> (or plain <c>"Property"</c> when a default
    /// target is given) to an actor. Valid entries are applied even when others fail.
    /// </summary>
    public static void ApplyPropertyBag(Actor actor, JsonElement? bag, Component? defaultTarget,
                                        List<string> applied, List<string> failed)
    {
        if (bag is not { ValueKind: JsonValueKind.Object } b) return;

        foreach (var entry in b.EnumerateObject())
        {
            try
            {
                object target;
                Type   type;
                string propertyName;

                int dot = entry.Name.LastIndexOf('.');
                if (dot > 0)
                {
                    string owner = entry.Name[..dot];
                    propertyName = entry.Name[(dot + 1)..];

                    if (string.Equals(owner, "Actor", StringComparison.OrdinalIgnoreCase))
                    {
                        target = actor;
                        type   = actor.GetType();
                    }
                    else
                    {
                        var component = FindComponent(actor, owner);
                        target = component;
                        type   = component.GetType();
                    }
                }
                else if (defaultTarget != null)
                {
                    target       = defaultTarget;
                    type         = defaultTarget.GetType();
                    propertyName = entry.Name;
                }
                else
                {
                    throw new McpToolException($"'{entry.Name}': use 'ComponentType.Property' keys, e.g. 'Light3D.Intensity'.");
                }

                var result = SetProperty(target, type, propertyName, entry.Value);
                applied.Add($"{entry.Name} = {result?.ToJsonString() ?? "null"}");
            }
            catch (McpToolException ex)
            {
                failed.Add($"{entry.Name}: {ex.FullMessage}");
            }
        }
    }

    // -------------------------------------------------------------------------
    // stderr capture — the serialiser reports unresolved types there
    // -------------------------------------------------------------------------

    /// <summary>
    /// Tees <see cref="Console.Error"/> while a scene loads so the loader's diagnostics reach
    /// the caller as warnings. Process-global, so only used on the game thread.
    /// </summary>
    public sealed class ConsoleErrorCapture : IDisposable
    {
        private readonly TextWriter   _original;
        private readonly StringWriter _buffer = new();

        public ConsoleErrorCapture()
        {
            _original = Console.Error;
            Console.SetError(new TeeWriter(_original, _buffer));
        }

        public IReadOnlyList<string> Lines
            => _buffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        public void Dispose() => Console.SetError(_original);

        private sealed class TeeWriter : TextWriter
        {
            private readonly TextWriter _a, _b;
            public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
            public override System.Text.Encoding Encoding => _a.Encoding;
            public override void Write(char value) { _a.Write(value); _b.Write(value); }
            public override void Write(string? value) { _a.Write(value); _b.Write(value); }
            public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
            public override void Flush() { _a.Flush(); _b.Flush(); }
        }
    }
}
