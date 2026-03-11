using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using nkast.Aether.Physics2D.Dynamics;

using XnaVec2 = Microsoft.Xna.Framework.Vector2;

namespace SexyBiscuit.Engine.Physics;

/// <summary>
/// Kinematic character controller designed for 2D side-scrolling platformers.
/// Attach to an Actor alongside a <see cref="Rigidbody2D"/> (set kinematic) and
/// a <see cref="BoxCollider2D"/> or <see cref="CircleCollider2D"/>.
///
/// Call <see cref="Move"/> each <c>FixedUpdate</c> with horizontal input (-1..1).
/// Call <see cref="Jump"/> when you want the character to jump.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public sealed class CharacterController2D : Component
{
    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    /// <summary>Horizontal movement speed in world units per second. Default 300.</summary>
    public float MoveSpeed       { get; set; } = 300f;

    /// <summary>Vertical impulse applied when jumping. Default 700.</summary>
    public float JumpForce       { get; set; } = 700f;

    /// <summary>
    /// Maximum slope angle (degrees) the character can walk on without sliding.
    /// Slopes steeper than this are treated as walls. Default 45.
    /// </summary>
    public float SlopeAngleLimit { get; set; } = 45f;

    /// <summary>
    /// Length of the downward ground-detection ray, expressed as a fraction of
    /// the character's height.  Tuned for a unit-height character.  Default 0.1.
    /// </summary>
    public float GroundCheckDistance { get; set; } = 0.1f;

    // -----------------------------------------------------------------------
    // State (read-only)
    // -----------------------------------------------------------------------

    /// <summary>True when the character is standing on the ground this frame.</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>True when the character is on a slope within <see cref="SlopeAngleLimit"/>.</summary>
    public bool IsOnSlope  { get; private set; }

    /// <summary>The current slope angle in degrees (0 = flat ground).</summary>
    public float SlopeAngle { get; private set; }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------
    private Rigidbody2D? _rb;

    // The estimated half-height of the collider, used to offset the ground ray origin.
    private float _colliderHalfHeight = 0.5f;

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    public override void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        if (_rb != null)
        {
            _rb.IsKinematic   = false; // Use dynamic with constrained rotation for best results
            _rb.FreezeRotation = true;
            _rb.GravityScale  = 1f;
        }

        // Try to read collider extents for accurate ground ray placement
        var box = GetComponent<BoxCollider2D>();
        if (box != null)
        {
            _colliderHalfHeight = box.Size.Y * 0.5f;
        }
        else
        {
            var circle = GetComponent<CircleCollider2D>();
            if (circle != null)
                _colliderHalfHeight = circle.Radius;
        }
    }

    // -----------------------------------------------------------------------
    // Fixed Update — ground detection
    // -----------------------------------------------------------------------

    public override void FixedUpdate(float dt)
    {
        DetectGround();
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Applies horizontal movement.  Call each <c>FixedUpdate</c>.
    /// </summary>
    /// <param name="horizontalInput">Value in the range [-1, 1].  Positive = right.</param>
    public void Move(float horizontalInput)
    {
        if (_rb == null) return;

        float targetVx = horizontalInput * MoveSpeed;

        // On a walkable slope, decompose the movement along the slope surface
        if (IsOnSlope && IsGrounded)
        {
            // Slope normal points away from the surface; tangent is perpendicular
            // We push the character along the surface tangent instead of purely horizontal
            float slopeRad = MathHelper.ToRadians(SlopeAngle);
            float vx = targetVx * MathF.Cos(slopeRad);
            float vy = -MathF.Abs(targetVx) * MathF.Sin(slopeRad); // negative = downward in screen coords

            var currentVel = _rb.LinearVelocity;
            _rb.LinearVelocity = new XnaVec2(vx, vy + (IsGrounded ? 0f : currentVel.Y));
        }
        else
        {
            var currentVel = _rb.LinearVelocity;
            _rb.LinearVelocity = new XnaVec2(targetVx, currentVel.Y);
        }
    }

    /// <summary>
    /// Makes the character jump.  Only applies the impulse when grounded.
    /// </summary>
    public void Jump()
    {
        if (!IsGrounded || _rb == null) return;

        // Cancel any downward velocity before applying jump impulse
        _rb.LinearVelocity = new XnaVec2(_rb.LinearVelocity.X, 0f);
        _rb.AddImpulse(new XnaVec2(0f, -JumpForce)); // negative Y = upward in screen space
    }

    // -----------------------------------------------------------------------
    // Ground detection
    // -----------------------------------------------------------------------

    private void DetectGround()
    {
        // Cast a ray downward from the bottom centre of the character
        var origin = Actor.Transform.Position + new XnaVec2(0f, _colliderHalfHeight);
        var dir    = new XnaVec2(0f, 1f); // downward in screen space
        float dist = _colliderHalfHeight * GroundCheckDistance + 0.05f;

        IsGrounded = false;
        IsOnSlope  = false;
        SlopeAngle = 0f;

        if (!PhysicsSystem2D.Instance.Raycast(origin, dir, dist, out var hit)) return;

        // Ignore hits against ourselves
        if (hit.Actor == Actor) return;

        IsGrounded = true;

        // Compute slope angle from the hit normal
        // Normal (0,-1) means flat floor; any deviation is a slope.
        float dot   = XnaVec2.Dot(hit.Normal, new XnaVec2(0f, -1f));
        dot         = Math.Clamp(dot, -1f, 1f);
        SlopeAngle  = MathHelper.ToDegrees(MathF.Acos(dot));

        if (SlopeAngle > 0.5f && SlopeAngle <= SlopeAngleLimit)
            IsOnSlope = true;
        else if (SlopeAngle > SlopeAngleLimit)
        {
            // Too steep — character slides; treat as not grounded for jump purposes
            IsGrounded = false;
        }
    }
}
