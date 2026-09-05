using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Gameplay;

/// <summary>
/// Drives an actor along a ballistic arc: launch velocity, gravity, drag, optional homing
/// and bouncing. Modelled on Unreal's <c>UProjectileMovementComponent</c>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a rigidbody. A bullet does not want a full simulation — it wants a
/// predictable arc, a cheap sweep, and to be destroyed on contact. Running thousands of
/// them through Bepu would cost far more than integrating a parabola, and would make the
/// trajectory harder to reason about, not easier.
/// </para>
/// <para>
/// Collision is a raycast along the frame's movement rather than a point test at the
/// destination. A fast projectile moves further in one frame than a wall is thick, so a
/// point test walks straight through it.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var bullet = scene.AddActor(new Actor("Bullet") { LifeSpan = 5f });
/// bullet.AddComponent&lt;Transform3D&gt;().Position = muzzle;
///
/// var move = bullet.AddComponent&lt;ProjectileMovement&gt;();
/// move.Velocity        = aimDirection * 60f;
/// move.GravityScale    = 0.2f;
/// move.DestroyOnImpact = true;
/// move.Impact.Add(hit =&gt; SpawnDecal(hit.Point, hit.Normal));
/// </code>
/// </example>
public sealed class ProjectileMovement : Component
{
    /// <summary>Current world-space velocity in units per second.</summary>
    public Vector3 Velocity { get; set; }

    /// <summary>Initial speed applied along the actor's forward axis on Start, if non-zero.</summary>
    public float InitialSpeed { get; set; }

    /// <summary>Speed ceiling. Zero means unlimited.</summary>
    public float MaxSpeed { get; set; }

    /// <summary>Multiplier on world gravity. 0 flies straight, 1 is a thrown rock.</summary>
    public float GravityScale { get; set; } = 1f;

    /// <summary>Gravity in units per second squared, before <see cref="GravityScale"/>.</summary>
    public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);

    /// <summary>Fraction of speed shed per second. 0 is a vacuum.</summary>
    public float Drag { get; set; }

    /// <summary>Turns the actor to face its direction of travel.</summary>
    public bool OrientToVelocity { get; set; } = true;

    /// <summary>Radius used for the sweep. Zero casts a thin ray.</summary>
    public float CollisionRadius { get; set; }

    /// <summary>Stops and destroys the actor on the first impact.</summary>
    public bool DestroyOnImpact { get; set; } = true;

    /// <summary>
    /// Fraction of speed retained when bouncing. Only used when
    /// <see cref="DestroyOnImpact"/> is false.
    /// </summary>
    public float Bounciness { get; set; } = 0.5f;

    /// <summary>Bounces below this speed stop the projectile dead rather than jittering.</summary>
    public float MinBounceSpeed { get; set; } = 0.5f;

    /// <summary>Homing target. When set, the projectile steers toward it.</summary>
    public Actor? HomingTarget { get; set; }

    /// <summary>Steering strength toward <see cref="HomingTarget"/>, in units per second squared.</summary>
    public float HomingAcceleration { get; set; } = 40f;

    /// <summary>Raised on the first surface hit, with the impact details.</summary>
    public SBEvent<Physics.RaycastHit3D> Impact { get; } = new();

    /// <summary>False once the projectile has stopped.</summary>
    public bool IsActive { get; private set; } = true;

    private Transform3D _transform = null!;

    public override void Start()
    {
        _transform = Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();

        if (InitialSpeed > 0f && Velocity == Vector3.Zero)
            Velocity = _transform.Forward * InitialSpeed;
    }

    public override void Update(float dt)
    {
        if (!IsActive || dt <= 0f) return;

        if (HomingTarget != null)
        {
            var targetTransform = HomingTarget.GetComponent<Transform3D>();
            if (targetTransform != null)
            {
                var toTarget = SBMath.SafeNormalize(targetTransform.Position - _transform.Position);
                Velocity += toTarget * HomingAcceleration * dt;
            }
        }

        Velocity += Gravity * GravityScale * dt;

        if (Drag > 0f) Velocity *= MathF.Max(0f, 1f - Drag * dt);

        if (MaxSpeed > 0f && Velocity.LengthSquared() > MaxSpeed * MaxSpeed)
            Velocity = SBMath.SafeNormalize(Velocity) * MaxSpeed;

        var step = Velocity * dt;
        float distance = step.Length();

        if (distance > SBMath.Epsilon && TrySweep(step, distance, out var hit))
        {
            HandleImpact(hit);
            return;
        }

        _transform.Position += step;

        if (OrientToVelocity && Velocity.LengthSquared() > SBMath.Epsilon)
            _transform.LookAt(_transform.Position + Velocity);
    }

    /// <summary>Casts along this frame's movement so a fast projectile cannot tunnel.</summary>
    private bool TrySweep(Vector3 step, float distance, out Physics.RaycastHit3D hit)
    {
        var direction = step / distance;
        float reach = distance + CollisionRadius;

        return Physics.PhysicsSystem3D.Instance.Raycast(_transform.Position, direction, reach, out hit);
    }

    private void HandleImpact(Physics.RaycastHit3D hit)
    {
        // Stop just short of the surface so the next frame does not start inside it.
        _transform.Position = hit.Point + hit.Normal * MathF.Max(CollisionRadius, 0.01f);
        Impact.Broadcast(hit);

        if (DestroyOnImpact)
        {
            IsActive = false;
            Actor.Destroy();
            return;
        }

        Velocity = Vector3.Reflect(Velocity, hit.Normal) * Bounciness;

        if (Velocity.Length() < MinBounceSpeed)
        {
            Velocity = Vector3.Zero;
            IsActive = false;
        }
    }
}

/// <summary>
/// Spins an actor at a constant rate. Modelled on Unreal's
/// <c>URotatingMovementComponent</c>.
/// </summary>
/// <remarks>
/// Small enough to be worth having built in: pickups, fans, turntables and rotating
/// hazards all want exactly this, and writing it per-game is pure repetition.
/// </remarks>
public sealed class RotatingMovement : Component
{
    /// <summary>Rotation rate in degrees per second, per axis.</summary>
    public Vector3 RotationRate { get; set; } = new(0f, 90f, 0f);

    /// <summary>
    /// Rotates about the world axes rather than the actor's own.
    /// </summary>
    /// <remarks>
    /// Local is the default because that is what makes a tumbling object tumble. World
    /// space is what you want for something that must keep spinning about a fixed axis
    /// regardless of how it is already tilted — a compass needle, a planet.
    /// </remarks>
    public bool UseWorldSpace { get; set; }

    /// <summary>Orbits this offset from the actor's origin instead of spinning in place.</summary>
    public Vector3 PivotOffset { get; set; }

    private Transform3D _transform = null!;

    public override void Start()
        => _transform = Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();

    public override void Update(float dt)
    {
        if (RotationRate == Vector3.Zero) return;

        var delta = Quaternion.CreateFromYawPitchRoll(
            MathHelper.ToRadians(RotationRate.Y * dt),
            MathHelper.ToRadians(RotationRate.X * dt),
            MathHelper.ToRadians(RotationRate.Z * dt));

        if (PivotOffset != Vector3.Zero)
        {
            // Orbit: rotate the offset vector and move the actor to match, so the pivot
            // stays put while the actor swings around it.
            var pivot   = _transform.Position - Vector3.Transform(PivotOffset, _transform.Rotation);
            var rotated = Vector3.Transform(PivotOffset, _transform.Rotation * delta);
            _transform.Position = pivot + rotated;
        }

        _transform.Rotation = UseWorldSpace
            ? delta * _transform.Rotation
            : _transform.Rotation * delta;
    }
}

/// <summary>
/// Simple acceleration-based movement with no gravity or ground contact, for pawns that
/// fly, swim or hover. Modelled on Unreal's <c>UFloatingPawnMovement</c>.
/// </summary>
/// <remarks>
/// Consumes the pawn's accumulated movement input the same way <see cref="Character"/>
/// does, so a controller written for one works with the other unchanged — which is the
/// point of routing intent through the pawn rather than straight at a transform.
/// </remarks>
public sealed class FloatingPawnMovement : Component
{
    /// <summary>Top speed in units per second.</summary>
    public float MaxSpeed { get; set; } = 12f;

    /// <summary>How hard the pawn accelerates toward the input direction.</summary>
    public float Acceleration { get; set; } = 40f;

    /// <summary>How hard it slows when there is no input.</summary>
    public float Deceleration { get; set; } = 30f;

    /// <summary>Turns the pawn to face its direction of travel.</summary>
    public bool OrientToVelocity { get; set; }

    /// <summary>Degrees per second when <see cref="OrientToVelocity"/> is on.</summary>
    public float RotationSpeed { get; set; } = 360f;

    /// <summary>Current velocity in units per second.</summary>
    public Vector3 Velocity { get; private set; }

    private Transform3D _transform = null!;
    private Pawn?       _pawn;

    public override void Start()
    {
        _transform = Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();
        _pawn      = Actor as Pawn;
    }

    public override void Update(float dt)
    {
        var input = _pawn?.ConsumeMovementInput() ?? Vector3.Zero;

        if (input.LengthSquared() > SBMath.Epsilon)
        {
            Velocity += SBMath.SafeNormalize(input) * Acceleration * dt;

            if (Velocity.LengthSquared() > MaxSpeed * MaxSpeed)
                Velocity = SBMath.SafeNormalize(Velocity) * MaxSpeed;
        }
        else
        {
            Velocity = SBMath.MoveTowards(Velocity, Vector3.Zero, Deceleration * dt);
        }

        if (Velocity.LengthSquared() < SBMath.Epsilon) return;

        _transform.Position += Velocity * dt;

        if (OrientToVelocity)
        {
            float targetYaw = MathF.Atan2(Velocity.X, Velocity.Z) * SBMath.Rad2Deg;
            float yaw = SBMath.MoveTowardsAngle(_transform.EulerAngles.Y, targetYaw, RotationSpeed * dt);
            _transform.EulerAngles = _transform.EulerAngles with { Y = yaw };
        }
    }
}
