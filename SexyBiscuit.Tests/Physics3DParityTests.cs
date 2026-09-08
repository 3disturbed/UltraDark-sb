using BepuPhysics.Collidables;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Physics;
using Xunit;

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
