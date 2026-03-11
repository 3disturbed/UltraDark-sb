using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using BepuPhysics;
using BepuPhysics.Collidables;

using XnaVec3 = Microsoft.Xna.Framework.Vector3;

namespace SexyBiscuit.Engine.Physics;

// ---------------------------------------------------------------------------
// Collider3D (abstract base)
// ---------------------------------------------------------------------------

/// <summary>
/// Abstract base for all 3D collider components.
/// Subclasses implement <see cref="RegisterShape"/> to add a Bepu shape and body
/// to the <see cref="PhysicsSystem3D"/>.
/// </summary>
public abstract class Collider3D : Component
{
    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    /// <summary>
    /// When true this shape acts as a sensor/trigger: collision events fire but
    /// no physical response is generated.
    /// Note: Bepu does not have native sensor support; this flag is respected
    /// through <see cref="PhysicsSystem3D"/> event filtering.
    /// </summary>
    public bool IsTrigger { get; set; } = false;

    /// <summary>Surface friction coefficient. Default 0.5.</summary>
    public float Friction    { get; set; } = 0.5f;

    /// <summary>Coefficient of restitution (bounciness). Default 0.</summary>
    public float Restitution { get; set; } = 0f;

    // -----------------------------------------------------------------------
    // Abstract
    // -----------------------------------------------------------------------

    /// <summary>
    /// Called by <see cref="Rigidbody3D.Awake"/> to add the shape and body into the simulation.
    /// Implementations must call <see cref="PhysicsSystem3D.AddBox"/>,
    /// <see cref="PhysicsSystem3D.AddSphere"/>, etc. and may store the returned
    /// <see cref="BodyHandle"/>.
    /// </summary>
    public abstract void RegisterShape(PhysicsSystem3D physics, Actor actor, float mass);

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    public override void Awake()
    {
        // Ensure there is a Rigidbody3D that will drive body creation.
        // If none exists, add one — it will call RegisterShape from its own Awake.
        if (GetComponent<Rigidbody3D>() == null)
            Actor.AddComponent<Rigidbody3D>();
    }

    public override void OnDestroy()
    {
        // Body cleanup is handled by Rigidbody3D.OnDestroy
    }
}

// ---------------------------------------------------------------------------
// BoxCollider3D
// ---------------------------------------------------------------------------

/// <summary>
/// Axis-aligned box collider.
/// </summary>
public sealed class BoxCollider3D : Collider3D
{
    /// <summary>Half-extents of the box on each axis. Default (0.5, 0.5, 0.5).</summary>
    public XnaVec3 HalfExtents { get; set; } = XnaVec3.One * 0.5f;

    public override void RegisterShape(PhysicsSystem3D physics, Actor actor, float mass)
    {
        physics.AddBox(actor, HalfExtents, mass);
    }
}

// ---------------------------------------------------------------------------
// SphereCollider3D
// ---------------------------------------------------------------------------

/// <summary>
/// Sphere collider.
/// </summary>
public sealed class SphereCollider3D : Collider3D
{
    /// <summary>Radius of the sphere. Default 0.5.</summary>
    public float Radius { get; set; } = 0.5f;

    public override void RegisterShape(PhysicsSystem3D physics, Actor actor, float mass)
    {
        physics.AddSphere(actor, Radius, mass);
    }
}

// ---------------------------------------------------------------------------
// CapsuleCollider3D
// ---------------------------------------------------------------------------

/// <summary>
/// Capsule collider — a cylinder capped with hemispheres.
/// Useful for characters.
/// </summary>
public sealed class CapsuleCollider3D : Collider3D
{
    /// <summary>Radius of the capsule hemisphere ends. Default 0.5.</summary>
    public float Radius { get; set; } = 0.5f;

    /// <summary>Length of the cylindrical shaft (not including the hemispherical caps). Default 1.</summary>
    public float Length { get; set; } = 1f;

    public override void RegisterShape(PhysicsSystem3D physics, Actor actor, float mass)
    {
        physics.AddCapsule(actor, Radius, Length, mass);
    }
}
