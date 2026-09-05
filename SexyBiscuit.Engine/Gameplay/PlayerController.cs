using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Input;
using SexyBiscuit.Engine.Rendering;

namespace SexyBiscuit.Engine.Gameplay;

/// <summary>
/// The bridge between a human player's input devices and a <see cref="Pawn"/>.
/// Modelled on Unreal's <c>APlayerController</c>.
/// </summary>
/// <remarks>
/// Input is read through named actions from <see cref="InputManager"/> rather than raw keys,
/// so rebinding works without touching gameplay code. Override <see cref="SetupInput"/> to
/// declare bindings and <see cref="OnPlayerTick"/> to translate them into pawn intent.
/// </remarks>
/// <example>
/// <code>
/// public class MyPlayerController : PlayerController
/// {
///     protected override void OnPlayerTick(Pawn pawn, InputManager input, float dt)
///     {
///         var fwd   = pawn.GetComponent&lt;Transform3D&gt;()!.Forward;
///         var right = pawn.GetComponent&lt;Transform3D&gt;()!.Right;
///         pawn.AddMovementInput(fwd,   input.GetAxis("MoveVertical"));
///         pawn.AddMovementInput(right, input.GetAxis("MoveHorizontal"));
///
///         if (input.IsPressed("Jump") &amp;&amp; pawn is Character c) c.Jump();
///     }
/// }
/// </code>
/// </example>
public class PlayerController : Controller
{
    /// <summary>
    /// Zero-based local player index. Drives which gamepad this controller reads
    /// and which split-screen viewport it owns.
    /// </summary>
    public int PlayerIndex { get; set; }

    /// <summary>Per-player replicated stats — score, name, ping. May be null in single-player.</summary>
    public PlayerState? PlayerState { get; set; }

    /// <summary>The camera this player views the world through. Falls back to <see cref="Camera3D.Main"/>.</summary>
    public Camera3D? ViewCamera { get; set; }

    /// <summary>
    /// When true the controller feeds input to its pawn each frame. Set false while a menu
    /// is open so the pawn stops moving without unpossessing it.
    /// </summary>
    public bool InputEnabled { get; set; } = true;

    /// <summary>Mouse look sensitivity in degrees per pixel of mouse movement.</summary>
    public float LookSensitivity { get; set; } = 0.15f;

    /// <summary>Gamepad look sensitivity in degrees per second at full stick deflection.</summary>
    public float GamepadLookSpeed { get; set; } = 180f;

    /// <summary>Inverts vertical look.</summary>
    public bool InvertLookY { get; set; }

    private bool _inputConfigured;

    public PlayerController() : base("PlayerController") { }

    /// <summary>The engine input manager. Null before the engine has initialised.</summary>
    protected static InputManager? Input => EngineHost.Current?.Input;

    protected override void OnStart()
    {
        if (_inputConfigured || Input == null) return;
        SetupInput(Input);
        _inputConfigured = true;
    }

    /// <summary>
    /// Declare or rebind input actions here. Called once when the controller starts.
    /// The default action map already provides MoveHorizontal, MoveVertical, Jump and Fire.
    /// </summary>
    protected virtual void SetupInput(InputManager input) { }

    protected override void Update(float dt)
    {
        var input = Input;
        var pawn  = ControlledPawn;
        if (!InputEnabled || input == null || pawn == null) return;

        ApplyLookInput(pawn, input, dt);
        OnPlayerTick(pawn, input, dt);

        if (pawn.UseControllerRotationYaw)
        {
            var t3d = pawn.GetComponent<Transform3D>();
            if (t3d != null)
                t3d.EulerAngles = t3d.EulerAngles with { Y = pawn.ControlRotation.Y };
        }
    }

    /// <summary>
    /// Applies mouse and right-stick look to the pawn's <see cref="Pawn.ControlRotation"/>.
    /// Override to change or suppress look handling.
    /// </summary>
    protected virtual void ApplyLookInput(Pawn pawn, InputManager input, float dt)
    {
        float invert = InvertLookY ? -1f : 1f;

        var mouse = input.MouseDelta;
        if (mouse != Vector2.Zero)
        {
            pawn.AddControllerYawInput(mouse.X * LookSensitivity);
            pawn.AddControllerPitchInput(mouse.Y * LookSensitivity * invert);
        }

        var pad = input.GetGamepad(PlayerIndex);
        if (pad.IsConnected)
        {
            var look = pad.RightStick;
            if (look != Vector2.Zero)
            {
                pawn.AddControllerYawInput(look.X * GamepadLookSpeed * dt);
                pawn.AddControllerPitchInput(-look.Y * GamepadLookSpeed * dt * invert);
            }
        }
    }

    /// <summary>
    /// Translate input into movement intent here. Called every frame while a pawn is possessed
    /// and <see cref="InputEnabled"/> is true.
    /// </summary>
    protected virtual void OnPlayerTick(Pawn pawn, InputManager input, float dt) { }

    /// <summary>
    /// Projects the mouse cursor into the world and returns the ray, using
    /// <see cref="ViewCamera"/> or the main camera. Null when no camera is available.
    /// </summary>
    public (Vector3 origin, Vector3 direction)? GetCursorRay()
    {
        var cam = ViewCamera ?? Camera3D.Main;
        var gd  = EngineHost.Current?.GraphicsDevice;
        if (cam == null || gd == null || Input == null) return null;
        return cam.ScreenToWorldRay(Input.MousePosition, gd);
    }
}
