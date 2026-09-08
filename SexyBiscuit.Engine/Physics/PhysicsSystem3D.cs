using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;

using XnaVec3 = Microsoft.Xna.Framework.Vector3;
using XnaQuat = Microsoft.Xna.Framework.Quaternion;
using NumVec3 = System.Numerics.Vector3;
using NumQuat = System.Numerics.Quaternion;

namespace SexyBiscuit.Engine.Physics;

// ---------------------------------------------------------------------------
// RaycastHit3D
// ---------------------------------------------------------------------------

/// <summary>Result of a 3D physics raycast.</summary>
public struct RaycastHit3D
{
    public Actor?  Actor    { get; init; }
    public XnaVec3 Point    { get; init; }
    public XnaVec3 Normal   { get; init; }
    public float   Distance { get; init; }
}

// ---------------------------------------------------------------------------
// Bepu callback structs
// ---------------------------------------------------------------------------

/// <summary>
/// Narrow-phase callbacks wired into collision event dispatch.
/// </summary>
internal struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    internal PhysicsSystem3D Owner;

    public void Initialize(Simulation simulation) { }

    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
        => a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;

    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
        => true;

    public bool ConfigureContactManifold<TManifold>(
        int workerIndex,
        CollidablePair pair,
        ref TManifold manifold,
        out PairMaterialProperties pairMaterial) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial = new PairMaterialProperties
        {
            FrictionCoefficient      = 0.5f,
            MaximumRecoveryVelocity  = 2f,
            SpringSettings           = new SpringSettings(30f, 1f)
        };

        // Dispatch collision events
        if (manifold.Count > 0)
            Owner.OnContactAdded(pair, ref manifold);

        return true;
    }

    public bool ConfigureContactManifold(
        int workerIndex,
        CollidablePair pair,
        int childIndexA,
        int childIndexB,
        ref ConvexContactManifold manifold)
        => true;

    public void Dispose() { }
}

/// <summary>
/// The bodies that have opted out of world gravity, shared by reference with the pose
/// integrator's callback struct.
/// </summary>
/// <remarks>
/// Bepu integrates a bundle of bodies at a time and its callback takes no per-body
/// parameter, so a per-body switch has to be looked up from inside the integrator. Handles
/// are stable and the active-set index the integrator is handed is not, so the lookup goes
/// index → handle → set. It runs only while <see cref="Any"/> is true, which leaves the
/// ordinary everything-falls case on the single broadcast it always used. Reads happen on
/// Bepu's worker threads and writes on the thread that owns the component, between steps.
/// </remarks>
internal sealed class GravityExemptions
{
    private readonly HashSet<int> _handles = new();

    /// <summary>The simulation's body store, for the index → handle lookup.</summary>
    internal Bodies? Bodies;

    /// <summary>True when at least one body has opted out.</summary>
    internal bool Any => _handles.Count > 0;

    /// <summary>Switches world gravity on or off for one body.</summary>
    internal void Set(BodyHandle handle, bool useGravity)
    {
        if (useGravity) _handles.Remove(handle.Value);
        else _handles.Add(handle.Value);
    }

    /// <summary>True when world gravity still pulls on the body.</summary>
    internal bool IsEnabled(BodyHandle handle) => !_handles.Contains(handle.Value);

    /// <summary>Drops a handle as its body leaves the simulation.</summary>
    internal void Forget(BodyHandle handle) => _handles.Remove(handle.Value);

    /// <summary>Gravity's multiplier for one lane of an integration bundle: 1 or 0.</summary>
    internal float ScaleForActiveIndex(int index)
    {
        var bodies = Bodies;
        if (bodies == null || index < 0 || index >= bodies.ActiveSet.Count) return 1f;
        return _handles.Contains(bodies.ActiveSet.IndexToHandle[index].Value) ? 0f : 1f;
    }
}

/// <summary>
/// Pose integration callbacks — applies gravity and damping each simulation step.
/// </summary>
internal struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    public readonly AngularIntegrationMode AngularIntegrationMode
        => AngularIntegrationMode.Nonconserving;

    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    private NumVec3        _gravity;
    private float          _linearDamping;
    private float          _angularDamping;
    private NumVec3        _dtGravity;
    private float          _dtLinearDamping;
    private float          _dtAngularDamping;

    // A reference, so the copy of this struct Bepu keeps sees the same table the engine
    // writes to — the same trick NarrowPhaseCallbacks uses to reach its owner.
    private readonly GravityExemptions? _exemptions;

    public PoseIntegratorCallbacks(NumVec3 gravity, GravityExemptions? exemptions = null,
                                   float linearDamping = 0f, float angularDamping = 0f)
    {
        _gravity        = gravity;
        _exemptions     = exemptions;
        _linearDamping  = linearDamping;
        _angularDamping = angularDamping;
        _dtGravity      = default;
        _dtLinearDamping  = default;
        _dtAngularDamping = default;
    }

    public void Initialize(Simulation simulation) { }

    public void PrepareForIntegration(float dt)
    {
        _dtGravity        = _gravity * dt;
        _dtLinearDamping  = MathF.Pow(Math.Clamp(1f - _linearDamping,  0f, 1f), dt);
        _dtAngularDamping = MathF.Pow(Math.Clamp(1f - _angularDamping, 0f, 1f), dt);
    }

    public void IntegrateVelocity(
        Vector<int>    bodyIndices,
        Vector3Wide    position,
        QuaternionWide orientation,
        BodyInertiaWide localInertia,
        Vector<int>    integrationMask,
        int            workerIndex,
        Vector<float>  dt,
        ref BodyVelocityWide velocity)
    {
        var gravity = Vector3Wide.Broadcast(_dtGravity);

        // Rigidbody3D.UseGravity: zero this step's pull for the lanes that opted out. The
        // bundle is only walked once something has, so a world where everything falls pays
        // nothing for the feature.
        if (_exemptions is { Any: true })
        {
            Span<float> scales = stackalloc float[Vector<float>.Count];
            for (int lane = 0; lane < scales.Length; lane++)
                scales[lane] = _exemptions.ScaleForActiveIndex(bodyIndices[lane]);

            var scale = new Vector<float>(scales);
            gravity.X *= scale;
            gravity.Y *= scale;
            gravity.Z *= scale;
        }

        velocity.Linear  = (velocity.Linear  + gravity) * new Vector<float>(_dtLinearDamping);
        velocity.Angular =  velocity.Angular * new Vector<float>(_dtAngularDamping);
    }
}

// ---------------------------------------------------------------------------
// Raycast hit handler
// ---------------------------------------------------------------------------

internal struct RaycastHitHandler : IRayHitHandler
{
    private readonly PhysicsSystem3D _owner;
    private readonly float           _maxDistance;

    public RaycastHit3D  Best;
    public bool          DidHit;

    public RaycastHitHandler(PhysicsSystem3D owner, float maxDistance)
    {
        _owner       = owner;
        _maxDistance = maxDistance;
        Best         = default;
        DidHit       = false;
    }

    public bool AllowTest(CollidableReference collidable) => true;
    public bool AllowTest(CollidableReference collidable, int childIndex) => true;

    public void OnRayHit(
        in RayData ray,
        ref float  maximumT,
        float      t,
        in NumVec3 normal,
        CollidableReference collidable,
        int        childIndex)
    {
        if (t >= maximumT) return;

        maximumT = t;
        DidHit   = true;

        Actor? actor = null;
        if (collidable.Mobility == CollidableMobility.Dynamic ||
            collidable.Mobility == CollidableMobility.Kinematic)
        {
            _owner.TryGetActorByHandle(new BodyHandle(collidable.RawHandleValue), out actor);
        }

        Best = new RaycastHit3D
        {
            Actor    = actor,
            Point    = PhysicsConvert3D.ToXna(ray.Origin + ray.Direction * t),
            Normal   = PhysicsConvert3D.ToXna(normal),
            Distance = t
        };
    }
}

// ---------------------------------------------------------------------------
// PhysicsSystem3D
// ---------------------------------------------------------------------------

/// <summary>
/// Singleton manager for the Bepu Physics v2 3D simulation.
/// Call <see cref="FixedStep"/> once per fixed-update tick from your game loop.
/// </summary>
public sealed class PhysicsSystem3D : IDisposable
{
    // -----------------------------------------------------------------------
    // Singleton
    // -----------------------------------------------------------------------
    public static PhysicsSystem3D Instance { get; } = new PhysicsSystem3D();

    // -----------------------------------------------------------------------
    // Bepu internals
    // -----------------------------------------------------------------------
    public  Simulation    Simulation  { get; }
    private BufferPool    _pool;

    // -----------------------------------------------------------------------
    // Actor ↔ BodyHandle mapping
    // -----------------------------------------------------------------------
    private readonly Dictionary<Actor,      BodyHandle> _actorToHandle = new();
    private readonly Dictionary<BodyHandle, Actor>      _handleToActor = new();

    // Read by the pose integrator, written by Rigidbody3D.UseGravity.
    private readonly GravityExemptions _gravityExemptions = new();

    // Pending collision events queued from narrow-phase callbacks (which run on worker threads)
    private readonly List<(CollidablePair pair, XnaVec3 point, XnaVec3 normal)> _pendingContacts = new();
    private readonly object _contactLock = new();

    // -----------------------------------------------------------------------
    // Construction / Dispose
    // -----------------------------------------------------------------------
    private PhysicsSystem3D()
    {
        _pool = new BufferPool();

        var narrowPhase = new NarrowPhaseCallbacks { Owner = this };
        var poseIntegrator = new PoseIntegratorCallbacks(
            new NumVec3(0f, -9.81f, 0f), _gravityExemptions);

        Simulation = Simulation.Create(
            _pool,
            narrowPhase,
            poseIntegrator,
            new SolveDescription(8, 1));

        _gravityExemptions.Bodies = Simulation.Bodies;
    }

    public void Dispose()
    {
        Simulation.Dispose();
        _pool.Clear();
    }

    // -----------------------------------------------------------------------
    // Simulation step
    // -----------------------------------------------------------------------

    /// <summary>
    /// Steps the Bepu simulation by <paramref name="dt"/> seconds and syncs all
    /// body poses back to their owning actor's <see cref="Transform3D"/>.
    /// </summary>
    public void FixedStep(float dt)
    {
        Simulation.Timestep(dt);

        // Sync poses → Transforms
        foreach (var (actor, handle) in _actorToHandle)
        {
            if (!actor.IsActive) continue;

            var body = Simulation.Bodies.GetBodyReference(handle);
            var pose = body.Pose;

            var t3d = actor.GetComponent<Transform3D>();
            if (t3d == null) continue;

            t3d.Position = PhysicsConvert3D.ToXna(pose.Position);
            t3d.Rotation = PhysicsConvert3D.ToXna(pose.Orientation);
        }

        // Dispatch queued contact events on the main thread
        DispatchPendingContacts();
    }

    // -----------------------------------------------------------------------
    // Body management
    // -----------------------------------------------------------------------

    /// <summary>
    /// Adds a dynamic box body for the given actor and returns its handle.
    /// </summary>
    public BodyHandle AddBox(Actor actor, XnaVec3 halfExtents, float mass)
    {
        var shape   = new Box(halfExtents.X * 2f, halfExtents.Y * 2f, halfExtents.Z * 2f);
        var inertia = shape.ComputeInertia(mass);
        var idx     = Simulation.Shapes.Add(shape);

        var t3d    = actor.GetComponent<Transform3D>();
        var pos    = t3d != null ? PhysicsConvert3D.ToNum(t3d.Position) : NumVec3.Zero;
        var orient = t3d != null ? PhysicsConvert3D.ToNum(t3d.Rotation) : NumQuat.Identity;

        var handle = Simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(pos, orient),
            inertia,
            new CollidableDescription(idx, 0.1f),
            new BodyActivityDescription(0.01f)));

        Register(actor, handle);
        return handle;
    }

    /// <summary>
    /// Adds a dynamic sphere body for the given actor.
    /// </summary>
    public BodyHandle AddSphere(Actor actor, float radius, float mass)
    {
        var shape   = new Sphere(radius);
        var inertia = shape.ComputeInertia(mass);
        var idx     = Simulation.Shapes.Add(shape);

        var t3d    = actor.GetComponent<Transform3D>();
        var pos    = t3d != null ? PhysicsConvert3D.ToNum(t3d.Position) : NumVec3.Zero;
        var orient = t3d != null ? PhysicsConvert3D.ToNum(t3d.Rotation) : NumQuat.Identity;

        var handle = Simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(pos, orient),
            inertia,
            new CollidableDescription(idx, 0.1f),
            new BodyActivityDescription(0.01f)));

        Register(actor, handle);
        return handle;
    }

    /// <summary>
    /// Adds a dynamic capsule body for the given actor.
    /// </summary>
    public BodyHandle AddCapsule(Actor actor, float radius, float length, float mass)
    {
        var shape   = new Capsule(radius, length);
        var inertia = shape.ComputeInertia(mass);
        var idx     = Simulation.Shapes.Add(shape);

        var t3d    = actor.GetComponent<Transform3D>();
        var pos    = t3d != null ? PhysicsConvert3D.ToNum(t3d.Position) : NumVec3.Zero;
        var orient = t3d != null ? PhysicsConvert3D.ToNum(t3d.Rotation) : NumQuat.Identity;

        var handle = Simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(pos, orient),
            inertia,
            new CollidableDescription(idx, 0.1f),
            new BodyActivityDescription(0.01f)));

        Register(actor, handle);
        return handle;
    }

    /// <summary>
    /// Adds a dynamic cylinder body for the given actor.
    /// </summary>
    /// <remarks>
    /// Bepu treats cylinders as a special case with a more expensive contact solver than
    /// boxes or capsules. Prefer a capsule for characters and a box for crates; reach for
    /// a cylinder when the flat circular face matters — wheels, barrels, coins.
    /// </remarks>
    public BodyHandle AddCylinder(Actor actor, float radius, float length, float mass)
    {
        var shape   = new Cylinder(radius, length);
        var inertia = shape.ComputeInertia(mass);
        var idx     = Simulation.Shapes.Add(shape);

        var t3d    = actor.GetComponent<Transform3D>();
        var pos    = t3d != null ? PhysicsConvert3D.ToNum(t3d.Position) : NumVec3.Zero;
        var orient = t3d != null ? PhysicsConvert3D.ToNum(t3d.Rotation) : NumQuat.Identity;

        var handle = Simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(pos, orient),
            inertia,
            new CollidableDescription(idx, 0.1f),
            new BodyActivityDescription(0.01f)));

        Register(actor, handle);
        return handle;
    }

    /// <summary>
    /// Adds a dynamic body built from several child shapes rigidly welded together.
    /// </summary>
    /// <remarks>
    /// A compound is how you get a concave dynamic body — a table, an L-shaped block, a
    /// vehicle chassis with a bumper. Bepu has no concave dynamic primitive, so the shape
    /// is decomposed into convex parts and their inertias are combined about the
    /// compound's centre of mass.
    /// </remarks>
    /// <param name="actor">Actor the body belongs to.</param>
    /// <param name="children">Child shapes with their local poses and individual masses.</param>
    public BodyHandle AddCompound(Actor actor, IReadOnlyList<CompoundChildShape> children)
    {
        if (children.Count == 0)
            throw new ArgumentException("A compound needs at least one child shape.", nameof(children));

        using var builder = new CompoundBuilder(_pool, Simulation.Shapes, children.Count);

        foreach (var child in children)
        {
            var pose = new RigidPose(
                PhysicsConvert3D.ToNum(child.LocalPosition),
                PhysicsConvert3D.ToNum(child.LocalRotation));

            switch (child.Kind)
            {
                case CompoundChildKind.Box:
                    builder.Add(new Box(child.Size.X, child.Size.Y, child.Size.Z), pose, child.Mass);
                    break;
                case CompoundChildKind.Sphere:
                    builder.Add(new Sphere(child.Radius), pose, child.Mass);
                    break;
                case CompoundChildKind.Capsule:
                    builder.Add(new Capsule(child.Radius, child.Length), pose, child.Mass);
                    break;
                case CompoundChildKind.Cylinder:
                    builder.Add(new Cylinder(child.Radius, child.Length), pose, child.Mass);
                    break;
            }
        }

        builder.BuildDynamicCompound(out var compoundChildren, out var inertia, out var center);
        var compound = new Compound(compoundChildren);
        var idx      = Simulation.Shapes.Add(compound);

        var t3d    = actor.GetComponent<Transform3D>();
        var pos    = t3d != null ? PhysicsConvert3D.ToNum(t3d.Position) : NumVec3.Zero;
        var orient = t3d != null ? PhysicsConvert3D.ToNum(t3d.Rotation) : NumQuat.Identity;

        var handle = Simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(pos + center, orient),
            inertia,
            new CollidableDescription(idx, 0.1f),
            new BodyActivityDescription(0.01f)));

        Register(actor, handle);
        return handle;
    }

    /// <summary>Adds a static (immovable) box to the world. Not bound to any actor.</summary>
    public void AddStaticBox(XnaVec3 position, XnaQuat rotation, XnaVec3 halfExtents)
    {
        var shape = new Box(halfExtents.X * 2f, halfExtents.Y * 2f, halfExtents.Z * 2f);
        var idx   = Simulation.Shapes.Add(shape);

        Simulation.Statics.Add(new StaticDescription(
            new RigidPose(
                PhysicsConvert3D.ToNum(position),
                PhysicsConvert3D.ToNum(rotation)),
            idx));
    }

    /// <summary>
    /// Adds a static triangle mesh to the world.
    /// <paramref name="vertices"/> and <paramref name="indices"/> form the mesh triangles.
    /// </summary>
    /// <remarks>
    /// Static only. Bepu supports concave meshes for statics but not for dynamic bodies,
    /// because a general concave inertia tensor and contact manifold are not tractable at
    /// simulation speed. For a concave moving object, use <see cref="AddCompound"/> to
    /// build it out of convex parts.
    /// </remarks>
    /// <param name="vertices">Mesh vertices in local space.</param>
    /// <param name="indices">Triangle indices, three per triangle.</param>
    /// <param name="position">World position of the mesh origin.</param>
    /// <param name="rotation">World orientation of the mesh.</param>
    /// <param name="scale">Per-axis scale applied to the mesh.</param>
    public void AddStaticMesh(XnaVec3[] vertices, int[] indices, XnaVec3 position,
                              XnaQuat? rotation = null, XnaVec3? scale = null)
    {
        int triCount = indices.Length / 3;
        _pool.Take<Triangle>(triCount, out var triangles);

        for (int i = 0; i < triCount; i++)
        {
            triangles[i] = new Triangle(
                PhysicsConvert3D.ToNum(vertices[indices[i * 3]]),
                PhysicsConvert3D.ToNum(vertices[indices[i * 3 + 1]]),
                PhysicsConvert3D.ToNum(vertices[indices[i * 3 + 2]]));
        }

        var meshScale = scale.HasValue ? PhysicsConvert3D.ToNum(scale.Value) : NumVec3.One;
        var mesh = new Mesh(triangles, meshScale, _pool);
        var idx  = Simulation.Shapes.Add(mesh);

        Simulation.Statics.Add(new StaticDescription(
            new RigidPose(
                PhysicsConvert3D.ToNum(position),
                rotation.HasValue ? PhysicsConvert3D.ToNum(rotation.Value) : NumQuat.Identity),
            idx));
    }

    /// <summary>
    /// Removes the body associated with <paramref name="actor"/> from the simulation.
    /// </summary>
    public void RemoveBody(Actor actor)
    {
        if (!_actorToHandle.TryGetValue(actor, out var handle)) return;
        _actorToHandle.Remove(actor);
        _handleToActor.Remove(handle);
        _gravityExemptions.Forget(handle);
        Simulation.Bodies.Remove(handle);
    }

    // -----------------------------------------------------------------------
    // Per-body gravity
    // -----------------------------------------------------------------------

    /// <summary>
    /// Switches world gravity on or off for one body — what <see cref="Rigidbody3D.UseGravity"/>
    /// is made of.
    /// </summary>
    /// <remarks>
    /// Bepu applies gravity in the pose integrator, over a bundle of bodies at a time and
    /// with no per-body parameter of its own, so the exemption is held here and the
    /// integrator zeroes that body's share of the pull. The exemption is dropped with the
    /// body in <see cref="RemoveBody"/>, so a recycled handle does not inherit it.
    /// </remarks>
    public void SetGravityEnabled(BodyHandle handle, bool enabled)
        => _gravityExemptions.Set(handle, enabled);

    /// <summary>True when world gravity still pulls on the body.</summary>
    public bool IsGravityEnabled(BodyHandle handle) => _gravityExemptions.IsEnabled(handle);

    // -----------------------------------------------------------------------
    // Raycast
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fires a ray into the 3D world.  Returns true if any body was hit.
    /// </summary>
    public bool Raycast(
        XnaVec3 origin,
        XnaVec3 direction,
        float   distance,
        out RaycastHit3D hit,
        int layerMask = -1)
    {
        var dir = XnaVec3.Normalize(direction);
        var handler = new RaycastHitHandler(this, distance);

        Simulation.RayCast(
            PhysicsConvert3D.ToNum(origin),
            PhysicsConvert3D.ToNum(dir),
            distance,
            ref handler);

        hit = handler.Best;
        return handler.DidHit;
    }

    // -----------------------------------------------------------------------
    // Internal helpers
    // -----------------------------------------------------------------------

    private void Register(Actor actor, BodyHandle handle)
    {
        _actorToHandle[actor]  = handle;
        _handleToActor[handle] = actor;
    }

    internal bool TryGetActorByHandle(BodyHandle handle, out Actor? actor)
        => _handleToActor.TryGetValue(handle, out actor);

    public bool TryGetHandle(Actor actor, out BodyHandle handle)
        => _actorToHandle.TryGetValue(actor, out handle);

    // Called from NarrowPhaseCallbacks — may be on a worker thread
    internal void OnContactAdded<TManifold>(CollidablePair pair, ref TManifold manifold)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        // Grab the first contact point and normal, then enqueue for main-thread dispatch
        if (manifold.Count == 0) return;

        manifold.GetContact(0, out var offset, out var normal, out _, out _);

        XnaVec3 point  = PhysicsConvert3D.ToXna(offset);
        XnaVec3 nrmXna = PhysicsConvert3D.ToXna(normal);

        lock (_contactLock)
            _pendingContacts.Add((pair, point, nrmXna));
    }

    private void DispatchPendingContacts()
    {
        List<(CollidablePair, XnaVec3, XnaVec3)> snapshot;
        lock (_contactLock)
        {
            if (_pendingContacts.Count == 0) return;
            snapshot = new List<(CollidablePair, XnaVec3, XnaVec3)>(_pendingContacts);
            _pendingContacts.Clear();
        }

        foreach (var (pair, point, normal) in snapshot)
        {
            Actor? actorA = null;
            Actor? actorB = null;

            if (pair.A.Mobility == CollidableMobility.Dynamic ||
                pair.A.Mobility == CollidableMobility.Kinematic)
                TryGetActorByHandle(new BodyHandle(pair.A.RawHandleValue), out actorA);

            if (pair.B.Mobility == CollidableMobility.Dynamic ||
                pair.B.Mobility == CollidableMobility.Kinematic)
                TryGetActorByHandle(new BodyHandle(pair.B.RawHandleValue), out actorB);

            if (actorA != null && actorB != null)
            {
                var dataA = new CollisionData
                {
                    Other            = actorB,
                    ContactPoint     = new Microsoft.Xna.Framework.Vector2(point.X, point.Z),
                    Normal           = new Microsoft.Xna.Framework.Vector2(normal.X, normal.Z),
                    RelativeVelocity = 0f
                };
                var dataB = new CollisionData
                {
                    Other            = actorA,
                    ContactPoint     = dataA.ContactPoint,
                    Normal           = -dataA.Normal,
                    RelativeVelocity = 0f
                };

                actorA.OnCollisionEnter(dataA);
                actorB.OnCollisionEnter(dataB);
            }
        }
    }
}

// ---------------------------------------------------------------------------
// PhysicsConvert3D
// ---------------------------------------------------------------------------

/// <summary>
/// Conversion helpers between MonoGame/XNA types and System.Numerics types used by Bepu.
/// </summary>
public static class PhysicsConvert3D
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static XnaVec3 ToXna(NumVec3 v) => new XnaVec3(v.X, v.Y, v.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NumVec3 ToNum(XnaVec3 v) => new NumVec3(v.X, v.Y, v.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static XnaQuat ToXna(NumQuat q) => new XnaQuat(q.X, q.Y, q.Z, q.W);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NumQuat ToNum(XnaQuat q) => new NumQuat(q.X, q.Y, q.Z, q.W);
}
