using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Rendering;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>
/// The JSON Claude reads about a scene. Deliberately not the on-disk format: camelCase, runtime
/// ids, class names, rotations in degrees, component types by short name, and only the
/// properties a person could edit.
/// </summary>
public static class SceneViews
{
    /// <summary>The runtime class name, or the class a file asked for when it could not be resolved.</summary>
    public static string ClassName(Actor actor)
        => actor.GetComponent<MissingActorClass>()?.ClassName ?? actor.GetType().Name;

    /// <summary>The short type name, or the original name for a placeholder.</summary>
    public static string ComponentTypeName(Component component)
        => component is MissingComponent missing ? missing.TypeName : component.GetType().Name;

    /// <summary>One line per actor: enough to pick it, place it and know what it is.</summary>
    public static JsonObject ActorRow(Actor actor, bool includeComponents = true)
    {
        var row = new JsonObject
        {
            ["id"]     = actor.Id,
            ["name"]   = actor.Name,
            ["class"]  = ClassName(actor),
            ["layer"]  = actor.Layer_?.Name,
            ["tag"]    = actor.Tag,
            ["active"] = actor.IsActive,
        };

        if (includeComponents)
        {
            var components = new JsonArray();
            foreach (var component in actor.GetAllComponents())
            {
                if (component is Transform or MissingActorClass) continue;
                components.Add(ComponentTypeName(component));
            }
            row["components"] = components;
        }

        var t3d = actor.GetComponent<Transform3D>();
        row["position"] = t3d != null ? ValueConverter.ToJson(t3d.Position) : ValueConverter.ToJson(actor.Transform.Position);
        return row;
    }

    /// <summary>The full picture of one actor.</summary>
    public static JsonObject ActorView(Actor actor, IMcpSceneHost? host = null)
    {
        var view = ActorRow(actor, includeComponents: false);
        view.Remove("position");

        if (actor.LifeSpan > 0f) view["lifeSpan"] = actor.LifeSpan;

        var t3d = actor.GetComponent<Transform3D>();
        view["transform3d"] = t3d != null ? Transform3DView(t3d) : null;
        if (t3d == null || !IsIdentity(actor.Transform))
            view["transform2d"] = Transform2DView(actor.Transform);

        if (t3d != null && actor.GetComponent<MeshRenderer>() is { } mesh)
        {
            var bounds = mesh.WorldBounds;
            view["bounds"] = new JsonObject
            {
                ["min"] = ValueConverter.ToJson(bounds.Min),
                ["max"] = ValueConverter.ToJson(bounds.Max),
            };
        }

        var components = new JsonArray();
        foreach (var component in actor.GetAllComponents())
        {
            if (component is Transform or Transform3D or MissingActorClass) continue;
            components.Add(ComponentView(component));
        }
        view["components"] = components;

        if (actor.GetType() != typeof(Actor))
        {
            var own = PropertiesOf(actor, actor.GetType(), declaredBelow: typeof(Actor));
            if (own.Count > 0) view["properties"] = own;
        }

        if (host != null) view["selected"] = ReferenceEquals(host.SelectedActor, actor);
        return view;
    }

    public static JsonObject ComponentView(Component component)
    {
        if (component is MissingComponent missing)
        {
            return new JsonObject
            {
                ["type"]       = missing.TypeName,
                ["missing"]    = true,
                ["enabled"]    = component.Enabled,
                ["properties"] = missing.Properties != null
                    ? JsonNode.Parse(JsonSerializer.Serialize(missing.Properties)) ?? new JsonObject()
                    : new JsonObject(),
            };
        }

        return new JsonObject
        {
            ["type"]       = component.GetType().Name,
            ["enabled"]    = component.Enabled,
            ["properties"] = PropertiesOf(component, component.GetType(), declaredBelow: null),
        };
    }

    /// <summary>Every editable property as readable JSON. Unreadable ones are skipped, not fatal.</summary>
    public static JsonObject PropertiesOf(object target, Type type, Type? declaredBelow)
    {
        var result = new JsonObject();

        foreach (var property in ComponentReflection.EditableProperties(type))
        {
            if (declaredBelow != null)
            {
                var declaring = property.DeclaringType;
                if (declaring == null || declaring == declaredBelow || !declaredBelow.IsAssignableFrom(declaring)) continue;
            }

            try
            {
                result[property.Name] = ValueConverter.ToJson(property.GetValue(target));
            }
            catch (Exception)
            {
                // A getter that throws (a GPU handle before the device exists) is not the caller's problem.
            }
        }

        return result;
    }

    public static JsonObject Transform3DView(Transform3D t) => new()
    {
        ["position"]      = ValueConverter.ToJson(t.Position),
        ["rotation"]      = ValueConverter.ToJson(t.EulerAngles),
        ["scale"]         = ValueConverter.ToJson(t.Scale),
        ["localPosition"] = ValueConverter.ToJson(t.LocalPosition),
        ["forward"]       = ValueConverter.ToJson(t.Forward),
    };

    public static JsonObject Transform2DView(Transform t) => new()
    {
        ["position"] = ValueConverter.ToJson(t.Position),
        ["rotation"] = MathF.Round(MathHelper.ToDegrees(t.Rotation), 4),
        ["scale"]    = ValueConverter.ToJson(t.Scale),
    };

    /// <summary>The scene at a glance: layers, actor rows, and whether it can render and play.</summary>
    public static JsonObject SceneSummary(Core.Scene scene, IMcpSceneHost host, SceneUndoStack? undo, bool includeComponents)
    {
        var layers = new JsonArray();
        foreach (var layer in scene.Layers)
        {
            layers.Add(new JsonObject
            {
                ["name"]       = layer.Name,
                ["order"]      = layer.Order,
                ["actorCount"] = layer.Actors.Count,
                ["visible"]    = layer.Visible,
            });
        }

        var actors = new JsonArray();
        var all    = scene.Layers.SelectMany(l => l.Actors).ToList();
        foreach (var actor in all)
            actors.Add(ActorRow(actor, includeComponents));

        var summary = new JsonObject
        {
            ["name"]    = scene.Name,
            ["path"]    = host.CurrentScenePath,
            ["dirty"]   = host.SceneDirty,
            ["playing"] = host.IsPlaying,
            ["layers"]  = layers,
            ["actors"]  = actors,
            ["checks"]  = new JsonObject
            {
                ["hasMainCamera"]  = all.Any(a => a.IsActive && a.Tag == "MainCamera3D" && a.GetComponent<Camera3D>() != null),
                ["hasLight"]       = all.Any(a => a.GetComponent<Light3D>() != null || a.GetComponent<Light2D>() != null || a.GetComponent<SkyLight>() != null),
                ["hasPlayerStart"] = all.Any(a => a.GetComponent<PlayerStart>() != null),
                ["hasGameMode"]    = all.Any(a => a is GameMode),
            },
        };

        if (undo != null)
        {
            summary["undoCount"] = undo.UndoCount;
            summary["redoCount"] = undo.RedoCount;
        }

        if (host.SelectedActor is { } selected)
            summary["selected"] = new JsonObject { ["id"] = selected.Id, ["name"] = selected.Name };

        return summary;
    }

    private static bool IsIdentity(Transform t)
        => t.LocalPosition == Vector2.Zero && t.LocalRotation == 0f && t.LocalScale == Vector2.One;
}
