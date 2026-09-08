using System.Text.Json;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.UI;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Holds the layout engine to the golden cases the browser engine reads from the same file.
/// </summary>
/// <remarks>
/// Two independently written layout engines agree only where something forces them to. The
/// other parity tests in this repository read the opposite engine's source with a regular
/// expression, which can see a missing member and cannot see a number that is quietly wrong —
/// and a layout engine is almost entirely numbers. So both sides run the same fixture and
/// compare rectangles, which is the only check that would actually catch the drift.
/// </remarks>
public class UiLayoutTests
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

    private const float Tolerance = 0.01f;

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
        string path = Path.Combine(RepoRoot, "html5", "src", "ui", "layout-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("cases").Clone();
    }

    // -------------------------------------------------------------------------
    // The fixture
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ALayoutCaseResolvesToTheRectanglesBothEnginesAgreeOn(string name)
    {
        JsonElement test = FindCase(name);

        var canvas = new UiCanvas { ScaleMode = UiScaleMode.ConstantPixel };
        float width  = test.GetProperty("viewport")[0].GetSingle();
        float height = test.GetProperty("viewport")[1].GetSingle();
        canvas.SetViewport(width, height);

        // The document's own root carries the layout the fixture asked for, so its
        // properties and children are adopted onto the canvas root rather than nesting
        // one root inside another. Adopt is the engine's own code, so a property added
        // to a case cannot be silently dropped by a copy list this file forgot to update.
        canvas.Adopt(UiDocument.FromElement(test.GetProperty("tree")));
        canvas.Layout();

        foreach (JsonProperty expectation in test.GetProperty("expect").EnumerateObject())
        {
            UiNode? node = expectation.Name == "root" ? canvas.Root : canvas.Find(expectation.Name);
            Assert.True(node != null, $"The case names \"{expectation.Name}\", which the tree does not contain.");

            JsonElement e = expectation.Value;
            var expected = new RectangleF(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle());

            AssertRect(expected, node!.Rect, expectation.Name);
        }
    }

    private static JsonElement FindCase(string name)
    {
        foreach (JsonElement c in Cases().EnumerateArray())
            if (c.GetProperty("name").GetString() == name) return c;

        throw new InvalidOperationException($"No layout case named \"{name}\".");
    }

    private static void AssertRect(RectangleF expected, RectangleF actual, string who)
    {
        bool same = MathF.Abs(expected.X - actual.X) < Tolerance
                 && MathF.Abs(expected.Y - actual.Y) < Tolerance
                 && MathF.Abs(expected.Width - actual.Width) < Tolerance
                 && MathF.Abs(expected.Height - actual.Height) < Tolerance;

        Assert.True(same, $"\"{who}\" expected {expected} but was {actual}.");
    }

    // -------------------------------------------------------------------------
    // Invalidation
    // -------------------------------------------------------------------------

    /// <summary>
    /// The whole reason a node caches its rectangle is that the old widget tree recomputed
    /// bounds by walking to the root on every single read. A setter that forgets to mark the
    /// tree dirty gives a layout that is stale for one frame and then correct, which is the
    /// worst kind of bug to reproduce.
    /// </summary>
    [Fact]
    public void ChangingASizeMarksEveryAncestorForMeasuringAgain()
    {
        var canvas = new UiCanvas();
        canvas.SetViewport(800f, 600f);

        UiNode row   = canvas.Root.Add(new UiNode { Layout = LayoutMode.Row });
        UiNode child = row.Add(new UiNode { WidthMode = SizeMode.Fixed, Width = 100f, HeightMode = SizeMode.Fixed, Height = 20f });

        canvas.Layout();
        Assert.False(canvas.Root.MeasureDirty);

        child.Width = 250f;

        Assert.True(child.MeasureDirty);
        Assert.True(row.MeasureDirty);
        Assert.True(canvas.Root.MeasureDirty);

        canvas.Layout();
        Assert.Equal(250f, child.Rect.Width, 3);
    }

    /// <summary>A resize has to move an anchored node, or a rotated phone leaves the HUD off-screen.</summary>
    [Fact]
    public void AnAnchoredNodeMovesWhenTheViewportDoes()
    {
        var canvas = new UiCanvas();
        canvas.SetViewport(800f, 600f);

        UiNode pin = canvas.Root.Add(new UiNode
        {
            Positioning = PositionMode.Absolute,
            Anchor      = UiAnchor.BottomRight,
            Offset      = new Vector2(-12f, -12f),
            WidthMode   = SizeMode.Fixed, Width  = 100f,
            HeightMode  = SizeMode.Fixed, Height = 30f,
        });

        canvas.Layout();
        Assert.Equal(688f, pin.Rect.X, 3);

        canvas.SetViewport(1024f, 768f);
        canvas.Layout();
        Assert.Equal(912f, pin.Rect.X, 3);
        Assert.Equal(726f, pin.Rect.Y, 3);
    }

    // -------------------------------------------------------------------------
    // Hit testing
    // -------------------------------------------------------------------------

    /// <summary>
    /// The canvas this replaces had a public, documented scale matrix that nothing called, so
    /// a pointer stayed in raw screen pixels while the UI was drawn scaled. Hit-testing and
    /// drawing therefore disagreed at every window size but one.
    /// </summary>
    [Theory]
    [InlineData(UiScaleMode.ScaleToFit, 1600, 1200)]
    [InlineData(UiScaleMode.ScaleToFit, 640, 480)]
    [InlineData(UiScaleMode.ScaleToFill, 1600, 900)]
    [InlineData(UiScaleMode.Match, 1280, 720)]
    [InlineData(UiScaleMode.ConstantPixel, 900, 700)]
    public void ThePointerHitsWhateverIsDrawnUnderIt(UiScaleMode mode, int viewportWidth, int viewportHeight)
    {
        var canvas = new UiCanvas { ScaleMode = mode, ReferenceResolution = new Vector2(1920f, 1080f) };
        canvas.SetViewport(viewportWidth, viewportHeight);

        UiNode button = canvas.Root.Add(new UiNode
        {
            Kind        = UiKind.Button,
            Positioning = PositionMode.Absolute,
            Anchor      = UiAnchor.Center,
            WidthMode   = SizeMode.Fixed, Width  = 200f,
            HeightMode  = SizeMode.Fixed, Height = 60f,
        });

        canvas.Layout();

        // The exact middle of the viewport is the exact middle of the centred button.
        var middle = new Vector2(viewportWidth * 0.5f, viewportHeight * 0.5f);
        Assert.Same(button, canvas.HitTest(middle));

        // And a corner of the screen is not.
        Assert.Null(canvas.HitTest(new Vector2(1f, 1f)));
    }

    /// <summary>
    /// A panel with nothing drawn in it is a layout row. If it swallowed clicks, every
    /// container in a UI would be an invisible obstacle in front of its own children.
    /// </summary>
    [Fact]
    public void AnEmptyLayoutRowDoesNotEatTheClickMeantForItsButton()
    {
        var canvas = new UiCanvas();
        canvas.SetViewport(800f, 600f);

        canvas.Root.Layout = LayoutMode.Row;
        UiNode row = canvas.Root.Add(new UiNode { Layout = LayoutMode.Row, WidthMode = SizeMode.Fixed, Width = 400f, HeightMode = SizeMode.Fixed, Height = 100f });
        UiNode button = row.Add(new UiNode { Kind = UiKind.Button, WidthMode = SizeMode.Fixed, Width = 200f, HeightMode = SizeMode.Fixed, Height = 50f });

        canvas.Layout();

        Assert.Same(button, canvas.HitTest(new Vector2(100f, 25f)));
        // Past the button but still inside the row: nothing, because the row paints nothing.
        Assert.Null(canvas.HitTest(new Vector2(300f, 25f)));
    }

    /// <summary>The node a player can see on top is the node they hit.</summary>
    [Fact]
    public void TheTopMostNodeTakesTheHitRatherThanEveryNodeUnderThePointer()
    {
        var canvas = new UiCanvas();
        canvas.SetViewport(800f, 600f);

        UiNode under = canvas.Root.Add(Box("under", 0f, 0f, 200f, 200f));
        UiNode over  = canvas.Root.Add(Box("over", 50f, 50f, 100f, 100f));

        canvas.Layout();

        Assert.Same(over, canvas.HitTest(new Vector2(100f, 100f)));
        Assert.Same(under, canvas.HitTest(new Vector2(180f, 180f)));
    }

    /// <summary>A clipped-away child is not clickable where it is not drawn.</summary>
    [Fact]
    public void ANodeScrolledOutOfSightIsNotClickable()
    {
        var canvas = new UiCanvas();
        canvas.SetViewport(400f, 100f);

        canvas.Root.Layout = LayoutMode.Column;
        canvas.Root.Scroll = ScrollMode.Vertical;

        UiNode first = canvas.Root.Add(Row("first", 200f, 80f));
        for (int i = 1; i < 5; i++) canvas.Root.Add(Row($"row{i}", 200f, 80f));

        canvas.Layout();
        Assert.Same(first, canvas.HitTest(new Vector2(10f, 10f)));

        // Five 80px rows in a 100px window scroll 300px, so 200 is well past the first row.
        canvas.Root.ScrollOffset = new Vector2(0f, 200f);
        canvas.Layout();

        // The first row is now above the viewport, so nothing of it is under the pointer.
        Assert.NotSame(first, canvas.HitTest(new Vector2(10f, 10f)));
    }

    [Fact]
    public void APanelBuiltHiddenLaysOutWhenItIsShown()
    {
        // InvalidateMeasure stops at the first node already fully dirty, which is only
        // sound while a dirty node implies dirty ancestors. Nothing cleared a subtree the
        // layout pass never reached, so a node under a hidden one stayed dirty for ever;
        // the next change inside it broke out of that walk at once, the root was never
        // marked, and UiCanvas.Layout() skipped the pass. A panel built hidden and shown
        // later therefore never laid out at all — zero rect, invisible, un-clickable —
        // which is what a pause menu, a dialog and a card picker all are.
        UiCanvas.ClearAll();
        var canvas = new UiCanvas { ScaleMode = UiScaleMode.ConstantPixel };
        canvas.SetViewport(1280, 720);

        var title = new UiNode { Name = "title", Kind = UiKind.Label, Text = "x" };
        var card = new UiNode
        {
            Name = "card", Visible = false, Layout = LayoutMode.Column,
            WidthMode = SizeMode.Fixed, Width = 200f,
            HeightMode = SizeMode.Fixed, Height = 100f,
        };
        card.Add(title);

        var root = new UiNode { Name = "root", Layout = LayoutMode.Column };
        root.Add(card);
        canvas.Adopt(root);

        canvas.Layout();

        Assert.False(title.MeasureDirty,
            "a node under a hidden one was left dirty, so nothing below it can ever mark the root");

        // What opening a menu does: fill the labels in, then show it.
        title.Text = "Dash Nova";
        card.Visible = true;
        Assert.True(canvas.Root.MeasureDirty, "showing a panel did not reach the root");

        canvas.Layout();
        Assert.Equal(200f, card.Rect.Width);
        Assert.True(title.Rect.Width > 0f, "the label inside it was never measured");
    }

    /// <summary>A box that takes part in its parent's layout.</summary>
    private static UiNode Row(string name, float w, float h) => new()
    {
        Name       = name,
        Kind       = UiKind.Panel,
        Background = Color.White,
        WidthMode  = SizeMode.Fixed, Width  = w,
        HeightMode = SizeMode.Fixed, Height = h,
    };

    private static UiNode Box(string name, float x, float y, float w, float h) => new()
    {
        Name        = name,
        Kind        = UiKind.Panel,
        Background  = Color.White,
        Positioning = PositionMode.Absolute,
        Offset      = new Vector2(x, y),
        WidthMode   = SizeMode.Fixed, Width  = w,
        HeightMode  = SizeMode.Fixed, Height = h,
    };
}
