using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Input;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Touch as an action-map device.
/// </summary>
/// <remarks>
/// Touch existed — touch points, a virtual joystick, pinch — but was reachable only through
/// <c>InputManager.Touch</c> directly. No action could be driven by a thumbstick, so a game
/// written against the action map had no way to run on a phone short of bypassing the map, and
/// with it rebinding and gamepad support. These tests drive the manager the way a device would.
/// </remarks>
public class TouchActionTests
{
    private const int ScreenWidth  = 800;
    private const int ScreenHeight = 600;

    private static TouchPoint Touch(int id, float x, float y, TouchPhase phase) => new()
    {
        Id = id,
        Position = new Vector2(x, y),
        Phase = phase,
    };

    private static InputManager NewInput()
    {
        var input = new InputManager(new EngineConfig());
        input.Touch.SetScreenSize(ScreenWidth, ScreenHeight);
        return input;
    }

    /// <summary>A thumb on the left half drives the movement axes through the default map.</summary>
    [Fact]
    public void TheLeftThumbstickDrivesTheMovementActions()
    {
        var input = NewInput();

        // Land a finger, then drag it right by a full stick radius.
        input.Touch.ApplyTouches(new[] { Touch(1, 100f, 400f, TouchPhase.Began) });
        Assert.Equal(0f, input.GetAxis("MoveX"), 3);

        input.Touch.ApplyTouches(new[] { Touch(1, 100f + input.Touch.LeftJoystick.Radius, 400f, TouchPhase.Moved) });

        Assert.Equal(1f, input.GetAxis("MoveX"), 2);
    }

    /// <summary>
    /// Pushing the stick up has to read the same sign as pressing W, which the keyboard binding
    /// produces by inverting. The stick's Y is screen-space and already negative upwards.
    /// </summary>
    [Fact]
    public void PushingTheStickUpMatchesTheSignOfPressingForward()
    {
        var input = NewInput();

        input.Touch.ApplyTouches(new[] { Touch(1, 100f, 400f, TouchPhase.Began) });
        input.Touch.ApplyTouches(new[] { Touch(1, 100f, 400f - input.Touch.LeftJoystick.Radius, TouchPhase.Moved) });

        Assert.Equal(-1f, input.GetAxis("MoveY"), 2);
    }

    /// <summary>Two thumbs, two sticks, no fighting over either finger.</summary>
    [Fact]
    public void TheTwoSticksClaimOppositeHalvesOfTheScreen()
    {
        var input = NewInput();
        float radius = input.Touch.LeftJoystick.Radius;

        input.Touch.ApplyTouches(new[]
        {
            Touch(1, 100f, 400f, TouchPhase.Began),   // left half
            Touch(2, 700f, 400f, TouchPhase.Began),   // right half
        });

        input.Touch.ApplyTouches(new[]
        {
            Touch(1, 100f + radius, 400f, TouchPhase.Moved),
            Touch(2, 700f, 400f - radius, TouchPhase.Moved),
        });

        Assert.Equal(1f,  input.GetAxis("MoveX"), 2);
        Assert.Equal(-1f, input.GetAxis("CameraY"), 2);
        Assert.Equal(0f,  input.GetAxis("MoveY"), 2);
        Assert.Equal(0f,  input.GetAxis("CameraX"), 2);
    }

    /// <summary>A touch binding with no axis is a tap anywhere on the screen.</summary>
    [Fact]
    public void ATouchBindingWithoutAnAxisReadsAsAScreenTap()
    {
        var input = NewInput();
        input.RebindAction("Fire", new InputBinding { Device = "touch" });

        Assert.False(input.IsHeld("Fire"));

        input.Touch.ApplyTouches(new[] { Touch(1, 400f, 300f, TouchPhase.Began) });
        Assert.True(input.IsPressed("Fire"));
        Assert.True(input.IsHeld("Fire"));

        input.Touch.ApplyTouches(new[] { Touch(1, 400f, 300f, TouchPhase.Stationary) });
        Assert.False(input.IsPressed("Fire"));
        Assert.True(input.IsHeld("Fire"));

        input.Touch.ApplyTouches(new[] { Touch(1, 400f, 300f, TouchPhase.Ended) });
        Assert.True(input.IsReleased("Fire"));
    }

    /// <summary>
    /// PinchDelta reported zero on every frame: it compared the current distance against a
    /// previous-position table that had already been advanced to the current positions.
    /// </summary>
    [Fact]
    public void PinchReportsTheChangeInFingerDistance()
    {
        var input = NewInput();

        input.Touch.ApplyTouches(new[]
        {
            Touch(1, 300f, 300f, TouchPhase.Began),
            Touch(2, 400f, 300f, TouchPhase.Began),
        });
        Assert.Equal(0f, input.Touch.PinchDelta, 3);

        // Spread them from 100 apart to 160 apart.
        input.Touch.ApplyTouches(new[]
        {
            Touch(1, 270f, 300f, TouchPhase.Moved),
            Touch(2, 430f, 300f, TouchPhase.Moved),
        });
        Assert.Equal(60f, input.Touch.PinchDelta, 2);

        // And back together.
        input.Touch.ApplyTouches(new[]
        {
            Touch(1, 300f, 300f, TouchPhase.Moved),
            Touch(2, 400f, 300f, TouchPhase.Moved),
        });
        Assert.Equal(-60f, input.Touch.PinchDelta, 2);
    }

    /// <summary>Pinch is reachable as an axis, so a zoom control can be rebound like anything else.</summary>
    [Fact]
    public void PinchIsReachableThroughTheActionMap()
    {
        var input = NewInput();
        input.RebindAction("Zoom", new InputBinding { Device = "touch", Axis = "PinchDelta" });

        input.Touch.ApplyTouches(new[]
        {
            Touch(1, 300f, 300f, TouchPhase.Began),
            Touch(2, 400f, 300f, TouchPhase.Began),
        });
        input.Touch.ApplyTouches(new[]
        {
            Touch(1, 280f, 300f, TouchPhase.Moved),
            Touch(2, 420f, 300f, TouchPhase.Moved),
        });

        Assert.Equal(40f, input.GetAxis("Zoom"), 2);
    }

    /// <summary>Lifting the finger releases the stick rather than leaving it deflected.</summary>
    [Fact]
    public void LiftingTheThumbRecentresTheStick()
    {
        var input = NewInput();

        input.Touch.ApplyTouches(new[] { Touch(1, 100f, 400f, TouchPhase.Began) });
        input.Touch.ApplyTouches(new[] { Touch(1, 180f, 400f, TouchPhase.Moved) });
        Assert.True(input.GetAxis("MoveX") > 0.9f);

        input.Touch.ApplyTouches(new[] { Touch(1, 180f, 400f, TouchPhase.Ended) });
        Assert.Equal(0f, input.GetAxis("MoveX"), 3);
        Assert.False(input.Touch.LeftJoystick.IsActive);
    }

    /// <summary>
    /// The default map has to be playable on a phone as it stands, or every project starts by
    /// discovering that it is not.
    /// </summary>
    [Fact]
    public void TheDefaultMapBindsTouchForMovementAndCamera()
    {
        var map = ActionMap.Default();

        foreach (var action in new[] { "MoveX", "MoveY", "CameraX", "CameraY" })
        {
            Assert.True(
                map.Actions[action].Bindings.Any(b => b.Device == "touch" && b.Axis != null),
                $"'{action}' has no touch binding");
        }
    }

    /// <summary>Touch bindings have to survive a save and load like any other device.</summary>
    [Fact]
    public void ATouchBindingRoundTripsThroughJson()
    {
        var original = ActionMap.Default();
        var restored = original.Clone();

        var before = original.Actions["MoveX"].Bindings.First(b => b.Device == "touch");
        var after  = restored.Actions["MoveX"].Bindings.First(b => b.Device == "touch");

        Assert.Equal(before.Axis, after.Axis);
        Assert.Equal("LeftJoystickX", after.Axis);
    }
}
