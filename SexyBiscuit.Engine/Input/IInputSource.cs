using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace SexyBiscuit.Engine.Input;

/// <summary>
/// The devices one binding is evaluated against. <see cref="InputManager"/> is the whole machine's
/// view of them; a per-player view narrows it to the devices that player owns.
/// </summary>
/// <remarks>
/// This exists so binding evaluation can be a pure function of a map and a device view. Before it,
/// evaluation was ten private methods on the manager whose gamepad arms all read pad zero, so an
/// action could only ever mean player one.
/// </remarks>
public interface IInputSource
{
    bool IsKeyDown(Keys key);
    bool IsKeyPressed(Keys key);
    bool IsKeyReleased(Keys key);

    bool IsMouseButtonDown(MouseButton button);
    bool IsMouseButtonPressed(MouseButton button);
    bool IsMouseButtonReleased(MouseButton button);

    Vector2 MouseDelta  { get; }
    float   ScrollDelta { get; }

    /// <summary>The gamepad this view reads, or null when it owns none or none is connected.</summary>
    GamepadState? ActiveGamepad { get; }

    /// <summary>The touch screen this view reads, or null when it owns none.</summary>
    TouchManager? ActiveTouch { get; }
}
