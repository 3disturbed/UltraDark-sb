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
    public void ASceneIsInspectableWithoutBeingTicked()
    {
        // A tool that builds a scene and then reads it — an editor outliner, a level
        // validator, an asset cooker — never calls Update. Layer.AddActor only queues, so
        // without an explicit flush every actor is invisible to everything except the
        // component registries, and the scene renders correctly while appearing empty.
        var scene = SceneTemplates.CreateDefault3D();

        Assert.Empty(scene.Layers.SelectMany(l => l.Actors));

        scene.FlushPendingActors();

        try
        {
            var actors = scene.Layers.SelectMany(l => l.Actors).ToList();
            Assert.Contains(actors, a => a.Name == "Floor");
            Assert.Contains(actors, a => a.Name == "Cube");
            Assert.Contains(actors, a => a.Name == "Sky");
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Fact]
    public void FlushingIsIdempotentAndPicksUpLaterSpawns()
    {
        var scene = new Engine.Core.Scene("Flush");
        scene.AddActor(new Actor("First"));
        scene.FlushPendingActors();
        scene.FlushPendingActors();   // must not duplicate

        Assert.Single(scene.Layers.SelectMany(l => l.Actors));

        scene.AddActor(new Actor("Second"));
        scene.FlushPendingActors();

        Assert.Equal(2, scene.Layers.SelectMany(l => l.Actors).Count());

        // A queued destroy is applied by the same flush.
        scene.FindByName("First")!.Destroy();
        scene.FlushPendingActors();

        Assert.DoesNotContain(scene.Layers.SelectMany(l => l.Actors), a => a.Name == "First");
        scene.Destroy();
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

/// <summary>
/// Covers the components added to close the gap with Unreal's common set.
/// </summary>
public class ComponentCoverageTests
{
    [Fact]
    public void TheAddComponentListIsNotEmpty()
    {
        // It was. Assembly.GetTypes throws when any one type's dependencies are missing —
        // the engine's optional Steamworks reference guarantees it — and the editor's
        // guard discarded the whole assembly rather than keeping what had loaded.
        var components = ReflectionUtil.FindComponentTypes().ToList();

        Assert.True(components.Count > 40,
            $"expected the engine's full component set, found {components.Count}");

        Assert.Contains(components, t => t == typeof(MeshRenderer));
        Assert.Contains(components, t => t == typeof(Light3D));

        // Transform3D is addable — unlike the 2D Transform it is not automatic.
        Assert.Contains(components, t => t == typeof(Transform3D));
        Assert.DoesNotContain(components, t => t == typeof(Transform));
    }

    [Theory]
    [InlineData(typeof(SpringArm))]
    [InlineData(typeof(ProjectileMovement))]
    [InlineData(typeof(RotatingMovement))]
    [InlineData(typeof(FloatingPawnMovement))]
    [InlineData(typeof(Spline))]
    [InlineData(typeof(SplineFollower))]
    [InlineData(typeof(InstancedMeshRenderer))]
    [InlineData(typeof(TextRenderer3D))]
    [InlineData(typeof(Decal3D))]
    [InlineData(typeof(SkyLight))]
    [InlineData(typeof(AudioListener3D))]
    [InlineData(typeof(CameraShake))]
    public void EachUnrealEquivalentIsPresentAndAttachable(Type componentType)
    {
        var actor = new Actor("Host");
        var component = actor.AddComponentByType(componentType);

        Assert.NotNull(component);
        Assert.Same(actor, component.Actor);
        Assert.Contains(ReflectionUtil.FindComponentTypes(), t => t == componentType);
    }

    [Fact]
    public void APrimitiveKnowsItsBoundsBeforeItHasEverDrawn()
    {
        // Culling and picking both read bounds against a scene that may not have rendered
        // yet. A plane still carrying the default unit cube, scaled up for a floor,
        // becomes a box that contains the whole level and swallows every ray.
        var actor = new Actor("Floor");
        actor.AddComponent<Transform3D>().LocalScale = new Vector3(30f, 1f, 30f);

        var mesh = actor.AddComponent<MeshRenderer>();
        mesh.MeshType = MeshPrimitive.Plane;

        Assert.Equal(0f, mesh.LocalBounds.Extents.Y, 4);
        Assert.Equal(0f, mesh.WorldBounds.Extents.Y, 4);
        Assert.Equal(15f, mesh.WorldBounds.Extents.X, 3);
    }

    [Fact]
    public void ASplineTravelsAtAConstantSpeed()
    {
        // Equal steps in the curve parameter cover unequal ground, so anything moving by
        // parameter speeds up on straights and crawls round corners.
        var actor = new Actor("Path");
        var spline = actor.AddComponent<Spline>();
        spline.Points.AddRange(new[]
        {
            new Vector3(0, 0, 0), new Vector3(5, 0, 0),
            new Vector3(5, 0, 5), new Vector3(0, 0, 5),
        });
        spline.Rebuild();

        Assert.True(spline.Length > 14f, $"expected roughly 15 units, measured {spline.Length}");

        // Sample at even distances; each hop should cover roughly the same ground.
        const int steps = 20;
        float previousHop = -1f;
        var previous = spline.GetPointAtDistance(0f);

        for (int i = 1; i <= steps; i++)
        {
            var point = spline.GetPointAtDistance(spline.Length * i / steps);
            float hop = Vector3.Distance(previous, point);

            if (previousHop > 0f)
                Assert.True(MathF.Abs(hop - previousHop) < 0.25f,
                    $"step {i} covered {hop:F3} after {previousHop:F3} — not constant speed");

            previousHop = hop;
            previous = point;
        }
    }

    [Fact]
    public void ASplinePassesThroughItsControlPoints()
    {
        // Catmull-Rom rather than Bezier precisely so a waypoint is where you put it.
        var actor = new Actor("Path");
        var spline = actor.AddComponent<Spline>();
        var waypoint = new Vector3(4f, 1f, -2f);

        spline.Points.AddRange(new[] { Vector3.Zero, waypoint, new Vector3(8f, 0f, 0f) });
        spline.Rebuild();

        // The middle control point sits at t = 0.5 on a three-point open spline.
        var onCurve = spline.Evaluate(0.5f);
        Assert.Equal(waypoint.X, onCurve.X, 3);
        Assert.Equal(waypoint.Y, onCurve.Y, 3);
        Assert.Equal(waypoint.Z, onCurve.Z, 3);
    }

    [Fact]
    public void ProjectileMovementFallsUnderGravity()
    {
        var actor = new Actor("Shell");
        actor.AddComponent<Transform3D>();

        var move = actor.AddComponent<ProjectileMovement>();
        move.Velocity = new Vector3(10f, 0f, 0f);
        move.Start();

        for (int i = 0; i < 30; i++) move.Update(1f / 60f);

        var position = actor.GetComponent<Transform3D>()!.Position;
        Assert.True(position.X > 4f, "should have travelled forward");
        Assert.True(position.Y < -0.5f, $"should have fallen, but Y is {position.Y}");
    }

    [Fact]
    public void RotatingMovementAccumulatesRotation()
    {
        var actor = new Actor("Fan");
        var transform = actor.AddComponent<Transform3D>();

        var spin = actor.AddComponent<RotatingMovement>();
        spin.RotationRate = new Vector3(0f, 90f, 0f);
        spin.Start();

        // Half a second at 90 deg/s is 45 degrees.
        for (int i = 0; i < 30; i++) spin.Update(1f / 60f);

        Assert.Equal(45f, transform.EulerAngles.Y, 0);
    }
}

/// <summary>
/// Covers the snapshot/restore cycle the editor's Play and Stop buttons rely on.
/// </summary>
/// <remarks>
/// Stop used to deserialise the snapshot and then discard it, calling CreateScene with
/// only its name — which makes a new empty scene. Pressing Stop wiped the level.
/// </remarks>
public class PlayModeSnapshotTests
{
    [Fact]
    public void ASnapshotRestoresEveryActorAndItsMaterials()
    {
        var original = SceneTemplates.CreateDefault3D("Level");
        original.FlushPendingActors();

        string snapshot = SceneSerializer.Serialize(original);

        // Simulate play mode wrecking the scene.
        original.FindByName("Cube")!.Destroy();
        original.FlushPendingActors();
        Assert.Null(original.FindByName("Cube"));
        original.Destroy();

        var restored = SceneSerializer.Deserialize(snapshot);
        restored.FlushPendingActors();

        try
        {
            var cube = restored.FindByName("Cube");
            Assert.NotNull(cube);

            var mesh = cube!.GetComponent<MeshRenderer>();
            Assert.NotNull(mesh);
            Assert.Equal(MeshPrimitive.Cube, mesh!.MeshType);

            // Materials are a List<Material3D>. Collections used to be skipped wholesale,
            // so a restored scene came back with every object untextured.
            Assert.Single(mesh.Materials);
            Assert.Equal(new Color(214, 92, 76), mesh.Materials[0].AlbedoColor);
        }
        finally
        {
            restored.Destroy();
        }
    }

    [Fact]
    public void MaterialAssetPathsSurviveASnapshot()
    {
        var scene = new Engine.Core.Scene("Textured");
        var actor = scene.AddActor(new Actor("Crate"));
        actor.AddComponent<Transform3D>();

        var mesh = actor.AddComponent<MeshRenderer>();
        mesh.MeshType = MeshPrimitive.Cube;
        mesh.Materials.Add(new Material3D
        {
            AlbedoMapPath = "Assets/crate_albedo.png",
            NormalMapPath = "Assets/crate_normal.png",
            Roughness     = 0.35f,
            Metallic      = 0.8f,
        });
        scene.FlushPendingActors();

        var restored = SceneSerializer.Deserialize(SceneSerializer.Serialize(scene));
        restored.FlushPendingActors();

        try
        {
            var material = restored.FindByName("Crate")!.GetComponent<MeshRenderer>()!.Materials.Single();

            // The texture itself is a GPU handle and cannot round-trip; the path can, and
            // is what lets a loader rebuild it.
            Assert.Equal("Assets/crate_albedo.png", material.AlbedoMapPath);
            Assert.Equal("Assets/crate_normal.png", material.NormalMapPath);
            Assert.Equal(0.35f, material.Roughness, 4);
            Assert.Equal(0.8f, material.Metallic, 4);
        }
        finally
        {
            scene.Destroy();
            restored.Destroy();
        }
    }

    [Fact]
    public void AListOfVectorsSurvivesASnapshot()
    {
        // Spline.Points is a get-only List<Vector3>: excluded twice over before, once for
        // being a collection and once for having no setter.
        var scene = new Engine.Core.Scene("Path");
        var actor = scene.AddActor(new Actor("Route"));
        var spline = actor.AddComponent<Spline>();
        spline.Points.AddRange(new[] { Vector3.Zero, new Vector3(3, 0, 0), new Vector3(3, 0, 4) });
        spline.LoopMode = SplineLoopMode.Loop;
        scene.FlushPendingActors();

        var restored = SceneSerializer.Deserialize(SceneSerializer.Serialize(scene));
        restored.FlushPendingActors();

        try
        {
            var copy = restored.FindByName("Route")!.GetComponent<Spline>();
            Assert.NotNull(copy);
            Assert.Equal(3, copy!.Points.Count);
            Assert.Equal(new Vector3(3, 0, 4), copy.Points[2]);
            Assert.Equal(SplineLoopMode.Loop, copy.LoopMode);
        }
        finally
        {
            scene.Destroy();
            restored.Destroy();
        }
    }

    [Fact]
    public void RestoringTwiceInARowIsStable()
    {
        // Play, stop, play, stop. Each cycle must produce the same scene, not accumulate
        // or lose content.
        var scene = SceneTemplates.CreateDefault3D("Cycle");
        scene.FlushPendingActors();

        int Count(Engine.Core.Scene s) => s.Layers.SelectMany(l => l.Actors).Count();
        int expected = Count(scene);

        for (int cycle = 0; cycle < 3; cycle++)
        {
            string snapshot = SceneSerializer.Serialize(scene);
            scene.Destroy();

            scene = SceneSerializer.Deserialize(snapshot);
            scene.FlushPendingActors();

            Assert.Equal(expected, Count(scene));
        }

        scene.Destroy();
    }
}
