using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Input;
using SexyBiscuit.Engine.UI;

namespace Cookies.TouchControls;

/// <summary>
/// The turn-on for on-screen controls. Builds a canvas of thumb sticks and buttons and keeps them
/// fed, so a controller written for a keyboard runs on a phone unchanged.
/// </summary>
/// <remarks>
/// The controls are painted as nodes on a <see cref="UiCanvas"/> and driven straight from
/// <see cref="TouchManager"/>. The canvas routes a single pointer, which is right for a menu and
/// wrong for a pad — a stick and a button have to be held at the same time — so the overlay does
/// its own per-finger claiming and leaves the canvas to the painting.
/// </remarks>
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

    /// <summary>The canvas the controls are painted on, once it has been built.</summary>
    public UiCanvas? Canvas { get; private set; }

    /// <summary>The sticks and buttons, for a game that wants to move or restyle them.</summary>
    public TouchStick? Left { get; private set; }
    public TouchStick? Right { get; private set; }
    public IReadOnlyList<TouchButton> Keys => _buttons;

    private readonly List<TouchButton> _buttons = new();
    private readonly HashSet<int> _claimed = new();
    private bool _visible = true;

    public override void Start()
    {
        UiCanvas canvas = Actor.GetComponent<UiCanvas>() ?? Actor.AddComponent<UiCanvas>();
        Canvas = canvas;

        // Device pixels, so a finger's position and a control's rectangle are the same units.
        canvas.ScaleMode = UiScaleMode.ConstantPixel;
        canvas.PlayerIndex = PlayerIndex;

        // The overlay is painted, never navigated: a focus ring on a thumb stick would be noise,
        // and the canvas must not eat a tap meant for the game's own UI.
        canvas.Interactive = false;
        canvas.Order = 100;

        if (SimulateWithMouse && Actor.GetComponent<MouseTouchSimulator>() == null)
            Actor.AddComponent<MouseTouchSimulator>();

        Build(canvas);
        ApplyVisibility();
    }

    public override void Update(float dt)
    {
        if (AutoHideOnDesktop) ApplyVisibility();

        var touch = EngineHost.Current?.Input.Touch;
        IReadOnlyList<TouchPoint> touches = touch?.Touches ?? Array.Empty<TouchPoint>();

        // One pass, so a finger claimed by a stick cannot also press a button under it.
        _claimed.Clear();
        Left?.Update(touches, _claimed);
        Right?.Update(touches, _claimed);
        foreach (TouchButton button in _buttons) button.Update(touches, _claimed);
    }

    public override void OnDestroy() => ReleaseAll();

    /// <summary>Lets go of everything, for a scene change or a pause.</summary>
    public void ReleaseAll()
    {
        Left?.Release();
        Right?.Release();
        foreach (TouchButton button in _buttons) button.Release();
    }

    private void Build(UiCanvas canvas)
    {
        PlayerInput? player = EngineHost.Current?.Input.GetPlayer(PlayerIndex);
        Vector2 size = ScreenSize();
        var tint = Color.White;

        if (LeftStick)
        {
            Left = new TouchStick(canvas.Root, tint, Opacity)
            {
                ActionX = "MoveX", ActionY = "MoveY",
                Radius = StickRadius, Floating = FloatingSticks, Player = player,
                Home = new Vector2(Margin.X, size.Y - Margin.Y),
            };
        }

        if (RightStick)
        {
            Right = new TouchStick(canvas.Root, tint, Opacity)
            {
                ActionX = "CameraX", ActionY = "CameraY",
                Radius = StickRadius, Floating = FloatingSticks, Player = player,
                Home = new Vector2(size.X - Margin.X, size.Y - Margin.Y),
            };
        }

        // Buttons stack up the right-hand side, above the right stick when there is one.
        float lift = RightStick ? Margin.Y + StickRadius * 2f : Margin.Y;

        for (int i = 0; i < Buttons.Count; i++)
        {
            var button = new TouchButton(canvas.Root, tint, Opacity)
            {
                Action = Buttons[i],
                Radius = ButtonRadius,
                Player = player,
                Home = new Vector2(size.X - Margin.X * 0.6f - i % 2 * ButtonRadius * 2.4f,
                                   size.Y - lift - i / 2 * ButtonRadius * 2.4f),
            };
            button.Rebuild();
            _buttons.Add(button);
        }
    }

    private void ApplyVisibility()
    {
        bool wanted = !AutoHideOnDesktop || HasTouchScreen();
        if (wanted == _visible) return;

        _visible = wanted;
        if (!wanted) ReleaseAll();

        if (Left != null) Left.Visible = wanted;
        if (Right != null) Right.Visible = wanted;
        foreach (TouchButton button in _buttons) button.Visible = wanted;
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
            : new Vector2(device.PresentationParameters.BackBufferWidth,
                          device.PresentationParameters.BackBufferHeight);
    }
}
