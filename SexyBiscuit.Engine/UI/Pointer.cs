using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// One thing pointing at the UI: the mouse, or one finger. Widgets that only ever want a cursor can
/// ignore this entirely; the ones that want two fingers at once cannot.
/// </summary>
public readonly record struct Pointer
{
    /// <summary>The mouse's id. Touches take 1 and up, so a finger never collides with it.</summary>
    public const int MouseId = 0;

    /// <summary>Stable while the pointer is down, so a widget can hold on to the one it claimed.</summary>
    public int Id { get; init; }

    /// <summary>Where it is, in screen pixels.</summary>
    public Vector2 Position { get; init; }

    public bool IsDown       { get; init; }
    public bool JustPressed  { get; init; }
    public bool JustReleased { get; init; }

    /// <summary>Which player it belongs to, or -1 when it belongs to nobody in particular.</summary>
    public int PlayerIndex { get; init; }

    /// <summary>True for the mouse, false for a finger.</summary>
    public bool IsMouse => Id == MouseId;
}
