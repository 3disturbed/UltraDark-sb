using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Input;
using SexyBiscuit.Engine.UI;

namespace Cookies.TouchControls;

/// <summary>
/// A round action button. Holds one named action down through the player's virtual input layer for
/// as long as a finger is on it.
/// </summary>
/// <remarks>
/// The engine's own touch bindings cannot do this: a touch binding with no axis reads as "a tap
/// anywhere on the screen", which cannot tell two on-screen buttons apart.
/// </remarks>
public sealed class TouchButton : Widget
{
    /// <summary>The action this button holds down.</summary>
    public string Action { get; set; } = "Jump";

    /// <summary>What is written on it. Defaults to the action's first letter.</summary>
    public string? Label { get; set; }

    /// <summary>How big it is, in pixels.</summary>
    public float Radius { get; set; } = 44f;

    /// <summary>Who is pressing it.</summary>
    public PlayerInput? Player { get; set; }

    /// <summary>True while a finger is on it.</summary>
    public bool IsPressed => _pointerId != null;

    private int? _pointerId;

    public override void HandlePointer(in Pointer pointer)
    {
        if (!Visible || !Interactable) return;

        if (_pointerId == null)
        {
            if (!pointer.JustPressed || !ContainsPoint(pointer.Position)) return;

            _pointerId = pointer.Id;
            Player?.PressVirtual(Action);
            RaiseClick();
            return;
        }

        if (pointer.Id != _pointerId) return;

        // Sliding off the button releases it, the way a real button does.
        if (pointer.JustReleased || !pointer.IsDown || !ContainsPoint(pointer.Position)) Release();
    }

    /// <summary>Lets the action go, for a scene change or losing focus.</summary>
    public void Release()
    {
        if (_pointerId == null) return;

        _pointerId = null;
        Player?.ReleaseVirtual(Action);
    }

    public override void Draw(SpriteBatch sb, SpriteFont? font)
    {
        if (!Visible) return;

        var bounds = Bounds;
        var centre = new Vector2(bounds.X + bounds.Width * 0.5f, bounds.Y + bounds.Height * 0.5f);
        float alpha = Opacity * (IsPressed ? 1.6f : 1f);

        int r = (int)Radius;
        for (int y = -r; y <= r; y++)
        {
            int half = (int)MathF.Sqrt(MathF.Max(0f, Radius * Radius - y * y));
            if (half > 0) FillRect(sb, new Rectangle((int)centre.X - half, (int)centre.Y + y, half * 2, 1), Tint * alpha);
        }

        string text = Label ?? (Action.Length > 0 ? Action[..1] : "");
        if (font != null && text.Length > 0) DrawTextInRect(sb, font, text, bounds, Color.White * 0.9f);
    }
}
