using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Input;
using SexyBiscuit.Engine.UI;

namespace Cookies.TouchControls;

/// <summary>
/// The turn-on for on-screen controls. Builds a canvas of thumb sticks and buttons and keeps them
/// fed, so a controller written for a keyboard runs on a phone unchanged.
/// </summary>
public sealed class TouchOverlay : Component
{
    /// <summary>Whether to draw a left stick, for movement.</summary>
    public bool LeftStick { get; set; } = true;

    /// <summary>Whether to draw a right stick, for the camera. Off for a 2D game.</summary>
    public bool RightStick { get; set; }

    /// <summary>The actions to put on buttons, in order along the bottom right.</summary>
    public List<string> Buttons { get; set; } = new() { "Jump" };

    /// <summary>Hide the whole overlay when the device has no touch screen.</summary>
    public bool AutoHideOnDesktop { get; set; } = true;

    /// <summary>Whether a stick appears where the thumb lands rather than sitting in one place.</summary>
    public bool FloatingSticks { get; set; } = true;

    /// <summary>How far a stick's knob travels, in pixels.</summary>
    public float StickRadius { get; set; } = 80f;

    /// <summary>How big an action button is, in pixels.</summary>
    public float ButtonRadius { get; set; } = 44f;

    /// <summary>Distance from the screen edges, in pixels.</summary>
    public Vector2 Margin { get; set; } = new(120f, 120f);

    /// <summary>How solid the controls look, from 0 to 1.</summary>
    public float Opacity { get; set; } = 0.35f;

    /// <summary>Which player these controls belong to.</summary>
    public int PlayerIndex { get; set; }

    /// <summary>Adds a mouse simulator, so the overlay can be tried where there is no touch panel.</summary>
    public bool SimulateWithMouse { get; set; }

    /// <summary>The canvas the controls live on, once it has been built.</summary>
    public Canvas? Canvas { get; private set; }

    /// <summary>The sticks and buttons, for a game that wants to move or restyle them.</summary>
    public TouchStick?             Left    { get; private set; }
    public TouchStick?             Right   { get; private set; }
    public IReadOnlyList<TouchButton> Keys => _buttons;

    private readonly List<TouchButton> _buttons = new();
    private bool _visible = true;

    public override void Start()
    {
        var canvas = Actor.GetComponent<Canvas>() ?? Actor.AddComponent<Canvas>();
        Canvas      = canvas;
        canvas.TouchInput = true;
        canvas.PlayerIndex = PlayerIndex;

        if (SimulateWithMouse && Actor.GetComponent<MouseTouchSimulator>() == null)
            Actor.AddComponent<MouseTouchSimulator>();

        Build(canvas);
        ApplyVisibility();
    }

    public override void Update(float dt)
    {
        if (AutoHideOnDesktop) ApplyVisibility();
    }

    /// <summary>Lets go of everything, for a scene change or a pause.</summary>
    public void ReleaseAll()
    {
        Left?.Release();
        Right?.Release();
        foreach (var button in _buttons) button.Release();
    }

    private void Build(Canvas canvas)
    {
        var player = EngineHost.Current?.Input.GetPlayer(PlayerIndex);
        var size   = ScreenSize();

        if (LeftStick)
        {
            Left = Stick(player, "MoveX", "MoveY",
                         new Vector2(Margin.X, size.Y - Margin.Y));
            canvas.AddWidget(Left);
        }

        if (RightStick)
        {
            Right = Stick(player, "CameraX", "CameraY",
                          new Vector2(size.X - Margin.X, size.Y - Margin.Y));
            canvas.AddWidget(Right);
        }

        // Buttons stack up the right-hand side, above the right stick when there is one.
        float lift = RightStick ? Margin.Y + StickRadius * 2f : Margin.Y;
        for (int i = 0; i < Buttons.Count; i++)
        {
            var button = new TouchButton
            {
                Action  = Buttons[i],
                Radius  = ButtonRadius,
                Player  = player,
                Opacity = Opacity,
                Size    = new Vector2(ButtonRadius * 2f, ButtonRadius * 2f),
            };

            var centre = new Vector2(size.X - Margin.X * 0.6f - i % 2 * ButtonRadius * 2.4f,
                                     size.Y - lift - i / 2 * ButtonRadius * 2.4f);
            button.Position = centre - button.Size * 0.5f;

            _buttons.Add(button);
            canvas.AddWidget(button);
        }
    }

    private TouchStick Stick(PlayerInput? player, string actionX, string actionY, Vector2 centre)
    {
        // The region a thumb may land in is wider than the stick, so a floating stick has room.
        var size  = new Vector2(StickRadius * 3f, StickRadius * 3f);
        return new TouchStick
        {
            ActionX  = actionX,
            ActionY  = actionY,
            Radius   = StickRadius,
            Floating = FloatingSticks,
            Player   = player,
            Opacity  = Opacity,
            Size     = size,
            Position = centre - size * 0.5f,
        };
    }

    private void ApplyVisibility()
    {
        bool wanted = !AutoHideOnDesktop || HasTouchScreen();
        if (wanted == _visible) return;

        _visible = wanted;
        if (!wanted) ReleaseAll();

        if (Left  != null) Left.Visible  = wanted;
        if (Right != null) Right.Visible = wanted;
        foreach (var button in _buttons) button.Visible = wanted;
    }

    /// <summary>
    /// Whether anything has touched the screen. There is no "is this a phone" flag on DesktopGL, so
    /// the honest test is whether a finger has ever arrived.
    /// </summary>
    private static bool HasTouchScreen()
    {
        var touch = EngineHost.Current?.Input.Touch;
        return touch is { Touches.Count: > 0 };
    }

    private static Vector2 ScreenSize()
    {
        var device = EngineHost.Current?.GraphicsDevice;
        return device == null
            ? new Vector2(1920f, 1080f)
            : new Vector2(device.PresentationParameters.BackBufferWidth, device.PresentationParameters.BackBufferHeight);
    }
}
