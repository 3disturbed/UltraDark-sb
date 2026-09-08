using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.UI;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The graphics menu, driven the way a player drives it.
/// </summary>
/// <remarks>
/// The mirror of <c>html5/tests/graphicsMenu.test.js</c>. Both suites build the menu from
/// <c>html5/src/ui/graphics-menu.json</c> and click the same controls, so a row that
/// appears on one engine and not on the other fails here.
/// </remarks>
public class GraphicsMenuTests : IDisposable
{
    public GraphicsMenuTests() => UiCanvas.ClearAll();

    public void Dispose()
    {
        // A leaked canvas stays in the static paint list and poisons the next test.
        UiCanvas.ClearAll();
        GC.SuppressFinalize(this);
    }

    // -------------------------------------------------------------------------
    // Fixtures
    // -------------------------------------------------------------------------

    /// <summary>A desktop-shaped machine: everything the native engine can do.</summary>
    private static GraphicsCapabilities DesktopCaps() => new()
    {
        Renderer = "MonoGame DesktopGL", Adapter = "Test Adapter", Platform = "Test",
        Shadows = true, PostProcessing = true, Lighting2D = true,
        Anisotropy = true, DisplayControl = true, PixelRatio = false,
    };

    /// <summary>A browser-shaped machine: no shadow pass, no post chain, no 2D light map.</summary>
    private static GraphicsCapabilities WebCaps() => new()
    {
        Renderer = "WebGL2", Adapter = "Test GPU", Platform = "Web",
        Shadows = false, PostProcessing = false, Lighting2D = false,
        Anisotropy = true, DisplayControl = false, PixelRatio = true,
    };

    private static GraphicsMenu OpenMenu(GraphicsCapabilities? caps = null)
    {
        var settings = new GraphicsSettings();
        settings.ApplyPreset("medium");

        var menu = new GraphicsMenu(settings, caps ?? DesktopCaps());
        menu.Open();
        menu.Canvas.SetViewport(1280f, 720f);
        menu.Canvas.Layout();
        return menu;
    }

    /// <summary>One frame: lay out, feed input, then let the menu read what happened.</summary>
    private static void Frame(GraphicsMenu menu, UiInputFrame input = default)
    {
        menu.Canvas.Layout();
        menu.Canvas.Input.Update(input);
        menu.Tick();
    }

    /// <summary>
    /// Switches tab and lets the rebuilt rows get rectangles.
    /// </summary>
    /// <remarks>
    /// Two frames, because that is what really happens: Tick rebuilds the body when the
    /// strip's selection moves, and the new rows are not laid out until the next frame's
    /// layout pass. A test that clicked immediately would be clicking at (0, 0).
    /// </remarks>
    private static void ShowTab(GraphicsMenu menu, string id)
    {
        var tabs = menu.Canvas.Find("tabs")!;
        tabs.SelectedIndex = TabIndex(id);
        Frame(menu);
        Frame(menu);
    }

    private static int TabIndex(string id)
    {
        var schema = GraphicsMenu.MenuSchema.Shared;
        for (int i = 0; i < schema.Tabs.Count; i++)
            if (schema.Tabs[i].Id == id) return i;

        throw new InvalidOperationException($"The description has no \"{id}\" tab.");
    }

    /// <summary>A press and a release on a node's centre, which is one click.</summary>
    private static void Click(GraphicsMenu menu, UiNode node)
    {
        Vector2 screen = menu.Canvas.CanvasToScreen(node.Rect.Centre);
        Frame(menu, new UiInputFrame { Pointer = screen, PointerDown = true });
        Frame(menu, new UiInputFrame { Pointer = screen, PointerDown = false });
    }

    // -------------------------------------------------------------------------
    // The status line
    // -------------------------------------------------------------------------

    [Fact]
    public void TheMenuCarriesTheEngineVersionOnALineAtItsFoot()
    {
        // The whole reason the status line exists, and the one thing asked for by name.
        GraphicsMenu menu = OpenMenu();
        UiNode? status = menu.Canvas.Find("status");

        Assert.NotNull(status);
        Assert.Contains(EngineInfo.Version, status!.Text);
        Assert.Contains("SexyBiscuit", status.Text);
        Assert.Contains("Preset: Medium", status.Text);

        // And it is the last thing but one in the panel, so it stays at the foot on
        // every tab rather than scrolling away with the rows.
        UiNode panel = menu.Canvas.Find("panel")!;
        Assert.Same(status, panel.Children[^2]);
    }

    [Fact]
    public void TheStatusLineFollowsThePresetWithoutBeingRebuilt()
    {
        GraphicsMenu menu = OpenMenu();

        var settings = new GraphicsSettings();
        settings.ApplyPreset("ultra");
        menu.Adopt(settings, DesktopCaps());

        Assert.Contains("Preset: Ultra", menu.Canvas.Find("status")!.Text);

        settings.Preset = "custom";
        menu.Refresh();
        Assert.Contains("Preset: Custom", menu.Canvas.Find("status")!.Text);
    }

    // -------------------------------------------------------------------------
    // Presets and tabs
    // -------------------------------------------------------------------------

    [Fact]
    public void ClickingAPresetAppliesEveryOneOfItsValues()
    {
        GraphicsMenu menu = OpenMenu();
        UiNode? battery = menu.Canvas.Find("preset:battery");
        Assert.NotNull(battery);

        GraphicsSettings? announced = null;
        menu.Changed = s => announced = s;
        Click(menu, battery!);

        Assert.NotNull(announced);
        Assert.Equal("battery", announced!.Preset);
        Assert.Equal(0.5f, announced.RenderScale, 3);
        Assert.Equal(ShadowQuality.Off, announced.Shadows);
    }

    [Fact]
    public void EveryTabInTheDescriptionCanBeOpenedAndBuildsItsRows()
    {
        GraphicsMenu menu = OpenMenu();

        foreach (GraphicsMenu.MenuTab tab in GraphicsMenu.MenuSchema.Shared.Tabs)
        {
            ShowTab(menu, tab.Id);
            Assert.True(menu.Canvas.Find("body")!.Children.Count > 0,
                        $"The \"{tab.Label}\" tab built no rows.");
        }
    }

    // -------------------------------------------------------------------------
    // Platform filtering
    // -------------------------------------------------------------------------

    [Fact]
    public void ARowThePlatformCannotHonourIsAbsentRatherThanMerelyDead()
    {
        // A control that is visibly present and permanently dead reads as a bug, and the
        // browser renderer genuinely has no shadow pass to attach one to.
        GraphicsMenu web = OpenMenu(WebCaps());
        ShowTab(web, "quality");

        Assert.Null(web.Canvas.Find("shadows"));
        Assert.NotNull(web.Canvas.Find("textureFiltering"));

        GraphicsMenu desktop = OpenMenu(DesktopCaps());
        ShowTab(desktop, "quality");
        Assert.NotNull(desktop.Canvas.Find("shadows"));
    }

    [Fact]
    public void EachPlatformGetsItsOwnAdvancedKnobsAndNotTheOthersOnes()
    {
        GraphicsMenu web = OpenMenu(WebCaps());
        ShowTab(web, "advanced");
        Assert.NotNull(web.Canvas.Find("shaderPrecision"));
        Assert.Null(web.Canvas.Find("msaa"));

        GraphicsMenu desktop = OpenMenu(DesktopCaps());
        ShowTab(desktop, "advanced");
        Assert.NotNull(desktop.Canvas.Find("msaa"));
        Assert.Null(desktop.Canvas.Find("shaderPrecision"));
    }

    // -------------------------------------------------------------------------
    // Controls
    // -------------------------------------------------------------------------

    [Fact]
    public void DraggingASliderWritesTheValueThroughToTheSettings()
    {
        GraphicsMenu menu = OpenMenu();
        ShowTab(menu, "display");

        UiNode? slider = menu.Canvas.Find("renderScale");
        Assert.NotNull(slider);
        Assert.Equal(UiKind.Slider, slider!.Kind);

        GraphicsSettings? announced = null;
        menu.Changed = s => announced = s;

        // Just inside the end of the track. The last pixel belongs to no rectangle on
        // either engine — rects are half-open so a pointer on a seam cannot hit two
        // adjacent controls — and the step rounds the rest of the way to the maximum.
        var at = new Vector2(slider.Rect.Right - 1f, slider.Rect.Centre.Y);
        Frame(menu, new UiInputFrame { Pointer = menu.Canvas.CanvasToScreen(at), PointerDown = true });

        Assert.NotNull(announced);
        Assert.Equal(2f, announced!.RenderScale, 3);
        Assert.Equal("custom", announced.Preset);
    }

    [Fact]
    public void AToggleFlipsTheSettingItIsBoundTo()
    {
        GraphicsMenu menu = OpenMenu();
        ShowTab(menu, "display");

        UiNode? vsync = menu.Canvas.Find("vsync");
        Assert.NotNull(vsync);

        GraphicsSettings? announced = null;
        menu.Changed = s => announced = s;
        Click(menu, vsync!);

        Assert.NotNull(announced);
        Assert.False(announced!.VSync);
    }

    [Fact]
    public void AnIntegerSettingNeverPicksUpAFractionFromItsControl()
    {
        // A shadow map of 1023.9997 is a texture allocation the driver refuses.
        GraphicsMenu menu = OpenMenu();
        ShowTab(menu, "advanced");

        UiNode? lights = menu.Canvas.Find("maxLightsPerObject");
        Assert.NotNull(lights);

        GraphicsSettings? announced = null;
        menu.Changed = s => announced = s;

        var at = new Vector2(lights!.Rect.X + lights.Rect.Width * 0.63f, lights.Rect.Centre.Y);
        Frame(menu, new UiInputFrame { Pointer = menu.Canvas.CanvasToScreen(at), PointerDown = true });

        Assert.NotNull(announced);
        Assert.InRange(announced!.MaxLightsPerObject, 1, 8);
    }

    [Fact]
    public void ASettledMenuReportsNoChangeSoItDoesNotRewriteItselfEveryFrame()
    {
        // A strict comparison would see every dropdown as changed on every frame and the
        // menu would re-lay itself out sixty times a second.
        GraphicsMenu menu = OpenMenu();
        ShowTab(menu, "quality");

        int changes = 0;
        menu.Changed = _ => changes++;
        for (int i = 0; i < 5; i++) Frame(menu);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void CloseHidesTheMenuAndSaysSoOnce()
    {
        GraphicsMenu menu = OpenMenu();

        int closed = 0;
        menu.Closed = () => closed++;
        Click(menu, menu.Canvas.Find("close")!);

        Assert.Equal(1, closed);
        Assert.False(menu.IsOpen);
        Assert.False(menu.Canvas.Interactive);
    }

    [Fact]
    public void TheBenchmarkButtonAsksItsOwnerToRunOneRatherThanRunningItItself()
    {
        GraphicsMenu menu = OpenMenu();
        ShowTab(menu, "benchmark");

        int asked = 0;
        menu.BenchmarkRequested = () => asked++;
        Click(menu, menu.Canvas.Find("benchmark")!);

        Assert.Equal(1, asked);
    }
}
