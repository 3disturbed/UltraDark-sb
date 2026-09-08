using System.Numerics;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;

using XnaVec3 = Microsoft.Xna.Framework.Vector3;
using XnaQuat = Microsoft.Xna.Framework.Quaternion;
using NumVec3 = System.Numerics.Vector3;
using NumQuat = System.Numerics.Quaternion;

namespace SexyBiscuit.Engine.Physics;

/// <summary>
/// Component that gives an <see cref="Actor"/> a simulated 3D rigid body backed by Bepu Physics v2.
/// If no <see cref="Collider3D"/> is present on the actor at <see cref="Awake"/> time, a unit sphere
/// shape is added automatically.
/// </summary>
public sealed class Rigidbody3D : Component
{
    // -----------------------------------------------------------------------
    // Cached configuration (applied when handle is created)
    // -----------------------------------------------------------------------
    private float   _mass            = 1f;
    private XnaVec3 _linearVelocity  = XnaVec3.Zero;
    private XnaVec3 _angularVelocity = XnaVec3.Zero;
    private float   _linearDamping   = 0f;
    private float   _angularDamping  = 0f;
    private bool    _isKinematic     = false;
    private bool    _useGravity      = true;
    private bool    _freezeRotation  = false;

    private BodyHandle _handle;
    private bool       _hasHandle;

    // The inertia in force when the rotation lock went on, so taking it off puts back what
    // the shape computed rather than an approximation of it.
    private BodyInertia _unlockedInertia;
    private bool        _hasUnlockedInertia;

    // -----------------------------------------------------------------------
    // Properties
    // -----------------------------------------------------------------------

    /// <summary>Mass of the body in kilograms. Default 1.</summary>
    public float Mass
    {
        get => _mass;
        set
        {
            _mass = value;
            if (_hasHandle)
            {
                var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_handle);
                // Re-compute inertia using existing shape
                var inertia = body.LocalInertia;
                // Scale inertia proportionally (approximate; proper inertia requires shape access)
                float ratio = _mass > 0f ? value / _mass : 1f;
                Symmetric3x3.Scale(inertia.InverseInertiaTensor, ratio, out var scaledInertia);
                body.LocalInertia = new BodyInertia
                {
                    InverseMass    = value > 0f ? 1f / value : 0f,
                    InverseInertiaTensor = scaledInertia
                };
            }
        }
    }

    /// <summary>Linear (translational) velocity in world space.</summary>
    public XnaVec3 LinearVelocity
    {
        get
        {
            if (_hasHandle)
            {
                var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_handle);
                return PhysicsConvert3D.ToXna(body.Velocity.Linear);
            }
            return _linearVelocity;
        }
        set
        {
            _linearVelocity = value;
            if (_hasHandle)
            {
                var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_handle);
                body.Velocity.Linear = PhysicsConvert3D.ToNum(value);
                body.Awake = true;
            }
        }
    }

    /// <summary>Angular (rotational) velocity in world space (radians per second per axis).</summary>
    public XnaVec3 AngularVelocity
    {
        get
        {
            if (_hasHandle)
            {
                var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_handle);
                return PhysicsConvert3D.ToXna(body.Velocity.Angular);
            }
            return _angularVelocity;
        }
        set
        {
            _angularVelocity = value;
            if (_hasHandle)
            {
                var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_handle);
                body.Velocity.Angular = PhysicsConvert3D.ToNum(value);
                body.Awake = true;
            }
        }
    }

    /// <summary>
    /// Linear drag coefficient. Applied per-step by <see cref="PoseIntegratorCallbacks"/>.
    /// Stored here for reference; the integrator uses per-simulation values unless overridden.
    /// </summary>
    public float LinearDamping
    {
        get => _linearDamping;
        set => _linearDamping = value;
    }

    /// <summary>Angular drag coefficient.</summary>
    public float AngularDamping
    {
        get => _angularDamping;
        set => _angularDamping = value;
    }

    /// <summary>Whether world gravity pulls on this body. Default true.</summary>
    /// <remarks>
    /// Bepu applies gravity in the pose integrator, which sees a bundle of bodies at a time
    /// and takes no per-body parameter, so the exemption is registered with
    /// <see cref="PhysicsSystem3D.SetGravityEnabled"/> and the integrator zeroes this body's
    /// share of the pull. Unlike <see cref="Rigidbody2D.GravityScale"/>, which Aether cannot
    /// honour and which is therefore stored for game logic only, this one is real.
    /// </remarks>
    public bool UseGravity
    {
        get => _useGravity;
        set
        {
            _useGravity = value;
            if (_hasHandle) PhysicsSystem3D.Instance.SetGravityEnabled(_handle, value);
        }
    }

    /// <summary>
    /// When true the body keeps its orientation: neither torque nor a contact can spin it.
    /// Default false.
    /// </summary>
    /// <remarks>
    /// Bepu locks rotation through the inertia tensor — infinite angular inertia, which is a
    /// zero inverse — so the solver has nothing to turn and <see cref="AddTorque"/> lands on
    /// a body that cannot answer it. The tensor in force when the lock goes on is kept and
    /// put back when it comes off, rather than falling back to the sphere approximation
    /// <see cref="IsKinematic"/> uses.
    /// </remarks>
    public bool FreezeRotation
    {
        get => _freezeRotation;
        set
        {
            _freezeRotation = value;
            if (_hasHandle) ApplyRotationLock();
        }
    }

    /// <summary>
    /// When true the body is kinematic: its velocity is integrated but forces are not applied.
    /// </summary>
    public bool IsKinematic
    {
        get => _isKinematic;
        set
        {
            _isKinematic = value;
            if (_hasHandle)
            {
                var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_handle);
                if (value)
                {
                    body.BecomeKinematic();
                }
                else
                {
                    // Restore dynamic inertia using mass (approximate sphere inertia)
                    var sphere  = new Sphere(0.5f);
                    var inertia = sphere.ComputeInertia(_mass);
                    body.LocalInertia = inertia;
                }
            }
        }
    }

    /// <summary>Direct access to the Bepu BodyHandle (valid after Awake).</summary>
    public BodyHandle Handle => _handle;

    /// <summary>Whether the handle has been created successfully.</summary>
    public bool HasHandle => _hasHandle;

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    public override void Awake()
    {
        var collider = GetComponent<Collider3D>();
        if (collider != null)
        {
            // Collider registers the body itself via RegisterShape
            collider.RegisterShape(PhysicsSystem3D.Instance, Actor, _mass);
        }
        else
        {
            // No collider: default unit sphere
            _handle    = PhysicsSystem3D.Instance.AddSphere(Actor, 0.5f, _mass);
            _hasHandle = true;
        }

        // Retrieve handle from the system mapping if the collider created it
        if (!_hasHandle && PhysicsSystem3D.Instance.TryGetHandle(Actor, out var h))
        {
            _handle    = h;
            _hasHandle = true;
        }

        if (_hasHandle)
        {
            ApplyCachedValues();
        }
    }

    public override void OnDestroy()
    {
        if (_hasHandle)
        {
            PhysicsSystem3D.Instance.RemoveBody(Actor);
            _hasHandle = false;
        }
    }

    /// <summary>
    /// Reads back body velocity from the simulation so property getters stay current.
    /// Note: PhysicsSystem3D.FixedStep syncs positions; this step syncs velocity caches.
    /// </summary>
    public override void FixedUpdate(float dt)
    {
        if (!_hasHandle) return;

        var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_handle);
        _linearVelocity  = PhysicsConvert3D.ToXna(body.Velocity.Linear);
        _angularVelocity = PhysicsConvert3D.ToXna(body.Velocity.Angular);
    }

    // -----------------------------------------------------------------------
    // Force API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Applies a world-space force (Newtons) at the centre of mass.
    /// </summary>
    public void AddForce(XnaVec3 force)
    {
        if (!_hasHandle) return;
        var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_handle);
        body.ApplyLinearImpulse(PhysicsConvert3D.ToNum(force));
        body.Awake = true;
    }

    /// <summary>
    /// Applies an instantaneous linear impulse (kg·m/s) at the centre of mass.
    /// </summary>
    public void AddImpulse(XnaVec3 impulse)
    {
        if (!_hasHandle) return;
        var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_handle);
        body.ApplyLinearImpulse(PhysicsConvert3D.ToNum(impulse));
        body.Awake = true;
    }

    /// <summary>
    /// Applies a world-space angular impulse (angular momentum, kg·m²/s).
    /// </summary>
    public void AddTorque(XnaVec3 torque)
    {
        if (!_hasHandle) return;
        var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_handle);
        body.ApplyAngularImpulse(PhysicsConvert3D.ToNum(torque));
        body.Awake = true;
    }

    // -----------------------------------------------------------------------
    // Internal helpers
    // -----------------------------------------------------------------------

    private void ApplyCachedValues()
    {
        if (!_hasHandle) return;

        var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_handle);
        body.Velocity.Linear  = PhysicsConvert3D.ToNum(_linearVelocity);
        body.Velocity.Angular = PhysicsConvert3D.ToNum(_angularVelocity);

        if (_isKinematic)
            body.BecomeKinematic();

        // The shape is registered by the collider, so the body only exists from here on:
        // anything a scene set before Awake has been sitting in a field until now.
        PhysicsSystem3D.Instance.SetGravityEnabled(_handle, _useGravity);
        ApplyRotationLock();

        body.Awake = true;
    }

    /// <summary>Puts <see cref="FreezeRotation"/> into the body's inertia, or takes it out.</summary>
    private void ApplyRotationLock()
    {
        var body = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(_handle);

        if (_freezeRotation)
        {
            if (!_hasUnlockedInertia)
            {
                _unlockedInertia    = body.LocalInertia;
                _hasUnlockedInertia = true;
            }

            var locked = body.LocalInertia;
            locked.InverseInertiaTensor = default;
            body.LocalInertia = locked;

            body.Velocity.Angular = NumVec3.Zero;
            _angularVelocity      = XnaVec3.Zero;
        }
        else if (_hasUnlockedInertia)
        {
            body.LocalInertia   = _unlockedInertia;
            _hasUnlockedInertia = false;
        }
    }
}
