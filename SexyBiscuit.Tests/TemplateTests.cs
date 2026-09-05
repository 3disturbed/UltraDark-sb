using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.Scene;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Loads every shipped project template through the real serializer.
/// </summary>
/// <remarks>
/// Every one of these files used to load with zero components: the format they were
/// written in did not match what the serializer reads, and unknown properties were
/// skipped in silence, so nothing complained. A template that silently produces bare
/// actors is worse than one that fails loudly, and only an end-to-end load catches it.
/// </remarks>
public class ProjectTemplateTests
{
    /// <summary>Walks up from the test binary to the repository root.</summary>
    private static string TemplatesRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Templates")))
                dir = dir.Parent;

            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "Templates");
        }
    }

    public static TheoryData<string> AllTemplateScenes
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var file in Directory
                .EnumerateFiles(TemplatesRoot, "*.scene", SearchOption.AllDirectories)
                .OrderBy(f => f))
            {
                data.Add(Path.GetRelativePath(TemplatesRoot, file));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllTemplateScenes))]
    public void EveryTemplateSceneLoadsWithItsComponents(string relativePath)
    {
        var scene = SceneSerializer.LoadFromFile(Path.Combine(TemplatesRoot, relativePath));
        scene.Update(0.016f);

        try
        {
            var actors = scene.Layers.SelectMany(l => l.Actors).ToList();
            Assert.NotEmpty(actors);

            // Every actor carries a Transform; anything beyond that is real content.
            int components = actors.Sum(a => a.GetAllComponents().Count - 1);
            Assert.True(components > 0,
                $"{relativePath} loaded {actors.Count} actors but no components — " +
                "the file's component types or property names do not match the engine.");
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Fact]
    public void EveryTemplateSceneTearsDownWithoutThrowing()
    {
        // Physics colliders detach from bodies on destroy, and component destroy order
        // follows the file's component order — a scene listing Rigidbody2D before its
        // colliders used to take Aether down with a null reference.
        foreach (var relative in Directory
            .EnumerateFiles(TemplatesRoot, "*.scene", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(TemplatesRoot, f)))
        {
            var scene = SceneSerializer.LoadFromFile(Path.Combine(TemplatesRoot, relative));
            scene.Update(0.016f);
            scene.Destroy();
        }
    }
}

public class SceneTemplateTests
{
    [Fact]
    public void TheDefault3DSceneHasEverythingNeededToSeeSomething()
    {
        var scene = SceneTemplates.CreateDefault3D();
        scene.Update(0.016f);

        try
        {
            var actors = scene.Layers.SelectMany(l => l.Actors).ToList();

            Assert.Contains(actors, a => a.GetComponent<Skybox>() != null);
            Assert.Contains(actors, a => a.GetComponent<MeshRenderer>() != null);
            Assert.Contains(actors, a => a.GetComponent<PlayerStart>() != null);
            Assert.Contains(actors, a => a is GameMode);

            // A scene lit by one light reads as flat silhouettes on the unlit side.
            var lights = actors.Select(a => a.GetComponent<Light3D>()).Where(l => l != null).ToList();
            Assert.True(lights.Count >= 2, "expected a key light and a fill light");
            Assert.Contains(lights, l => l!.CastsShadows);
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Fact]
    public void TheDefaultSceneRoundTripsThroughTheSerializer()
    {
        var original = SceneTemplates.CreateDefault3D("Round Trip");
        original.Update(0.016f);

        var restored = SceneSerializer.Deserialize(SceneSerializer.Serialize(original));
        restored.Update(0.016f);

        try
        {
            var floor = restored.FindByName("Floor");
            Assert.NotNull(floor);

            var mesh = floor!.GetComponent<MeshRenderer>();
            Assert.NotNull(mesh);
            Assert.Equal(MeshPrimitive.Plane, mesh!.MeshType);

            // Scale is the thing that makes the floor a floor rather than a tile.
            Assert.Equal(30f, floor.GetComponent<Transform3D>()!.LocalScale.X, 3);
        }
        finally
        {
            original.Destroy();
            restored.Destroy();
        }
    }

    [Fact]
    public void EnumsSerialiseAsNamesSoAFileStaysReadable()
    {
        var scene = new Engine.Core.Scene("Enums");
        var actor = scene.AddActor(new Actor("Lamp"));
        actor.AddComponent<Light3D>().Type = LightType.Spot;
        scene.Update(0.016f);

        string json = SceneSerializer.Serialize(scene);
        scene.Destroy();

        Assert.Contains("\"Spot\"", json);
    }
}

public class SceneSerializerLeniencyTests
{
    [Fact]
    public void ABareComponentTypeNameResolves()
    {
        // Nobody should have to write the assembly-qualified name to place a camera.
        const string json = """
        {
          "name": "Bare",
          "layers": [{ "name": "default", "order": 0, "actors": [{
            "name": "Cam", "tag": "Untagged", "layer": 0, "active": true,
            "position": [0, 0], "rotation": 0, "scale": [1, 1],
            "components": [{ "type": "Camera3D", "properties": { "FieldOfView": 72.0 } }]
          }]}]
        }
        """;

        var scene = SceneSerializer.Deserialize(json);
        scene.Update(0.016f);

        try
        {
            var camera = scene.FindByName("Cam")!.GetComponent<Camera3D>();
            Assert.NotNull(camera);
            Assert.Equal(72f, camera!.FieldOfView, 3);
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void TheReadableTransformObjectFormIsAccepted()
    {
        const string json = """
        {
          "name": "Readable",
          "layers": [{ "name": "default", "order": 0, "actors": [{
            "name": "Prop", "tag": "Untagged", "layer": 0, "active": true,
            "transform3d": { "x": 1, "y": 2, "z": 3, "rotY": 90, "scaleX": 2, "scaleY": 2, "scaleZ": 2 },
            "components": []
          }]}]
        }
        """;

        var scene = SceneSerializer.Deserialize(json);
        scene.Update(0.016f);

        try
        {
            var t = scene.FindByName("Prop")!.GetComponent<Transform3D>();
            Assert.NotNull(t);
            Assert.Equal(new Vector3(1, 2, 3), t!.LocalPosition);
            Assert.Equal(2f, t.LocalScale.X, 3);

            // Euler is stored as a quaternion, and reading it back at exactly 90 degrees
            // lands in the gimbal-lock region where asin loses precision — about 0.02 of
            // a degree here. That lossiness is the reason the serializer writes
            // quaternions rather than Euler, so a tolerance is the right assertion.
            Assert.Equal(90f, t.LocalEulerAngles.Y, 1);

            // What actually matters is where the object points: a 90 degree yaw maps
            // local forward onto world -X.
            Assert.Equal(-1f, t.Forward.X, 3);
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void AColourWritesAsHexAndReadsFromEitherForm()
    {
        const string json = """
        {
          "name": "Colours",
          "layers": [{ "name": "default", "order": 0, "actors": [{
            "name": "Lamp", "tag": "Untagged", "layer": 0, "active": true,
            "position": [0, 0], "rotation": 0, "scale": [1, 1],
            "components": [{ "type": "Light3D", "properties": {
              "Color": { "R": 255, "G": 128, "B": 64, "A": 255 }
            }}]
          }]}]
        }
        """;

        var scene = SceneSerializer.Deserialize(json);
        scene.Update(0.016f);

        try
        {
            var light = scene.FindByName("Lamp")!.GetComponent<Light3D>();
            Assert.NotNull(light);
            Assert.Equal(new Color(255, 128, 64, 255), light!.Color);
        }
        finally { scene.Destroy(); }
    }
}
