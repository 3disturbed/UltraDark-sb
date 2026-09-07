using System.Text.Json;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scripting;
using SexyBiscuit.Engine.UI;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The screen-space UI a script builds, and the two silent failures it could have.
/// </summary>
/// <remarks>
/// The first is text that draws as nothing. That is not hypothetical: the editor's
/// widgets have always opened with <c>if (font == null) return;</c> and there has
/// never been a font asset, so every label they drew was invisible. The script UI
/// uses a glyph table embedded from the browser's own file, and if the resource
/// name in the csproj is wrong it fails the same silent way — so the load is
/// asserted rather than assumed.
///
/// The second is a handle whose properties read <c>undefined</c>. The contract's
/// <c>uiElement</c> section says what a script may reach for; this holds Jint to it.
/// </remarks>
public class ScriptUiTests
{
    private static string RepoRoot
    {
        get
        {
            var repo = EngineRepoLocator.Find();
            Assert.NotNull(repo);
            return repo!.Root;
        }
    }

    private static (Scene scene, JintRuntime runtime) Runtime()
    {
        var scene = new Scene("ScriptUi");
        var actor = scene.AddActor(new Actor("Host"));
        scene.FlushPendingActors();
        return (scene, new JintRuntime(actor));
    }

    // -------------------------------------------------------------------------
    // The font
    // -------------------------------------------------------------------------

    [Fact]
    public void TheSharedGlyphTableIsActuallyEmbeddedAndLoads()
    {
        // If this fails, text renders as nothing and no other test notices.
        Assert.True(BitmapFont.IsLoaded,
            "BitmapFont found no embedded font5x7.json — check the EmbeddedResource LogicalName");
        Assert.Equal(95, BitmapFont.GlyphCount);
    }

    [Fact]
    public void BothEnginesMeasureTextIdentically()
    {
        // The browser computes length * (width + spacing) - spacing. Same arithmetic
        // here, or a centred label sits in a different place on each engine.
        Assert.Equal(5f, BitmapFont.MeasureLine("A"));
        Assert.Equal(11f, BitmapFont.MeasureLine("AB"));
        Assert.Equal(22f, BitmapFont.MeasureLine("AB", 2f));
        Assert.Equal(0f, BitmapFont.MeasureLine(""));
        Assert.Equal(BitmapFont.MeasureLine("longer"), BitmapFont.MeasureWidest("a\nlonger"));
        Assert.Equal(16f, BitmapFont.MeasureHeight("one\ntwo"));
    }

    [Fact]
    public void TheGlyphTableOnDiskIsTheOneTheBrowserImports()
    {
        string json = File.ReadAllText(Path.Combine(RepoRoot, "html5/src/ui/font5x7.json"));
        using var document = JsonDocument.Parse(json);

        int onDisk = 0;
        foreach (var _ in document.RootElement.GetProperty("glyphs").EnumerateObject()) onDisk++;

        Assert.Equal(onDisk, BitmapFont.GlyphCount);
        Assert.Equal(BitmapFont.GlyphWidth, document.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(BitmapFont.GlyphHeight, document.RootElement.GetProperty("height").GetInt32());
    }

    // -------------------------------------------------------------------------
    // Layout, which is the whole reason the UI exists
    // -------------------------------------------------------------------------

    [Fact]
    public void AnAnchoredElementStaysPutWhenTheWindowChangesSize()
    {
        var ui = new ScriptUi();
        var badge = ui.Add(new UiElement { Kind = UiKind.Panel, Anchor = UiAnchor.BottomRight, X = -12, Y = -12, Width = 80, Height = 28 });

        ui.SetViewport(800, 600);
        RectangleF small = ui.RectOf(badge);
        Assert.Equal(800 - 12, small.X + small.Width);
        Assert.Equal(600 - 12, small.Y + small.Height);

        ui.SetViewport(1920, 1080);
        RectangleF large = ui.RectOf(badge);
        Assert.Equal(1920 - 12, large.X + large.Width);
        Assert.Equal(1080 - 12, large.Y + large.Height);
    }

    [Fact]
    public void CentreReallyIsTheCentre()
    {
        var ui = new ScriptUi();
        ui.SetViewport(1000, 500);
        var element = ui.Add(new UiElement { Kind = UiKind.Panel, Anchor = UiAnchor.Center, Width = 100, Height = 50 });

        RectangleF r = ui.RectOf(element);
        Assert.Equal(500f, r.X + r.Width / 2f);
        Assert.Equal(250f, r.Y + r.Height / 2f);
    }

    [Fact]
    public void ALabelWithNoSizeMeasuresItselfSoAnchoringWorks()
    {
        var ui = new ScriptUi();
        ui.SetViewport(1000, 500);

        var right = ui.Add(new UiElement { Kind = UiKind.Label, Anchor = UiAnchor.BottomRight, X = -12, Y = -12, Width = 0, Height = 0, Text = "bottom right", Scale = 2f });
        RectangleF r = ui.RectOf(right);
        Assert.True(r.Width > 0f, "the label should take its width from its text");
        Assert.Equal(1000 - 12, r.X + r.Width);

        var middle = ui.Add(new UiElement { Kind = UiKind.Label, Anchor = UiAnchor.Center, Width = 0, Height = 0, Text = "centred", Scale = 2f });
        RectangleF c = ui.RectOf(middle);
        Assert.Equal(500f, c.X + c.Width / 2f);

        // An explicit width still wins.
        var fixedWidth = ui.Add(new UiElement { Kind = UiKind.Label, Text = "x", Width = 200, Height = 40 });
        Assert.Equal(200f, ui.RectOf(fixedWidth).Width);
    }

    // -------------------------------------------------------------------------
    // Interaction
    // -------------------------------------------------------------------------

    [Fact]
    public void AClickIsAReleaseInsideAndLastsExactlyOneFrame()
    {
        var ui = new ScriptUi();
        ui.SetViewport(400, 300);
        var button = ui.Add(new UiElement { Kind = UiKind.Button, X = 100, Y = 100, Width = 80, Height = 30 });

        ui.SetPointer(120, 110, down: true);
        ui.Update();
        Assert.False(button.Clicked);
        Assert.True(button.Hovered);

        ui.SetPointer(120, 110, down: false);
        ui.Update();
        Assert.True(button.Clicked);

        // A second Update with no new pointer must not fire it again — the browser
        // had exactly this bug, and a button that fires twice buys an item twice.
        ui.Update();
        Assert.False(button.Clicked);
    }

    [Fact]
    public void ALabelNeverEatsAClick()
    {
        var ui = new ScriptUi();
        ui.SetViewport(400, 300);
        var label = ui.Add(new UiElement { Kind = UiKind.Label, Width = 400, Height = 300, Text = "hello" });

        ui.SetPointer(200, 150, down: true);
        ui.Update();
        ui.SetPointer(200, 150, down: false);
        ui.Update();

        Assert.False(label.Clicked);
        Assert.False(label.Hovered);
    }

    // -------------------------------------------------------------------------
    // The contract
    // -------------------------------------------------------------------------

    [Fact]
    public void AUiHandleExposesEveryMemberTheContractPromises()
    {
        var (scene, runtime) = Runtime();
        try
        {
            string contractPath = Path.Combine(RepoRoot, "html5", "src", "scripting", "bridge-api.json");
            using var contract = JsonDocument.Parse(File.ReadAllText(contractPath));

            runtime.Evaluate("var handle = UI.panel(0, 0, 10, 10);");
            Assert.Equal("object", runtime.Evaluate("typeof handle")!.ToString());

            foreach (JsonProperty member in contract.RootElement.GetProperty("uiElement").EnumerateObject())
            {
                string kind = member.Value.GetProperty("kind").GetString()!;
                string actual = runtime.Evaluate($"typeof handle.{member.Name}")!.ToString();

                if (kind == "fn")
                    Assert.True(actual == "function", $"handle.{member.Name} should be a function, was {actual}");
                else
                    Assert.True(actual != "undefined", $"handle.{member.Name} is missing from the Jint UI handle");
            }

            runtime.Evaluate("handle.destroy();");
        }
        finally
        {
            ScriptUi.Instance.Clear();
            scene.Destroy();
        }
    }

    [Fact]
    public void AScriptCanOnlyClearItsOwnElements()
    {
        // Two scripts sharing one canvas: UI.clear() in one must not blank the other,
        // or a HUD disappears the first time a menu closes itself.
        var (sceneA, runtimeA) = Runtime();
        var (sceneB, runtimeB) = Runtime();
        try
        {
            runtimeA.Evaluate("UI.label(0, 0, 'from A');");
            runtimeB.Evaluate("UI.label(0, 20, 'from B');");
            Assert.Equal(2, ScriptUi.Instance.Elements.Count);

            runtimeA.Evaluate("UI.clear();");

            Assert.Single(ScriptUi.Instance.Elements);
            Assert.Equal("from B", ScriptUi.Instance.Elements[0].Text);
        }
        finally
        {
            ScriptUi.Instance.Clear();
            sceneA.Destroy();
            sceneB.Destroy();
        }
    }

    [Fact]
    public void AColourWrittenAnyOfTheAcceptedWaysArrivesAsThatColour()
    {
        var (scene, runtime) = Runtime();
        try
        {
            runtime.Evaluate("var a = UI.panel(0,0,4,4,{ background: '#ff8040' });");
            runtime.Evaluate("var b = UI.panel(0,0,4,4,{ background: { R: 255, G: 128, B: 64 } });");

            var elements = ScriptUi.Instance.Elements;
            Assert.Equal(new Color(255, 128, 64), elements[0].Background);
            Assert.Equal(new Color(255, 128, 64), elements[1].Background);
        }
        finally
        {
            ScriptUi.Instance.Clear();
            scene.Destroy();
        }
    }
}
