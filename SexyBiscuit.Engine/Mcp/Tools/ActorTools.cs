using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.Scene;
using static SexyBiscuit.Engine.Mcp.SceneToolSupport;

namespace SexyBiscuit.Engine.Mcp.Tools;

/// <summary>Creating, removing and moving actors.</summary>
public sealed class ActorTools
{
    private readonly IMcpSceneHost _host;

    public ActorTools(IMcpSceneHost host) => _host = host;

    /// <summary>Every shape <c>spawn_actor</c> accepts, for its description and its error.</summary>
    /// <remarks>
    /// Read off the enum rather than written out, because it was written out: Capsule and
    /// Torus were in the browser's MeshPrimitive for months, and this tool went on telling
    /// callers that six shapes existed.
    /// </remarks>
    private static readonly string ShapeList = string.Join(", ",
        Enum.GetNames<MeshPrimitive>().Where(n => n != nameof(MeshPrimitive.None)));

    [McpTool("spawn_actor",
        "Create an actor and select it. Give one of: shape, a built-in mesh with its own coloured material (Cube, " +
        "Sphere, Plane, Quad, Cylinder, Cone, Capsule, Torus — unit-sized, use scale); preset, a palette entry " +
        "(Camera, Point Light, Character…); class, an Actor subclass such as 'GameMode'; or components, bare type " +
        "names (required companions are added). position/rotation/scale with 3 elements make a 3D actor (rotation is " +
        "[pitch, yaw, roll] degrees), 2 elements a 2D one. properties sets initial values keyed 'Type.Property'. " +
        "list=true returns the preset palette instead of spawning.",
        Mutating = true, Label = "Spawn actor '{name}'")]
    public McpToolResult SpawnActor(
        [McpParam("Actor name; defaults to the shape, preset or class")] string? name = null,
        [McpParam("Built-in mesh: Cube, Sphere, Plane, Quad, Cylinder, Cone, Capsule or Torus")] string? shape = null,
        [McpParam("Palette preset name; list=true names them all")] string? preset = null,
        [McpParam("Actor subclass to construct")] string? @class = null,
        [McpParam("Component type names to add")] string[]? components = null,
        [McpParam("[x, y, z] world units, or [x, y] pixels for 2D")] float[]? position = null,
        [McpParam("[pitch, yaw, roll] degrees, or [degrees] for 2D")] float[]? rotation = null,
        [McpParam("[x, y, z] or [x, y]")] float[]? scale = null,
        [McpParam("Layer name; created if missing")] string? layer = null,
        [McpParam("Tag, e.g. 'MainCamera3D' or 'Player'")] string? tag = null,
        [McpParam("Initial property values, e.g. {\"Light3D.Intensity\": 2}")] JsonElement? properties = null,
        [McpParam("Albedo colour")] Color? color = null,
        [McpParam("0 = dielectric, 1 = metal")] float? metallic = null,
        [McpParam("0 = mirror, 1 = matte")] float? roughness = null,
        [McpParam("Give the actor a Transform3D even without a 3D position")] bool transform3d = true,
        [McpParam("Return the preset palette instead of spawning")] bool list = false)
    {
        if (list) return PresetPalette();

        var scene = RequireScene(_host);
        if (shape != null && preset != null)
            throw new McpToolException("Give shape or preset, not both.", "shape is a built-in mesh; preset is a palette entry.");

        Actor actor;
        if (shape != null)
        {
            if (!Enum.TryParse<MeshPrimitive>(shape, ignoreCase: true, out var primitive) || primitive == MeshPrimitive.None)
                throw new McpToolException($"'{shape}' is not a primitive shape.", $"Use one of: {ShapeList}.");

            actor = new Actor(name ?? primitive.ToString());
            EnsureTransform3D(actor);
            actor.AddComponent<MeshRenderer>().MeshType = primitive;
        }
        else if (preset != null)
        {
            var entry = ActorPresets.Find(preset)
                ?? throw new McpToolException($"No preset named '{preset}'.",
                    ComponentReflection.Suggest(preset, ActorPresets.All.Select(p => p.Name)) + " Call spawn_actor with list=true.");

            // The preset builds its own transforms, so transform3d is not forced on it: a 2D
            // preset such as Canvas or Camera 2D must stay 2D.
            actor = entry.Build();
            if (name != null) actor.Name = name;
        }
        else
        {
            actor = ConstructActor(@class, name ?? "Actor");
            if (name != null) actor.Name = name;

            bool wants3d = transform3d || position is { Length: 3 } || rotation is { Length: 3 } || scale is { Length: 3 };
            if (position is { Length: 2 } || rotation is { Length: 1 } || scale is { Length: 2 }) wants3d = wants3d && position is not { Length: 2 };
            if (wants3d) EnsureTransform3D(actor);
        }

        if (tag != null) actor.Tag = tag;

        foreach (var typeName in components ?? Array.Empty<string>())
        {
            var type = ResolveComponentTypeOrThrow(typeName);
            if (!actor.HasComponent(type)) actor.AddComponent(type);
        }

        // A shape always gets its own material; anything with a MeshRenderer takes the colour
        // and finish it was given, so a Mesh preset needs no second call.
        if (actor.GetComponent<MeshRenderer>() is { } mesh && (shape != null || color.HasValue || metallic.HasValue || roughness.HasValue))
        {
            var material = mesh.EnsureOwnMaterial(0);
            if (color.HasValue)     material.AlbedoColor = color.Value;
            else if (shape != null) material.AlbedoColor = Color.LightGray;
            if (metallic.HasValue)  material.Metallic    = Math.Clamp(metallic.Value, 0f, 1f);
            if (roughness.HasValue) material.Roughness   = Math.Clamp(roughness.Value, 0f, 1f);
        }

        ApplyTransform(actor, position, rotation, scale, space: "world");

        var applied = new List<string>();
        var failed  = new List<string>();
        ApplyPropertyBag(actor, properties, null, applied, failed);

        scene.AddActor(actor, ResolveLayerName(scene, layer));
        scene.FlushPendingActors();
        _host.SelectActor(actor);

        var result = McpToolResult.Json(SceneViews.ActorStub(actor), $"Spawned '{actor.Name}' (id {actor.Id}).");
        foreach (var f in failed) result.WithWarning(f);
        return result;
    }

    /// <summary>The palette <c>preset</c> names, with what each one builds.</summary>
    private static McpToolResult PresetPalette()
    {
        var list = new JsonArray();
        foreach (var preset in ActorPresets.All)
        {
            var components = new JsonArray();
            string className = "Actor";
            try
            {
                var sample = preset.Build();
                className = sample.GetType().Name;
                foreach (var c in sample.GetAllComponents())
                    if (c is not Transform) components.Add(c.GetType().Name);
                sample.InternalDestroy();     // built only to look at; leave the registries clean
            }
            catch (Exception)
            {
                // A preset that cannot build headless still deserves a listing.
            }

            list.Add(new JsonObject
            {
                ["name"]        = preset.Name,
                ["category"]    = preset.Category,
                ["description"] = preset.Description,
                ["class"]       = className,
                ["components"]  = components,
            });
        }

        var result = McpToolResult.Json(list, $"{list.Count} preset(s); pass one as spawn_actor's preset.");
        result.NoChange = true;
        return result;
    }

    [McpTool("destroy_actor", "Remove an actor from the scene. Undo brings it back.", Mutating = true, Destructive = true,
             Label = "Destroy actor {actor}")]
    public McpToolResult DestroyActor([McpParam("Actor id or name")] string actor)
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);
        var info   = new JsonObject { ["id"] = target.Id, ["name"] = target.Name };

        if (ReferenceEquals(_host.SelectedActor, target)) _host.SelectActor(null);

        target.Destroy();
        scene.FlushPendingActors();

        return McpToolResult.Json(new JsonObject { ["destroyed"] = info }, $"Destroyed '{target.Name}'.");
    }

    [McpTool("set_actor",
        "Set an actor's name, tag, active flag, scene layer (a draw-order group, created if missing), physics layer " +
        "(the integer Actor.Layer used by collision masks) or lifeSpan in seconds (0 = forever).",
        Mutating = true, Label = "Edit actor {actor}")]
    public McpToolResult SetActor(
        [McpParam("Actor id or name")] string actor,
        [McpParam("New name")] string? name = null,
        [McpParam("New tag")] string? tag = null,
        [McpParam("Enable or disable the actor")] bool? active = null,
        [McpParam("Scene layer to move it to")] string? layer = null,
        [McpParam("Draw order for a newly created layer")] int layerOrder = 0,
        [McpParam("Integer physics/collision layer")] int? physicsLayer = null,
        [McpParam("Seconds until self-destruct; 0 disables")] float? lifeSpan = null)
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);

        if (name != null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new McpToolException("name must not be empty.");
            target.Name = name.Trim();
        }

        if (tag != null)           target.Tag      = tag;
        if (active.HasValue)       target.IsActive = active.Value;
        if (physicsLayer.HasValue) target.Layer    = physicsLayer.Value;
        if (lifeSpan.HasValue)     target.LifeSpan = lifeSpan.Value;

        if (layer != null)
        {
            scene.MoveActor(target, ResolveLayerName(scene, layer), layerOrder);
            scene.FlushPendingActors();
        }

        return McpToolResult.Json(SceneViews.ActorRow(target));
    }

    [McpTool("duplicate_actor",
        "Clone an actor with all of its components and properties, offset by delta world units (pixels for 2D). The " +
        "copy is placed in the same layer and becomes selected.",
        Mutating = true, Label = "Duplicate {actor}")]
    public McpToolResult DuplicateActor(
        [McpParam("Actor id or name")] string actor,
        [McpParam("Name for the copy; defaults to '<name> (copy)'")] string? newName = null,
        [McpParam("Offset from the original, [x, y, z] or [x, y]")] float[]? offset = null)
    {
        var scene  = RequireScene(_host);
        var source = ActorRef.Resolve(scene, actor);

        var dto  = SceneSerializer.BuildActorDto(source);
        var copy = SceneSerializer.BuildActor(dto);
        copy.Name = newName ?? source.Name + " (copy)";

        var t3d = copy.GetComponent<Transform3D>();
        if (t3d != null)
        {
            var delta = offset == null ? new Vector3(1f, 0f, 0f) : ToVector3(offset, "offset");
            t3d.LocalPosition += delta;
        }
        else
        {
            var delta = offset == null ? new Vector2(16f, 0f) : ToVector2(offset, "offset");
            copy.Transform.LocalPosition += delta;
        }

        scene.AddActor(copy, source.Layer_?.Name ?? "default");
        scene.FlushPendingActors();
        _host.SelectActor(copy);

        return McpToolResult.Json(SceneViews.ActorStub(copy), $"Duplicated '{source.Name}' as '{copy.Name}' (id {copy.Id}).");
    }

    [McpTool("set_transform",
        "Set or change position, rotation and/or scale. 3 elements address the Transform3D (added if missing) — " +
        "rotation is [pitch, yaw, roll] in degrees; 2 elements address the 2D transform — rotation is [degrees]. " +
        "relative=true adds position and composes rotation instead of replacing them; space 'local' works along the " +
        "parent's axes (relative: the actor's own). lookAt or lookAtActor aims a 3D actor's forward axis (-Z, where " +
        "cameras and lights point) instead of taking a rotation.",
        Mutating = true, Label = "Move {actor}")]
    public McpToolResult SetTransform(
        [McpParam("Actor id or name")] string actor,
        [McpParam("[x, y, z] or [x, y]")] float[]? position = null,
        [McpParam("[pitch, yaw, roll] degrees or [degrees]")] float[]? rotation = null,
        [McpParam("[x, y, z] or [x, y]")] float[]? scale = null,
        [McpParam("World point to aim at [x, y, z]")] float[]? lookAt = null,
        [McpParam("Actor id or name to aim at")] string? lookAtActor = null,
        [McpParam("Treat position and rotation as deltas")] bool relative = false,
        [McpParam("'world' or 'local'")] string space = "world")
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);

        bool aiming = lookAt != null || lookAtActor != null;
        if (aiming && rotation != null)
            throw new McpToolException("Give rotation or lookAt, not both.");

        if (position is { Length: 3 } || rotation is { Length: 3 } || scale is { Length: 3 })
            EnsureTransform3D(target);

        if (relative)
        {
            if (position != null) Translate(target, position, space);
            if (rotation != null) Rotate(target, rotation, space);
            ApplyTransform(target, null, null, scale, space);
        }
        else
        {
            ApplyTransform(target, position, rotation, scale, space);
        }

        if (aiming) LookAt(scene, target, lookAt, lookAtActor);

        return McpToolResult.Json(TransformResult(target));
    }

    [McpTool("attach_actor",
        "Attach an actor to a parent, so it moves, rotates and scales with it and is destroyed with it. Both the 2D " +
        "and 3D transforms follow. Omit parent to detach the actor to the scene root, keeping its world position; " +
        "children=true detaches its children instead.",
        Mutating = true, Label = "Attach {actor} to {parent}")]
    public McpToolResult AttachActor(
        [McpParam("Actor id or name -- the child")] string actor,
        [McpParam("Actor id or name -- the new parent. Omit to detach to the scene root.")] string? parent = null,
        [McpParam("Keep the child where it is on screen (true), or treat its current transform as a local offset from the parent (false)")]
        bool keepWorldTransform = true,
        [McpParam("Detach this actor's children instead of the actor itself")] bool children = false)
    {
        var scene  = RequireScene(_host);
        var child  = ActorRef.Resolve(scene, actor);
        var target = string.IsNullOrWhiteSpace(parent) ? null : ActorRef.Resolve(scene, parent);

        if (children)
        {
            if (target != null) throw new McpToolException("children=true detaches the actor's children; do not also give a parent.");
            child.DetachChildren();
            return McpToolResult.Json(SceneViews.ActorRow(child), $"Detached {child.Children.Count} child(ren) from '{child.Name}'.");
        }

        try
        {
            child.AttachTo(target, keepWorldTransform);
        }
        catch (InvalidOperationException ex)
        {
            // A cycle. The engine refuses it because every hierarchy walk would hang; the
            // tool has to say so rather than surfacing it as an internal error.
            throw new McpToolException(ex.Message, "Attach the other way round, or detach one of them first.");
        }

        return McpToolResult.Json(SceneViews.ActorRow(child),
            target == null ? $"Detached '{child.Name}'." : $"Attached '{child.Name}' to '{target.Name}'.");
    }

    // -------------------------------------------------------------------------

    private static Actor ConstructActor(string? className, string name)
    {
        if (string.IsNullOrWhiteSpace(className)) return new Actor(name);

        var type = SceneSerializer.ResolveActorType(className)
            ?? throw new McpToolException($"Unknown actor class '{className}'.",
                ComponentReflection.Suggest(className, ReflectionUtil.FindActorTypes().Select(t => t.Name)) + " Call get_code_project with actorClasses.");

        try
        {
            return (Actor)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            throw new McpToolException($"Could not construct '{className}': {ex.GetBaseException().Message}");
        }
    }

    /// <summary>Moves by a delta; 'local' moves along the actor's own axes.</summary>
    private static void Translate(Actor target, float[] delta, string space)
    {
        if (target.GetComponent<Transform3D>() is { } t3d && delta.Length == 3)
        {
            var d = ToVector3(delta, "position");
            t3d.Position += IsLocal(space) ? t3d.Right * d.X + t3d.Up * d.Y + t3d.Forward * d.Z : d;
        }
        else
        {
            var d = ToVector2(delta, "position");
            var t = target.Transform;
            t.Position += IsLocal(space) ? t.Right * d.X + t.Up * d.Y : d;
        }
    }

    /// <summary>Composes a rotation delta in degrees with the actor's current rotation.</summary>
    private static void Rotate(Actor target, float[] delta, string space)
    {
        if (target.GetComponent<Transform3D>() is { } t3d && delta.Length == 3)
        {
            var q = Transform3D.EulerToQuaternion(ToVector3(delta, "rotation"));
            t3d.Rotation = IsLocal(space) ? t3d.Rotation * q : q * t3d.Rotation;
        }
        else
        {
            if (delta.Length != 1) throw new McpToolException("A 2D rotation delta is a single value [degrees].");
            target.Transform.Rotation += MathHelper.ToRadians(delta[0]);
        }
    }

    /// <summary>Aims the forward axis at a world point or at another actor.</summary>
    private static void LookAt(Core.Scene scene, Actor self, float[]? point, string? targetActor)
    {
        var t3d = self.GetComponent<Transform3D>()
            ?? throw new McpToolException($"'{self.Name}' has no Transform3D.", "lookAt is for 3D actors.");

        if (targetActor != null)
        {
            var other = ActorRef.Resolve(scene, targetActor);
            t3d.LookAt(other.GetComponent<Transform3D>()?.Position
                ?? throw new McpToolException($"'{other.Name}' has no Transform3D to look at."));
        }
        else
        {
            t3d.LookAt(ToVector3(point!, "lookAt"));
        }
    }

    internal static void ApplyTransform(Actor actor, float[]? position, float[]? rotation, float[]? scale, string space)
    {
        bool local = IsLocal(space);
        var t3d = actor.GetComponent<Transform3D>();

        if (position != null)
        {
            if (position.Length == 3 && t3d != null)
            {
                if (local) t3d.LocalPosition = ToVector3(position, "position"); else t3d.Position = ToVector3(position, "position");
            }
            else if (position.Length == 2)
            {
                if (local) actor.Transform.LocalPosition = ToVector2(position, "position"); else actor.Transform.Position = ToVector2(position, "position");
            }
            else
            {
                throw new McpToolException(t3d == null
                    ? "'position' has 3 elements but the actor has no Transform3D; pass transform3d=true or use set_transform, which adds one."
                    : $"'position' must have 2 or 3 elements; got {position.Length}.");
            }
        }

        if (rotation != null)
        {
            if (rotation.Length == 3 && t3d != null)
            {
                var q = Transform3D.EulerToQuaternion(ToVector3(rotation, "rotation"));
                if (local) t3d.LocalRotation = q; else t3d.Rotation = q;
            }
            else if (rotation.Length == 1)
            {
                float radians = MathHelper.ToRadians(rotation[0]);
                if (local) actor.Transform.LocalRotation = radians; else actor.Transform.Rotation = radians;
            }
            else
            {
                throw new McpToolException("'rotation' is [pitch, yaw, roll] degrees for a 3D actor or [degrees] for a 2D one.");
            }
        }

        if (scale != null)
        {
            if (scale.Length == 3 && t3d != null)
            {
                if (local) t3d.LocalScale = ToVector3(scale, "scale"); else t3d.Scale = ToVector3(scale, "scale");
            }
            else if (scale.Length == 2)
            {
                if (local) actor.Transform.LocalScale = ToVector2(scale, "scale"); else actor.Transform.Scale = ToVector2(scale, "scale");
            }
            else if (scale.Length == 1)
            {
                if (t3d != null) t3d.LocalScale = new Vector3(scale[0]); else actor.Transform.LocalScale = new Vector2(scale[0]);
            }
            else
            {
                throw new McpToolException($"'scale' must have 1, 2 or 3 elements; got {scale.Length}.");
            }
        }
    }

    private static bool IsLocal(string space)
    {
        if (string.Equals(space, "local", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(space, "world", StringComparison.OrdinalIgnoreCase)) return false;
        throw new McpToolException($"Unknown space '{space}'.", "Use 'world' or 'local'.");
    }

    private static JsonObject TransformResult(Actor actor)
    {
        var result = new JsonObject { ["id"] = actor.Id, ["name"] = actor.Name };
        var t3d = actor.GetComponent<Transform3D>();
        if (t3d != null) result["transform3d"] = SceneViews.Transform3DView(t3d);
        else result["transform2d"] = SceneViews.Transform2DView(actor.Transform);
        return result;
    }
}
