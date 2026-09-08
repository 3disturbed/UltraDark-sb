using BepuPhysics.Collidables;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Physics;
using Xunit;

using XnaVec3 = Microsoft.Xna.Framework.Vector3;

namespace SexyBiscuit.Tests;

/// <summary>
/// The 3D component properties the browser serialises, doing something on this engine.
/// </summary>
/// <remarks>
/// <see cref="ComponentSchemaParityTests"/> asks only whether a property of that name
/// exists, because that is all it can ask of a source file. It would therefore be satisfied
/// by a property that parses out of a scene, sits in a field and changes nothing — which is
/// worse than the gap it replaced, since the sweep would then be quiet about it. These are
/// the other half: each one drives the property from a scene's point of view and reads back
/// the thing it is supposed to move.
/// </remarks>
public class Physics3DParityTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Re-registers the body after the shape's dimensions have been set, the way
    /// <c>wiki/06-physics.md</c> documents: a collider builds its shape in <c>Awake</c>,
    /// which has already run by the time <c>AddComponent</c> returns.
    /// </summary>
    private static Rigidbody3D Rebuild(Actor actor)
    {
        var body = actor.GetComponent<Rigidbody3D>()!;
        PhysicsSystem3D.Instance.RemoveBody(actor);
        body.Awake();
        return body;
    }

    // -------------------------------------------------------------------------
    // CapsuleCollider3D.height
    // -------------------------------------------------------------------------

    /// <summary>
    /// A capsule sized by total height reaches Bepu as the shaft that height implies.
    /// </summary>
    /// <remarks>
    /// The browser stores <c>radius</c> and <c>height</c>; this engine stored the radius and
    /// the shaft, so a scene setting <c>height</c> was dropped with a warning and the capsule
    /// came out at the default 2 metres. A 1.8 m character standing in a 1.8 m doorway in the
    /// browser walked into the lintel natively.
    /// </remarks>
    [Fact]
    public void ACapsuleSizedByHeightRegistersTheShaftThatHeightImplies()
    {
        var scene = new Scene("CapsuleHeight");
        var actor = scene.AddActor(new Actor("Walker"));
        var capsule = actor.AddComponent<CapsuleCollider3D>();
        scene.FlushPendingActors();

        try
        {
            capsule.Radius = 0.35f;
            capsule.Height = 1.8f;
            Assert.Equal(1.1f, capsule.Length, 3);   // 1.8 total, less two 0.35 caps

            var body = Rebuild(actor);
            var reference = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(body.Handle);
            var shape = PhysicsSystem3D.Instance.Simulation.Shapes.GetShape<Capsule>(reference.Collidable.Shape.Index);

            Assert.Equal(0.35f, shape.Radius, 3);
            Assert.Equal(1.1f, shape.Length, 3);
        }
        finally
        {
            PhysicsSystem3D.Instance.RemoveBody(actor);
            scene.Destroy();
        }
    }

    // -------------------------------------------------------------------------
    // MeshCollider3D.padding
    // -------------------------------------------------------------------------

    /// <summary>
    /// A padded mesh collider bakes a surface that stands proud of the model by that much.
    /// </summary>
    /// <remarks>
    /// The browser fits a box to the renderer's bounds and grows it by <c>padding</c> on
    /// every side; this engine had no such property, so a scene asking for clearance got a
    /// collider flush with the art and characters scraped through it. Here a flat floor at
    /// y = 0 is padded by a quarter metre and every baked triangle should sit at y = 0.25.
    /// </remarks>
    [Fact]
    public void APaddedMeshBakesItsSurfaceProudOfTheModel()
    {
        var scene = new Scene("MeshPadding");
        var actor = scene.AddActor(new Actor("Floor"));
        var collider = actor.AddComponent<MeshCollider3D>();
        scene.FlushPendingActors();

        collider.SetMesh(
            new[]
            {
                new XnaVec3(-1f, 0f, -1f), new XnaVec3(1f, 0f, -1f),
                new XnaVec3(1f, 0f, 1f),   new XnaVec3(-1f, 0f, 1f),
            },
            new[] { 0, 2, 1, 0, 3, 2 });   // wound so the floor faces up
        collider.Padding = 0.25f;

        var statics = PhysicsSystem3D.Instance.Simulation.Statics;
        int before = statics.Count;
        collider.Start();
        var handle = statics.IndexToHandle[before];

        try
        {
            Assert.True(collider.IsBaked);

            var index = statics.GetStaticReference(handle).Shape;
            var shape = PhysicsSystem3D.Instance.Simulation.Shapes.GetShape<Mesh>(index.Index);
            for (int i = 0; i < shape.Triangles.Length; i++)
            {
                var triangle = shape.Triangles[i];
                foreach (var corner in new[] { triangle.A, triangle.B, triangle.C })
                    Assert.Equal(0.25f, corner.Y, 3);
            }
        }
        finally
        {
            PhysicsSystem3D.Instance.Simulation.Statics.Remove(handle);
            scene.Destroy();
        }
    }

    /// <summary>
    /// Padding moves a vertex along the surface it belongs to, not away from the origin, so
    /// a shape is inflated rather than scaled.
    /// </summary>
    [Fact]
    public void PaddingPushesEachVertexAlongItsOwnNormal()
    {
        // A ramp: one plane tilted about the x axis, facing up and towards -z.
        var vertices = new[]
        {
            new XnaVec3(-1f, 0f, -1f), new XnaVec3(1f, 0f, -1f),   // the foot
            new XnaVec3(-1f, 1f, 0f),  new XnaVec3(1f, 1f, 0f),    // the top
        };
        var padded = MeshCollider3D.Inflate(vertices, new[] { 0, 3, 1, 0, 2, 3 }, 0.5f);

        // Every vertex moves the full padding along the ramp's own normal, so none of them
        // moves along x — which a scale about the mesh's centre would have done.
        var offset = padded[0] - vertices[0];
        Assert.Equal(0f, offset.X, 3);
        Assert.Equal(0.5f, offset.Length(), 3);
        Assert.True(offset.Y > 0f && offset.Z < 0f, $"the ramp's face moved to {offset}");
        for (int i = 1; i < vertices.Length; i++)
        {
            Assert.Equal(offset.Y, (padded[i] - vertices[i]).Y, 3);
            Assert.Equal(offset.Z, (padded[i] - vertices[i]).Z, 3);
        }
    }

    // -------------------------------------------------------------------------
    // CapsuleCollider3D.height (continued)
    // -------------------------------------------------------------------------

    /// <summary>
    /// The default capsule is the browser's default capsule, and the shaft a scene written
    /// before <c>Height</c> existed asks for still lands.
    /// </summary>
    [Fact]
    public void ACapsulesTwoLengthsAgreeWithEachOtherAndWithTheBrowsersDefaults()
    {
        var capsule = new CapsuleCollider3D();

        Assert.Equal(0.5f, capsule.Radius, 3);
        Assert.Equal(2f, capsule.Height, 3);
        Assert.Equal(1f, capsule.Length, 3);

        capsule.Length = 1.2f;
        Assert.Equal(2.2f, capsule.Height, 3);

        // Never negative: a capsule shorter than its own caps is a sphere, not an error.
        capsule.Height = 0.1f;
        Assert.Equal(0f, capsule.Length, 3);
    }
}
