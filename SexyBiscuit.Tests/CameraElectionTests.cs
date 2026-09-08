using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Pins which camera <see cref="Camera3D.Main"/> elects, and its world-to-screen projection.
/// </summary>
/// <remarks>
/// Both existed on one engine only. The browser fell back from "MainCamera3D" to "MainCamera"
/// and then to any camera at all; this engine returned null unless the exact tag was present,
/// and <c>RenderSystem3D.Render</c> returns immediately on a null camera. The bundled
/// <c>3D Scene</c> template tags its camera "MainCamera", so it rendered in the browser and
/// rendered nothing here. World-space UI resolves its camera through the same property, which
/// is how the disagreement was found. The twin is <c>html5/tests/cameraElection.test.js</c>.
/// </remarks>
public class CameraElectionTests : IDisposable
{
    private readonly List<Scene> _scenes = new();

    /// <summary>
    /// A camera left behind by another test would win this election, and the whole point of
    /// these tests is which camera wins. <c>UiCanvas.ClearAll</c> exists for the same reason.
    /// </summary>
    public CameraElectionTests() => Camera3D.ClearAll();

    public void Dispose()
    {
        foreach (Scene scene in _scenes) scene.Destroy();
        Camera3D.ClearAll();
        GC.SuppressFinalize(this);
    }

    private Camera3D Add(Scene scene, string tag)
    {
        var actor = new Actor("Camera") { Tag = tag };
        actor.AddComponent<Transform3D>();
        Camera3D camera = actor.AddComponent<Camera3D>();
        scene.AddActor(actor);
        scene.FlushPendingActors();
        return camera;
    }

    private Scene NewScene(string name)
    {
        var scene = new Scene(name);
        _scenes.Add(scene);
        return scene;
    }

    [Fact]
    public void ACameraTaggedMainCameraWinsWhenNothingCarriesTheThreeDTag()
    {
        // Why: the whole bundled 3D template is tagged this way, and it rendered nothing here.
        Scene scene = NewScene("Fallback");
        Camera3D camera = Add(scene, "MainCamera");

        Assert.Same(camera, Camera3D.Main);
    }

    [Fact]
    public void TheThreeDTagStillBeatsThePlainOne()
    {
        Scene scene = NewScene("Order");
        Add(scene, "MainCamera");
        Camera3D wanted = Add(scene, "MainCamera3D");

        Assert.Same(wanted, Camera3D.Main);
    }

    [Fact]
    public void AnUntaggedCameraIsBetterThanNoCameraAtAll()
    {
        Scene scene = NewScene("Untagged");
        Camera3D camera = Add(scene, "");

        Assert.Same(camera, Camera3D.Main);
    }

    [Fact]
    public void ASwitchedOffCameraNeverWins()
    {
        // Why: the browser used to skip this check, so a scene that disabled its menu camera
        // rendered through it there and through the next one here.
        Scene scene = NewScene("Disabled");
        Camera3D off = Add(scene, "MainCamera3D");
        off.Enabled = false;

        Camera3D live = Add(scene, "MainCamera");

        Assert.Same(live, Camera3D.Main);
    }

    [Fact]
    public void APointBehindTheCameraProjectsToNothingRatherThanTheOppositeCorner()
    {
        // Why: the perspective divide flips a point behind the viewer to the far side of the
        // screen. A caller that projects without the check draws a marker for the enemy
        // standing behind them, in the wrong place, with no way to tell.
        Scene scene = NewScene("Behind");
        Camera3D camera = Add(scene, "MainCamera3D");
        camera.GetTransform3D().Position = new Vector3(0f, 0f, 10f);

        Assert.NotNull(camera.WorldToScreen(Vector3.Zero, 1280f, 720f));
        Assert.Null(camera.WorldToScreen(new Vector3(0f, 0f, 40f), 1280f, 720f));
    }

    [Fact]
    public void ThePointStraightAheadProjectsToTheMiddleOfTheScreen()
    {
        Scene scene = NewScene("Centre");
        Camera3D camera = Add(scene, "MainCamera3D");
        camera.GetTransform3D().Position = new Vector3(0f, 0f, 10f);

        Vector2 screen = camera.WorldToScreen(Vector3.Zero, 1280f, 720f)!.Value;

        Assert.Equal(640f, screen.X, 3);
        Assert.Equal(360f, screen.Y, 3);
    }
}
