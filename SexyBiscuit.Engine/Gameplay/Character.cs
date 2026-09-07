using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Physics;

namespace SexyBiscuit.Engine.Gameplay;

/// <summary>
/// A <see cref="Pawn"/> that walks — it owns a <see cref="CharacterController3D"/> and
/// converts the movement intent its controller pushes into capsule movement.
/// Modelled on Unreal's <c>ACharacter</c>.
/// </summary>
/// <remarks>
/// The controller and transform components are created on first Start if the actor does not
/// already have them, so a character works with no setup while still letting you configure
/// the capsule yourself before it starts.
/// </remarks>
/// <example>
/// <code>
/// var hero = scene.AddActor(new Character("Hero"));
/// hero.WalkSpeed = 6f;
/// hero.OrientToMovement = true;
///
/// var pc = scene.AddActor(new MyPlayerController());
/// pc.Possess(hero);
/// </code>
/// </example>
public class Character : Pawn
{
    /// <summary>Ground speed in world units per second.</summary>
    public float WalkSpeed { get; set; } = 5f;

    /// <summary>Multiplier applied to <see cref="WalkSpeed"/> while <see cref="IsSprinting"/>.</summary>
    public float SprintMultiplier { get; set; } = 1.8f;

    /// <summary>Set by the controller to request sprint speed.</summary>
    public bool IsSprinting { get; set; }

    /// <summary>
    /// When true the character turns to face its movement direction instead of the
    /// controller's yaw. Typical for third-person; leave false for first-person.
    /// </summary>
    public bool OrientToMovement { get; set; }

    /// <summary>Degrees per second the character rotates when <see cref="OrientToMovement"/> is on.</summary>
    public float RotationSpeed { get; set; } = 720f;

    /// <summary>Eye height above the actor origin, used by <see cref="Pawn.GetViewLocation"/>.</summary>
    public float EyeHeight { get; set; } = 1.7f;

    /// <summary>The capsule controller doing the actual sweeping and collision resolution.</summary>
    public CharacterController3D Movement { get; private set; } = null!;

    /// <summary>The character's 3D transform.</summary>
    /// <remarks>
    /// Deliberately hides <see cref="Actor.Transform3D"/>, which is nullable because most actors
    /// are 2D. A Character always has one — <see cref="OnStart"/> adds it if the scene did not —
    /// so this narrows the type for the movement code rather than making every call site
    /// null-check something that cannot be null. Both resolve to the same component.
    /// </remarks>
    public new Transform3D Transform3D { get; private set; } = null!;

    /// <summary>True while the capsule is resting on walkable ground.</summary>
    public bool IsGrounded => Movement.IsGrounded;

    /// <summary>Raised the frame the character leaves the ground with upward velocity.</summary>
    public SBEvent Jumped { get; } = new();

    /// <summary>Raised the frame the character lands after being airborne.</summary>
    public SBEvent Landed { get; } = new();

    private bool _wasGrounded = true;

    public Character() : base("Character") { }
    public Character(string name) : base(name) { }

    protected override void OnStart()
    {
        Transform3D = GetComponent<Transform3D>() ?? AddComponent<Transform3D>();
        Movement    = GetComponent<CharacterController3D>() ?? AddComponent<CharacterController3D>();
        Movement.MoveSpeed = WalkSpeed;
    }

    protected override void Update(float dt)
    {
        var intent = ConsumeMovementInput();

        float speed = WalkSpeed * (IsSprinting ? SprintMultiplier : 1f);
        Movement.MoveSpeed = speed;
        Movement.Move(intent * speed);

        if (OrientToMovement && intent.LengthSquared() > SBMath.Epsilon)
        {
            float targetYaw  = MathF.Atan2(intent.X, intent.Z) * SBMath.Rad2Deg;
            float currentYaw = Transform3D.EulerAngles.Y;
            float newYaw     = SBMath.MoveTowardsAngle(currentYaw, targetYaw, RotationSpeed * dt);
            Transform3D.EulerAngles = Transform3D.EulerAngles with { Y = newYaw };
        }

        // Ground-state edge detection for landing effects and animation.
        bool grounded = Movement.IsGrounded;
        if (grounded && !_wasGrounded) Landed.Broadcast();
        _wasGrounded = grounded;
    }

    /// <summary>
    /// Launches the character upward if it is on the ground. Ignored while airborne —
    /// override to add double-jump or coyote-time rules.
    /// </summary>
    public virtual void Jump()
    {
        if (!Movement.IsGrounded) return;
        Movement.Jump();
        Jumped.Broadcast();
    }

    /// <inheritdoc/>
    public override Vector3 GetViewLocation()
        => Transform3D is null ? Vector3.Zero : Transform3D.Position + Vector3.Up * EyeHeight;
}
