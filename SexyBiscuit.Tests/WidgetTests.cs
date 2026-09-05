using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Assets;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;
using Xunit;

namespace SexyBiscuit.Tests;

public class CheckboxTests
{
    /// <summary>Clicks a widget: press inside, then release inside.</summary>
    private static void Click(Widget w, Vector2 at)
    {
        w.HandleInput(at, mouseDown: true,  mouseJustPressed: true);
        w.HandleInput(at, mouseDown: false, mouseJustPressed: false);
    }

    private static Vector2 Centre(Widget w)
        => new(w.Bounds.Center.X, w.Bounds.Center.Y);

    [Fact]
    public void ClickingTogglesTheState()
    {
        var box = new Checkbox();
        Click(box, Centre(box));
        Assert.True(box.IsChecked);

        Click(box, Centre(box));
        Assert.False(box.IsChecked);
    }

    [Fact]
    public void ReleasingOutsideCancelsTheClick()
    {
        var box = new Checkbox();
        box.HandleInput(Centre(box), mouseDown: true, mouseJustPressed: true);
        box.HandleInput(new Vector2(9999, 9999), mouseDown: false, mouseJustPressed: false);

        Assert.False(box.IsChecked);
    }

    [Fact]
    public void CheckedChangedFiresOnceForARealChange()
    {
        var box = new Checkbox();
        int fired = 0;
        box.CheckedChanged.Add(_ => fired++);

        box.IsChecked = true;
        box.IsChecked = true;   // no change, no event

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ANonInteractableCheckboxIgnoresClicks()
    {
        var box = new Checkbox { Interactable = false };
        Click(box, Centre(box));
        Assert.False(box.IsChecked);
    }
}

public class RadioGroupTests
{
    [Fact]
    public void TheFirstOptionAddedBecomesTheSelection()
    {
        var group = new RadioGroup();
        var first = group.Add(new Toggle { Text = "A" });
        group.Add(new Toggle { Text = "B" });

        Assert.Same(first, group.Selected);
        Assert.True(first.IsChecked);
    }

    [Fact]
    public void SelectingOneUnchecksTheRest()
    {
        var group = new RadioGroup();
        var a = group.Add(new Toggle());
        var b = group.Add(new Toggle());
        var c = group.Add(new Toggle());

        group.Select(c);

        Assert.False(a.IsChecked);
        Assert.False(b.IsChecked);
        Assert.True(c.IsChecked);
    }

    [Fact]
    public void ClickingTheSelectedOptionDoesNotUncheckIt()
    {
        var group = new RadioGroup();
        var a = group.Add(new Toggle());
        group.Add(new Toggle());

        var centre = new Vector2(a.Bounds.Center.X, a.Bounds.Center.Y);
        a.HandleInput(centre, mouseDown: true, mouseJustPressed: true);
        a.HandleInput(centre, mouseDown: false, mouseJustPressed: false);

        Assert.True(a.IsChecked);
    }

    [Fact]
    public void SelectByValueFindsTheMatchingOption()
    {
        var group = new RadioGroup();
        group.Add(new Toggle { Value = "easy" });
        group.Add(new Toggle { Value = "hard" });

        Assert.True(group.SelectByValue("hard"));
        Assert.Equal("hard", group.SelectedValue);
        Assert.False(group.SelectByValue("nightmare"));
    }

    [Fact]
    public void SelectionChangedFiresOnlyOnAnActualChange()
    {
        var group = new RadioGroup();
        var a = group.Add(new Toggle());
        var b = group.Add(new Toggle());

        int fired = 0;
        group.SelectionChanged.Add(_ => fired++);

        group.Select(b);
        group.Select(b);

        Assert.Equal(1, fired);
    }
}

public class DropdownTests
{
    [Fact]
    public void SetOptionsSelectsTheFirstEntry()
    {
        var dd = new Dropdown();
        dd.SetOptions("Low", "High");
        Assert.Equal(0, dd.SelectedIndex);
        Assert.Equal("Low", dd.SelectedText);
    }

    [Fact]
    public void AnEmptyDropdownShowsThePlaceholder()
    {
        var dd = new Dropdown { Placeholder = "Pick one" };
        Assert.Equal(-1, dd.SelectedIndex);
        Assert.Equal("Pick one", dd.SelectedText);
    }

    [Fact]
    public void SelectedIndexIsClampedIntoRange()
    {
        var dd = new Dropdown();
        dd.SetOptions("A", "B", "C");

        dd.SelectedIndex = 99;
        Assert.Equal(2, dd.SelectedIndex);

        dd.SelectedIndex = -5;
        Assert.Equal(0, dd.SelectedIndex);
    }

    [Fact]
    public void ClickingTheClosedControlExpandsIt()
    {
        var dd = new Dropdown();
        dd.SetOptions("A", "B");

        var centre = new Vector2(dd.Bounds.Center.X, dd.Bounds.Center.Y);
        dd.HandleInput(centre, mouseDown: true, mouseJustPressed: true);

        Assert.True(dd.IsExpanded);
    }

    [Fact]
    public void ClickingAwayDismissesTheOpenList()
    {
        var dd = new Dropdown();
        dd.SetOptions("A", "B");

        var centre = new Vector2(dd.Bounds.Center.X, dd.Bounds.Center.Y);
        dd.HandleInput(centre, mouseDown: true, mouseJustPressed: true);
        dd.HandleInput(new Vector2(9999, 9999), mouseDown: true, mouseJustPressed: true);

        Assert.False(dd.IsExpanded);
    }
}

public class TabViewTests
{
    [Fact]
    public void TheFirstPageAddedIsSelected()
    {
        var tabs = new TabView();
        tabs.AddPage("One");
        tabs.AddPage("Two");

        Assert.Equal(0, tabs.SelectedIndex);
        Assert.Equal("One", tabs.SelectedPage!.Title);
    }

    [Fact]
    public void RemovingThePageBeforeTheSelectionKeepsTheIndexInRange()
    {
        var tabs = new TabView();
        var a = tabs.AddPage("A");
        tabs.AddPage("B");
        tabs.SelectedIndex = 1;

        tabs.RemovePage(a);

        Assert.InRange(tabs.SelectedIndex, 0, tabs.Pages.Count - 1);
    }

    [Fact]
    public void RemovingTheLastPageClearsTheSelection()
    {
        var tabs = new TabView();
        var only = tabs.AddPage("Only");
        tabs.RemovePage(only);

        Assert.Equal(-1, tabs.SelectedIndex);
        Assert.Null(tabs.SelectedPage);
    }

    [Fact]
    public void ContentBoundsSitBelowTheTabBar()
    {
        var tabs = new TabView { Size = new Vector2(400, 300), TabBarHeight = 30 };
        Assert.Equal(270, tabs.ContentBounds.Height);
        Assert.Equal(30, tabs.ContentBounds.Y - tabs.Bounds.Y);
    }

    [Fact]
    public void SelectedChangedFiresOnlyOnAnActualChange()
    {
        var tabs = new TabView();
        tabs.AddPage("A");
        tabs.AddPage("B");

        int fired = 0;
        tabs.SelectedChanged.Add(_ => fired++);

        tabs.SelectedIndex = 1;
        tabs.SelectedIndex = 1;

        Assert.Equal(1, fired);
    }
}

public class LoadingScreenTests
{
    // AssetManager needs a GraphicsDevice, so these drive the scheduling half of
    // LoadingScreen through the injectable load action.
    private static LoadingScreen MakeLoader() => new(_ => { });

    private static LoadingScreen MakeFailingLoader()
        => new(r => throw new FileNotFoundException($"no such asset: {r.Path}"));

    [Fact]
    public void AnEmptyBatchCompletesImmediately()
    {
        var loader = MakeLoader();
        bool completed = false;
        loader.Completed.Add(() => completed = true);

        loader.Start();

        Assert.True(completed);
        Assert.True(loader.IsComplete);
        Assert.Equal(1f, loader.Progress);
    }

    [Fact]
    public void ProgressIsWeightedByAssetCost()
    {
        var loader = MakeLoader();
        loader.Add<object>("big.png", weight: 3f);
        loader.Add<object>("small.png", weight: 1f);

        loader.ItemsPerFrame = 1;
        loader.Start();

        loader.Tick();
        Assert.Equal(0.75f, loader.Progress, 3);   // 3 of 4 weight units

        loader.Tick();
        Assert.Equal(1f, loader.Progress, 3);
    }

    [Fact]
    public void ItemsPerFrameLimitsHowMuchOneTickDoes()
    {
        var loader = MakeLoader();
        for (int i = 0; i < 10; i++) loader.Add<object>($"a{i}.png");

        loader.ItemsPerFrame = 3;
        loader.Start();
        loader.Tick();

        Assert.Equal(0.3f, loader.Progress, 3);
        Assert.False(loader.IsComplete);
    }

    [Fact]
    public void FailuresAreRecordedAndDoNotStopTheBatch()
    {
        var loader = MakeFailingLoader();
        loader.Add<object>("missing-one.png");
        loader.Add<object>("missing-two.png");

        loader.ItemsPerFrame = 10;
        loader.Start();
        loader.Tick();

        Assert.True(loader.IsComplete);
        Assert.Equal(2, loader.Failures.Count);
    }

    [Fact]
    public void CompletedFiresExactlyOnce()
    {
        var loader = MakeLoader();
        loader.Add<object>("x.png");

        int fired = 0;
        loader.Completed.Add(() => fired++);

        loader.Start();
        loader.Tick();
        loader.Tick();   // extra ticks after completion do nothing

        Assert.Equal(1, fired);
    }

    [Fact]
    public void AManifestQueuesItsEntriesAndReportsBadLines()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sb-manifest-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Join('\n', new[]
        {
            "# a comment",
            "",
            "String|Assets/one.txt",
            "NoSuchType|Assets/two.txt",
            "malformed line",
        }));

        try
        {
            var loader = MakeLoader();
            loader.AddManifest(path);
            loader.ItemsPerFrame = 10;
            loader.Start();
            loader.Tick();

            // One well-formed entry was queued; the bad type and the malformed
            // line were recorded rather than throwing.
            Assert.Contains(loader.Failures, f => f.error.Contains("unknown type"));
            Assert.Contains(loader.Failures, f => f.error.Contains("expected"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class CameraShakeTests
{
    private static CameraShake Attach()
    {
        var actor = new Actor("Camera");
        actor.AddComponent<Transform3D>();
        var shake = actor.AddComponent<CameraShake>();
        shake.Start();
        return shake;
    }

    [Fact]
    public void TraumaSaturatesAtOne()
    {
        var shake = Attach();
        shake.AddTrauma(0.7f);
        shake.AddTrauma(0.7f);
        Assert.Equal(1f, shake.Trauma, 4);
    }

    [Fact]
    public void TraumaDecaysToZero()
    {
        var shake = Attach();
        shake.UseUnscaledTime = false;
        shake.DecayPerSecond = 1f;
        shake.AddTrauma(1f);

        for (int i = 0; i < 70; i++) shake.LateUpdate(1f / 60f);

        Assert.Equal(0f, shake.Trauma, 4);
    }

    [Fact]
    public void TheCameraReturnsToItsBasePoseOnceTheShakeEnds()
    {
        var shake = Attach();
        shake.UseUnscaledTime = false;
        var t = shake.Actor.GetComponent<Transform3D>()!;
        t.LocalPosition = new Vector3(1f, 2f, 3f);

        shake.AddTrauma(1f);
        for (int i = 0; i < 5; i++) shake.LateUpdate(1f / 60f);

        shake.Reset();
        shake.LateUpdate(1f / 60f);

        Assert.Equal(1f, t.LocalPosition.X, 4);
        Assert.Equal(2f, t.LocalPosition.Y, 4);
        Assert.Equal(3f, t.LocalPosition.Z, 4);
    }

    [Fact]
    public void NoTraumaMeansNoOffset()
    {
        var shake = Attach();
        shake.LateUpdate(1f / 60f);
        Assert.Equal(Vector3.Zero, shake.CurrentOffset);
    }

    [Fact]
    public void TheOffsetStaysWithinMaxOffset()
    {
        var shake = Attach();
        shake.UseUnscaledTime = false;
        shake.MaxOffset = 0.5f;
        shake.DecayPerSecond = 0f;
        shake.AddTrauma(1f);

        for (int i = 0; i < 200; i++)
        {
            shake.LateUpdate(1f / 60f);
            Assert.InRange(shake.CurrentOffset.X, -0.5f, 0.5f);
            Assert.InRange(shake.CurrentOffset.Y, -0.5f, 0.5f);
            Assert.InRange(shake.CurrentOffset.Z, -0.5f, 0.5f);
        }
    }
}
