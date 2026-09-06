using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Input;

namespace Cookies.TouchControls;

/// <summary>
/// Turns mouse drags into a single touch, so on-screen controls can be tried on a desktop. The
/// editor has no touch panel at all, so without this the overlay draws and does nothing.
/// </summary>
/// <remarks>Development aid. Leave it off in a shipped build; it costs a frame of mouse state.</remarks>
public sealed class MouseTouchSimulator : Component
{
    /// <summary>The id the simulated finger uses. Anything a real panel will not produce.</summary>
    public int TouchId { get; set; } = 900;

    private bool _wasDown;

    public override void Update(float dt)
    {
        var input = EngineHost.Current?.Input;
        if (input == null) return;

        bool down = input.IsMouseButtonDown(MouseButton.Left);
        var  at   = input.MousePosition;

        if (!down && !_wasDown)
        {
            _wasDown = false;
            return;
        }

        var phase = down
            ? _wasDown ? TouchPhase.Moved : TouchPhase.Began
            : TouchPhase.Ended;

        input.Touch.SubmitTouches(new[] { new TouchPoint { Id = TouchId, Position = at, Phase = phase } });
        _wasDown = down;
    }
}
