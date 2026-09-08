using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.UI;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The focus model, which is the whole reason a gamepad or a TV remote can drive a UI
/// at all — the engine had none, so nothing but a pointer could reach a widget.
/// </summary>
public class UiFocusTests
{
    private static UiCanvas Canvas(int width = 800, int height = 600)
    {
        var canvas = new UiCanvas();
        canvas.SetViewport(width, height);
        return canvas;
    }

    private static UiNode Button(string name, float x, float y, float w = 200f, float h = 40f) => new()
    {
        Name        = name,
        Kind        = UiKind.Button,
        Positioning = PositionMode.Absolute,
        Offset      = new Vector2(x, y),
        WidthMode   = SizeMode.Fixed, Width  = w,
        HeightMode  = SizeMode.Fixed, Height = h,
    };

    // -------------------------------------------------------------------------
    // What can take focus
    // -------------------------------------------------------------------------

    [Fact]
    public void ALabelIsNotFocusableButAButtonIs()
    {
        UiCanvas canvas = Canvas();
        UiNode label = canvas.Root.Add(new UiNode { Kind = UiKind.Label, Name = "title", Text = "Paused" });
        UiNode button = canvas.Root.Add(Button("play", 0f, 60f));
        canvas.Layout();

        Assert.False(UiFocus.IsFocusable(label));
        Assert.True(UiFocus.IsFocusable(button));
    }

    /// <summary>A hidden panel's buttons must not be reachable, or focus lands off-screen.</summary>
    [Fact]
    public void AButtonInsideAHiddenPanelCannotTakeFocus()
    {
        UiCanvas canvas = Canvas();
        UiNode panel = canvas.Root.Add(new UiNode { Name = "panel", WidthMode = SizeMode.Stretch, HeightMode = SizeMode.Stretch });
        UiNode button = panel.Add(Button("hidden", 0f, 0f));
        canvas.Layout();
        Assert.True(UiFocus.IsFocusable(button));

        panel.Visible = false;
        canvas.Layout();
        Assert.False(UiFocus.IsFocusable(button));
    }

    /// <summary>
    /// A row scrolled out of sight is not clickable — a pointer cannot reach what it
    /// cannot see — but it must stay navigable, or a long list would be impossible to
    /// move through with a stick.
    /// </summary>
    [Fact]
    public void ARowScrolledOutOfSightStaysNavigableEvenThoughItIsNotClickable()
    {
        UiCanvas canvas = Canvas(400, 100);
        canvas.Root.Layout = LayoutMode.Column;
        canvas.Root.Scroll = ScrollMode.Vertical;

        var rows = new List<UiNode>();
        for (int i = 0; i < 5; i++)
            rows.Add(canvas.Root.Add(new UiNode
            {
                Name = $"row{i}", Kind = UiKind.Button,
                WidthMode = SizeMode.Fixed, Width = 200f,
                HeightMode = SizeMode.Fixed, Height = 80f,
            }));

        canvas.Root.ScrollOffset = new Vector2(0f, 200f);
        canvas.Layout();

        Assert.False(UiFocus.IsFocusable(rows[0]) && canvas.HitTest(rows[0].Rect.Centre) == rows[0]);
        Assert.True(UiFocus.IsFocusable(rows[0]));
    }

    // -------------------------------------------------------------------------
    // Scopes
    // -------------------------------------------------------------------------

    /// <summary>
    /// A pause menu must not be able to lose the cursor to the HUD behind it. One flag
    /// does it for every input class at once.
    /// </summary>
    [Fact]
    public void AModalTrapsFocusInsideItself()
    {
        UiCanvas canvas = Canvas();
        UiNode hud = canvas.Root.Add(Button("hudButton", 0f, 0f));

        UiNode dialog = canvas.Root.Add(new UiNode
        {
            Name = "dialog", Modal = true,
            Positioning = PositionMode.Absolute, Anchor = UiAnchor.Center,
            WidthMode = SizeMode.Fixed, Width = 300f,
            HeightMode = SizeMode.Fixed, Height = 200f,
            Layout = LayoutMode.Column,
        });
        UiNode ok = dialog.Add(new UiNode { Name = "ok", Kind = UiKind.Button, WidthMode = SizeMode.Fixed, Width = 100f, HeightMode = SizeMode.Fixed, Height = 40f });
        dialog.Add(new UiNode { Name = "cancel", Kind = UiKind.Button, WidthMode = SizeMode.Fixed, Width = 100f, HeightMode = SizeMode.Fixed, Height = 40f });

        canvas.Layout();
        var focus = new UiFocus(canvas.Root);

        Assert.Same(dialog, focus.ActiveScope);
        Assert.DoesNotContain(hud, focus.Candidates());

        focus.FocusFirst();
        Assert.Same(ok, focus.Focused);

        // Every direction, repeatedly: focus must never escape the dialog.
        foreach (NavDirection d in new[] { NavDirection.Up, NavDirection.Down, NavDirection.Left, NavDirection.Right })
            for (int i = 0; i < 4; i++)
            {
                focus.Navigate(d);
                Assert.NotSame(hud, focus.Focused);
            }

        dialog.Visible = false;
        canvas.Layout();
        Assert.Same(canvas.Root, focus.ActiveScope);
    }

    // -------------------------------------------------------------------------
    // Moving focus
    // -------------------------------------------------------------------------

    [Fact]
    public void NavigatingAColumnWalksItInOrderAndStopsAtTheEnd()
    {
        UiCanvas canvas = Canvas();
        UiNode a = canvas.Root.Add(Button("a", 0f, 0f));
        UiNode b = canvas.Root.Add(Button("b", 0f, 60f));
        UiNode c = canvas.Root.Add(Button("c", 0f, 120f));
        canvas.Layout();

        var focus = new UiFocus(canvas.Root);
        focus.FocusFirst();
        Assert.Same(a, focus.Focused);

        Assert.True(focus.Navigate(NavDirection.Down));
        Assert.Same(b, focus.Focused);

        Assert.True(focus.Navigate(NavDirection.Down));
        Assert.Same(c, focus.Focused);

        // Nothing below the last one, so nothing happens rather than wrapping surprisingly.
        Assert.False(focus.Navigate(NavDirection.Down));
        Assert.Same(c, focus.Focused);

        Assert.True(focus.Navigate(NavDirection.Up));
        Assert.Same(b, focus.Focused);
    }

    /// <summary>
    /// An explicit order should survive one of its items being disabled, without the
    /// author writing any conditionals.
    /// </summary>
    [Fact]
    public void AnOverrideChainHopsOverALinkThatCannotTakeFocus()
    {
        UiCanvas canvas = Canvas();
        UiNode a = canvas.Root.Add(Button("a", 0f, 0f));
        UiNode b = canvas.Root.Add(Button("b", 0f, 60f));
        UiNode c = canvas.Root.Add(Button("c", 0f, 120f));

        a.NavDown = "b";
        b.NavDown = "c";
        canvas.Layout();

        var focus = new UiFocus(canvas.Root);
        focus.Focus(a);

        Assert.True(focus.Navigate(NavDirection.Down));
        Assert.Same(b, focus.Focused);

        // Disable the middle link; the chain should carry on to c rather than dead-end.
        focus.Focus(a);
        b.Focusable = Focusability.No;
        Assert.True(focus.Navigate(NavDirection.Down));
        Assert.Same(c, focus.Focused);
    }

    /// <summary>
    /// Deleting a row near the bottom of a list must not teleport the player back to the
    /// top of it, which is what "focus the first child" would do.
    /// </summary>
    [Fact]
    public void LosingTheFocusedNodeMovesToTheNearestOneRatherThanTheFirst()
    {
        UiCanvas canvas = Canvas();
        canvas.Root.Add(Button("first", 0f, 0f));
        UiNode fourth = canvas.Root.Add(Button("fourth", 0f, 180f));
        UiNode fifth = canvas.Root.Add(Button("fifth", 0f, 240f));
        canvas.Layout();

        var focus = new UiFocus(canvas.Root);
        focus.Focus(fifth);

        fifth.Visible = false;
        canvas.Layout();

        Assert.True(focus.Repair());
        Assert.Same(fourth, focus.Focused);
    }

    /// <summary>A two-column dialog should open on its top-left control, not its second column.</summary>
    [Fact]
    public void InitialFocusFollowsReadingOrderRatherThanTheOrderNodesWereAdded()
    {
        UiCanvas canvas = Canvas();
        canvas.Root.Add(Button("rightColumnTop", 400f, 0f, 150f, 40f));
        UiNode topLeft = canvas.Root.Add(Button("leftColumnTop", 0f, 0f, 150f, 40f));
        canvas.Root.Add(Button("leftColumnNext", 0f, 60f, 150f, 40f));
        canvas.Layout();

        var focus = new UiFocus(canvas.Root);
        focus.FocusFirst();

        Assert.Same(topLeft, focus.Focused);
    }
}

/// <summary>
/// Which device is believed to be driving, and how hard it is to change that belief.
/// </summary>
public class UiInputModeTests
{
    /// <summary>
    /// A worn thumbstick resting past the threshold must not make the focus ring appear
    /// while somebody is using the mouse.
    /// </summary>
    [Fact]
    public void AThumbstickRestingOffCentreEngagesOnceAndThenStaysQuiet()
    {
        var modes = new UiInputModeTracker();
        modes.NoteMouseButton();
        Assert.Equal(UiInputMode.Pointer, modes.Mode);

        // A stick resting at 0.6, frame after frame, is one deliberate push, not sixty.
        Assert.False(modes.NoteNavigationAxis(0.6f));   // first frame above: not yet believed
        Assert.True(modes.NoteNavigationAxis(0.6f));    // second frame: believed
        Assert.Equal(UiInputMode.Directional, modes.Mode);

        modes.NoteMouseButton();
        Assert.Equal(UiInputMode.Pointer, modes.Mode);

        // Still resting at 0.6 and never released: it must not claim the mode again.
        for (int i = 0; i < 60; i++) Assert.False(modes.NoteNavigationAxis(0.6f));
        Assert.Equal(UiInputMode.Pointer, modes.Mode);
    }

    /// <summary>A shove hard enough is believed straight away; waiting a frame would feel laggy.</summary>
    [Fact]
    public void ADecisivePushIsBelievedOnTheFirstFrame()
    {
        var modes = new UiInputModeTracker();
        Assert.True(modes.NoteNavigationAxis(0.9f));
        Assert.Equal(UiInputMode.Directional, modes.Mode);
    }

    /// <summary>
    /// A trackpad under a palm, or a mouse on a slightly uneven desk, must not steal the
    /// mode back from a gamepad one pixel at a time.
    /// </summary>
    [Fact]
    public void AJitteringMouseNeverOutrunsTheDecayButARealMovementDoes()
    {
        var modes = new UiInputModeTracker();
        modes.NoteNavigationButton();
        Assert.Equal(UiInputMode.Directional, modes.Mode);

        for (int i = 0; i < 120; i++)
        {
            modes.Tick(1f / 60f);
            Assert.False(modes.NoteMouseMotion(new Vector2(0.5f, 0f)));
        }
        Assert.Equal(UiInputMode.Directional, modes.Mode);

        // Somebody actually reaching for the mouse crosses in a couple of frames.
        modes.NoteMouseMotion(new Vector2(6f, 0f));
        Assert.True(modes.NoteMouseMotion(new Vector2(6f, 0f)));
        Assert.Equal(UiInputMode.Pointer, modes.Mode);
    }

    /// <summary>
    /// The ring shows only under directional input and hover only under a pointer, which
    /// is what lets one UI feel native on a pad, a mouse and a phone at once.
    /// </summary>
    [Fact]
    public void TheRingAndTheHoverBelongToDifferentDevices()
    {
        var modes = new UiInputModeTracker();

        modes.NoteMouseButton();
        Assert.True(modes.ShowHover);
        Assert.False(modes.ShowFocusRing);

        modes.NoteNavigationButton();
        Assert.False(modes.ShowHover);
        Assert.True(modes.ShowFocusRing);

        modes.NoteTouch();
        Assert.False(modes.ShowHover);
        Assert.False(modes.ShowFocusRing);
    }

    /// <summary>A tap must not be immediately undone by whatever the OS reports as mouse movement.</summary>
    [Fact]
    public void AMouseEventJustAfterATouchIsIgnored()
    {
        var modes = new UiInputModeTracker();
        modes.NoteTouch();

        modes.Tick(0.1f);
        Assert.False(modes.NoteMouseMotion(new Vector2(50f, 0f)));
        Assert.Equal(UiInputMode.Touch, modes.Mode);

        modes.Tick(1f);
        Assert.True(modes.NoteMouseMotion(new Vector2(50f, 0f)));
        Assert.Equal(UiInputMode.Pointer, modes.Mode);
    }
}
