using System.Numerics;
using SexyBiscuit.Engine.Core;
using BepuPhysics;
using BepuPhysics.Constraints;

using XnaVec3 = Microsoft.Xna.Framework.Vector3;

namespace SexyBiscuit.Engine.Physics;

/// <summary>
/// Base class for the 3D joints. A constraint component links the actor's
/// <see cref="Rigidbody3D"/> to <see cref="ConnectedBody"/> and keeps the underlying
/// Bepu constraint alive for as long as the component is.
/// </summary>
/// <remarks>
/// <para>
/// Bepu constraints are value types added to the simulation and referenced by a handle, so
/// changing a joint's settings means re-adding it. Each subclass exposes the settings it
/// cares about and calls <see cref="Rebuild"/> when they change.
/// </para>
/// <para>
/// Every joint exposes <see cref="SpringFrequency"/> and <see cref="SpringDamping"/>. A high
/// frequency with damping near 1 gives a rigid joint; lower values give a springy one. These
/// map straight onto Bepu's <c>SpringSettings</c>.
/// </para>
/// </remarks>
public abstract class Constraint3D : Component
{
    /// <summary>
    /// The other body in the joint. Leave null to pin the actor to the world — the engine
    /// substitutes a static anchor at the connection point.
    /// </summary>
    public Rigidbody3D? ConnectedBody { get; set; }

    /// <summary>Stiffness in Hz. Higher is more rigid; 30 behaves like a solid joint.</summary>
    public float SpringFrequency { get; set; } = 30f;

    /// <summary>Damping ratio. 1 is critically damped; below 1 lets the joint oscillate.</summary>
    public float SpringDamping { get; set; } = 1f;

    /// <summary>The Bepu handle for this constraint, valid while <see cref="IsActive"/>.</summary>
    public ConstraintHandle Handle { get; private set; }

    /// <summary>True while the constraint exists in the simulation.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The actor's own rigidbody, resolved on Start.</summary>
    protected Rigidbody3D? Body { get; private set; }

    /// <summary>Spring settings assembled from <see cref="SpringFrequency"/> and <see cref="SpringDamping"/>.</summary>
    protected SpringSettings Spring => new(SpringFrequency, SpringDamping);

    public override void Start()
    {
        Body = Actor.GetComponent<Rigidbody3D>();
        Rebuild();
    }

    /// <summary>
    /// Removes and recreates the underlying constraint. Call after changing any setting
    /// that is baked into the constraint description.
    /// </summary>
    public void Rebuild()
    {
        Remove();

        if (Body is not { HasHandle: true }) return;
        if (ConnectedBody is { HasHandle: false }) return;

        var simulation = PhysicsSystem3D.Instance.Simulation;
        var a = Body.Handle;

        // With no connected body, anchor to the actor's own body twice is meaningless —
        // Bepu needs two distinct bodies, so an unconnected joint is simply skipped.
        if (ConnectedBody == null) return;

        Handle   = Add(simulation, a, ConnectedBody.Handle);
        IsActive = true;
    }

    /// <summary>Creates the Bepu constraint between the two bodies and returns its handle.</summary>
    protected abstract ConstraintHandle Add(Simulation simulation, BodyHandle a, BodyHandle b);

    /// <summary>Removes the constraint from the simulation.</summary>
    public void Remove()
    {
        if (!IsActive) return;
        PhysicsSystem3D.Instance.Simulation.Solver.Remove(Handle);
        IsActive = false;
    }

    public override void OnDestroy() => Remove();

    /// <summary>Converts an XNA vector to the System.Numerics type Bepu uses.</summary>
    protected static Vector3 ToNum(XnaVec3 v) => new(v.X, v.Y, v.Z);
}

/// <summary>
/// Pins two bodies together at a point while leaving rotation free — a shoulder joint,
/// a chain link, a pendulum mount.
/// </summary>
/// <example>
/// <code>
/// var joint = link.AddComponent&lt;BallSocketConstraint&gt;();
/// joint.ConnectedBody = previousLink.GetComponent&lt;Rigidbody3D&gt;();
/// joint.LocalOffsetA  = new Vector3(0f, 0.5f, 0f);
/// joint.LocalOffsetB  = new Vector3(0f, -0.5f, 0f);
/// joint.Rebuild();
/// </code>
/// </example>
public sealed class BallSocketConstraint : Constraint3D
{
    /// <summary>Anchor point in this body's local space.</summary>
    public XnaVec3 LocalOffsetA { get; set; }

    /// <summary>Anchor point in the connected body's local space.</summary>
    public XnaVec3 LocalOffsetB { get; set; }

    protected override ConstraintHandle Add(Simulation simulation, BodyHandle a, BodyHandle b)
        => simulation.Solver.Add(a, b, new BallSocket
        {
            LocalOffsetA   = ToNum(LocalOffsetA),
            LocalOffsetB   = ToNum(LocalOffsetB),
            SpringSettings = Spring,
        });
}

/// <summary>
/// Allows rotation about a single shared axis and nothing else — a door, a wheel, a lever.
/// </summary>
/// <remarks>
/// The hinge axis is given in each body's own local space. For two bodies that start
/// aligned, the same axis value works for both.
/// </remarks>
public sealed class HingeConstraint : Constraint3D
{
    /// <summary>Anchor point in this body's local space.</summary>
    public XnaVec3 LocalOffsetA { get; set; }

    /// <summary>Anchor point in the connected body's local space.</summary>
    public XnaVec3 LocalOffsetB { get; set; }

    /// <summary>Hinge axis in this body's local space.</summary>
    public XnaVec3 LocalAxisA { get; set; } = XnaVec3.Up;

    /// <summary>Hinge axis in the connected body's local space.</summary>
    public XnaVec3 LocalAxisB { get; set; } = XnaVec3.Up;

    protected override ConstraintHandle Add(Simulation simulation, BodyHandle a, BodyHandle b)
        => simulation.Solver.Add(a, b, new Hinge
        {
            LocalOffsetA   = ToNum(LocalOffsetA),
            LocalOffsetB   = ToNum(LocalOffsetB),
            LocalHingeAxisA = Vector3.Normalize(ToNum(LocalAxisA)),
            LocalHingeAxisB = Vector3.Normalize(ToNum(LocalAxisB)),
            SpringSettings = Spring,
        });
}

/// <summary>
/// Confines one body to slide along a line fixed in the other — a piston, a lift, a drawer.
/// </summary>
/// <remarks>
/// Implemented with Bepu's <c>PointOnLineServo</c>, which constrains position to the line but
/// leaves rotation free. Pair it with an <see cref="AngularWeldConstraint"/> when the sliding
/// part must also keep its orientation.
/// </remarks>
public sealed class SliderConstraint : Constraint3D
{
    /// <summary>Point on this body that is held on the line, in local space.</summary>
    public XnaVec3 LocalOffsetA { get; set; }

    /// <summary>Point on the connected body that rides the line, in local space.</summary>
    public XnaVec3 LocalOffsetB { get; set; }

    /// <summary>Direction of the line, in this body's local space.</summary>
    public XnaVec3 LocalAxis { get; set; } = XnaVec3.Up;

    protected override ConstraintHandle Add(Simulation simulation, BodyHandle a, BodyHandle b)
        => simulation.Solver.Add(a, b, new PointOnLineServo
        {
            LocalOffsetA   = ToNum(LocalOffsetA),
            LocalOffsetB   = ToNum(LocalOffsetB),
            LocalDirection = Vector3.Normalize(ToNum(LocalAxis)),
            SpringSettings = Spring,
            ServoSettings  = ServoSettings.Default,
        });
}

/// <summary>
/// Keeps two anchor points within a distance range — a rope that can go slack, or a
/// rigid rod when min and max are equal.
/// </summary>
public sealed class DistanceLimitConstraint : Constraint3D
{
    /// <summary>Anchor point in this body's local space.</summary>
    public XnaVec3 LocalOffsetA { get; set; }

    /// <summary>Anchor point in the connected body's local space.</summary>
    public XnaVec3 LocalOffsetB { get; set; }

    /// <summary>Shortest allowed distance between the anchors.</summary>
    public float MinDistance { get; set; }

    /// <summary>Longest allowed distance between the anchors.</summary>
    public float MaxDistance { get; set; } = 2f;

    protected override ConstraintHandle Add(Simulation simulation, BodyHandle a, BodyHandle b)
        => simulation.Solver.Add(a, b, new DistanceLimit
        {
            LocalOffsetA   = ToNum(LocalOffsetA),
            LocalOffsetB   = ToNum(LocalOffsetB),
            MinimumDistance = MathF.Max(0f, MinDistance),
            MaximumDistance = MathF.Max(MinDistance, MaxDistance),
            SpringSettings = Spring,
        });
}

/// <summary>
/// Locks the relative orientation of two bodies while leaving their positions free.
/// Useful alongside <see cref="SliderConstraint"/> to stop a sliding part from spinning.
/// </summary>
public sealed class AngularWeldConstraint : Constraint3D
{
    /// <summary>Relative orientation to hold. Identity keeps whatever alignment they start with.</summary>
    public Microsoft.Xna.Framework.Quaternion LocalOrientation { get; set; }
        = Microsoft.Xna.Framework.Quaternion.Identity;

    protected override ConstraintHandle Add(Simulation simulation, BodyHandle a, BodyHandle b)
        => simulation.Solver.Add(a, b, new AngularServo
        {
            TargetRelativeRotationLocalA = new Quaternion(
                LocalOrientation.X, LocalOrientation.Y, LocalOrientation.Z, LocalOrientation.W),
            SpringSettings = Spring,
            ServoSettings  = ServoSettings.Default,
        });
}
