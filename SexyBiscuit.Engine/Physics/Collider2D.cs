using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using nkast.Aether.Physics2D.Collision.Shapes;
using nkast.Aether.Physics2D.Common;
using nkast.Aether.Physics2D.Dynamics;
using nkast.Aether.Physics2D;

using XnaVec2 = Microsoft.Xna.Framework.Vector2;
using AetherVec2 = nkast.Aether.Physics2D.Common.Vector2;

namespace SexyBiscuit.Engine.Physics;

// ---------------------------------------------------------------------------
// PhysicsMaterial2D
// ---------------------------------------------------------------------------

/// <summary>
/// Describes the surface properties (friction, restitution, density) of a 2D collider.
/// </summary>
public sealed class PhysicsMaterial2D
{
    /// <summary>Coefficient of friction (0 = frictionless, 1 = very rough). Default 0.3.</summary>
    public float Friction    { get; set; } = 0.3f;

    /// <summary>Coefficient of restitution / bounciness (0 = no bounce, 1 = perfect bounce). Default 0.</summary>
    public float Restitution { get; set; } = 0f;

    /// <summary>Mass per unit area used by Aether to auto-compute body mass. Default 1.</summary>
    public float Density     { get; set; } = 1f;
}

// ---------------------------------------------------------------------------
// Collider2D (abstract base)
// ---------------------------------------------------------------------------

/// <summary>
/// Abstract base for all 2D collider components.
/// Subclasses implement <see cref="CreateFixture"/> to supply the Aether shape.
/// The component automatically finds or creates a <see cref="Rigidbody2D"/> and
/// attaches the fixture to its body on <see cref="Awake"/>.
/// </summary>
public abstract class Collider2D : Component
{
    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    /// <summary>
    /// When true this shape acts as a trigger (sensor): it generates enter/stay/exit
    /// events but does not produce collision response.
    /// </summary>
    public bool IsTrigger { get; set; } = false;

    /// <summary>
    /// Surface material. If null, default Aether values are used.
    /// </summary>
    public PhysicsMaterial2D? Material { get; set; }

    // -----------------------------------------------------------------------
    // Internal
    // -----------------------------------------------------------------------
    private Fixture? _fixture;

    /// <summary>The Aether fixture created by this collider (available after Awake).</summary>
    public Fixture? Fixture => _fixture;

    // -----------------------------------------------------------------------
    // Abstract
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates and returns the Aether <see cref="Fixture"/> attached to <paramref name="body"/>.
    /// Subclasses must implement this to define their shape.
    /// </summary>
    public abstract Fixture CreateFixture(Body body);

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    public override void Awake()
    {
        // Ensure the actor has a Rigidbody2D; create one (static) if absent.
        var rb = GetComponent<Rigidbody2D>();
        if (rb == null)
        {
            // AddComponent calls Awake on the new component, which calls CreateBody.
            rb = Actor.AddComponent<Rigidbody2D>();
        }

        var body = rb.Body;
        if (body == null) return;

        _fixture = CreateFixture(body);

        // Apply material
        if (Material != null)
        {
            _fixture.Friction    = Material.Friction;
            _fixture.Restitution = Material.Restitution;
        }

        _fixture.IsSensor = IsTrigger;
    }

    public override void OnDestroy()
    {
        if (_fixture != null)
        {
            _fixture.Body?.Remove(_fixture);
            _fixture = null;
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    protected static float GetDensity(PhysicsMaterial2D? mat) => mat?.Density ?? 1f;
}

// ---------------------------------------------------------------------------
// BoxCollider2D
// ---------------------------------------------------------------------------

/// <summary>
/// Axis-aligned rectangle collider.
/// </summary>
public sealed class BoxCollider2D : Collider2D
{
    /// <summary>Full width and height of the box. Default (1, 1).</summary>
    public XnaVec2 Size   { get; set; } = XnaVec2.One;

    /// <summary>Local-space offset of the box centre relative to the actor's pivot. Default (0, 0).</summary>
    public XnaVec2 Offset { get; set; } = XnaVec2.Zero;

    public override Fixture CreateFixture(Body body)
    {
        float hx = Size.X * 0.5f;
        float hy = Size.Y * 0.5f;

        var vertices = PolygonTools.CreateRectangle(hx, hy,
            new AetherVec2(Offset.X, Offset.Y),
            0f);
        var shape = new PolygonShape(vertices, GetDensity(Material));

        return body.CreateFixture(shape);
    }
}

// ---------------------------------------------------------------------------
// CircleCollider2D
// ---------------------------------------------------------------------------

/// <summary>
/// Circle collider.
/// </summary>
public sealed class CircleCollider2D : Collider2D
{
    /// <summary>Radius of the circle. Default 0.5.</summary>
    public float   Radius { get; set; } = 0.5f;

    /// <summary>Local-space offset of the circle centre. Default (0, 0).</summary>
    public XnaVec2 Offset { get; set; } = XnaVec2.Zero;

    public override Fixture CreateFixture(Body body)
    {
        var shape = new CircleShape(Radius, GetDensity(Material))
        {
            Position = new AetherVec2(Offset.X, Offset.Y)
        };

        return body.CreateFixture(shape);
    }
}

// ---------------------------------------------------------------------------
// PolygonCollider2D
// ---------------------------------------------------------------------------

/// <summary>
/// Convex polygon collider. Supports up to 8 vertices (Aether limit).
/// The vertices must form a convex hull in counter-clockwise order.
/// </summary>
public sealed class PolygonCollider2D : Collider2D
{
    /// <summary>
    /// Vertices in local space. Must be convex and wound counter-clockwise.
    /// Maximum 8 vertices (Aether limit). Default is a unit square.
    /// </summary>
    public XnaVec2[] Points { get; set; } = new[]
    {
        new XnaVec2(-0.5f, -0.5f),
        new XnaVec2( 0.5f, -0.5f),
        new XnaVec2( 0.5f,  0.5f),
        new XnaVec2(-0.5f,  0.5f)
    };

    public override Fixture CreateFixture(Body body)
    {
        int count = Math.Min(Points.Length, Settings.MaxPolygonVertices);

        var verts = new Vertices(count);
        for (int i = 0; i < count; i++)
            verts.Add(new AetherVec2(Points[i].X, Points[i].Y));

        var shape = new PolygonShape(verts, GetDensity(Material));
        return body.CreateFixture(shape);
    }
}
