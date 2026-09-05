using System.Numerics;
using ImGuiNET;
using SexyBiscuit.Engine.AI;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.UI;
using XnaVector3 = Microsoft.Xna.Framework.Vector3;
using Scene = SexyBiscuit.Engine.Core.Scene;

namespace SexyBiscuit.Editor.Panels;

/// <summary>
/// A searchable palette of ready-made actors, in the spirit of Unreal's Place Actors tab.
/// </summary>
/// <remarks>
/// The same presets are on the Create menu, but a docked palette is a different tool: it
/// stays open while you block out a level, and it can be searched. Building a lit 3D scene
/// from the menu means four separate trips; here it is three clicks in one place.
/// </remarks>
public sealed class PlaceActorsPanel
{
    /// <summary>One entry in the palette.</summary>
    private sealed record Entry(string Category, string Name, string Tooltip, Func<Actor> Build);

    private readonly List<Entry> _entries;
    private string _searchText = string.Empty;

    public PlaceActorsPanel()
    {
        _entries = new List<Entry>
        {
            new("Basic", "Empty Actor", "A bare actor with only a 2D transform.",
                () => new Actor("Actor")),

            new("Basic", "Empty Actor (3D)", "An actor with a Transform3D, ready for 3D components.",
                () => { var a = new Actor("Actor 3D"); a.AddComponent<Transform3D>(); return a; }),

            new("Geometry", "Mesh", "A MeshRenderer. Draws a unit cube until you load a model.",
                () => { var a = new Actor("Mesh"); a.AddComponent<Transform3D>(); a.AddComponent<MeshRenderer>(); return a; }),

            new("Geometry", "Skinned Mesh", "A rigged mesh driven by a SkeletalAnimator.",
                () =>
                {
                    var a = new Actor("Skinned Mesh");
                    a.AddComponent<Transform3D>();
                    a.AddComponent<SkeletalAnimator>();
                    a.AddComponent<SkinnedMeshRenderer>();
                    return a;
                }),

            new("Geometry", "Skybox", "Cubemap or gradient sky, drawn before opaque geometry.",
                () => { var a = new Actor("Skybox"); a.AddComponent<Transform3D>(); a.AddComponent<Skybox>(); return a; }),

            new("Lights", "Directional Light", "Sunlight. Direction comes from the transform's forward axis.",
                () => BuildLight(LightType.Directional, "Sun")),

            new("Lights", "Point Light", "Omnidirectional light with a range and inverse-square falloff.",
                () => BuildLight(LightType.Point, "Point Light")),

            new("Lights", "Spot Light", "A cone of light. Set SpotAngle for the cone width.",
                () => BuildLight(LightType.Spot, "Spot Light")),

            new("Lights", "2D Light", "Radial light for the 2D lighting pipeline.",
                () => { var a = new Actor("Light 2D"); a.AddComponent<Light2D>(); return a; }),

            new("Cameras", "Camera", "A Camera3D tagged MainCamera3D so the viewport renders through it.",
                () =>
                {
                    var a = new Actor("Camera") { Tag = "MainCamera3D" };
                    a.AddComponent<Transform3D>().Position = new XnaVector3(0f, 2f, 8f);
                    a.AddComponent<Camera3D>();
                    return a;
                }),

            new("Cameras", "Fly Camera", "A camera with WASD and mouse-look for flying around a scene.",
                () =>
                {
                    var a = new Actor("Fly Camera") { Tag = "MainCamera3D" };
                    a.AddComponent<Transform3D>().Position = new XnaVector3(0f, 2f, 8f);
                    a.AddComponent<Camera3D>();
                    a.AddComponent<FlyCamController>();
                    return a;
                }),

            new("Cameras", "Camera 2D", "Orthographic 2D camera with follow and bounds clamping.",
                () => { var a = new Actor("Camera 2D"); a.AddComponent<Camera2D>(); return a; }),

            new("Gameplay", "Game Mode", "Match rules: spawning, respawn delay, score and time limits.",
                () => new GameMode()),

            new("Gameplay", "Character", "A walking pawn with a capsule controller.",
                () => { var c = new Character("Character"); c.AddComponent<Transform3D>(); return c; }),

            new("Gameplay", "AI Character", "A character with a NavMeshAgent, ready for an AIController.",
                () =>
                {
                    var c = new Character("AI Character");
                    c.AddComponent<Transform3D>();
                    c.AddComponent<NavMeshAgent>().DriveCharacter = true;
                    return c;
                }),

            new("Gameplay", "Player Start", "A spawn point. The game mode picks between all of them.",
                () =>
                {
                    var a = new Actor("Player Start");
                    a.AddComponent<Transform3D>();
                    a.AddComponent<PlayerStart>();
                    return a;
                }),

            new("Effects", "Particle System (3D)", "Camera-facing or mesh particles from a fixed pool.",
                () => { var a = new Actor("Particles"); a.AddComponent<Transform3D>(); a.AddComponent<ParticleSystem3D>(); return a; }),

            new("Effects", "Particle Emitter (2D)", "Sprite particles for the 2D pipeline.",
                () => { var a = new Actor("Emitter"); a.AddComponent<ParticleEmitter>(); return a; }),

            new("2D", "Sprite", "A SpriteRenderer.",
                () => { var a = new Actor("Sprite"); a.AddComponent<SpriteRenderer>(); return a; }),

            new("2D", "Tilemap", "A TilemapRenderer. Load a Tiled JSON map into it.",
                () => { var a = new Actor("Tilemap"); a.AddComponent<TilemapRenderer>(); return a; }),

            new("UI", "Canvas", "Screen-space UI root.",
                () => { var a = new Actor("Canvas"); a.AddComponent<Canvas>(); return a; }),

            new("UI", "World Canvas", "UI positioned in 3D space — nameplates, in-world screens.",
                () => { var a = new Actor("World Canvas"); a.AddComponent<Transform3D>(); a.AddComponent<WorldCanvas>(); return a; }),
        };
    }

    private static Actor BuildLight(LightType type, string name)
    {
        var a = new Actor(name);
        var t = a.AddComponent<Transform3D>();
        var light = a.AddComponent<Light3D>();
        light.Type = type;

        if (type == LightType.Directional)
        {
            t.EulerAngles = new XnaVector3(-50f, 30f, 0f);
        }
        else
        {
            t.Position  = new XnaVector3(0f, 3f, 0f);
            light.Range = 10f;
        }

        return a;
    }

    public void Draw(Scene? scene)
    {
        if (!ImGui.Begin("Place Actors"))
        {
            ImGui.End();
            return;
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##search", "Search\u2026", ref _searchText, 128);
        string filter = _searchText;

        ImGui.Separator();

        if (scene == null)
        {
            ImGui.TextDisabled("No scene open.");
            ImGui.End();
            return;
        }

        var matches = _entries.Where(e =>
            filter.Length == 0 ||
            e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            e.Category.Contains(filter, StringComparison.OrdinalIgnoreCase));

        // A search collapses the categories away — when you have typed a filter you want
        // the hits, not the taxonomy.
        bool searching = filter.Length > 0;

        foreach (var group in matches.GroupBy(e => e.Category))
        {
            if (!searching && !ImGui.CollapsingHeader(group.Key, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            if (searching) ImGui.TextDisabled(group.Key);

            foreach (var entry in group)
            {
                if (ImGui.Selectable($"  {entry.Name}"))
                    Place(scene, entry);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(entry.Tooltip);
            }

            if (searching) ImGui.Separator();
        }

        ImGui.End();
    }

    private static void Place(Scene scene, Entry entry)
    {
        var actor = entry.Build();
        scene.AddActor(actor, EditorState.SelectedLayer?.Name ?? "default");
        EditorState.SelectActor(actor);
        ConsoleLog.Add($"Placed {entry.Name}", LogLevel.Info);
    }
}
