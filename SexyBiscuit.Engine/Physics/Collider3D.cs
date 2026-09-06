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

// ---------------------------------------------------------------------------
// CylinderCollider3D
// ---------------------------------------------------------------------------

/// <summary>
/// Cylinder collider, aligned with the actor's local Y axis.
/// </summary>
/// <remarks>
/// Bepu solves cylinder contacts with a dedicated, more expensive path than boxes or
/// capsules. Use a capsule for characters and a box for crates; reach for a cylinder when
/// the flat circular face matters — wheels, barrels, coins.
/// </remarks>
public sealed class CylinderCollider3D : Collider3D
{
    /// <summary>Radius of the circular cross-section. Default 0.5.</summary>
    public float Radius { get; set; } = 0.5f;

    /// <summary>Height along the local Y axis. Default 1.</summary>
    public float Length { get; set; } = 1f;

    public override void RegisterShape(PhysicsSystem3D physics, Actor actor, float mass)
        => physics.AddCylinder(actor, Radius, Length, mass);
}

// ---------------------------------------------------------------------------
// MeshCollider3D
// ---------------------------------------------------------------------------

/// <summary>
/// Static collision from an arbitrary triangle mesh. Concave shapes are fine.
/// </summary>
/// <remarks>
/// <para>
/// Static only, which is a Bepu constraint rather than an engine one: a general concave
/// inertia tensor and contact manifold are not tractable at simulation speed. For a
/// concave object that has to move, use <see cref="CompoundCollider3D"/> and build it out
/// of convex parts.
/// </para>
/// <para>
/// Because the body is static there is no <see cref="Rigidbody3D"/> and nothing to move
/// it — the geometry is baked at its transform when <see cref="Component.Start"/> runs.
/// Change the transform afterwards and the collision stays where it was.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var terrain = actor.AddComponent&lt;MeshCollider3D&gt;();
/// terrain.SetMesh(heightfieldVertices, heightfieldIndices);
/// </code>
/// </example>
public sealed class MeshCollider3D : Collider3D
{
    /// <summary>Mesh vertices in local space.</summary>
    public XnaVec3[] Vertices { get; private set; } = Array.Empty<XnaVec3>();

    /// <summary>Triangle indices, three per triangle.</summary>
    public int[] Indices { get; private set; } = Array.Empty<int>();

    /// <summary>True once the geometry has been handed to the simulation.</summary>
    public bool IsBaked { get; private set; }

    /// <summary>Replaces the collision geometry. Call before Start; it is baked once.</summary>
    public void SetMesh(XnaVec3[] vertices, int[] indices)
    {
        if (indices.Length % 3 != 0)
            throw new ArgumentException("Index count must be a multiple of three.", nameof(indices));

        Vertices = vertices;
        Indices  = indices;
    }

    /// <summary>
    /// Builds collision from a loaded <see cref="Rendering.MeshRenderer"/>'s source model.
    /// The renderer must have called LoadModel first.
    /// </summary>
    public bool SetMeshFromModel(string modelPath)
    {
#if ANDROID
        // Android has no AssimpNet:
        // the package ships native libassimp for desktop only. Saying so at the call
        // site beats a native load failing deep inside a frame.
        System.Console.Error.WriteLine($"[Collider3D] 3D model import is not available on Android: '{modelPath}'.");
        return false;
#else
        try
        {
            using var ctx = new Assimp.AssimpContext();
            var scene = ctx.ImportFile(modelPath, Assimp.PostProcessSteps.Triangulate);

            var verts = new List<XnaVec3>();
            var idx   = new List<int>();

            foreach (var mesh in scene.Meshes)
            {
                int baseIndex = verts.Count;
                foreach (var v in mesh.Vertices) verts.Add(new XnaVec3(v.X, v.Y, v.Z));
                foreach (var face in mesh.Faces)
                    foreach (var i in face.Indices) idx.Add(baseIndex + i);
            }

            SetMesh(verts.ToArray(), idx.ToArray());
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MeshCollider3D] Could not load '{modelPath}': {ex.Message}");
            return false;
        }
#endif
    }

    // A static mesh needs no Rigidbody3D, so this collider opts out of the base
    // class's "create one if missing" behaviour and registers itself instead.
    public override void Awake() { }

    public override void Start()
    {
        if (IsBaked || Vertices.Length == 0 || Indices.Length == 0) return;

        var t3d = Actor.GetComponent<Transform3D>();
        PhysicsSystem3D.Instance.AddStaticMesh(
            Vertices, Indices,
            t3d?.Position ?? XnaVec3.Zero,
            t3d?.Rotation,
            t3d?.Scale);

        IsBaked = true;
    }

    public override void RegisterShape(PhysicsSystem3D physics, Actor actor, float mass)
    {
        // Never called: this collider is static and does not go through Rigidbody3D.
    }
}

// ---------------------------------------------------------------------------
// CompoundCollider3D
// ---------------------------------------------------------------------------

/// <summary>Primitive types a compound child can take.</summary>
public enum CompoundChildKind
{
    Box,
    Sphere,
    Capsule,
    Cylinder,
}

/// <summary>One convex piece of a <see cref="CompoundCollider3D"/>.</summary>
public sealed class CompoundChildShape
{
    /// <summary>Which primitive this child is.</summary>
    public CompoundChildKind Kind { get; set; } = CompoundChildKind.Box;

    /// <summary>Offset from the compound's origin, in local space.</summary>
    public XnaVec3 LocalPosition { get; set; }

    /// <summary>Orientation within the compound.</summary>
    public Microsoft.Xna.Framework.Quaternion LocalRotation { get; set; }
        = Microsoft.Xna.Framework.Quaternion.Identity;

    /// <summary>Full extents, for <see cref="CompoundChildKind.Box"/>.</summary>
    public XnaVec3 Size { get; set; } = XnaVec3.One;

    /// <summary>Radius, for sphere, capsule and cylinder children.</summary>
    public float Radius { get; set; } = 0.5f;

    /// <summary>Length, for capsule and cylinder children.</summary>
    public float Length { get; set; } = 1f;

    /// <summary>This piece's share of the body's mass.</summary>
    public float Mass { get; set; } = 1f;

    /// <summary>Creates a box child.</summary>
    public static CompoundChildShape Box(XnaVec3 size, XnaVec3 offset, float mass = 1f)
        => new() { Kind = CompoundChildKind.Box, Size = size, LocalPosition = offset, Mass = mass };

    /// <summary>Creates a sphere child.</summary>
    public static CompoundChildShape Sphere(float radius, XnaVec3 offset, float mass = 1f)
        => new() { Kind = CompoundChildKind.Sphere, Radius = radius, LocalPosition = offset, Mass = mass };

    /// <summary>Creates a capsule child.</summary>
    public static CompoundChildShape Capsule(float radius, float length, XnaVec3 offset, float mass = 1f)
        => new() { Kind = CompoundChildKind.Capsule, Radius = radius, Length = length, LocalPosition = offset, Mass = mass };

    /// <summary>Creates a cylinder child.</summary>
    public static CompoundChildShape Cylinder(float radius, float length, XnaVec3 offset, float mass = 1f)
        => new() { Kind = CompoundChildKind.Cylinder, Radius = radius, Length = length, LocalPosition = offset, Mass = mass };
}

/// <summary>
/// Several convex shapes welded into one rigid body — the way to build a concave
/// dynamic object.
/// </summary>
/// <remarks>
/// Bepu has no concave dynamic primitive, so a table, an L-shaped block or a vehicle
/// chassis is expressed as convex parts. Their inertias are combined about the compound's
/// centre of mass, so a lopsided arrangement tips the way you would expect.
/// </remarks>
/// <example>
/// <code>
/// var table = actor.AddComponent&lt;CompoundCollider3D&gt;();
/// table.Children.Add(CompoundChildShape.Box(new Vector3(2f, 0.1f, 1f), new Vector3(0, 1f, 0), mass: 8f));
/// table.Children.Add(CompoundChildShape.Box(new Vector3(0.1f, 1f, 0.1f), new Vector3(-0.9f, 0.5f, -0.4f), mass: 1f));
/// // ...three more legs
/// </code>
/// </example>
public sealed class CompoundCollider3D : Collider3D
{
    /// <summary>The convex pieces making up this body. Must have at least one.</summary>
    public List<CompoundChildShape> Children { get; } = new();

    public override void RegisterShape(PhysicsSystem3D physics, Actor actor, float mass)
    {
        if (Children.Count == 0)
        {
            Console.Error.WriteLine(
                $"[CompoundCollider3D] '{actor.Name}' has no child shapes; no body was created.");
            return;
        }

        physics.AddCompound(actor, Children);
    }
}
