using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Input;
using SexyBiscuit.Engine.UI;

namespace Cookies.TouchControls;

/// <summary>
/// A thumb stick drawn on screen. Claims one finger inside its region and drives two named axes
/// through the player's virtual input layer for as long as that finger is down.
/// </summary>
/// <remarks>
/// <para>
/// It drives named actions rather than the engine's built-in touch joysticks, which claim half the
/// screen each. That works for one stick and falls apart the moment a game wants a stick and two
/// buttons on the same side.
/// </para>
/// <para>
/// The visuals are nodes on a <see cref="UiCanvas"/>, so the painter puts them in screen space
/// with everything else. The <em>input</em> comes straight from <see cref="TouchManager"/> rather
/// than through the canvas, because the tree routes one pointer and a pad needs a finger per
/// stick at the same time.
/// </para>
/// </remarks>
public sealed class TouchStick
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

    /// <summary>The centre of the region a thumb may land in.</summary>
    public Vector2 Home { get; set; }

    /// <summary>Where the stick is anchored right now: its home, or wherever a floating thumb landed.</summary>
    public Vector2 Origin { get; private set; }

    /// <summary>The stick's value, from -1 to 1 on each axis.</summary>
    public Vector2 Value { get; private set; }

    /// <summary>True while a thumb is on it.</summary>
    public bool IsActive => _fingerId != null;

    /// <summary>Whether the stick is drawn and takes input at all.</summary>
    public bool Visible
    {
        get => _base.Visible;
        set { _base.Visible = value; _knob.Visible = value; }
    }

    private readonly UiNode _base;
    private readonly UiNode _knob;
    private int? _fingerId;

    public TouchStick(UiNode parent, Color tint, float opacity)
    {
        _base = parent.Add(Disc(Radius * 2f, tint, opacity * 0.5f));
        _knob = parent.Add(Disc(Radius * 0.84f, tint, opacity));
    }

    private static UiNode Disc(float size, Color tint, float opacity) => new()
    {
        Kind = UiKind.Panel,
        Positioning = PositionMode.Absolute,
        WidthMode = SizeMode.Fixed, Width = size,
        HeightMode = SizeMode.Fixed, Height = size,
        Background = tint,
        Opacity = opacity,
    };

    /// <summary>
    /// Reads this frame's touches and drives the axes.
    /// </summary>
    /// <param name="touches">Every finger currently on the screen.</param>
    /// <param name="claimed">Fingers other controls have already taken.</param>
    public void Update(IReadOnlyList<TouchPoint> touches, HashSet<int> claimed)
    {
        if (!Visible) { Release(); Layout(); return; }

        if (_fingerId == null)
        {
            foreach (TouchPoint touch in touches)
            {
                if (claimed.Contains(touch.Id)) continue;
                if (touch.Phase != TouchPhase.Began) continue;
                if (Vector2.Distance(touch.Position, Home) > Radius * 1.5f) continue;

                _fingerId = touch.Id;
                Origin = Floating ? touch.Position : Home;
                break;
            }

            if (_fingerId == null) { Layout(); return; }
        }

        TouchPoint? held = null;
        foreach (TouchPoint touch in touches)
            if (touch.Id == _fingerId) { held = touch; break; }

        if (held is not { } finger || finger.Phase is TouchPhase.Ended or TouchPhase.Cancelled)
        {
            Release();
            Layout();
            return;
        }

        claimed.Add(finger.Id);

        Vector2 offset = finger.Position - Origin;
        float distance = offset.Length();

        Vector2 value = distance <= 0.0001f
            ? Vector2.Zero
            : offset / MathF.Max(distance, Radius) * MathF.Min(distance / Radius, 1f);

        if (value.Length() < DeadZone) value = Vector2.Zero;

        // Screen y grows downward; every action axis in this engine has up as positive.
        Value = new Vector2(value.X, -value.Y);
        Push();
        Layout();
    }

    /// <summary>Drops the thumb and zeroes the axes, for a scene change or losing focus.</summary>
    public void Release()
    {
        if (_fingerId == null && Value == Vector2.Zero) return;

        _fingerId = null;
        Value = Vector2.Zero;
        Push();
    }

    private void Push()
    {
        if (Player == null) return;

        Player.SetVirtualAxis(ActionX, Value.X);
        Player.SetVirtualAxis(ActionY, Value.Y);
    }

    /// <summary>Moves the two nodes to where the stick currently is.</summary>
    private void Layout()
    {
        Vector2 anchor = IsActive ? Origin : Home;
        Vector2 knob = anchor + new Vector2(Value.X, -Value.Y) * Radius;

        _base.Offset = anchor - new Vector2(_base.Width, _base.Height) * 0.5f;
        _knob.Offset = knob - new Vector2(_knob.Width, _knob.Height) * 0.5f;
    }
}
