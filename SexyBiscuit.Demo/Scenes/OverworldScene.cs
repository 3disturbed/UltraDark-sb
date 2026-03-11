using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Demo.Actors;
using SexyBiscuit.Demo.Systems;
using SexyBiscuit.Demo.UI;

namespace SexyBiscuit.Demo.Scenes;

/// <summary>
/// Code-first loader for the Overworld scene.
/// Places the player, wild pets, lighting, HUD and camera.
/// </summary>
public static class OverworldScene
{
    // Species definitions: (name, element, level range)
    private static readonly (string Name, string Species, PetElement Element, int MinLevel, int MaxLevel)[] SpeciesDefs =
    {
        ("Embear",   "Embear",   PetElement.Fire,  2, 5),
        ("Aquafin",  "Aquafin",  PetElement.Water, 2, 5),
        ("Leaftoad", "Leaftoad", PetElement.Grass,  2, 5),
    };

    public static void Load(SceneManager sm)
    {
        var scene = sm.CreateScene("Overworld");

        // -----------------------------------------------------------------
        // Player
        // -----------------------------------------------------------------
        var player = new PlayerActor();
        player.Transform.Position = Vector2.Zero;
        scene.AddActor(player);

        // Give the player a starter party pet if they have none
        if (PartyManager.Party.Count == 0)
        {
            var starter = new PetActor("Embear", "Embear", PetElement.Fire, 5);
            starter.LearnMove(new PetMove("Ember",    PetElement.Fire,  40, 5, 0.95f));
            starter.LearnMove(new PetMove("Scratch",  PetElement.Normal, 35, 0, 1.00f));
            PartyManager.AddToParty(starter);
        }

        // -----------------------------------------------------------------
        // Wild pets — 5 actors at random positions within ±20 units
        // -----------------------------------------------------------------
        for (int i = 0; i < 5; i++)
        {
            var def     = SpeciesDefs[i % SpeciesDefs.Length];
            int level   = def.MinLevel + Random.Shared.Next(def.MaxLevel - def.MinLevel + 1);
            var petData = CreateWildPet(def.Name, def.Species, def.Element, level);

            var wildActor = new WildPetActor(petData);
            wildActor.Transform.Position = new Vector2(
                (Random.Shared.NextSingle() * 2f - 1f) * 20f,
                (Random.Shared.NextSingle() * 2f - 1f) * 20f);

            scene.AddActor(wildActor);
        }

        // -----------------------------------------------------------------
        // Directional light (3D scene lighting)
        // -----------------------------------------------------------------
        var lightActor = new Actor("DirectionalLight");
        lightActor.Tag = "Light";
        // Orient Transform3D so that Forward points in the desired light direction
        var lightT3d = lightActor.AddComponent<Transform3D>();
        lightT3d.LookAt(new Vector3(-0.5f, -1f, -0.5f));
        var light = lightActor.AddComponent<Light3D>();
        light.Type      = LightType.Directional;
        light.Color     = Color.White;
        light.Intensity = 1f;
        scene.AddActor(lightActor);

        // -----------------------------------------------------------------
        // Overworld HUD
        // -----------------------------------------------------------------
        var hud = new OverworldHUD(player);
        scene.AddActor(hud, "ui");

        // -----------------------------------------------------------------
        // Camera — 3D orbit camera that follows the player
        // -----------------------------------------------------------------
        var cameraActor = new Actor("MainCamera");
        cameraActor.Tag = "MainCamera3D";
        var t3d = cameraActor.AddComponent<Transform3D>();
        t3d.Position = new Vector3(0f, 8f, 12f);

        var cam3d = cameraActor.AddComponent<Camera3D>();
        cam3d.FieldOfView = 60f;
        cam3d.NearClip    = 0.1f;
        cam3d.FarClip     = 500f;

        scene.AddActor(cameraActor);

        // Orbit camera update happens via OverworldCameraController
        var camController = cameraActor.AddComponent<OverworldCameraController>();
        camController.Target = player;
    }

    // -------------------------------------------------------------------------
    // Wild pet factory
    // -------------------------------------------------------------------------
    private static PetActor CreateWildPet(string name, string species, PetElement element, int level)
    {
        var pet = new PetActor(name, species, element, level) { IsWild = true };

        // Assign species-appropriate moves
        switch (element)
        {
            case PetElement.Fire:
                pet.LearnMove(new PetMove("Ember",        PetElement.Fire,  40, 5, 0.95f));
                pet.LearnMove(new PetMove("Tackle",       PetElement.Normal, 35, 0, 1.00f));
                break;
            case PetElement.Water:
                pet.LearnMove(new PetMove("Water Gun",    PetElement.Water, 40, 5, 1.00f));
                pet.LearnMove(new PetMove("Tackle",       PetElement.Normal, 35, 0, 1.00f));
                break;
            case PetElement.Grass:
                pet.LearnMove(new PetMove("Vine Whip",    PetElement.Grass,  45, 5, 1.00f));
                pet.LearnMove(new PetMove("Tackle",       PetElement.Normal, 35, 0, 1.00f));
                break;
            default:
                pet.LearnMove(new PetMove("Tackle",       PetElement.Normal, 35, 0, 1.00f));
                break;
        }

        return pet;
    }
}

// ---------------------------------------------------------------------------
// Orbit camera controller component
// ---------------------------------------------------------------------------

/// <summary>
/// Simple 3D follow-camera that orbits behind and above a target actor.
/// </summary>
public class OverworldCameraController : SexyBiscuit.Engine.Core.Component
{
    public Actor?  Target      { get; set; }
    public float   Distance    { get; set; } = 12f;
    public float   HeightOffset { get; set; } = 8f;
    public float   LerpSpeed   { get; set; } = 5f;

    public override void LateUpdate(float dt)
    {
        if (Target == null) return;

        var targetPos2d = Target.Transform.Position;
        var desiredPos  = new Vector3(targetPos2d.X, HeightOffset, targetPos2d.Y + Distance);

        var t3d = Actor.GetComponent<Transform3D>();
        if (t3d == null) return;

        // Exponential lerp for smooth follow
        float lerpT = 1f - MathF.Exp(-LerpSpeed * dt);
        t3d.Position = Vector3.Lerp(t3d.Position, desiredPos, lerpT);

        // Always look at the target
        t3d.LookAt(new Vector3(targetPos2d.X, 0f, targetPos2d.Y));
    }
}
