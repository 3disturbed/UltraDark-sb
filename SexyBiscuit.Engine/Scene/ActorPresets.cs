using SexyBiscuit.Engine.AI;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Chibi;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.UI;
using XnaVector3 = Microsoft.Xna.Framework.Vector3;

namespace SexyBiscuit.Engine.Scene;

/// <summary>One entry of the actor palette: what it is and how to build it.</summary>
public sealed record ActorPreset(string Category, string Name, string Description, Func<Actor> Build);

/// <summary>
/// The actors a scene almost always needs, as one-click presets. Shared by the editor's Place
/// Actors panel, its Create menu and the MCP <c>place_actor</c> tool, so all three offer the
/// same list.
/// </summary>
/// <remarks>
/// Building a 3D scene by hand means adding a bare actor, then a Transform3D, then a Camera3D,
/// then remembering the "MainCamera3D" tag — four steps to get anything on screen. Each preset
/// is one step.
/// </remarks>
public static class ActorPresets
{
    public static IReadOnlyList<ActorPreset> All { get; } = new List<ActorPreset>
    {
        new("Basic", "Empty Actor", "A bare actor with only a 2D transform.",
            () => new Actor("Actor")),

        new("Basic", "Empty Actor (3D)", "An actor with a Transform3D, ready for 3D components.",
            () => { var a = new Actor("Actor 3D"); a.AddComponent<Transform3D>(); return a; }),

        new("Geometry", "Mesh", "A MeshRenderer. Draws a unit cube until you load a model or set MeshType.",
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

        new("Characters", "Chibi", "A chibi character built from primitives, already playing an idle.",
            () =>
            {
                var a = new Actor("Chibi");
                a.AddComponent<Transform3D>();
                a.AddComponent<ChibiCharacter>();
                a.AddComponent<ChibiAnimator>();
                return a;
            }),

        new("Characters", "Chibi (random)", "A different coordinated character every time you place one.",
            () =>
            {
                var a = new Actor("Chibi");
                a.AddComponent<Transform3D>();
                // A fresh seed per placement: a crowd wants forty villagers, not forty of
                // the same villager, and the seed is what the scene file remembers.
                a.AddComponent<ChibiCharacter>().Seed = System.Random.Shared.Next(1, 1_000_000);
                a.AddComponent<ChibiAnimator>();
                return a;
            }),

        new("Lights", "Directional Light", "Sunlight. Direction comes from the transform's forward axis.",
            () => BuildLight(LightType.Directional, "Sun")),

        new("Lights", "Point Light", "Omnidirectional light with a range and inverse-square falloff.",
            () => BuildLight(LightType.Point, "Point Light")),

        new("Lights", "Spot Light", "A cone of light. Set SpotAngle for the cone width.",
            () => BuildLight(LightType.Spot, "Spot Light")),

        new("Lights", "2D Light", "Radial light for the 2D lighting pipeline.",
            () => { var a = new Actor("Light 2D"); a.AddComponent<Light2D>(); return a; }),

        new("Cameras", "Camera", "A Camera3D tagged MainCamera3D so the viewport and the game render through it.",
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

        new("2D", "Sprite", "A SpriteRenderer. Set TexturePath to give it an image.",
            () => { var a = new Actor("Sprite"); a.AddComponent<SpriteRenderer>(); return a; }),

        new("2D", "Tilemap", "A TilemapRenderer. Load a Tiled JSON map into it.",
            () => { var a = new Actor("Tilemap"); a.AddComponent<TilemapRenderer>(); return a; }),

        new("UI", "Canvas", "Screen-space UI root.",
            () => { var a = new Actor("Canvas"); a.AddComponent<Canvas>(); return a; }),

        new("UI", "World Canvas", "UI positioned in 3D space — nameplates, in-world screens.",
            () => { var a = new Actor("World Canvas"); a.AddComponent<Transform3D>(); a.AddComponent<WorldCanvas>(); return a; }),
    };

    /// <summary>Finds a preset by name, exactly first and then ignoring case.</summary>
    public static ActorPreset? Find(string name)
        => All.FirstOrDefault(p => p.Name == name)
        ?? All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

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
}
