using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using nkast.Aether.Physics2D.Dynamics;

using XnaVec2 = Microsoft.Xna.Framework.Vector2;

namespace SexyBiscuit.Engine.Physics;

/// <summary>
/// Component that gives an <see cref="Actor"/> a simulated 2D rigid body.
/// Wraps an Aether Physics2D <see cref="Body"/> and exposes a Unity-like API.
/// </summary>
[RequireComponent(typeof(Transform))]
public sealed class Rigidbody2D : Component
{
    // -----------------------------------------------------------------------
    // Internal state
    // -----------------------------------------------------------------------
    private Body? _body;

    // Cached values applied to the body once it is created (or immediately if already created)
    private float    _mass            = 1f;
    private float    _gravityScale    = 1f;
    private XnaVec2  _linearVelocity  = XnaVec2.Zero;
    private float    _angularVelocity = 0f;
    private float    _linearDamping   = 0f;
    private float    _angularDamping  = 0f;
    private bool     _freezeRotation  = false;
    private bool     _isKinematic     = false;

    // -----------------------------------------------------------------------
    // Properties
    // -----------------------------------------------------------------------

    /// <summary>
    /// Mass of the body in kilograms.  Must be positive.
    /// Changing at runtime re-applies to the underlying body.
    /// </summary>
    public float Mass
    {
        get => _body != null ? _body.Mass : _mass;
        set
        {
            _mass = value;
            if (_body != null) _body.Mass = value;
        }
    }

    /// <summary>
    /// Multiplier applied to the world gravity for this body. Default 1.
    /// Note: Aether 2.1.0 does not support per-body gravity scale natively;
    /// this is stored locally for game-logic use.
    /// </summary>
    public float GravityScale
    {
        get => _gravityScale;
        set => _gravityScale = value;
    }

    /// <summary>Linear velocity of the body in world space (pixels per second).</summary>
    public XnaVec2 LinearVelocity
    {
        get => _body != null ? PhysicsConvert.ToXna(_body.LinearVelocity) : _linearVelocity;
        set
        {
            _linearVelocity = value;
            if (_body != null) _body.LinearVelocity = PhysicsConvert.ToAether(value);
        }
    }

    /// <summary>Angular velocity of the body in radians per second.</summary>
    public float AngularVelocity
    {
        get => _body != null ? _body.AngularVelocity : _angularVelocity;
        set
        {
            _angularVelocity = value;
            if (_body != null) _body.AngularVelocity = value;
        }
    }

    /// <summary>Linear damping coefficient (drag). Reduces linear velocity each step.</summary>
    public float LinearDamping
    {
        get => _body != null ? _body.LinearDamping : _linearDamping;
        set
        {
            _linearDamping = value;
            if (_body != null) _body.LinearDamping = value;
        }
    }

    /// <summary>Angular damping coefficient. Reduces angular velocity each step.</summary>
    public float AngularDamping
    {
        get => _body != null ? _body.AngularDamping : _angularDamping;
        set
        {
            _angularDamping = value;
            if (_body != null) _body.AngularDamping = value;
        }
    }

    /// <summary>
    /// When true the body will not rotate due to physics forces.
    /// Useful for characters and top-down objects.
    /// </summary>
    public bool FreezeRotation
    {
        get => _body != null ? _body.FixedRotation : _freezeRotation;
        set
        {
            _freezeRotation = value;
            if (_body != null) _body.FixedRotation = value;
        }
    }

    /// <summary>
    /// When true the body is kinematic: it is not affected by forces or gravity
    /// but can still push dynamic bodies and be moved via <see cref="LinearVelocity"/>.
    /// </summary>
    public bool IsKinematic
    {
        get => _isKinematic;
        set
        {
            _isKinematic = value;
            if (_body != null)
                _body.BodyType = value ? BodyType.Kinematic : BodyType.Dynamic;
        }
    }

    /// <summary>Computed Aether body type from <see cref="IsKinematic"/>.</summary>
    public BodyType BodyType => _isKinematic ? BodyType.Kinematic : BodyType.Dynamic;

    /// <summary>Direct access to the underlying Aether body (use with care).</summary>
    public Body? Body => _body;

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    public override void Awake()
    {
        _body = PhysicsSystem2D.Instance.CreateBody(Actor, BodyType);
        ApplyCachedValues();
    }

    public override void OnDestroy()
    {
        if (_body != null)
        {
            PhysicsSystem2D.Instance.UnregisterBody(Actor);
            _body = null;
        }
    }

    /// <summary>
    /// PhysicsSystem2D already syncs body→Transform during <c>FixedStep</c>;
    /// this override exists as a hook and records updated velocities.
    /// </summary>
    public override void FixedUpdate(float dt)
    {
        if (_body == null) return;

        // Keep cached values in sync so getters work even if body gets re-created
        _linearVelocity  = PhysicsConvert.ToXna(_body.LinearVelocity);
        _angularVelocity = _body.AngularVelocity;
    }

    // -----------------------------------------------------------------------
    // Force API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Applies a continuous force (Newtons) at the body's centre of mass.
    /// Use in <c>FixedUpdate</c> for sustained forces.
    /// </summary>
    public void AddForce(XnaVec2 force)
    {
        _body?.ApplyForce(PhysicsConvert.ToAether(force));
    }

    /// <summary>
    /// Applies a continuous force at a specific world-space point, potentially creating torque.
    /// </summary>
    public void AddForceAtPoint(XnaVec2 force, XnaVec2 worldPoint)
    {
        _body?.ApplyForce(PhysicsConvert.ToAether(force), PhysicsConvert.ToAether(worldPoint));
    }

    /// <summary>
    /// Applies an instantaneous impulse (kg·m/s) at the centre of mass.
    /// Use for jump forces, explosions, etc.
    /// </summary>
    public void AddImpulse(XnaVec2 impulse)
    {
        _body?.ApplyLinearImpulse(PhysicsConvert.ToAether(impulse));
    }

    /// <summary>
    /// Applies a rotational torque (N·m) to the body.
    /// </summary>
    public void AddTorque(float torque)
    {
        _body?.ApplyTorque(torque);
    }

    // -----------------------------------------------------------------------
    // Internal helpers
    // -----------------------------------------------------------------------

    private void ApplyCachedValues()
    {
        if (_body == null) return;

        _body.Mass            = _mass;
        _body.LinearDamping   = _linearDamping;
        _body.AngularDamping  = _angularDamping;
        _body.FixedRotation   = _freezeRotation;
        _body.LinearVelocity  = PhysicsConvert.ToAether(_linearVelocity);
        _body.AngularVelocity = _angularVelocity;
        _body.BodyType        = BodyType;
    }
}
