using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Gameplay;

/// <summary>
/// An actor that a <see cref="Controller"/> can possess — the physical representation of a
/// player or an AI in the world. Modelled on Unreal's <c>APawn</c>.
/// </summary>
/// <remarks>
/// A pawn does not decide what it wants to do; its controller does. Controllers push intent
/// through <see cref="AddMovementInput"/> during Update, and the pawn drains that intent once
/// per frame in <see cref="ConsumeMovementInput"/>. Accumulating rather than assigning means
/// several input sources (stick, keyboard, an AI steering behaviour) compose without one
/// clobbering another.
/// </remarks>
public class Pawn : Actor
{
    /// <summary>The controller currently possessing this pawn, or null when unpossessed.</summary>
    public Controller? Controller { get; internal set; }

    /// <summary>True when the possessing controller is a <see cref="PlayerController"/>.</summary>
    public bool IsPlayerControlled => Controller is PlayerController;

    /// <summary>True when the pawn is possessed by any controller.</summary>
    public bool IsControlled => Controller != null;

    /// <summary>
    /// Rotation the controller wants the pawn to face, in degrees (pitch, yaw, roll).
    /// A first-person pawn typically applies yaw to itself and pitch to its camera.
    /// </summary>
    public Vector3 ControlRotation { get; set; }

    /// <summary>
    /// When true the pawn's yaw is driven from <see cref="ControlRotation"/> each frame.
    /// Turn this off for a pawn that should face its movement direction instead.
    /// </summary>
    public bool UseControllerRotationYaw { get; set; } = true;

    // Accumulated this frame, drained by ConsumeMovementInput.
    private Vector3 _pendingMovementInput;

    public Pawn() { }
    public Pawn(string name) : base(name) { }

    // -------------------------------------------------------------------------
    // Movement intent
    // -------------------------------------------------------------------------

    /// <summary>
    /// Adds movement intent in world space. Call from a controller during Update; the pawn's
    /// movement component drains it in FixedUpdate or LateUpdate.
    /// </summary>
    /// <param name="worldDirection">Direction to move. Need not be normalised.</param>
    /// <param name="scale">Multiplier, typically the analogue stick magnitude in 0..1.</param>
    public void AddMovementInput(Vector3 worldDirection, float scale = 1f)
        => _pendingMovementInput += worldDirection * scale;

    /// <summary>
    /// Returns the movement intent accumulated this frame and clears it. The returned vector
    /// is clamped to unit length so holding two directions is not faster than one.
    /// </summary>
    public Vector3 ConsumeMovementInput()
    {
        var v = _pendingMovementInput;
        _pendingMovementInput = Vector3.Zero;

        float lenSq = v.LengthSquared();
        return lenSq > 1f ? v / MathF.Sqrt(lenSq) : v;
    }

    /// <summary>Movement intent accumulated so far this frame, without clearing it.</summary>
    public Vector3 PendingMovementInput => _pendingMovementInput;

    /// <summary>Adds to the controller's yaw, in degrees.</summary>
    public void AddControllerYawInput(float degrees)
        => ControlRotation = ControlRotation with { Y = SBMath.WrapAngle(ControlRotation.Y + degrees) };

    /// <summary>Adds to the controller's pitch, in degrees, clamped to avoid gimbal flip.</summary>
    public void AddControllerPitchInput(float degrees, float minPitch = -89f, float maxPitch = 89f)
        => ControlRotation = ControlRotation with
        {
            X = MathHelper.Clamp(ControlRotation.X + degrees, minPitch, maxPitch)
        };

    // -------------------------------------------------------------------------
    // Possession callbacks
    // -------------------------------------------------------------------------

    /// <summary>Called after a controller takes control of this pawn.</summary>
    public virtual void OnPossessed(Controller controller) { }

    /// <summary>Called after a controller releases this pawn.</summary>
    public virtual void OnUnPossessed(Controller controller) { }

    /// <summary>
    /// Where the view should sit for a camera attached to this pawn, in world space.
    /// Override for an eye-height offset on a humanoid character.
    /// </summary>
    public virtual Vector3 GetViewLocation()
        => GetComponent<Transform3D>()?.Position ?? Vector3.Zero;
}
