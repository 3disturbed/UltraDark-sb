using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.UI;

namespace Cookies.TouchControls;

/// <summary>
/// Keeps a canvas clear of a notch, a rounded corner or a television's overscan.
/// </summary>
/// <remarks>
/// This used to walk a canvas's widgets and clamp each one's position, which broke anything
/// stretched, did nothing for a nested child, and was a no-op for anything anchored rather than
/// positioned. The canvas now insets its own layout rectangle, so every node inherits the safe
/// area from the root and this is the two lines that set it.
/// </remarks>
public sealed class SafeArea : Component
{
    /// <summary>Insets in pixels: left, top, right, bottom.</summary>
    public Vector4 Insets { get; set; } = new(0f, 44f, 0f, 34f);

    public override void Start() => Apply();

    /// <summary>Pushes the insets onto the actor's canvas.</summary>
    public void Apply()
    {
        if (Actor.GetComponent<UiCanvas>() is not { } canvas) return;

        canvas.SafeArea = Insets;
        canvas.SafeAreaMode = SafeAreaMode.Inset;
    }
}
