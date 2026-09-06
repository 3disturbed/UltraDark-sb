using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Input;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Binding evaluation used to be ten private methods on the manager whose gamepad arms all read
/// pad zero, so an action could only ever mean player one. These pin the split that fixed it: the
/// manager keeps behaving exactly as before, and a second player reads only its own devices.
/// </summary>
public class PlayerInputTests
{
    private static InputManager Manager() => new(new EngineConfig());

    private static void Press(InputManager input, params Keys[] keys)
    {
        input.Sample(new KeyboardState(keys), new MouseState());
        input.Sample(new KeyboardState(keys), new MouseState());   // second sample: no longer "just pressed"
    }

    private static void Tap(InputManager input, params Keys[] keys)
    {
        input.Sample(new KeyboardState(), new MouseState());
        input.Sample(new KeyboardState(keys), new MouseState());
    }

    [Fact]
    public void TheManagersQueriesAndPlayerZeroAgree()
    {
        // The regression guard for the delegation: player zero owns every device, so nothing about
        // a single-player game changed when players were introduced.
        var input = Manager();
        Tap(input, Keys.Space, Keys.D);

        Assert.Equal(input.IsPressed("Jump"), input.GetPlayer(0).IsPressed("Jump"));
        Assert.Equal(input.IsHeld("Jump"),    input.GetPlayer(0).IsHeld("Jump"));
        Assert.Equal(input.GetAxis("MoveX"),  input.GetPlayer(0).GetAxis("MoveX"));
        Assert.True(input.IsPressed("Jump"));
        Assert.Equal(1f, input.GetAxis("MoveX"), 3);
    }

    [Fact]
    public void ASecondPlayerDoesNotReadTheFirstPlayersKeyboard()
    {
        var input = Manager();
        var two   = input.GetPlayer(1);
        two.Devices = InputDeviceKind.Gamepad;

        Tap(input, Keys.Space);

        Assert.True(input.GetPlayer(0).IsPressed("Jump"));
        Assert.False(two.IsPressed("Jump"));
    }

    [Fact]
    public void APlayerReadsThePadAtItsOwnIndex()
    {
        var input = Manager();
        var two   = input.GetPlayer(1);
        two.Devices = InputDeviceKind.Gamepad;

        // Pad one pushed right; pad zero at rest.
        input.SampleGamepad(1, new GamePadState(new Vector2(1f, 0f), Vector2.Zero, 0f, 0f, Buttons.A));

        Assert.Equal(1f, two.GetAxis("MoveX"), 3);
        Assert.True(two.IsHeld("Jump"));
        Assert.Equal(0f, input.GetPlayer(0).GetAxis("MoveX"), 3);
        Assert.False(input.GetPlayer(0).IsHeld("Jump"));
    }

    [Fact]
    public void AVirtualPressReadsLikeARealOneAndEndsWhenItIsReleased()
    {
        // An on-screen button, a replicated remote input and an AI all push here, so OnPlayerTick
        // cannot tell them from a device.
        var input  = Manager();
        var player = input.GetPlayer(0);

        player.PressVirtual("Attack");
        Assert.True(player.IsPressed("Attack"));
        Assert.True(player.IsHeld("Attack"));

        input.Sample(new KeyboardState(), new MouseState());
        player.Tick();
        Assert.False(player.IsPressed("Attack"));
        Assert.True(player.IsHeld("Attack"));

        player.ReleaseVirtual("Attack");
        Assert.True(player.IsReleased("Attack"));
        Assert.False(player.IsHeld("Attack"));
    }

    [Fact]
    public void AVirtualAxisWinsUntilItIsCleared()
    {
        var input  = Manager();
        var player = input.GetPlayer(0);

        player.SetVirtualAxis("MoveX", -0.5f);
        Assert.Equal(-0.5f, player.GetAxis("MoveX"), 3);

        player.ClearVirtual("MoveX");
        Assert.Equal(0f, player.GetAxis("MoveX"), 3);
    }

    [Fact]
    public void AnUnownedDeviceReadsAsNothingHappening()
    {
        var input  = Manager();
        var player = input.GetPlayer(0);
        player.Devices = InputDeviceKind.Gamepad;

        Press(input, Keys.Space);

        Assert.False(player.IsHeld("Jump"));
        Assert.Equal(Vector2.Zero, ((IInputSource)player).MouseDelta);
    }

    [Fact]
    public void AJoinScreenSeesAnyPadButton()
    {
        var input = Manager();
        var two   = input.GetPlayer(1);

        Assert.False(two.AnyInputThisFrame());
        input.SampleGamepad(1, new GamePadState(Vector2.Zero, Vector2.Zero, 0f, 0f, Buttons.Start));
        Assert.True(two.AnyInputThisFrame());
    }
}

public class ActionEvaluatorTests
{
    [Theory]
    [InlineData("A", Keys.A)]
    [InlineData("KeyA", Keys.A)]
    [InlineData("ArrowLeft", Keys.Left)]
    [InlineData("Digit1", Keys.D1)]
    [InlineData("ShiftLeft", Keys.LeftShift)]
    [InlineData("Space", Keys.Space)]
    public void ABrowserKeyNameResolvesToTheSameKey(string name, Keys expected)
    {
        // A bindings file written by the HTML5 port says "KeyA"; a bare enum parse rejects it
        // silently, because a binding that will not parse simply never fires.
        Assert.True(ActionEvaluator.TryParseKey(name, out var key));
        Assert.Equal(expected, key);
    }

    [Fact]
    public void AnUnknownKeyNameIsRejectedRatherThanGuessed()
        => Assert.False(ActionEvaluator.TryParseKey("NoSuchKey", out _));

    [Fact]
    public void ScaleAndInvertApplyToAnAxisBinding()
    {
        var input = new InputManager(new EngineConfig());
        input.Sample(new KeyboardState(Keys.D), new MouseState());

        var binding = new InputBinding { Device = "keyboard", PosKey = "D", NegKey = "A", Scale = 2f, Invert = true };

        Assert.Equal(-2f, ActionEvaluator.Axis(binding, input), 3);
    }
}
