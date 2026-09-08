using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Input;
using SexyBiscuit.Engine.UI;

namespace Cookies.TouchControls;

/// <summary>
/// An action button drawn on screen. Claims one finger and holds a named action down for as long
/// as that finger stays on it.
/// </summary>
/// <remarks>
/// Like <see cref="TouchStick"/>, the visuals are canvas nodes and the input comes from
/// <see cref="TouchManager"/>, so a stick and two buttons can all be held at once.
/// </remarks>
public sealed class TouchButton
{
    /// <summary>The action it holds down.</summary>
    public string Action { get; set; } = "Jump";

    /// <summary>What is written on it. Defaults to the action's first letter.</summary>
    public string? Label { get; set; }

    /// <summary>How big it is, in pixels.</summary>
    public float Radius { get; set; } = 44f;

    /// <summary>Who is pressing it.</summary>
    public PlayerInput? Player { get; set; }

    /// <summary>The centre of the button.</summary>
    public Vector2 Home { get; set; }

    /// <summary>True while a finger is on it.</summary>
    public bool IsPressed => _fingerId != null;

    /// <summary>Whether the button is drawn and takes input at all.</summary>
    public bool Visible
    {
        get => _node.Visible;
        set => _node.Visible = value;
    }

    private readonly UiNode _node;
    private readonly float _opacity;
    private int? _fingerId;

    public TouchButton(UiNode parent, Color tint, float opacity)
    {
        _opacity = opacity;
        _node = parent.Add(new UiNode
        {
            Kind = UiKind.Panel,
            Positioning = PositionMode.Absolute,
            Background = tint,
            Opacity = opacity,
            TextAlign = AlignMode.Center,
            VerticalAlign = AlignMode.Center,
        });
    }

    /// <summary>Sizes and labels the button. Call once the action and radius are set.</summary>
    public void Rebuild()
    {
        _node.WidthMode = SizeMode.Fixed;
        _node.Width = Radius * 2f;
        _node.HeightMode = SizeMode.Fixed;
        _node.Height = Radius * 2f;
        _node.Text = Label ?? (Action.Length > 0 ? Action[..1] : "");
        _node.Offset = Home - new Vector2(Radius, Radius);
    }

    /// <summary>Reads this frame's touches and holds or releases the action.</summary>
    public void Update(IReadOnlyList<TouchPoint> touches, HashSet<int> claimed)
    {
        if (!Visible) { Release(); return; }

        if (_fingerId == null)
        {
            foreach (TouchPoint touch in touches)
            {
                if (claimed.Contains(touch.Id)) continue;
                if (touch.Phase != TouchPhase.Began) continue;
                if (Vector2.Distance(touch.Position, Home) > Radius) continue;

                _fingerId = touch.Id;
                claimed.Add(touch.Id);
                Player?.PressVirtual(Action);
                _node.Opacity = MathF.Min(1f, _opacity * 2f);
                return;
            }
            return;
        }

        TouchPoint? held = null;
        foreach (TouchPoint touch in touches)
            if (touch.Id == _fingerId) { held = touch; break; }

        // Sliding off the button releases it, the way a real button does.
        bool gone = held is not { } finger
                 || finger.Phase is TouchPhase.Ended or TouchPhase.Cancelled
                 || Vector2.Distance(finger.Position, Home) > Radius;

        if (gone) { Release(); return; }

        claimed.Add(_fingerId.Value);
    }

    /// <summary>Lets the action go, for a scene change or losing focus.</summary>
    public void Release()
    {
        if (_fingerId == null) return;

        _fingerId = null;
        Player?.ReleaseVirtual(Action);
        _node.Opacity = _opacity;
    }
}
