using System.Text.Json;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.UI;
using Rendering = SexyBiscuit.Engine.Rendering;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Holds the world-space UI geometry to the golden cases the browser engine reads from the
/// same file.
/// </summary>
/// <remarks>
/// World UI is basis vectors and ray intersections all the way down, and the parity tests
/// that read the other engine's source with a regular expression can see a missing member
/// but never a number that is quietly wrong. A canvas mirrored left to right would still
/// face the camera; a canvas flipped top to bottom would still hit-test self-consistently.
/// Only a fixture both engines read catches those, so this file and
/// <c>html5/tests/uiWorld.test.js</c> run the same one.
/// </remarks>
public class UiWorldTests
{
    private const float Tolerance = 1e-4f;

    private static string RepoRoot
    {
        get
        {
            var repo = EngineRepoLocator.Find();
            Assert.NotNull(repo);
            return repo!.Root;
        }
    }

    public static TheoryData<string> CaseNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (JsonElement c in Cases().EnumerateArray())
                names.Add(c.GetProperty("name").GetString()!);
            return names;
        }
    }

    private static JsonElement Cases()
    {
        string path = Path.Combine(RepoRoot, "html5", "tests", "fixtures", "ui-world-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("cases").Clone();
    }

    // -------------------------------------------------------------------------
    // The fixture
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void AWorldCaseResolvesToTheGeometryBothEnginesAgreeOn(string name)
    {
        JsonElement test = FindCase(name);

        JsonElement canvas = test.GetProperty("canvas");
        JsonElement camera = test.GetProperty("camera");

        Vector3 cameraPosition = Vec3(camera.GetProperty("position"));
        Matrix view = Matrix.CreateLookAt(
            cameraPosition, Vec3(camera.GetProperty("target")), Vec3(camera.GetProperty("up")));

        UiWorld.Basis basis = UiWorld.Build(
            Vec3(canvas.GetProperty("anchor")),
            Enum.Parse<UiFacing>(canvas.GetProperty("facing").GetString()!),
            Quat(canvas),
            view,
            cameraPosition,
            Vec2(canvas.GetProperty("worldSize")));

        Vector2 canvasSize = Vec2(canvas.GetProperty("canvasSize"));

        // A case that only pins the basis has no "expect" at all, which is the point of it:
        // an orientation is worth asserting on its own, before any ray is fired at it.
        test.TryGetProperty("expect", out JsonElement expect);


        if (test.TryGetProperty("expectBasis", out JsonElement expectBasis))
        {
            AssertVector(expectBasis.GetProperty("right"),  basis.Right,  "right");
            AssertVector(expectBasis.GetProperty("up"),     basis.Up,     "up");
            AssertVector(expectBasis.GetProperty("normal"), basis.Normal, "normal");
        }

        if (test.TryGetProperty("canvasToWorld", out JsonElement back))
        {
            Vector3 world = UiWorld.CanvasToWorld(basis, Vec2(back), canvasSize);
            AssertVector(expect.GetProperty("world"), world, "world");
        }

        if (!test.TryGetProperty("ray", out JsonElement ray)) return;

        Vector3 origin    = Vec3(ray.GetProperty("origin"));
        Vector3 direction = Vector3.Normalize(Vec3(ray.GetProperty("direction")));

        Vector2? point = UiWorld.RayToCanvas(basis, origin, direction, canvasSize);

        if (expect.TryGetProperty("miss", out JsonElement miss) && miss.GetBoolean())
        {
            Assert.Null(point);
            Assert.Null(UiWorld.RayDistance(basis, origin, direction, canvasSize));
            return;
        }

        Assert.NotNull(point);
        JsonElement wanted = expect.GetProperty("canvas");
        Close(wanted[0].GetSingle(), point!.Value.X, "canvas.x");
        Close(wanted[1].GetSingle(), point!.Value.Y, "canvas.y");

        if (!expect.TryGetProperty("distance", out JsonElement distance)) return;

        float? actual = UiWorld.RayDistance(basis, origin, direction, canvasSize);

        if (distance.ValueKind == JsonValueKind.Null) Assert.Null(actual);
        else
        {
            Assert.NotNull(actual);
            Close(distance.GetSingle(), actual!.Value, "distance");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static JsonElement FindCase(string name)
    {
        foreach (JsonElement c in Cases().EnumerateArray())
            if (c.GetProperty("name").GetString() == name) return c;

        throw new InvalidOperationException($"No world case named '{name}'.");
    }

    private static Vector3 Vec3(JsonElement a)
        => new(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());

    private static Vector2 Vec2(JsonElement a)
        => new(a[0].GetSingle(), a[1].GetSingle());

    private static Quaternion Quat(JsonElement canvas)
        => canvas.TryGetProperty("planeRotation", out JsonElement q)
            ? new Quaternion(q[0].GetSingle(), q[1].GetSingle(), q[2].GetSingle(), q[3].GetSingle())
            : Quaternion.Identity;

    private static void AssertVector(JsonElement expected, Vector3 actual, string what)
    {
        Close(expected[0].GetSingle(), actual.X, $"{what}.x");
        Close(expected[1].GetSingle(), actual.Y, $"{what}.y");
        Close(expected[2].GetSingle(), actual.Z, $"{what}.z");
    }

    private static void Close(float expected, float actual, string what)
        => Assert.True(MathF.Abs(expected - actual) < Tolerance,
            $"{what}: expected {expected}, got {actual}");
}

/// <summary>
/// The behaviour that makes a saved widget usable in either space, and the camera election a
/// world canvas depends on.
/// </summary>
public class UiSpaceTests : IDisposable
{
    /// <summary>
    /// A camera left registered by another test would give these canvases a view to project
    /// through, and half of what is asserted here is what happens when there is none.
    /// <c>UiCanvas.ClearAll</c> exists for the same reason.
    /// </summary>
    public UiSpaceTests() => Rendering.Camera3D.ClearAll();

    public void Dispose()
    {
        Rendering.Camera3D.ClearAll();
        GC.SuppressFinalize(this);
    }

    private const string Document = """
        {
          "name": "panel",
          "layout": "column",
          "gap": 8,
          "padding": 12,
          "background": "#161920e6",
          "children": [
            { "name": "title", "kind": "label", "text": "TERMINAL", "scale": 2 },
            { "name": "go", "kind": "button", "text": "Engage", "width": "*" }
          ]
        }
        """;

    [Fact]
    public void OneDocumentAdoptedIntoEitherSpaceProducesTheSameTree()
    {
        // Why: this is the whole promise of putting the space on the canvas rather than in the
        // document. A widget saved once has to be usable on the screen and on a wall, and the
        // only way to say so is to adopt the same source into both and compare what came out.
        var screen = new UiCanvas { Space = UiSpace.Screen };
        var world  = new UiCanvas { Space = UiSpace.World, ReferenceResolution = new Vector2(400f, 200f) };

        try
        {
            screen.Adopt(UiDocument.FromJson(Document));
            world.Adopt(UiDocument.FromJson(Document));

            Assert.Equal(UiDocument.ToJson(screen.Root), UiDocument.ToJson(world.Root));
            Assert.Equal("panel", world.Root.Name);
            Assert.NotNull(world.Find("go"));
        }
        finally
        {
            screen.OnDestroy();
            world.OnDestroy();
        }
    }

    [Fact]
    public void AWorldCanvasIsItsReferenceResolutionWhateverTheWindowIs()
    {
        // Why: a canvas standing in the scene is a fixed sheet, not something fitted to a
        // window. If the viewport leaked into its size, the same wall-mounted UI would be laid
        // out differently on a phone and a monitor — and the texture behind it would resize
        // every time the player dragged the window.
        var canvas = new UiCanvas
        {
            Space = UiSpace.World,
            ScaleMode = UiScaleMode.ScaleToFit,      // ignored in world space, deliberately
            ReferenceResolution = new Vector2(400f, 200f),
        };

        try
        {
            canvas.SetViewport(1920f, 1080f);
            canvas.Layout();
            Assert.Equal(new Vector2(400f, 200f), canvas.CanvasSize);
            Assert.Equal(Vector2.One, canvas.Scale);

            canvas.SetViewport(640f, 360f);
            canvas.Layout();
            Assert.Equal(new Vector2(400f, 200f), canvas.CanvasSize);
            Assert.Equal(Vector2.Zero, canvas.CanvasOffset);
        }
        finally { canvas.OnDestroy(); }
    }

    [Fact]
    public void PixelsPerUnitIsTheOnlyThingThatSizesAWorldCanvas()
    {
        var canvas = new UiCanvas
        {
            Space = UiSpace.World,
            ReferenceResolution = new Vector2(400f, 200f),
            PixelsPerUnit = 100f,
        };

        try
        {
            canvas.Layout();
            Assert.Equal(new Vector2(4f, 2f), canvas.WorldSize);

            canvas.PixelsPerUnit = 200f;
            Assert.Equal(new Vector2(2f, 1f), canvas.WorldSize);
        }
        finally { canvas.OnDestroy(); }
    }

    [Fact]
    public void APointerThatMeetsNoWorldPlaneHitsNothingRatherThanTheNearestEdge()
    {
        // Why: the miss has to be a coordinate the ordinary rectangle test rejects, and it has
        // to be finite. An infinity would reach UiInput's slider arithmetic as a NaN and stop
        // being a miss at all.
        var canvas = new UiCanvas { Space = UiSpace.World, ReferenceResolution = new Vector2(400f, 200f) };

        try
        {
            canvas.Layout();

            // No Camera3D exists in this test, so every ray misses by construction.
            Vector2 point = canvas.ScreenToCanvas(new Vector2(640f, 360f));

            Assert.Equal(UiCanvas.Nowhere, point);
            Assert.True(float.IsFinite(point.X) && float.IsFinite(point.Y));
            Assert.Null(canvas.HitTest(new Vector2(640f, 360f)));
        }
        finally { canvas.OnDestroy(); }
    }

    [Fact]
    public void AFollowingNodeIsHiddenWhenThereIsNoCameraToProjectThrough()
    {
        // Why: without a camera the projection has no answer, and the wrong answer is to leave
        // the marker at its last position — which parks a damage number over the wrong enemy
        // for the rest of the scene.
        var canvas = new UiCanvas();

        try
        {
            var marker = new UiNode { Kind = UiKind.Label, Text = "12", WorldFollow = true };
            canvas.Root.Add(marker);

            canvas.SetViewport(1280f, 720f);
            canvas.Layout();

            Assert.False(marker.Visible);
            Assert.Equal(PositionMode.Absolute, marker.Positioning);
        }
        finally { canvas.OnDestroy(); }
    }

    [Fact]
    public void FollowingSurvivesTheDocumentRoundTrip()
    {
        // Why: a marker authored in a .ui file has to come back a marker. The three properties
        // go through the codec's own key list, so this also proves the list was extended.
        UiNode node = UiDocument.FromJson(
            """{ "kind": "label", "worldFollow": true, "worldAnchor": [1, 2, 3], "worldFollowDistance": 40 }""");

        Assert.True(node.WorldFollow);
        Assert.Equal(new Vector3(1f, 2f, 3f), node.WorldAnchor);
        Assert.Equal(40f, node.WorldFollowDistance);

        Assert.Contains("\"worldAnchor\":[1,2,3]", UiDocument.ToJson(node).Replace(" ", ""));
    }

    [Fact]
    public void AWorldAnchorNeedsThreeNumbersOnBothEngines()
    {
        // Why: two numbers is a typo with a plausible result. The browser codec throws here for
        // the same reason, so a mistake cannot mean different things on the two engines.
        Assert.Throws<UiDocumentException>(
            () => UiDocument.FromJson("""{ "worldAnchor": [1, 2] }"""));
    }
}
