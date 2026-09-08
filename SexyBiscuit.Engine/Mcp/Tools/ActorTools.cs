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

    [McpTool("spawn_actor",
        "Create an actor. components are type names such as 'MeshRenderer' or 'Light3D' (required companions are added " +
        "automatically); class is an Actor subclass such as 'GameMode', 'Character' or a project class. position/rotation/" +
        "scale with 3 elements make a 3D actor (rotation is [pitch, yaw, roll] degrees); 2 elements make a 2D one " +
        "(rotation [degrees]). properties sets initial values, keyed 'Type.Property'. The new actor becomes selected.",
        Mutating = true, Label = "Spawn actor '{name}'")]
    public McpToolResult SpawnActor(
        [McpParam("Actor name")] string name,
        [McpParam("Component type names to add")] string[]? components = null,
        [McpParam("Actor subclass to construct")] string? @class = null,
        [McpParam("[x, y, z] world units, or [x, y] pixels for 2D")] float[]? position = null,
        [McpParam("[pitch, yaw, roll] degrees, or [degrees] for 2D")] float[]? rotation = null,
        [McpParam("[x, y, z] or [x, y]")] float[]? scale = null,
        [McpParam("Layer name; created if missing")] string layer = "default",
        [McpParam("Tag, e.g. 'MainCamera3D' or 'Player'")] string? tag = null,
        [McpParam("Initial property values, e.g. {\"Light3D.Intensity\": 2}")] JsonElement? properties = null,
        [McpParam("Give the actor a Transform3D even without a 3D position")] bool transform3d = true)
    {
        var scene = RequireScene(_host);
        var actor = ConstructActor(@class, name);
        actor.Name = name;
        if (tag != null) actor.Tag = tag;

        bool wants3d = transform3d || position is { Length: 3 } || rotation is { Length: 3 } || scale is { Length: 3 };
        if (position is { Length: 2 } || rotation is { Length: 1 } || scale is { Length: 2 }) wants3d = wants3d && position is not { Length: 2 };

        if (wants3d) EnsureTransform3D(actor);
        ApplyTransform(actor, position, rotation, scale, space: "world");

        var applied = new List<string>();
        var failed  = new List<string>();

        foreach (var typeName in components ?? Array.Empty<string>())
        {
            var type = ResolveComponentTypeOrThrow(typeName);
            if (!actor.HasComponent(type)) actor.AddComponent(type);
        }

        ApplyPropertyBag(actor, properties, null, applied, failed);

        scene.AddActor(actor, ResolveLayerName(scene, layer));
        scene.FlushPendingActors();
        _host.SelectActor(actor);

        var result = McpToolResult.Json(SceneViews.ActorStub(actor), $"Spawned '{actor.Name}' (id {actor.Id}).");
        foreach (var f in failed) result.WithWarning(f);
        return result;
    }

    /// <summary>Every shape <c>spawn_primitive</c> accepts, for its description and its error.</summary>
    /// <remarks>
    /// Read off the enum rather than written out, because it was written out: Capsule and
    /// Torus were in the browser's MeshPrimitive for months, and this tool went on telling
    /// callers that six shapes existed.
    /// </remarks>
    private static readonly string ShapeList = string.Join(", ",
        Enum.GetNames<MeshPrimitive>().Where(n => n != nameof(MeshPrimitive.None)));

    [McpTool("spawn_primitive",
        "Place a built-in shape with its own coloured material: Cube, Sphere, Plane (1x1 floor tile), Quad (1x1 wall), " +
        "Cylinder, Cone, Capsule (two units tall) or Torus, otherwise unit-sized — use scale for dimensions. " +
        "The new actor becomes selected.",
        Mutating = true, Label = "Spawn {shape}")]
    public McpToolResult SpawnPrimitive(
        [McpParam("Cube, Sphere, Plane, Quad, Cylinder, Cone, Capsule or Torus")] string shape,
        [McpParam("Actor name; defaults to the shape")] string? name = null,
        [McpParam("[x, y, z] world units")] float[]? position = null,
        [McpParam("[pitch, yaw, roll] degrees")] float[]? rotation = null,
        [McpParam("[x, y, z] size multipliers")] float[]? scale = null,
        [McpParam("Albedo colour, '#RRGGBB' or a name")] Color? color = null,
        [McpParam("0 = dielectric, 1 = metal")] float metallic = 0f,
        [McpParam("0 = mirror, 1 = matte")] float roughness = 0.5f,
        [McpParam("Layer name")] string? layer = null,
        [McpParam("Tag")] string? tag = null)
    {
        var scene = RequireScene(_host);

        if (!Enum.TryParse<MeshPrimitive>(shape, ignoreCase: true, out var primitive) || primitive == MeshPrimitive.None)
            throw new McpToolException($"'{shape}' is not a primitive shape.", $"Use one of: {ShapeList}.");

        var actor = new Actor(name ?? primitive.ToString());
        if (tag != null) actor.Tag = tag;
        EnsureTransform3D(actor);

        var mesh = actor.AddComponent<MeshRenderer>();
        mesh.MeshType = primitive;

        var material = mesh.EnsureOwnMaterial(0);
        material.AlbedoColor = color ?? Color.LightGray;
        material.Metallic    = Math.Clamp(metallic, 0f, 1f);
        material.Roughness   = Math.Clamp(roughness, 0f, 1f);

        ApplyTransform(actor, position, rotation, scale, space: "world");

        scene.AddActor(actor, ResolveLayerName(scene, layer));
        scene.FlushPendingActors();
        _host.SelectActor(actor);

        return McpToolResult.Json(SceneViews.ActorStub(actor), $"Spawned {primitive} '{actor.Name}' (id {actor.Id}).");
    }

    [McpTool("place_actor",
        "Place a palette preset — the same list as the editor's Place Actors panel: Empty Actor, Empty Actor (3D), Mesh, " +
        "Skinned Mesh, Skybox, Directional Light, Point Light, Spot Light, 2D Light, Camera, Fly Camera, Camera 2D, " +
        "Game Mode, Character, AI Character, Player Start, Particle System (3D), Particle Emitter (2D), Sprite, Tilemap, " +
        "Canvas. Call list_actor_presets for descriptions.",
        Mutating = true, Label = "Place {preset}")]
    public McpToolResult PlaceActor(
        [McpParam("Preset name")] string preset,
        [McpParam("Actor name; defaults to the preset's")] string? name = null,
        [McpParam("[x, y, z] world units (or [x, y] for 2D presets)")] float[]? position = null,
        [McpParam("[pitch, yaw, roll] degrees")] float[]? rotation = null,
        [McpParam("Layer name")] string? layer = null,
        [McpParam("Initial property values keyed 'Type.Property'")] JsonElement? properties = null)
    {
        var scene = RequireScene(_host);

        var entry = ActorPresets.Find(preset)
            ?? throw new McpToolException($"No preset named '{preset}'.",
                ComponentReflection.Suggest(preset, ActorPresets.All.Select(p => p.Name)) + " Call list_actor_presets.");

        var actor = entry.Build();
        if (name != null) actor.Name = name;

        ApplyTransform(actor, position, rotation, null, space: "world");

        var applied = new List<string>();
        var failed  = new List<string>();
        ApplyPropertyBag(actor, properties, null, applied, failed);

        scene.AddActor(actor, ResolveLayerName(scene, layer));
        scene.FlushPendingActors();
        _host.SelectActor(actor);

        var result = McpToolResult.Json(SceneViews.ActorStub(actor), $"Placed {entry.Name} as '{actor.Name}' (id {actor.Id}).");
        foreach (var f in failed) result.WithWarning(f);
        return result;
    }

    [McpTool("list_actor_presets", "The palette presets with category, description and the components each one creates.")]
    public McpToolResult ListActorPresets()
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

        return McpToolResult.Json(list);
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

    [McpTool("rename_actor", "Rename an actor.", Mutating = true, Label = "Rename {actor} to '{newName}'")]
    public McpToolResult RenameActor([McpParam("Actor id or name")] string actor, [McpParam("New name")] string newName)
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);
        if (string.IsNullOrWhiteSpace(newName)) throw new McpToolException("newName must not be empty.");

        target.Name = newName.Trim();
        return McpToolResult.Json(SceneViews.ActorRow(target));
    }

    [McpTool("set_actor",
        "Set an actor's tag, active flag, physics layer (the integer Actor.Layer used by collision masks) or lifeSpan " +
        "in seconds (0 = forever).",
        Mutating = true, Label = "Edit actor {actor}")]
    public McpToolResult SetActor(
        [McpParam("Actor id or name")] string actor,
        [McpParam("New tag")] string? tag = null,
        [McpParam("Enable or disable the actor")] bool? active = null,
        [McpParam("Integer physics/collision layer")] int? physicsLayer = null,
        [McpParam("Seconds until self-destruct; 0 disables")] float? lifeSpan = null)
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);

        if (tag != null)          target.Tag      = tag;
        if (active.HasValue)      target.IsActive = active.Value;
        if (physicsLayer.HasValue) target.Layer   = physicsLayer.Value;
        if (lifeSpan.HasValue)    target.LifeSpan = lifeSpan.Value;

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
        "Set position, rotation and/or scale. 3 elements address the Transform3D (added if missing) — rotation is " +
        "[pitch, yaw, roll] in degrees; 2 elements address the 2D transform — rotation is [degrees]. space 'local' sets " +
        "values relative to the parent.",
        Mutating = true, Label = "Move {actor}")]
    public McpToolResult SetTransform(
        [McpParam("Actor id or name")] string actor,
        [McpParam("[x, y, z] or [x, y]")] float[]? position = null,
        [McpParam("[pitch, yaw, roll] degrees or [degrees]")] float[]? rotation = null,
        [McpParam("[x, y, z] or [x, y]")] float[]? scale = null,
        [McpParam("'world' or 'local'")] string space = "world")
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);

        if (position is { Length: 3 } || rotation is { Length: 3 } || scale is { Length: 3 })
            EnsureTransform3D(target);

        ApplyTransform(target, position, rotation, scale, space);
        return McpToolResult.Json(TransformResult(target));
    }

    [McpTool("translate", "Move an actor by a delta. space 'local' moves along the actor's own axes.",
             Mutating = true, Label = "Translate {actor}")]
    public McpToolResult Translate(
        [McpParam("Actor id or name")] string actor,
        [McpParam("[dx, dy, dz] world units or [dx, dy] pixels")] float[] delta,
        [McpParam("'world' or 'local'")] string space = "world")
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);

        if (target.GetComponent<Transform3D>() is { } t3d && delta.Length == 3)
        {
            var d = ToVector3(delta, "delta");
            t3d.Position += IsLocal(space) ? t3d.Right * d.X + t3d.Up * d.Y + t3d.Forward * d.Z : d;
        }
        else
        {
            var d = ToVector2(delta, "delta");
            var t = target.Transform;
            t.Position += IsLocal(space) ? t.Right * d.X + t.Up * d.Y : d;
        }

        return McpToolResult.Json(TransformResult(target));
    }

    [McpTool("rotate", "Rotate an actor by delta degrees [pitch, yaw, roll] (or [degrees] for 2D), composed with its current rotation.",
             Mutating = true, Label = "Rotate {actor}")]
    public McpToolResult Rotate(
        [McpParam("Actor id or name")] string actor,
        [McpParam("[pitch, yaw, roll] degrees or [degrees]")] float[] delta,
        [McpParam("'world' or 'local'")] string space = "world")
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);

        if (target.GetComponent<Transform3D>() is { } t3d && delta.Length == 3)
        {
            var q = Transform3D.EulerToQuaternion(ToVector3(delta, "delta"));
            t3d.Rotation = IsLocal(space) ? t3d.Rotation * q : q * t3d.Rotation;
        }
        else
        {
            if (delta.Length != 1) throw new McpToolException("A 2D rotation delta is a single value [degrees].");
            target.Transform.Rotation += MathHelper.ToRadians(delta[0]);
        }

        return McpToolResult.Json(TransformResult(target));
    }

    [McpTool("look_at",
        "Aim a 3D actor's forward axis (-Z, which is where cameras and lights point) at a world point or at another actor.",
        Mutating = true, Label = "Aim {actor}")]
    public McpToolResult LookAt(
        [McpParam("Actor id or name")] string actor,
        [McpParam("World point [x, y, z]")] float[]? target = null,
        [McpParam("Actor id or name to look at")] string? targetActor = null)
    {
        var scene = RequireScene(_host);
        var self  = ActorRef.Resolve(scene, actor);
        var t3d   = self.GetComponent<Transform3D>() ?? throw new McpToolException($"'{self.Name}' has no Transform3D.", "look_at is for 3D actors.");

        Vector3 point;
        if (targetActor != null)
        {
            var other = ActorRef.Resolve(scene, targetActor);
            point = other.GetComponent<Transform3D>()?.Position
                ?? throw new McpToolException($"'{other.Name}' has no Transform3D to look at.");
        }
        else if (target != null)
        {
            point = ToVector3(target, "target");
        }
        else
        {
            throw new McpToolException("Give either target [x, y, z] or targetActor.");
        }

        t3d.LookAt(point);
        return McpToolResult.Json(TransformResult(self));
    }

    [McpTool("move_to_layer", "Move an actor to another scene layer (a draw-order group), creating the layer if needed. Nothing is destroyed.",
             Mutating = true, Label = "Move {actor} to layer {layer}")]
    public McpToolResult MoveToLayer(
        [McpParam("Actor id or name")] string actor,
        [McpParam("Target layer name")] string layer,
        [McpParam("Draw order for a newly created layer")] int order = 0)
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);

        scene.MoveActor(target, ResolveLayerName(scene, layer), order);
        scene.FlushPendingActors();

        return McpToolResult.Json(SceneViews.ActorRow(target));
    }

    [McpTool("attach_actor",
        "Attach an actor to a parent, so it moves, rotates and scales with it and is destroyed with it. " +
        "Both the 2D and 3D transforms follow. Pass no parent to detach.",
        Mutating = true, Label = "Attach {actor} to {parent}")]
    public McpToolResult AttachActor(
        [McpParam("Actor id or name -- the child")] string actor,
        [McpParam("Actor id or name -- the new parent. Omit to detach to the scene root.")] string? parent = null,
        [McpParam("Keep the child where it is on screen (true), or treat its current transform as a local offset from the parent (false)")]
        bool keepWorldTransform = true)
    {
        var scene  = RequireScene(_host);
        var child  = ActorRef.Resolve(scene, actor);
        var target = string.IsNullOrWhiteSpace(parent) ? null : ActorRef.Resolve(scene, parent);

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

    [McpTool("detach_actor",
        "Detach an actor from its parent, returning it to the scene root. It keeps its world position and " +
        "stops being destroyed with the parent.",
        Mutating = true, Label = "Detach {actor}")]
    public McpToolResult DetachActor(
        [McpParam("Actor id or name")] string actor,
        [McpParam("Detach this actor's children instead of the actor itself")] bool children = false)
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);

        if (children) target.DetachChildren();
        else          target.AttachTo(null);

        return McpToolResult.Json(SceneViews.ActorRow(target),
            children ? $"Detached {target.Children.Count} child(ren) from '{target.Name}'." : $"Detached '{target.Name}'.");
    }

    // -------------------------------------------------------------------------

    private static Actor ConstructActor(string? className, string name)
    {
        if (string.IsNullOrWhiteSpace(className)) return new Actor(name);

        var type = SceneSerializer.ResolveActorType(className)
            ?? throw new McpToolException($"Unknown actor class '{className}'.",
                ComponentReflection.Suggest(className, ReflectionUtil.FindActorTypes().Select(t => t.Name)) + " Call list_actor_classes.");

        try
        {
            return (Actor)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            throw new McpToolException($"Could not construct '{className}': {ex.GetBaseException().Message}");
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
