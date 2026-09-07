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

            // …and a fresh Rigidbody2D is Dynamic, so until this line "static"
            // was only the comment's opinion: a bare collider fell. The browser
            // engine never integrates a collider that has no body of its own, so
            // the same scene stayed put there and drifted here — walls, cars and
            // crates sliding off the bottom of a native build while the player
            // and the enemies, whose scripts set a velocity every frame, looked
            // fine. GravityScale is no help: Aether has no per-body gravity, and
            // the property is stored for game logic only.
            rb.IsKinematic  = true;
            rb.GravityScale = 0f;
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
        DetachFixture(ref _fixture);
    }

    /// <summary>
    /// Detaches a fixture from its body, tolerating the body already being gone.
    /// </summary>
    /// <remarks>
    /// Components are destroyed in the order they were added, and a scene file is free to
    /// list Rigidbody2D before its colliders. When it does, the rigidbody's OnDestroy has
    /// already called World.Remove on the body by the time the collider runs, and Aether
    /// throws from inside Body.Remove on a body it no longer owns. There is nothing to
    /// clean up in that case — the whole body is being discarded — so swallowing it is
    /// correct rather than merely convenient.
    /// </remarks>
    internal static void DetachFixture(ref Fixture? fixture)
    {
        var target = fixture;
        fixture = null;

        if (target?.Body == null) return;

        try
        {
            target.Body.Remove(target);
        }
        catch (Exception)
        {
            // The body was torn down first. Nothing left to detach from.
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

// ---------------------------------------------------------------------------
// EdgeCollider2D
// ---------------------------------------------------------------------------

/// <summary>
/// An open polyline collider — terrain outlines, one-way platforms, level boundaries.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="PolygonCollider2D"/> this has no interior and no convexity or vertex
/// limit, so a whole hillside can be one collider. Nothing can be "inside" it; a fast body
/// can pass through if it moves further than the line in one step, which is what
/// continuous collision on the <see cref="Rigidbody2D"/> is for.
/// </para>
/// <para>
/// Aether's chain shape handles the ghost-vertex problem for you: without it, a body
/// sliding across the join between two segments catches on the internal corner.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var ground = actor.AddComponent&lt;EdgeCollider2D&gt;();
/// ground.Points = terrainOutline;      // local space, in order
/// ground.Loop   = false;               // true closes the polyline into a ring
/// </code>
/// </example>
public sealed class EdgeCollider2D : Collider2D
{
    /// <summary>
    /// Points along the line, in local space and in order. At least two are required.
    /// The default is a flat ten-unit segment.
    /// </summary>
    public XnaVec2[] Points { get; set; } = new[]
    {
        new XnaVec2(-5f, 0f),
        new XnaVec2( 5f, 0f),
    };

    /// <summary>Closes the polyline into a ring, joining the last point back to the first.</summary>
    public bool Loop { get; set; }

    public override Fixture CreateFixture(Body body)
    {
        if (Points.Length < 2)
            throw new InvalidOperationException(
                $"EdgeCollider2D on '{Actor.Name}' needs at least two points.");

        var verts = new Vertices(Points.Length);
        foreach (var p in Points) verts.Add(new AetherVec2(p.X, p.Y));

        var shape = new ChainShape(verts, Loop);
        return body.CreateFixture(shape);
    }
}

// ---------------------------------------------------------------------------
// CompositeCollider2D
// ---------------------------------------------------------------------------

/// <summary>One shape within a <see cref="CompositeCollider2D"/>.</summary>
public sealed class CompositeShape2D
{
    /// <summary>Offset from the actor's origin, in local space.</summary>
    public XnaVec2 Offset { get; set; }

    /// <summary>Rotation in radians, applied to a box shape.</summary>
    public float Rotation { get; set; }

    /// <summary>Full extents when this is a box. Ignored when <see cref="Radius"/> is set.</summary>
    public XnaVec2 Size { get; set; } = XnaVec2.One;

    /// <summary>Radius when this is a circle. Zero or less means the shape is a box.</summary>
    public float Radius { get; set; }

    /// <summary>True when this shape is a circle rather than a box.</summary>
    public bool IsCircle => Radius > 0f;

    /// <summary>Creates a box part.</summary>
    public static CompositeShape2D Box(XnaVec2 size, XnaVec2 offset = default, float rotation = 0f)
        => new() { Size = size, Offset = offset, Rotation = rotation };

    /// <summary>Creates a circle part.</summary>
    public static CompositeShape2D Circle(float radius, XnaVec2 offset = default)
        => new() { Radius = radius, Offset = offset };
}

/// <summary>
/// Several shapes attached to one body, so a concave outline behaves as a single object.
/// </summary>
/// <remarks>
/// <para>
/// Aether polygons must be convex and are capped at eight vertices. An L-shaped platform
/// or a spaceship silhouette therefore cannot be one polygon — this decomposes it into
/// parts that share a body, so it moves and collides as one rigid thing rather than as
/// several actors held together.
/// </para>
/// <para>
/// This is a component rather than a <see cref="Collider2D"/> subclass because the base
/// class produces exactly one fixture. Adding it creates a <see cref="Rigidbody2D"/> if
/// the actor has none, the same as any other collider.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var hull = actor.AddComponent&lt;CompositeCollider2D&gt;();
/// hull.Shapes.Add(CompositeShape2D.Box(new Vector2(3f, 1f)));
/// hull.Shapes.Add(CompositeShape2D.Box(new Vector2(1f, 3f), new Vector2(-1f, 1f)));
/// hull.Shapes.Add(CompositeShape2D.Circle(0.6f, new Vector2(1.5f, 0f)));
/// hull.Rebuild();
/// </code>
/// </example>
public sealed class CompositeCollider2D : Component
{
    /// <summary>The shapes making up this collider.</summary>
    public List<CompositeShape2D> Shapes { get; } = new();

    /// <summary>Surface material applied to every generated fixture.</summary>
    public PhysicsMaterial2D? Material { get; set; }

    /// <summary>Generated shapes act as triggers rather than solid geometry.</summary>
    public bool IsTrigger { get; set; }

    /// <summary>Fixtures produced by the most recent build.</summary>
    public IReadOnlyList<Fixture> Fixtures => _fixtures;

    private readonly List<Fixture> _fixtures = new();

    public override void Awake()
    {
        if (GetComponent<Rigidbody2D>() == null)
            Actor.AddComponent<Rigidbody2D>();
    }

    public override void Start() => Rebuild();

    /// <summary>
    /// Discards the existing fixtures and rebuilds them from <see cref="Shapes"/>.
    /// Safe to call at runtime after changing the shape list.
    /// </summary>
    public void Rebuild()
    {
        Clear();

        var body = GetComponent<Rigidbody2D>()?.Body;
        if (body == null) return;

        float density = Material?.Density ?? 1f;

        foreach (var part in Shapes)
        {
            Shape shape = part.IsCircle
                ? new CircleShape(part.Radius, density)
                {
                    Position = new AetherVec2(part.Offset.X, part.Offset.Y),
                }
                : new PolygonShape(
                    PolygonTools.CreateRectangle(
                        part.Size.X * 0.5f, part.Size.Y * 0.5f,
                        new AetherVec2(part.Offset.X, part.Offset.Y),
                        part.Rotation),
                    density);

            var fixture = body.CreateFixture(shape);

            if (Material != null)
            {
                fixture.Friction    = Material.Friction;
                fixture.Restitution = Material.Restitution;
            }

            fixture.IsSensor = IsTrigger;
            _fixtures.Add(fixture);
        }
    }

    /// <summary>Removes every generated fixture from the body.</summary>
    public void Clear()
    {
        for (int i = 0; i < _fixtures.Count; i++)
        {
            Fixture? fixture = _fixtures[i];
            Collider2D.DetachFixture(ref fixture);
        }

        _fixtures.Clear();
    }

    public override void OnDestroy() => Clear();
}
