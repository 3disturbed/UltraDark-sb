using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using nkast.Aether.Physics2D.Dynamics;
using nkast.Aether.Physics2D.Dynamics.Contacts;
using nkast.Aether.Physics2D.Collision;
using nkast.Aether.Physics2D.Collision.Shapes;
using nkast.Aether.Physics2D.Common;

using XnaVec2 = Microsoft.Xna.Framework.Vector2;
using AetherVec2 = nkast.Aether.Physics2D.Common.Vector2;

namespace SexyBiscuit.Engine.Physics;

// ---------------------------------------------------------------------------
// Conversion helpers
// ---------------------------------------------------------------------------

/// <summary>
/// Static helpers to convert between MonoGame/XNA Vector2 and Aether Vector2.
/// </summary>
public static class PhysicsConvert
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static XnaVec2 ToXna(AetherVec2 v) => new XnaVec2(v.X, v.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AetherVec2 ToAether(XnaVec2 v) => new AetherVec2(v.X, v.Y);
}

// ---------------------------------------------------------------------------
// RaycastHit2D
// ---------------------------------------------------------------------------

/// <summary>
/// Result of a 2D physics query (raycast, circle-cast, box-cast).
/// </summary>
public struct RaycastHit2D
{
    public Actor?   Actor    { get; init; }
    public XnaVec2  Point    { get; init; }
    public XnaVec2  Normal   { get; init; }
    public float    Distance { get; init; }
}

// ---------------------------------------------------------------------------
// PhysicsSystem2D
// ---------------------------------------------------------------------------

/// <summary>
/// Singleton manager for the Aether Physics2D world.
/// Call <see cref="FixedStep"/> once per fixed-update tick from your game loop.
/// </summary>
public sealed class PhysicsSystem2D
{
    // -----------------------------------------------------------------------
    // Singleton
    // -----------------------------------------------------------------------
    public static PhysicsSystem2D Instance { get; } = new PhysicsSystem2D();

    // -----------------------------------------------------------------------
    // World
    // -----------------------------------------------------------------------

    /// <summary>The underlying Aether simulation world.</summary>
    public World World { get; }

    /// <summary>
    /// Gets or sets the world gravity. Default is (0, 9.8) — downward in screen-space Y.
    /// </summary>
    public XnaVec2 Gravity
    {
        get => PhysicsConvert.ToXna(World.Gravity);
        set => World.Gravity = PhysicsConvert.ToAether(value);
    }

    // -----------------------------------------------------------------------
    // Body ↔ Actor mapping
    // -----------------------------------------------------------------------
    private readonly Dictionary<Actor, Body>  _actorToBody = new();
    private readonly Dictionary<Body,  Actor> _bodyToActor = new();

    // -----------------------------------------------------------------------
    // Collision event tracking  (for Enter/Stay/Exit semantics)
    // -----------------------------------------------------------------------
    // Key: ordered pair of actor IDs (smaller first); Value: contact count
    private readonly Dictionary<(uint, uint), int> _activeContacts      = new();
    private readonly Dictionary<(uint, uint), int> _activeContactsSensor = new();

    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------
    private PhysicsSystem2D()
    {
        World = new World(new AetherVec2(0f, 9.8f));

        // Subscribe to contact lifecycle events
        World.ContactManager.BeginContact += OnBeginContact;
        World.ContactManager.EndContact   += OnEndContact;
        World.ContactManager.PreSolve     += OnPreSolve;
    }

    // -----------------------------------------------------------------------
    // Simulation step
    // -----------------------------------------------------------------------

    /// <summary>
    /// Advances the physics simulation by <paramref name="dt"/> seconds,
    /// then writes every simulated body's position and rotation back to its
    /// owner <see cref="Actor"/>'s <see cref="Transform"/>.
    /// </summary>
    public void FixedStep(float dt)
    {
        // Aether recommends 8 velocity and 3 position iterations
        World.Step(dt);

        // Sync poses back to Actors
        foreach (var (actor, body) in _actorToBody)
        {
            if (!actor.IsActive) continue;
            actor.Transform.Position = PhysicsConvert.ToXna(body.Position);
            actor.Transform.Rotation = body.Rotation;
        }
    }

    // -----------------------------------------------------------------------
    // Body management
    // -----------------------------------------------------------------------

    /// <summary>
    /// Stores an existing <see cref="Body"/> → <see cref="Actor"/> mapping.
    /// </summary>
    public void RegisterBody(Actor actor, Body body)
    {
        _actorToBody[actor] = body;
        _bodyToActor[body]  = actor;
    }

    /// <summary>
    /// Removes the mapping for the given actor and destroys the body from the world.
    /// </summary>
    public void UnregisterBody(Actor actor)
    {
        if (!_actorToBody.TryGetValue(actor, out var body)) return;
        _actorToBody.Remove(actor);
        _bodyToActor.Remove(body);
        World.Remove(body);
    }

    /// <summary>
    /// Creates a new <see cref="Body"/> in the world at the actor's current world position,
    /// registers the mapping, and returns the body.
    /// </summary>
    public Body CreateBody(Actor actor, BodyType type)
    {
        var pos  = PhysicsConvert.ToAether(actor.Transform.Position);
        var body = World.CreateBody(pos, actor.Transform.Rotation, type);
        RegisterBody(actor, body);
        return body;
    }

    /// <summary>
    /// Returns the <see cref="Body"/> associated with <paramref name="actor"/>, or null.
    /// </summary>
    public Body? GetBody(Actor actor)
        => _actorToBody.TryGetValue(actor, out var b) ? b : null;

    // -----------------------------------------------------------------------
    // Queries — Raycast
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fires a ray from <paramref name="origin"/> in <paramref name="direction"/> for
    /// up to <paramref name="distance"/> metres.  Returns true if something was hit.
    /// </summary>
    /// <param name="layerMask">Actor.Layer bitmask filter; -1 = hit everything.</param>
    public bool Raycast(
        XnaVec2 origin,
        XnaVec2 direction,
        float   distance,
        out RaycastHit2D hit,
        int layerMask = -1)
    {
        direction.Normalize();
        var aOrigin = PhysicsConvert.ToAether(origin);
        var aEnd    = PhysicsConvert.ToAether(origin + direction * distance);

        RaycastHit2D best   = default;
        float        bestFrac = float.MaxValue;
        bool         found   = false;

        World.RayCast((fixture, point, normal, fraction) =>
        {
            var body = fixture.Body;
            if (!_bodyToActor.TryGetValue(body, out var actor)) return -1f;
            if (layerMask != -1 && (layerMask & (1 << actor.Layer)) == 0) return -1f;

            if (fraction < bestFrac)
            {
                bestFrac = fraction;
                best = new RaycastHit2D
                {
                    Actor    = actor,
                    Point    = PhysicsConvert.ToXna(point),
                    Normal   = PhysicsConvert.ToXna(normal),
                    Distance = fraction * distance
                };
                found = true;
            }

            return fraction; // continue, looking for closest
        }, aOrigin, aEnd);

        hit = best;
        return found;
    }

    // -----------------------------------------------------------------------
    // Queries — CircleCast
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns all actors whose physics bodies overlap a circle at <paramref name="origin"/>.
    /// </summary>
    public bool CircleCast(
        XnaVec2  origin,
        float    radius,
        int      layerMask,
        out Actor[] results)
    {
        var aOrigin = PhysicsConvert.ToAether(origin);
        var found   = new List<Actor>();

        var aabb = new AABB(aOrigin, radius, radius);
        World.QueryAABB(fixture =>
        {
            var body = fixture.Body;
            if (!_bodyToActor.TryGetValue(body, out var actor)) return true;
            if (layerMask != -1 && (layerMask & (1 << actor.Layer)) == 0) return true;

            // Refine: circle vs AABB of the fixture
            fixture.GetAABB(out var fAABB, 0);
            var center = fAABB.Center;
            float dx = Math.Abs(center.X - aOrigin.X);
            float dy = Math.Abs(center.Y - aOrigin.Y);
            float hw  = (fAABB.UpperBound.X - fAABB.LowerBound.X) * 0.5f;
            float hh  = (fAABB.UpperBound.Y - fAABB.LowerBound.Y) * 0.5f;
            float cx  = Math.Max(0f, dx - hw);
            float cy  = Math.Max(0f, dy - hh);

            if (cx * cx + cy * cy <= radius * radius)
                if (!found.Contains(actor))
                    found.Add(actor);

            return true;
        }, ref aabb);

        results = found.ToArray();
        return found.Count > 0;
    }

    // -----------------------------------------------------------------------
    // Queries — BoxCast
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns all actors whose physics bodies overlap an AABB.
    /// Note: rotation is not applied to the query AABB — use polygon queries for OBB.
    /// </summary>
    public bool BoxCast(
        XnaVec2  center,
        XnaVec2  halfExtents,
        float    angle,
        int      layerMask,
        out Actor[] results)
    {
        var aCenter = PhysicsConvert.ToAether(center);
        var aabb    = new AABB(
            new AetherVec2(aCenter.X - halfExtents.X, aCenter.Y - halfExtents.Y),
            new AetherVec2(aCenter.X + halfExtents.X, aCenter.Y + halfExtents.Y));

        var found = new List<Actor>();

        World.QueryAABB(fixture =>
        {
            var body = fixture.Body;
            if (!_bodyToActor.TryGetValue(body, out var actor)) return true;
            if (layerMask != -1 && (layerMask & (1 << actor.Layer)) == 0) return true;
            if (!found.Contains(actor)) found.Add(actor);
            return true;
        }, ref aabb);

        results = found.ToArray();
        return found.Count > 0;
    }

    // -----------------------------------------------------------------------
    // Internal: contact event handling
    // -----------------------------------------------------------------------

    private (uint, uint) MakeKey(uint a, uint b)
        => a <= b ? (a, b) : (b, a);

    private bool IsSensorContact(Contact contact)
    {
        // A contact is "sensor" if either participating fixture is a sensor
        return (contact.FixtureA?.IsSensor ?? false) || (contact.FixtureB?.IsSensor ?? false);
    }

    private bool TryGetActors(Contact contact, out Actor actorA, out Actor actorB)
    {
        actorA = null!;
        actorB = null!;
        if (contact.FixtureA?.Body == null || contact.FixtureB?.Body == null) return false;
        if (!_bodyToActor.TryGetValue(contact.FixtureA.Body, out actorA!)) return false;
        if (!_bodyToActor.TryGetValue(contact.FixtureB.Body, out actorB!)) return false;
        return true;
    }

    private bool OnBeginContact(Contact contact)
    {
        if (!TryGetActors(contact, out var a, out var b)) return true;

        bool sensor = IsSensorContact(contact);
        var  key    = MakeKey(a.Id, b.Id);

        if (sensor)
        {
            if (!_activeContactsSensor.TryGetValue(key, out int count) || count == 0)
            {
                _activeContactsSensor[key] = 1;
                a.OnTriggerEnter(b);
                b.OnTriggerEnter(a);
            }
            else
            {
                _activeContactsSensor[key] = count + 1;
            }
        }
        else
        {
            if (!_activeContacts.TryGetValue(key, out int count) || count == 0)
            {
                _activeContacts[key] = 1;
                var data = BuildCollisionData(contact, a, b);
                a.OnCollisionEnter(data);
                b.OnCollisionEnter(new CollisionData
                {
                    Other           = a,
                    ContactPoint    = data.ContactPoint,
                    Normal          = -data.Normal,
                    RelativeVelocity = data.RelativeVelocity
                });
            }
            else
            {
                _activeContacts[key] = count + 1;
            }
        }

        return true;
    }

    private void OnEndContact(Contact contact)
    {
        if (!TryGetActors(contact, out var a, out var b)) return;

        bool sensor = IsSensorContact(contact);
        var  key    = MakeKey(a.Id, b.Id);

        if (sensor)
        {
            if (_activeContactsSensor.TryGetValue(key, out int count))
            {
                int next = count - 1;
                _activeContactsSensor[key] = next;
                if (next <= 0)
                {
                    _activeContactsSensor.Remove(key);
                    a.OnTriggerExit(b);
                    b.OnTriggerExit(a);
                }
            }
        }
        else
        {
            if (_activeContacts.TryGetValue(key, out int count))
            {
                int next = count - 1;
                _activeContacts[key] = next;
                if (next <= 0)
                {
                    _activeContacts.Remove(key);
                    var data = BuildCollisionData(contact, a, b);
                    a.OnCollisionExit(data);
                    b.OnCollisionExit(new CollisionData
                    {
                        Other            = a,
                        ContactPoint     = data.ContactPoint,
                        Normal           = -data.Normal,
                        RelativeVelocity = data.RelativeVelocity
                    });
                }
            }
        }
    }

    private void OnPreSolve(Contact contact, ref Manifold oldManifold)
    {
        // Fire OnCollisionStay / OnTriggerStay for contacts that are persisting
        if (!TryGetActors(contact, out var a, out var b)) return;

        bool sensor = IsSensorContact(contact);
        var  key    = MakeKey(a.Id, b.Id);

        if (sensor)
        {
            if (_activeContactsSensor.ContainsKey(key))
            {
                a.OnTriggerStay(b);
                b.OnTriggerStay(a);
            }
        }
        else
        {
            if (_activeContacts.ContainsKey(key))
            {
                var data = BuildCollisionData(contact, a, b);
                a.OnCollisionStay(data);
                b.OnCollisionStay(new CollisionData
                {
                    Other            = a,
                    ContactPoint     = data.ContactPoint,
                    Normal           = -data.Normal,
                    RelativeVelocity = data.RelativeVelocity
                });
            }
        }
    }

    private static CollisionData BuildCollisionData(Contact contact, Actor a, Actor b)
    {
        contact.GetWorldManifold(out var worldNormal, out var points);

        // FixedArray2 always has 2 elements; grab the first contact point.
        XnaVec2 contactPoint = PhysicsConvert.ToXna(points[0]);

        // Relative velocity (scalar) — difference of linear velocities projected onto normal
        var  vA  = a.GetComponent<Rigidbody2D>()?.LinearVelocity ?? XnaVec2.Zero;
        var  vB  = b.GetComponent<Rigidbody2D>()?.LinearVelocity ?? XnaVec2.Zero;
        var  rel = vA - vB;
        var  nrm = PhysicsConvert.ToXna(worldNormal);
        float rv = XnaVec2.Dot(rel, nrm);

        return new CollisionData
        {
            Other            = b,
            ContactPoint     = contactPoint,
            Normal           = nrm,
            RelativeVelocity = rv
        };
    }
}
