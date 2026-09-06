using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.UI;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Widgets were written against one cursor: <c>HandleInput</c> takes a position and two booleans
/// and has no pointer id, so nothing could tell two fingers apart. The pointer overload extends
/// that rather than replacing it, and these pin both halves of the bargain.
/// </summary>
public class WidgetPointerTests
{
    /// <summary>A widget from before pointers existed: it only knows the old signature.</summary>
    private sealed class LegacyWidget : Widget
    {
        public int Clicks;

        public LegacyWidget() { Size = new Vector2(100f, 40f); OnClick += () => Clicks++; }

        public override void Draw(SpriteBatch sb, SpriteFont? font) { }
    }

    /// <summary>A widget that claims a pointer by id and follows it, the way a stick does.</summary>
    private sealed class ClaimingWidget : Widget
    {
        public int? Claimed;
        public readonly List<int> Seen = new();

        public ClaimingWidget() => Size = new Vector2(100f, 40f);

        public override void HandlePointer(in Pointer pointer)
        {
            Seen.Add(pointer.Id);

            if (Claimed == null && pointer.JustPressed && ContainsPoint(pointer.Position)) Claimed = pointer.Id;
            else if (Claimed == pointer.Id && pointer.JustReleased)                        Claimed = null;
        }

        public override void Draw(SpriteBatch sb, SpriteFont? font) { }
    }

    private static Pointer Press(int id, Vector2 at) => new()
    {
        Id = id, Position = at, IsDown = true, JustPressed = true,
    };

    private static Pointer Release(int id, Vector2 at) => new()
    {
        Id = id, Position = at, IsDown = false, JustReleased = true,
    };

    [Fact]
    public void AWidgetWrittenForAMouseStillClicksWhenDrivenByAPointer()
    {
        // The compatibility guarantee: thirteen existing widgets override only the old method.
        var widget = new LegacyWidget { Position = Vector2.Zero };

        widget.HandlePointer(Press(Pointer.MouseId, new Vector2(10f, 10f)));

        Assert.Equal(1, widget.Clicks);
    }

    [Fact]
    public void AWidgetOutsideThePointerIsNotClicked()
    {
        var widget = new LegacyWidget { Position = Vector2.Zero };

        widget.HandlePointer(Press(Pointer.MouseId, new Vector2(500f, 500f)));

        Assert.Equal(0, widget.Clicks);
    }

    [Fact]
    public void TwoFingersAreToldApartByTheirIds()
    {
        var widget = new ClaimingWidget { Position = Vector2.Zero };

        widget.HandlePointer(Press(1, new Vector2(10f, 10f)));
        widget.HandlePointer(Press(2, new Vector2(20f, 20f)));

        Assert.Equal(1, widget.Claimed);           // the second finger does not steal the first

        widget.HandlePointer(Release(2, new Vector2(20f, 20f)));
        Assert.Equal(1, widget.Claimed);           // nor does releasing it let go

        widget.HandlePointer(Release(1, new Vector2(10f, 10f)));
        Assert.Null(widget.Claimed);

        Assert.Equal(new[] { 1, 2, 2, 1 }, widget.Seen);
    }

    [Fact]
    public void TheMouseAndAFingerNeverShareAnId()
    {
        // A touch id of 0 would otherwise read as the mouse, and a widget claiming one would
        // follow the other.
        Assert.True(new Pointer { Id = Pointer.MouseId }.IsMouse);
        Assert.False(new Pointer { Id = 1 }.IsMouse);
    }
}
