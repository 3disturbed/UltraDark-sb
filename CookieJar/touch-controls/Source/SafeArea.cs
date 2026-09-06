using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.UI;

namespace Cookies.TouchControls;

/// <summary>
/// Keeps a canvas clear of a notch or a rounded corner by insetting everything on it.
/// </summary>
/// <remarks>
/// DesktopGL exposes no safe-area API, so the insets are set by hand rather than discovered. Take
/// them from the device you are shipping to.
/// </remarks>
public sealed class SafeArea : Component
{
    /// <summary>Insets in pixels: left, top, right, bottom.</summary>
    public Vector4 Insets { get; set; } = new(0f, 44f, 0f, 34f);

    /// <summary>Applies the insets once on start, and again whenever the window resizes.</summary>
    public bool FollowResize { get; set; } = true;

    private Point _lastSize;

    public override void Start() => Apply();

    public override void Update(float dt)
    {
        if (!FollowResize) return;

        var size = ScreenSize();
        if (size == _lastSize) return;

        _lastSize = size;
        Apply();
    }

    /// <summary>Nudges every widget on this actor's canvas inside the insets.</summary>
    public void Apply()
    {
        if (Actor.GetComponent<Canvas>() is not { } canvas) return;

        var size = ScreenSize();
        _lastSize = size;

        foreach (var widget in canvas.Children)
        {
            var position = widget.Position;

            position.X = Math.Clamp(position.X, Insets.X, MathF.Max(Insets.X, size.X - Insets.Z - widget.Size.X));
            position.Y = Math.Clamp(position.Y, Insets.Y, MathF.Max(Insets.Y, size.Y - Insets.W - widget.Size.Y));

            widget.Position = position;
        }
    }

    private static Point ScreenSize()
    {
        var device = EngineHost.Current?.GraphicsDevice;
        return device == null
            ? new Point(1920, 1080)
            : new Point(device.PresentationParameters.BackBufferWidth, device.PresentationParameters.BackBufferHeight);
    }
}
