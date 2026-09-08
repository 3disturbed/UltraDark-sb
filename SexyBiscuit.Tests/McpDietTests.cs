using System.Text.Json;
using System.Text.Json.Nodes;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Mcp;
using SexyBiscuit.Engine.Rendering;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The token diet: results are short, the catalogue carries no dead weight, and many edits fit
/// in one call.
/// </summary>
/// <remarks>
/// Every tool result is re-read on every later turn of a session, so its size is paid many
/// times over; a spawn that echoed the actor's full view cost about as much as the call that
/// made it. These tests pin the shapes the model reads.
/// </remarks>
public class McpDietTests
{
    private static float[] Floats(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<float>()).ToArray();

    [Fact]
    public void SpawnEchoesAStubNotTheFullView()
    {
        using var h = new SceneToolHarness();

        var stub = h.Ok("spawn_actor", new { shape = "Cube", name = "Crate", position = new[] { 1f, 2f, 3f } });

        Assert.Equal(new[] { "id", "layer", "name", "position" }, stub.AsObject().Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("Crate", stub["name"]!.GetValue<string>());
        Assert.Equal(new[] { 1f, 2f, 3f }, Floats(stub["position"]));

        // The full picture is one call away when it is wanted.
        var full = h.Ok("get_actor", new { actor = "Crate" });
        Assert.NotNull(full["components"]);
        var row = h.Ok("get_actor", new { actor = "Crate", detail = "row" });
        Assert.Null(row["transform3d"]);
        Assert.Contains("MeshRenderer", row["components"]!.AsArray().Select(c => c!.GetValue<string>()));
    }

    [Fact]
    public void CompactSummaryListsOneLinePerActorAndPages()
    {
        using var h = new SceneToolHarness();
        for (int i = 0; i < 5; i++)
            h.Ok("spawn_actor", new { shape = "Cube", name = $"Cube {i}", position = new[] { (float)i, 0f, 0f }, tag = i == 0 ? "Player" : null });

        var all = h.Call("get_scene_summary");
        Assert.False(all.IsError, all.FirstText);
        var lines = all.FirstText.Split('\n');

        Assert.Contains("5 actors", lines[0]);
        Assert.Contains("camera-", lines[0]);
        Assert.Contains("light-", lines[0]);
        Assert.Equal(6, lines.Length);
        Assert.StartsWith("#", lines[1]);
        Assert.Contains("Cube 0", lines[1]);
        Assert.Contains("tag=Player", lines[1]);
        Assert.Contains("MeshRenderer", lines[1]);
        Assert.Contains("@0,0,0", lines[1]);

        var page = h.Call("get_scene_summary", new { offset = 2, limit = 2 });
        var pageLines = page.FirstText.Split('\n');
        Assert.Equal(4, pageLines.Length);
        Assert.Contains("Cube 2", pageLines[1]);
        Assert.Contains("Cube 3", pageLines[2]);
        Assert.Contains("1 more (offset=4)", pageLines[3]);

        // The JSON form still exists, and pages the same way.
        var json = h.Ok("get_scene_summary", new { format = "json", offset = 4, limit = 10 });
        Assert.Single(json["actors"]!.AsArray());
        Assert.Null(json["nextOffset"]);
    }

    [Fact]
    public void SceneViewOffsetSkipsActors()
    {
        using var h = new SceneToolHarness();
        for (int i = 0; i < 4; i++) h.Ok("spawn_actor", new { shape = "Sphere", name = $"S{i}" });

        var first = h.Ok("get_scene_summary", new { format = "view", limit = 3 });
        Assert.True(first["truncated"]!.GetValue<bool>());
        Assert.Equal(3, first["nextOffset"]!.GetValue<int>());

        var rest = h.Ok("get_scene_summary", new { format = "view", offset = 3, limit = 3 });
        Assert.False(rest["truncated"]!.GetValue<bool>());
        var names = rest["layers"]!.AsArray().SelectMany(l => l!["actors"]!.AsArray()).Select(a => a!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "S3" }, names);

        // The file form is the exact scene JSON, compact.
        var file = h.Call("get_scene_summary", new { format = "file" });
        Assert.DoesNotContain("\n", file.FirstText);
        Assert.Contains("\"type\":\"MeshRenderer\"", file.FirstText);
    }

    [Fact]
    public void DescribeComponentsNamesOnlyGroupsByCategoryAndOneTypeGivesItsProperties()
    {
        using var h = new SceneToolHarness();

        var names = h.Call("describe_components");
        Assert.False(names.IsError);
        Assert.Contains("Rendering:", names.FirstText);
        Assert.Contains("MeshRenderer", names.FirstText);
        Assert.DoesNotContain("summary", names.FirstText);
        Assert.True(names.FirstText.Length < 3000, $"names-only listing is {names.FirstText.Length} chars");

        // An array result is wrapped as {"value": [...]} for the structured copy.
        var detailed = h.Ok("describe_components", new { category = "Rendering", namesOnly = false });
        var entry = detailed["value"]!.AsArray().First(e => e!["type"]!.GetValue<string>() == "MeshRenderer")!;
        Assert.Equal("Rendering", entry["category"]!.GetValue<string>());
        Assert.True(entry["summary"]!.GetValue<string>().Length <= 100);

        // Naming a type switches the same tool to the property description.
        var one = h.Ok("describe_components", new { type = "MeshRenderer" });
        Assert.Equal("MeshRenderer", one["type"]!.GetValue<string>());
        Assert.Contains("MeshType", one["properties"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));
    }

    [Fact]
    public void ApplySceneEditsRunsEveryOpUnderOneUndoStepAndResolvesReferences()
    {
        using var h = new SceneToolHarness();
        int before = h.Undo.UndoCount;

        var ops = new object[]
        {
            new { op = "spawn_actor", shape = "Cube", name = "Floor", position = new[] { 0f, 0f, 0f } },
            new { op = "spawn_actor", shape = "Sphere", name = "Ball", position = new[] { 0f, 1f, 0f } },
            new { op = "set_properties", actor = "$1", properties = new Dictionary<string, object> { ["Transform3D.Scale"] = new[] { 2f, 2f, 2f } } },
            new { op = "set_actor", actor = "$0", name = "Ground" },
            new { op = "set_actor", actor = "$1", layer = "props" },
        };

        var result = h.Ok("apply_scene_edits", new { ops });
        var rows = result["value"]!.AsArray();
        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.Null(r!["error"]));
        Assert.Equal(rows[0]!["id"]!.GetValue<long>(), (long)h.Scene.FindByName("Ground")!.Id);

        var ball = h.Scene.FindByName("Ball")!;
        Assert.Equal(2f, ball.GetComponent<Transform3D>()!.LocalScale.X, 3);
        Assert.Equal("props", ball.Layer_!.Name);

        // One undo step for the whole batch.
        Assert.Equal(before + 1, h.Undo.UndoCount);
        h.Ok("undo");
        Assert.Null(h.Scene.FindByName("Ground"));
        Assert.Null(h.Scene.FindByName("Ball"));
    }

    [Fact]
    public void ApplySceneEditsReportsPerOpFailuresAndContinuesUnlessAsked()
    {
        using var h = new SceneToolHarness();

        var ops = new object[]
        {
            new { op = "spawn_actor", shape = "Cube", name = "A" },
            new { op = "set_actor", actor = "NoSuchActor", name = "B" },
            new { op = "save_scene", path = "Scenes/x.scene" },   // scene I/O is not a batch op
            new { op = "spawn_actor", shape = "Cube", name = "C" },
        };

        var result = h.Call("apply_scene_edits", new { ops });
        Assert.False(result.IsError, result.FirstText);
        Assert.StartsWith("2 ok, 2 failed", result.FirstText);

        var rows = result.StructuredContent!["value"]!.AsArray();
        Assert.False(rows[1]!["ok"]!.GetValue<bool>());
        Assert.Contains("NoSuchActor", rows[1]!["error"]!.GetValue<string>());
        Assert.Contains("cannot be part of a batch", rows[2]!["error"]!.GetValue<string>());
        Assert.NotNull(h.Scene.FindByName("C"));

        var stopped = h.Call("apply_scene_edits", new { ops = new object[]
        {
            new { op = "destroy_actor", actor = "Nope" },
            new { op = "spawn_actor", shape = "Cube", name = "Never" },
        }, stopOnError = true });
        Assert.True(stopped.IsError);
        Assert.Null(h.Scene.FindByName("Never"));

        // A reference to a failed op is a clear error for that op only.
        var dangling = h.Call("apply_scene_edits", new { ops = new object[]
        {
            new { op = "set_actor", actor = "Nope", name = "X" },
            new { op = "set_actor", actor = "$0", tag = "T" },
        } });
        Assert.Contains("refers to op 0", dangling.FirstText);
    }

    [Fact]
    public void SpawnManyPlacesAGridWithNumberedNames()
    {
        using var h = new SceneToolHarness();

        var result = h.Ok("spawn_many", new { what = "Cube", grid = new[] { 3, 2 }, spacing = 2f, origin = new[] { 0f, 0f, 0f }, name = "Tile", color = "#00FF00" });
        var spawned = result["spawned"]!.AsArray();

        Assert.Equal(6, spawned.Count);
        Assert.Equal("Tile 1", spawned[0]!["name"]!.GetValue<string>());
        Assert.Equal(new[] { 4f, 0f, 2f }, Floats(spawned[5]!["position"]));
        Assert.Equal(6, h.Scene.Layers.Sum(l => l.Actors.Count));

        var preset = h.Ok("spawn_many", new { what = "Point Light", positions = new[] { new[] { 0f, 3f, 0f }, new[] { 4f, 3f, 0f } } });
        Assert.Equal(2, preset["spawned"]!.AsArray().Count);
        Assert.Equal(2, h.Scene.Layers.SelectMany(l => l.Actors).Count(a => a.GetComponent<Light3D>() != null));

        var bad = h.Call("spawn_many", new { what = "Dodecahedron", positions = new[] { new[] { 0f, 0f, 0f } } });
        Assert.True(bad.IsError);
    }

    [Fact]
    public void ToolsListOmitsScaffoldingWithNoInformation()
    {
        using var h = new SceneToolHarness();
        var list = h.Registry.DescribeForToolsList();

        var spawn = list.First(t => t!["name"]!.GetValue<string>() == "spawn_actor")!.AsObject();
        Assert.Null(spawn["annotations"]);                       // a plain mutating tool says nothing
        Assert.Null(spawn["inputSchema"]!["additionalProperties"]);
        Assert.Null(spawn["inputSchema"]!["properties"]!["list"]!["default"]);          // false
        Assert.NotNull(spawn["inputSchema"]!["properties"]!["transform3d"]!["default"]); // true is worth saying

        var summary = list.First(t => t!["name"]!.GetValue<string>() == "get_scene_summary")!.AsObject();
        Assert.True(summary["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.Null(summary["annotations"]!["destructiveHint"]);

        var destroy = list.First(t => t!["name"]!.GetValue<string>() == "destroy_actor")!.AsObject();
        Assert.True(destroy["annotations"]!["destructiveHint"]!.GetValue<bool>());

        string compact = list.ToJsonString();
        Assert.True(compact.Length < 24_000, $"the engine catalogue is {compact.Length} chars");
    }
}
