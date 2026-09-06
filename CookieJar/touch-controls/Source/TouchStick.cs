using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Input;
using SexyBiscuit.Engine.UI;

namespace Cookies.TouchControls;

/// <summary>
/// A thumb stick drawn on screen. Claims one pointer inside its region and drives two named axes
/// through the player's virtual input layer for as long as that pointer is down.
/// </summary>
/// <remarks>
/// It drives named actions rather than the engine's built-in touch joysticks, which claim half the
/// screen each. That works for one stick and falls apart the moment a game wants a stick and two
/// buttons on the same side.
/// </remarks>
public sealed class TouchStick : Widget
{
    /// <summary>The action the horizontal axis drives.</summary>
    public string ActionX { get; set; } = "MoveX";

    /// <summary>The action the vertical axis drives. Up is positive, as everywhere else.</summary>
    public string ActionY { get; set; } = "MoveY";

    /// <summary>How far the knob travels, in pixels.</summary>
    public float Radius { get; set; } = 80f;

    /// <summary>Movement under this fraction of the radius reads as nothing, so a resting thumb is still.</summary>
    public float DeadZone { get; set; } = 0.12f;

    /// <summary>When true the stick appears wherever the thumb lands, which is what phones do.</summary>
    public bool Floating { get; set; } = true;

    /// <summary>Who is pushing it.</summary>
    public PlayerInput? Player { get; set; }

    /// <summary>Where the stick is anchored right now: its home, or wherever a floating thumb landed.</summary>
    public Vector2 Origin { get; private set; }

    /// <summary>The stick's value, from -1 to 1 on each axis.</summary>
    public Vector2 Value { get; private set; }

    /// <summary>True while a thumb is on it.</summary>
    public bool IsActive => _pointerId != null;

    private int? _pointerId;

    public override void HandlePointer(in Pointer pointer)
    {
        if (!Visible || !Interactable) return;

        if (_pointerId == null)
        {
            if (!pointer.JustPressed || !ContainsPoint(pointer.Position)) return;

            _pointerId = pointer.Id;
            Origin     = Floating ? pointer.Position : Centre;
        }
        else if (pointer.Id != _pointerId)
        {
            return;   // somebody else's finger
        }

        if (pointer.JustReleased || !pointer.IsDown)
        {
            Release();
            return;
        }

        var offset = pointer.Position - Origin;
        float distance = offset.Length();

        var value = distance <= 0.0001f ? Vector2.Zero : offset / MathF.Max(distance, Radius) * MathF.Min(distance / Radius, 1f);
        if (value.Length() < DeadZone) value = Vector2.Zero;

        // Screen y grows downward; every action axis in this engine has up as positive.
        Value = new Vector2(value.X, -value.Y);
        Push();
    }

    /// <summary>Drops the thumb and zeroes the axes, for a scene change or losing focus.</summary>
    public void Release()
    {
        _pointerId = null;
        Value      = Vector2.Zero;
        Push();
    }

    private void Push()
    {
        if (Player == null) return;

        Player.SetVirtualAxis(ActionX, Value.X);
        Player.SetVirtualAxis(ActionY, Value.Y);
    }

    private Vector2 Centre
    {
        get
        {
            var bounds = Bounds;
            return new Vector2(bounds.X + bounds.Width * 0.5f, bounds.Y + bounds.Height * 0.5f);
        }
    }

    public override void Draw(SpriteBatch sb, SpriteFont? font)
    {
        if (!Visible) return;

        var home = IsActive ? Origin : Centre;
        var knob = home + new Vector2(Value.X, -Value.Y) * Radius;

        DrawRing(sb, home, Radius, Tint * (Opacity * 0.5f));
        DrawDisc(sb, knob, Radius * 0.42f, Tint * Opacity);
    }

    /// <summary>A filled circle from axis-aligned spans, so no texture is needed.</summary>
    private static void DrawDisc(SpriteBatch sb, Vector2 centre, float radius, Color colour)
    {
        int r = (int)radius;
        for (int y = -r; y <= r; y++)
        {
            int half = (int)MathF.Sqrt(MathF.Max(0f, radius * radius - y * y));
            if (half <= 0) continue;

            FillRect(sb, new Rectangle((int)centre.X - half, (int)centre.Y + y, half * 2, 1), colour);
        }
    }

    private static void DrawRing(SpriteBatch sb, Vector2 centre, float radius, Color colour)
    {
        const int Segments = 48;
        for (int i = 0; i < Segments; i++)
        {
            float angle = MathF.Tau * i / Segments;
            var point = centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            FillRect(sb, new Rectangle((int)point.X - 2, (int)point.Y - 2, 4, 4), colour);
        }
    }
}
