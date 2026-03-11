using System.Numerics;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using BepuPhysics;
using BepuPhysics.Collidables;

using XnaVec3 = Microsoft.Xna.Framework.Vector3;
using XnaQuat = Microsoft.Xna.Framework.Quaternion;
using NumVec3 = System.Numerics.Vector3;

namespace SexyBiscuit.Engine.Physics;

/// <summary>
/// Kinematic capsule character controller for 3D environments.
///
/// Usage:
///   1. Add this component to an Actor that also has a <see cref="CapsuleCollider3D"/>
///      and a <see cref="Rigidbody3D"/> (kinematic = true).
///   2. Call <see cref="Move"/> each <c>FixedUpdate</c> with a desired world-space velocity.
///   3. Call <see cref="Jump"/> when you want to jump.
///
/// The controller:
///   - Moves kinematically — other bodies are pushed but forces do not affect the character.
///   - Casts a short sphere sweep downward to detect ground and slope angle.
///   - Prevents movement up slopes steeper than <see cref="SlopeLimit"/>.
///   - Snaps the character to the ground when falling small distances.
/// </summary>
[RequireComponent(typeof(Rigidbody3D))]
public sealed class CharacterController3D : Component
{
    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    /// <summary>Maximum horizontal movement speed (world units / second). Default 5.</summary>
    public float MoveSpeed     { get; set; } = 5f;

    /// <summary>Vertical speed applied when jumping. Default 8.</summary>
    public float JumpSpeed     { get; set; } = 8f;

    /// <summary>Maximum height of a step the character can climb automatically. Default 0.3.</summary>
    public float StepUpHeight  { get; set; } = 0.3f;

    /// <summary>Maximum slope angle in degrees the character can walk on. Default 45.</summary>
    public float SlopeLimit    { get; set; } = 45f;

    /// <summary>
    /// Maximum downward distance (world units) for ground snapping.
    /// Keeps the character glued to surfaces on gentle descents. Default 0.1.
    /// </summary>
    public float SnapDistance  { get; set; } = 0.1f;

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    /// <summary>True when the character is standing on ground this frame.</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>True when the character is on a slope within <see cref="SlopeLimit"/>.</summary>
    public bool IsOnSlope  { get; private set; }

    /// <summary>Current slope angle in degrees. 0 = flat.</summary>
    public float SlopeAngle { get; private set; }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------
    private Rigidbody3D? _rb;

    // Capsule dimensions — read from CapsuleCollider3D or default
    private float _capsuleRadius = 0.5f;
    private float _capsuleLength = 1f;

    // Accumulated vertical velocity (only used when airborne)
    private float _verticalVelocity;

    // World gravity scalar (downward acceleration)
    private const float Gravity = 9.81f;

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    public override void Awake()
    {
        _rb = GetComponent<Rigidbody3D>();
        if (_rb != null)
            _rb.IsKinematic = true;

        var cap = GetComponent<CapsuleCollider3D>();
        if (cap != null)
        {
            _capsuleRadius = cap.Radius;
            _capsuleLength = cap.Length;
        }
    }

    // -----------------------------------------------------------------------
    // Fixed Update — gravity & ground detection
    // -----------------------------------------------------------------------

    public override void FixedUpdate(float dt)
    {
        DetectGround(dt);

        if (IsGrounded)
        {
            if (_verticalVelocity < 0f)
                _verticalVelocity = 0f;
        }
        else
        {
            // Apply gravity when airborne
            _verticalVelocity -= Gravity * dt;
        }

        // Apply accumulated vertical velocity to the body
        if (_rb != null && _rb.HasHandle)
        {
            var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_rb.Handle);
            var cur = body.Velocity.Linear;
            body.Velocity.Linear = new NumVec3(cur.X, _verticalVelocity, cur.Z);
        }
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Moves the character with the given world-space velocity vector.
    /// The Y component is ignored — vertical movement is controlled by gravity and
    /// <see cref="Jump"/>. Call each <c>FixedUpdate</c>.
    /// </summary>
    public void Move(XnaVec3 velocity)
    {
        if (_rb == null || !_rb.HasHandle) return;

        var horizontal = new XnaVec3(velocity.X, 0f, velocity.Z);

        // If on a slope, project movement onto slope plane to avoid bouncing
        if (IsOnSlope && IsGrounded)
        {
            horizontal = ProjectOntoSlopePlane(horizontal);
        }

        // Block movement if slope is too steep (treat as a wall)
        if (SlopeAngle > SlopeLimit && IsGrounded)
        {
            horizontal = XnaVec3.Zero;
            // Let gravity slide the character down the slope
        }

        var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_rb.Handle);
        float vy = body.Velocity.Linear.Y; // preserve vertical
        body.Velocity.Linear = new NumVec3(horizontal.X, vy, horizontal.Z);
        body.Awake = true;
    }

    /// <summary>
    /// Applies an upward velocity to the character. Only executes when grounded.
    /// </summary>
    public void Jump()
    {
        if (!IsGrounded || _rb == null || !_rb.HasHandle) return;

        _verticalVelocity = JumpSpeed;

        var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_rb.Handle);
        body.Velocity.Linear = new NumVec3(
            body.Velocity.Linear.X,
            _verticalVelocity,
            body.Velocity.Linear.Z);
        body.Awake = true;
    }

    // -----------------------------------------------------------------------
    // Ground detection via downward raycast
    // -----------------------------------------------------------------------

    private void DetectGround(float dt)
    {
        if (_rb == null || !_rb.HasHandle) return;

        var t3d = Actor.GetComponent<Transform3D>();
        if (t3d == null) return;

        IsGrounded  = false;
        IsOnSlope   = false;
        SlopeAngle  = 0f;

        // Origin: bottom of the capsule + a small step-up offset to avoid self-intersection
        float halfTotalHeight = _capsuleLength * 0.5f + _capsuleRadius;
        var origin = t3d.Position + new XnaVec3(0f, -halfTotalHeight + StepUpHeight, 0f);
        var dir    = new XnaVec3(0f, -1f, 0f);
        float dist = SnapDistance + StepUpHeight + 0.05f;

        if (!PhysicsSystem3D.Instance.Raycast(origin, dir, dist, out var hit))
            return;

        // Ignore hits with ourselves
        if (hit.Actor == Actor) return;

        IsGrounded = true;

        // Compute slope angle from the hit normal vs. world up
        float dot  = XnaVec3.Dot(hit.Normal, XnaVec3.Up);
        dot         = Math.Clamp(dot, -1f, 1f);
        SlopeAngle  = MathHelper.ToDegrees(MathF.Acos(dot));

        if (SlopeAngle > 0.5f && SlopeAngle <= SlopeLimit)
            IsOnSlope = true;
        else if (SlopeAngle > SlopeLimit)
        {
            // Too steep — slide without being considered grounded for jump
            IsGrounded = false;

            // Compute slide direction along slope
            var slideDir = XnaVec3.Cross(XnaVec3.Cross(hit.Normal, XnaVec3.Down), hit.Normal);
            if (slideDir.LengthSquared() > 0f)
            {
                slideDir = XnaVec3.Normalize(slideDir);
                float slideSpeed = Gravity * MathF.Sin(MathHelper.ToRadians(SlopeAngle));

                var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_rb.Handle);
                body.Velocity.Linear = PhysicsConvert3D.ToNum(slideDir * slideSpeed);
                body.Awake = true;
            }
        }

        // Ground snap: push character down onto surface if within snap range
        if (IsGrounded && hit.Distance > StepUpHeight)
        {
            float snapDelta = hit.Distance - StepUpHeight;
            if (snapDelta <= SnapDistance)
            {
                t3d.Position = t3d.Position + new XnaVec3(0f, -snapDelta, 0f);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Slope projection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Projects a movement vector onto the plane defined by the current slope normal,
    /// so the character slides along the surface rather than into it.
    /// </summary>
    private XnaVec3 ProjectOntoSlopePlane(XnaVec3 movement)
    {
        // We need the slope normal from the last raycast; re-cast to get it
        var t3d = Actor.GetComponent<Transform3D>();
        if (t3d == null) return movement;

        float halfTotalHeight = _capsuleLength * 0.5f + _capsuleRadius;
        var origin = t3d.Position + new XnaVec3(0f, -halfTotalHeight + StepUpHeight, 0f);

        if (!PhysicsSystem3D.Instance.Raycast(origin, new XnaVec3(0f, -1f, 0f), SnapDistance + StepUpHeight + 0.05f, out var hit))
            return movement;

        // Project onto the tangent plane of the slope
        var normal = XnaVec3.Normalize(hit.Normal);
        return movement - XnaVec3.Dot(movement, normal) * normal;
    }
}
