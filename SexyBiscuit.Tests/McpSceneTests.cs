using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Mcp;
using SexyBiscuit.Engine.Mcp.Tools;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.Scene;
using Xunit;

namespace SexyBiscuit.Tests;

public sealed class McpBattery : Component
{
    public int Charge { get; set; } = 100;
}

[RequireComponent(typeof(McpBattery))]
public sealed class McpNeedsBattery : Component
{
}

/// <summary>
/// The scene tools wired exactly as the editor wires them: snapshot before a mutation, flush
/// after every call. Headless, so every test disposes it to clear the component registries.
/// </summary>
internal sealed class SceneToolHarness : IDisposable
{
    public HeadlessSceneHost Host     { get; }
    public SceneUndoStack    Undo     { get; }
    public McpToolRegistry   Registry { get; }

    public SceneToolHarness(Engine.Core.Scene? scene = null, string? projectRoot = null)
    {
        Host     = new HeadlessSceneHost(scene, projectRoot);
        Undo     = new SceneUndoStack(Host);
        Registry = new McpToolRegistry(InlineMcpDispatcher.Instance);

        Registry.RegisterInstance(new SceneTools(Host, Undo));
        Registry.RegisterInstance(new ActorTools(Host));
        Registry.RegisterInstance(new ComponentTools(Host));
        Registry.RegisterInstance(new MaterialTools(Host));
        Registry.RegisterInstance(new UndoTools(Undo));
        Registry.RegisterInstance(new BatchTools(Host, Registry));

        Registry.BeforeMutation = (descriptor, args, _) =>
        {
            if (!Host.IsPlaying) Undo.Snapshot(McpToolRegistry.FormatLabel(descriptor.LabelTemplate, descriptor.Name, args));
        };
        Registry.AfterInvoke = (descriptor, _, result) =>
        {
            Host.ActiveScene?.FlushPendingActors();
            if (descriptor.Mutating && !result.NoChange) Host.SceneDirty = true;
        };
    }

    public Engine.Core.Scene Scene => Host.ActiveScene!;

    public McpToolResult Call(string tool, object? args = null)
        => Registry.InvokeAsync(tool, args == null ? null : JsonSerializer.SerializeToElement(args), McpCallContext.None)
                   .GetAwaiter().GetResult();

    /// <summary>Calls a tool that must succeed and returns its structured content.</summary>
    public JsonNode Ok(string tool, object? args = null)
    {
        var result = Call(tool, args);
        Assert.False(result.IsError, result.FirstText);
        Assert.NotNull(result.StructuredContent);
        return result.StructuredContent!;
    }

    public McpToolResult Fails(string tool, object? args = null)
    {
        var result = Call(tool, args);
        Assert.True(result.IsError, "expected an error result but got: " + result.FirstText);
        return result;
    }

    public void Dispose() => Host.Dispose();
}

public class SceneToolTests
{
    private static float[] Floats(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<float>()).ToArray();

    [Fact]
    public void SpawnActorIsVisibleToGetSceneSummaryImmediately()
    {
        using var h = new SceneToolHarness();

        var view = h.Ok("spawn_actor", new { name = "Crate", components = new[] { "MeshRenderer" }, position = new[] { 1f, 2f, 3f } });
        Assert.Equal("Crate", view["name"]!.GetValue<string>());
        Assert.Equal(new[] { 1f, 2f, 3f }, Floats(view["position"]));

        var summary = h.Ok("get_scene_summary", new { format = "json" });
        var row = summary["actors"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "Crate")!;
        var components = row["components"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Contains("Transform3D", components);
        Assert.Contains("MeshRenderer", components);

        Assert.True(h.Host.SceneDirty);
        Assert.Equal("Crate", h.Host.SelectedActor?.Name);
        Assert.Equal(1, h.Undo.UndoCount);
    }

    [Fact]
    public void SpawnPrimitiveCreatesAColouredMeshAtThePosition()
    {
        using var h = new SceneToolHarness();

        h.Ok("spawn_actor", new { shape = "sphere", position = new[] { 0f, 1f, 0f }, color = "#FF0000", scale = new[] { 2f, 2f, 2f } });

        var actor = h.Scene.FindByName("Sphere")!;
        var mesh  = actor.GetComponent<MeshRenderer>()!;
        Assert.Equal(MeshPrimitive.Sphere, mesh.MeshType);
        Assert.Equal(Color.Red, mesh.Materials[0].AlbedoColor);
        Assert.NotSame(Material3D.Default, mesh.Materials[0]);
        Assert.Equal(new Vector3(0f, 1f, 0f), actor.GetComponent<Transform3D>()!.Position);
        Assert.Equal(new Vector3(2f, 2f, 2f), actor.GetComponent<Transform3D>()!.Scale);

        // Not "Torus": that was this test's example of a shape the engine has not got
        // until MakeChibi added it, along with Capsule. Anything named here has to be a
        // shape MeshPrimitive genuinely lacks, or the test passes for the wrong reason.
        var bad = h.Fails("spawn_actor", new { shape = "Dodecahedron" });
        Assert.Contains("Cube, Sphere", bad.FirstText);
    }

    public static TheoryData<string> PresetNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var preset in ActorPresets.All) data.Add(preset.Name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(PresetNames))]
    public void SpawnActorBuildsEveryPreset(string preset)
    {
        using var h = new SceneToolHarness();

        var view = h.Ok("spawn_actor", new { preset });
        string name = view["name"]!.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(name));

        // Not Single: a Game Mode spawns its state, controller and pawn the moment it starts.
        Assert.Contains(h.Scene.Layers.SelectMany(l => l.Actors), a => a.Name == name);
    }

    [Fact]
    public void A3DPositionOnA2DPresetIsAClearError()
    {
        using var h = new SceneToolHarness();

        var result = h.Fails("spawn_actor", new { preset = "Sprite", position = new[] { 1f, 1f, 1f } });
        Assert.Contains("no Transform3D", result.FirstText);

        h.Ok("spawn_actor", new { preset = "Sprite", position = new[] { 10f, 20f } });
        Assert.Equal(new Vector2(10f, 20f), h.Scene.FindByName("Sprite")!.Transform.Position);
    }

    [Fact]
    public void SpawnActorListsThePresetsAndTheComponentsEachOneCreates()
    {
        using var h = new SceneToolHarness();
        int renderersBefore = MeshRenderer.All.Count;

        var list = h.Ok("spawn_actor", new { list = true }).AsObject()["value"]!.AsArray();
        var camera = list.Single(p => p!["name"]!.GetValue<string>() == "Camera")!;
        Assert.Contains("Camera3D", camera["components"]!.AsArray().Select(c => c!.GetValue<string>()));

        // Building the samples must not leave them in the renderer registry.
        Assert.Equal(renderersBefore, MeshRenderer.All.Count);
    }

    [Fact]
    public void ActorRefsResolveByIdThenByExactThenByCaseInsensitiveName()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { name = "Cube" });
        h.Ok("spawn_actor", new { name = "cube" });
        var lower = h.Scene.FindByName("cube")!;

        Assert.Equal("Cube", h.Ok("get_actor", new { actor = "Cube" })["name"]!.GetValue<string>());
        Assert.Equal("cube", h.Ok("get_actor", new { actor = lower.Id.ToString() })["name"]!.GetValue<string>());
        Assert.Equal("cube", h.Ok("get_actor", new { actor = "#" + lower.Id })["name"]!.GetValue<string>());

        // "CUBE" matches both only loosely, so it is ambiguous rather than a guess.
        var ambiguous = h.Fails("get_actor", new { actor = "CUBE" });
        Assert.Contains("2 actors", ambiguous.FirstText);

        var missing = h.Fails("get_actor", new { actor = "Sphere" });
        Assert.Contains("get_scene_summary", missing.FirstText);
    }

    [Fact]
    public void AnAmbiguousNameIsReportedWithCandidateIds()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { name = "Cube" });
        h.Ok("spawn_actor", new { name = "Cube" });
        var ids = h.Scene.Layers.SelectMany(l => l.Actors).Select(a => a.Id).ToList();

        var result = h.Fails("destroy_actor", new { actor = "Cube" });
        foreach (var id in ids) Assert.Contains(id.ToString(), result.FirstText);
        Assert.Equal(2, h.Scene.Layers.SelectMany(l => l.Actors).Count());
    }

    [Fact]
    public void SetPropertiesConvertsColoursEnumsVectorsAndRotations()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { preset = "Point Light", name = "Lamp" });
        var light = h.Scene.FindByName("Lamp")!;

        h.Ok("set_properties", new { actor = "Lamp", properties = new Dictionary<string, object> { ["Light3D.Color"] = "#00FF00" } });
        Assert.Equal(new Color(0, 255, 0), light.GetComponent<Light3D>()!.Color);

        // Type and property names are matched ignoring case, as they were one at a time.
        h.Ok("set_properties", new { actor = "Lamp", properties = new Dictionary<string, object> { ["light3d.type"] = "spot" } });
        Assert.Equal(LightType.Spot, light.GetComponent<Light3D>()!.Type);

        h.Ok("set_properties", new { actor = "Lamp", properties = new Dictionary<string, object> { ["Transform3D.Position"] = new { x = 1, y = 2, z = 3 } } });
        Assert.Equal(new Vector3(1f, 2f, 3f), light.GetComponent<Transform3D>()!.Position);

        h.Ok("set_properties", new { actor = "Lamp", properties = new Dictionary<string, object> { ["Transform3D.EulerAngles"] = new[] { 0f, 45f, 0f } } });
        var rotated = h.Ok("get_actor", new { actor = "Lamp", property = "Transform3D.EulerAngles" });
        Assert.Equal(45f, Floats(rotated["value"])[1], 2);

        h.Ok("set_properties", new { actor = "Lamp", properties = new Dictionary<string, object> { ["Actor.Tag"] = "Lighting" } });
        Assert.Equal("Lighting", light.Tag);
        Assert.Equal("Lighting", h.Ok("get_actor", new { actor = "Lamp", property = "Actor.Tag" })["value"]!.GetValue<string>());
    }

    [Fact]
    public void SetPropertiesRejectsAnUnknownPropertyWithSuggestions()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { preset = "Point Light", name = "Lamp" });

        var result = h.Fails("set_properties", new { actor = "Lamp", properties = new Dictionary<string, object> { ["Light3D.Intensty"] = 2 } });
        Assert.Contains("Intensity", result.FirstText);

        var wrongType = h.Fails("set_properties", new { actor = "Lamp", properties = new Dictionary<string, object> { ["MeshRender.MeshType"] = "Cube" } });
        Assert.Contains("It has:", wrongType.FirstText);

        // get_actor reads one back, and says the same thing when the name is wrong.
        Assert.Contains("Intensity", h.Fails("get_actor", new { actor = "Lamp", property = "Light3D.Intensty" }).FirstText);
    }

    [Fact]
    public void SetPropertiesAppliesTheValidEntriesAndReportsTheRest()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { preset = "Point Light", name = "Lamp" });

        var result = h.Call("set_properties", new
        {
            actor = "Lamp",
            properties = new Dictionary<string, object> { ["Light3D.Intensity"] = 2.5, ["Light3D.Nope"] = 1, ["Transform3D.Position"] = new[] { 0, 3, 0 } },
        });

        Assert.True(result.IsError);
        var content = result.StructuredContent!;
        Assert.Equal(2, content["applied"]!.AsArray().Count);
        Assert.Single(content["failed"]!.AsArray());
        Assert.Equal(2.5f, h.Scene.FindByName("Lamp")!.GetComponent<Light3D>()!.Intensity, 4);
    }

    [Fact]
    public void AddComponentHonoursRequireComponent()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { name = "Robot" });

        var view = h.Ok("add_component", new { actor = "Robot", componentType = "McpNeedsBattery" });
        Assert.Contains("McpBattery", view["alsoAdded"]!.AsArray().Select(n => n!.GetValue<string>()));

        var robot = h.Scene.FindByName("Robot")!;
        Assert.True(robot.HasComponent<McpBattery>());
        Assert.True(robot.HasComponent<McpNeedsBattery>());

        // Initial values by plain property name.
        h.Ok("add_component", new { actor = "Robot", componentType = "Light3D", properties = new { Intensity = 3 } });
        Assert.Equal(3f, robot.GetComponent<Light3D>()!.Intensity, 4);

        var unknown = h.Fails("add_component", new { actor = "Robot", componentType = "MeshRender" });
        Assert.Contains("MeshRenderer", unknown.FirstText);
    }

    [Fact]
    public void RemoveComponentRefusesTheTransform()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { name = "Thing", components = new[] { "Light3D" } });

        Assert.Contains("cannot be removed", h.Fails("remove_component", new { actor = "Thing", componentType = "Transform" }).FirstText);

        h.Ok("remove_component", new { actor = "Thing", componentType = "Light3D" });
        Assert.False(h.Scene.FindByName("Thing")!.HasComponent<Light3D>());
    }

    [Fact]
    public void DuplicateActorCopiesComponentsAndOffsetsThePosition()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { shape = "Cube", color = "#FF0000" });

        var copy = h.Ok("duplicate_actor", new { actor = "Cube", offset = new[] { 2f, 0f, 0f } });
        Assert.Equal("Cube (copy)", copy["name"]!.GetValue<string>());

        var actor = h.Scene.FindByName("Cube (copy)")!;
        Assert.Equal(new Vector3(2f, 0f, 0f), actor.GetComponent<Transform3D>()!.Position);
        Assert.Equal(Color.Red, actor.GetComponent<MeshRenderer>()!.Materials[0].AlbedoColor);
        Assert.Equal(2, h.Scene.Layers.SelectMany(l => l.Actors).Count());
    }

    [Fact]
    public void MoveToLayerKeepsTheActorAndItsComponentsAlive()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { shape = "Cube" });
        var mesh = h.Scene.FindByName("Cube")!.GetComponent<MeshRenderer>();

        var row = h.Ok("set_actor", new { actor = "Cube", layer = "props", layerOrder = 10 });
        Assert.Equal("props", row["layer"]!.GetValue<string>());

        var actor = h.Scene.FindByName("Cube")!;
        Assert.False(actor.IsDestroyed);
        Assert.Same(mesh, actor.GetComponent<MeshRenderer>());
        Assert.Contains(mesh!, MeshRenderer.All);
    }

    [Fact]
    public void DestroyActorRemovesItAfterTheCall()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { name = "Doomed" });

        h.Ok("destroy_actor", new { actor = "Doomed" });

        Assert.Null(h.Scene.FindByName("Doomed"));
        Assert.Null(h.Host.SelectedActor);
        Assert.Empty(h.Ok("find_actors", new { nameContains = "Doom" }).AsObject()["value"]!.AsArray());
    }

    [Fact]
    public void SetMaterialDoesNotMutateTheSharedDefaultMaterial()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { name = "Wall", components = new[] { "MeshRenderer" } });

        var view = h.Ok("set_material", new { actor = "Wall", albedoColor = "#0000FF", roughness = 0.1 });
        Assert.Equal("#0000FFFF", view["albedoColor"]!.GetValue<string>());

        var mesh = h.Scene.FindByName("Wall")!.GetComponent<MeshRenderer>()!;
        Assert.Equal(Color.Blue, mesh.Materials[0].AlbedoColor);
        Assert.Equal(Color.White, Material3D.Default.AlbedoColor);
        Assert.Equal(0.5f, Material3D.Default.Roughness, 4);
    }

    [Fact]
    public void SaveSceneWritesShortTypeNamesAndRemembersThePath()
    {
        string root = Path.Combine(Path.GetTempPath(), "sb-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var h = new SceneToolHarness(projectRoot: root);
            h.Ok("spawn_actor", new { shape = "Cube" });

            var saved = h.Ok("save_scene", new { path = "Scenes/Test.scene" });
            Assert.Equal("Scenes/Test.scene", saved["path"]!.GetValue<string>());
            Assert.Equal("Scenes/Test.scene", h.Host.CurrentScenePath);
            Assert.False(h.Host.SceneDirty);

            string json = File.ReadAllText(Path.Combine(root, "Scenes", "Test.scene"));
            Assert.Contains("\"type\": \"MeshRenderer\"", json);
            Assert.DoesNotContain("SexyBiscuit.Engine.Rendering.MeshRenderer,", json);

            // No path means "where it was last saved"; no extension means .scene.
            h.Ok("spawn_actor", new { shape = "Sphere" });
            Assert.Equal("Scenes/Test.scene", h.Ok("save_scene")["path"]!.GetValue<string>());
            Assert.Equal("Scenes/Other.scene", h.Ok("save_scene", new { path = "Scenes/Other" })["path"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveSceneRefusesAPathOutsideTheProjectRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "sb-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var h = new SceneToolHarness(projectRoot: root);
            var result = h.Fails("save_scene", new { path = "../escape.scene" });
            Assert.Contains("outside the project root", result.FirstText);
            Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "escape.scene")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadSceneReportsUnresolvedTypesAsWarnings()
    {
        string root = Path.Combine(Path.GetTempPath(), "sb-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Scenes"));
        File.WriteAllText(Path.Combine(root, "Scenes", "Odd.scene"), """
        {
          "name": "Odd",
          "layers": [{ "name": "default", "order": 0, "actors": [{
            "name": "Widget",
            "components": [{ "type": "Frobnicator", "properties": { "Speed": 3 } }, { "type": "Camera3D", "properties": {} }]
          }]}]
        }
        """);

        try
        {
            using var h = new SceneToolHarness(projectRoot: root);
            var result = h.Call("load_scene", new { path = "Scenes/Odd" });

            Assert.False(result.IsError, result.FirstText);
            Assert.Contains("Frobnicator", result.StructuredContent!["warnings"]!.ToJsonString());
            Assert.Equal("Scenes/Odd.scene", h.Host.CurrentScenePath);
            Assert.NotNull(h.Scene.FindByName("Widget")!.GetComponent<Camera3D>());

            Assert.Contains("No scene file", h.Fails("load_scene", new { path = "Scenes/Nowhere" }).FirstText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LookAtPointsTheForwardAxisAtTheTarget()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { name = "Eye", position = new[] { 0f, 0f, 0f } });

        var ahead = h.Ok("set_transform", new { actor = "Eye", lookAt = new[] { 0f, 0f, -5f } });
        var forward = Floats(ahead["transform3d"]!["forward"]);
        Assert.Equal(-1f, forward[2], 3);

        h.Ok("spawn_actor", new { name = "Mark", position = new[] { 5f, 0f, 0f } });
        var aside = h.Ok("set_transform", new { actor = "Eye", lookAtActor = "Mark" });
        forward = Floats(aside["transform3d"]!["forward"]);
        Assert.Equal(1f, forward[0], 3);
    }

    [Fact]
    public void TranslateAndRotateComposeWithTheCurrentTransform()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { name = "Mover", position = new[] { 1f, 0f, 0f } });

        var moved = h.Ok("set_transform", new { actor = "Mover", position = new[] { 1f, 2f, 3f }, relative = true });
        Assert.Equal(new[] { 2f, 2f, 3f }, Floats(moved["transform3d"]!["position"]));

        h.Ok("set_transform", new { actor = "Mover", rotation = new[] { 0f, 30f, 0f }, relative = true });
        var again = h.Ok("set_transform", new { actor = "Mover", rotation = new[] { 0f, 15f, 0f }, relative = true });
        Assert.Equal(45f, Floats(again["transform3d"]!["rotation"])[1], 2);

        // Local translation follows the rotated axes: +Z local is now off the world Z axis.
        var local = h.Ok("set_transform", new { actor = "Mover", position = new[] { 0f, 0f, 1f }, relative = true, space = "local" });
        Assert.NotEqual(3f + 1f, Floats(local["transform3d"]!["position"])[2], 2);

        // Without relative the same argument replaces the position outright.
        var absolute = h.Ok("set_transform", new { actor = "Mover", position = new[] { 0f, 0f, 0f } });
        Assert.Equal(new[] { 0f, 0f, 0f }, Floats(absolute["transform3d"]!["position"]));
    }

    [Fact]
    public void NewSceneUsesTheTemplateAndUndoRestoresThePreviousOne()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { name = "Keep" });

        var created = h.Call("new_scene", new { name = "Fresh", template = "empty" });
        Assert.False(created.IsError, created.FirstText);
        Assert.Contains("0 actors", created.FirstText);
        Assert.Equal("Fresh", h.Scene.Name);
        Assert.Null(h.Host.CurrentScenePath);

        var undone = h.Ok("undo");
        Assert.Single(undone["undone"]!.AsArray());
        Assert.NotNull(h.Scene.FindByName("Keep"));

        h.Call("new_scene", new { template = "default3d" });
        var threeD = h.Ok("get_scene_summary", new { format = "json" });
        Assert.True(threeD["checks"]!["hasGameMode"]!.GetValue<bool>());
        Assert.True(threeD["checks"]!["hasPlayerStart"]!.GetValue<bool>());
    }

    [Fact]
    public void GetSceneSummaryOffersBothTheViewAndTheFileFormat()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { shape = "Cube" });

        var view = h.Ok("get_scene_summary", new { format = "view" });
        Assert.False(view["truncated"]!.GetValue<bool>());
        Assert.Contains("Cube", view["layers"]!.ToJsonString());

        var file = h.Call("get_scene_summary", new { format = "file" });
        Assert.False(file.IsError);
        Assert.Contains("\"type\":\"MeshRenderer\"", file.FirstText);

        Assert.Contains("'compact', 'json', 'view' or 'file'", h.Fails("get_scene_summary", new { format = "xml" }).FirstText);
    }

    [Fact]
    public void MutationsDuringPlayModeAreRefusedForSceneIoOnly()
    {
        using var h = new SceneToolHarness();
        h.Host.IsPlaying = true;

        Assert.Contains("play mode", h.Fails("new_scene").FirstText);
        Assert.Contains("play mode", h.Fails("save_scene", new { path = "x.scene" }).FirstText);

        // Spawning still works while playing; it just is not snapshotted for undo.
        h.Ok("spawn_actor", new { name = "Live" });
        Assert.Equal(0, h.Undo.UndoCount);
    }
}

public class UndoStackTests
{
    [Fact]
    public void UndoRestoresTheSceneBeforeTheLastMutation()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { name = "First" });
        h.Ok("spawn_actor", new { name = "Second" });

        var result = h.Ok("undo");
        Assert.Equal("Spawn actor 'Second'", result["undone"]![0]!.GetValue<string>());
        Assert.NotNull(h.Scene.FindByName("First"));
        Assert.Null(h.Scene.FindByName("Second"));
        Assert.Equal(1, h.Undo.UndoCount);
        Assert.Equal(1, h.Undo.RedoCount);
    }

    [Fact]
    public void RedoReappliesAnUndoneMutation()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { name = "First" });
        h.Ok("undo");
        Assert.Null(h.Scene.FindByName("First"));

        h.Ok("redo");
        Assert.NotNull(h.Scene.FindByName("First"));
        Assert.Equal(0, h.Undo.RedoCount);

        // A new mutation after an undo discards the redo branch.
        h.Ok("undo");
        h.Ok("spawn_actor", new { name = "Other" });
        Assert.Equal(0, h.Undo.RedoCount);
        Assert.Equal("Nothing to redo.", h.Call("redo").FirstText.Split('\n')[0]);
    }

    [Fact]
    public void ANoOpMutationDoesNotPushASnapshot()
    {
        using var h = new SceneToolHarness();
        h.Ok("spawn_actor", new { name = "Cube", tag = "Prop" });
        Assert.Equal(1, h.Undo.UndoCount);

        h.Ok("set_actor", new { actor = "Cube", tag = "Prop" });    // state before == state after the spawn
        Assert.Equal(2, h.Undo.UndoCount);

        h.Ok("set_actor", new { actor = "Cube", tag = "Prop" });    // identical to the top snapshot: skipped
        Assert.Equal(2, h.Undo.UndoCount);
    }

    [Fact]
    public void TheStackIsCappedByCount()
    {
        using var host = new HeadlessSceneHost();
        var stack = new SceneUndoStack(host, maxEntries: 3);

        for (int i = 0; i < 5; i++)
        {
            host.ActiveScene!.AddActor(new Actor("A" + i));
            host.ActiveScene.FlushPendingActors();
            stack.Snapshot("step " + i);
        }

        Assert.Equal(3, stack.UndoCount);
        Assert.Equal(new[] { "step 2", "step 3", "step 4" }, stack.UndoLabels);
    }

    [Fact]
    public void UndoRestoresTheScenePathAndReselectsByName()
    {
        using var host = new HeadlessSceneHost();
        var stack = new SceneUndoStack(host);

        var cube = host.ActiveScene!.AddActor(new Actor("Cube"));
        host.ActiveScene.FlushPendingActors();
        host.CurrentScenePath = "Scenes/A.scene";
        host.SelectActor(cube);

        stack.Snapshot("before");
        host.CurrentScenePath = null;
        host.SelectActor(null);
        cube.Name = "Renamed";

        Assert.True(stack.Undo(out var label));
        Assert.Equal("before", label);
        Assert.Equal("Scenes/A.scene", host.CurrentScenePath);
        Assert.Equal("Cube", host.SelectedActor?.Name);
        Assert.NotSame(cube, host.SelectedActor);   // a restored instance, hence the "ids change" note
        Assert.True(host.SceneDirty);
    }
}
