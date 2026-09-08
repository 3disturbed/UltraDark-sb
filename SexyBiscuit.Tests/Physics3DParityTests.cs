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
    // CharacterController3D.radius, .height, .gravity and .airControl
    // -------------------------------------------------------------------------

    /// <summary>
    /// The controller's capsule and its fall are the browser's, down to the defaults.
    /// </summary>
    /// <remarks>
    /// None of these four existed here. The dimensions were read off a CapsuleCollider3D or
    /// left at a hard-coded 0.5 × 2, gravity was a private <c>const 9.81f</c>, and there was
    /// no air control at all — so a 1.8 m character tuned in the browser arrived native as a
    /// 2 m one falling at half the speed, and steered in mid-air like it was on the ground.
    /// </remarks>
    [Fact]
    public void AControllersCapsuleAndFallStartWhereTheBrowsersDo()
    {
        var controller = new CharacterController3D();

        Assert.Equal(0.35f, controller.Radius, 3);
        Assert.Equal(1.8f, controller.Height, 3);
        Assert.Equal(-20f, controller.Gravity, 3);
        Assert.Equal(0.4f, controller.AirControl, 3);
        Assert.Equal(0.9f, controller.FootOffset, 3);

        // A squat character is a ball: the soles cannot rise above the widest point.
        controller.Height = 0.5f;
        controller.Radius = 0.4f;
        Assert.Equal(0.4f, controller.FootOffset, 3);
    }

    /// <summary>
    /// A capsule collider on the same actor still wins, because it is the shape the
    /// simulation actually sees.
    /// </summary>
    [Fact]
    public void ACapsuleColliderOnTheSameActorSizesTheController()
    {
        var scene = new Scene("ControllerCapsule");
        var actor = scene.AddActor(new Actor("Walker"));

        var capsule = actor.AddComponent<CapsuleCollider3D>();
        capsule.Radius = 0.5f;
        capsule.Height = 2.4f;

        var controller = actor.AddComponent<CharacterController3D>();
        scene.FlushPendingActors();

        try
        {
            Assert.Equal(0.5f, controller.Radius, 3);
            Assert.Equal(2.4f, controller.Height, 3);
            Assert.Equal(1.2f, controller.FootOffset, 3);
        }
        finally
        {
            PhysicsSystem3D.Instance.RemoveBody(actor);
            scene.Destroy();
        }
    }

    /// <summary>
    /// A falling character accelerates at its own <c>Gravity</c>, and steering in the air is
    /// cut to <c>AirControl</c> of what was asked for.
    /// </summary>
    [Fact]
    public void AnAirborneCharacterFallsAtItsOwnGravityAndSteersAtAirControl()
    {
        var scene = new Scene("ControllerAir");
        var actor = scene.AddActor(new Actor("Walker"));

        // Far from anything another test may have left in the shared simulation, so the
        // ground check below finds nothing and the character is genuinely airborne.
        actor.AddComponent<Transform3D>().Position = new XnaVec3(500f, 200f, 500f);
        var controller = actor.AddComponent<CharacterController3D>();
        scene.FlushPendingActors();

        try
        {
            var body = actor.GetComponent<Rigidbody3D>()!;

            for (int step = 0; step < 60; step++) controller.FixedUpdate(1f / 60f);
            Assert.False(controller.IsGrounded);
            Assert.Equal(-20f, body.LinearVelocity.Y, 2);   // one second at -20 m/s²

            controller.Move(new XnaVec3(10f, 0f, 0f));
            Assert.Equal(4f, body.LinearVelocity.X, 3);     // 10 asked for, 0.4 of it granted

            controller.AirControl = 1f;
            controller.Move(new XnaVec3(10f, 0f, 0f));
            Assert.Equal(10f, body.LinearVelocity.X, 3);
        }
        finally
        {
            PhysicsSystem3D.Instance.RemoveBody(actor);
            scene.Destroy();
        }
    }

    // -------------------------------------------------------------------------
    // Rigidbody3D.useGravity and .freezeRotation
    // -------------------------------------------------------------------------

    /// <summary>
    /// A body that has switched gravity off stays where it is while its neighbour falls.
    /// </summary>
    /// <remarks>
    /// The browser skips the gravity term for a body whose <c>useGravity</c> is false. Bepu
    /// applies gravity in the pose integrator, over a whole bundle of bodies with no per-body
    /// parameter, which is why this engine had no such property at all — so a hovering
    /// platform or a floating pickup that stood still in the prototype dropped out of the
    /// world in a native build.
    /// </remarks>
    [Fact]
    public void ABodyThatHasSwitchedGravityOffStaysUpWhileItsNeighbourFalls()
    {
        var scene = new Scene("UseGravity");

        var falling = scene.AddActor(new Actor("Falling"));
        falling.AddComponent<Transform3D>();
        falling.AddComponent<Rigidbody3D>();

        // Well clear of the first: two unit spheres sharing the origin would push each other
        // apart and the test would be measuring the contact solver instead.
        var hovering = scene.AddActor(new Actor("Hovering"));
        hovering.AddComponent<Transform3D>().Position = new XnaVec3(10f, 0f, 0f);
        var hoveringBody = hovering.AddComponent<Rigidbody3D>();
        hoveringBody.UseGravity = false;
        scene.FlushPendingActors();

        try
        {
            for (int step = 0; step < 60; step++) PhysicsSystem3D.Instance.FixedStep(1f / 60f);

            Assert.True(falling.GetComponent<Transform3D>()!.Position.Y < -1f,
                "the ordinary body should have fallen about five metres in a second");
            Assert.Equal(0f, hovering.GetComponent<Transform3D>()!.Position.Y, 3);
            Assert.False(PhysicsSystem3D.Instance.IsGravityEnabled(hoveringBody.Handle));
        }
        finally
        {
            PhysicsSystem3D.Instance.RemoveBody(falling);
            PhysicsSystem3D.Instance.RemoveBody(hovering);
            scene.Destroy();
        }
    }

    /// <summary>
    /// The exemption goes with the body, so a handle Bepu hands out again does not inherit it.
    /// </summary>
    [Fact]
    public void RemovingABodyForgetsThatItHadGravitySwitchedOff()
    {
        var scene = new Scene("GravityHandles");
        var actor = scene.AddActor(new Actor("Hovering"));
        actor.AddComponent<Transform3D>();
        var body = actor.AddComponent<Rigidbody3D>();
        scene.FlushPendingActors();

        try
        {
            body.UseGravity = false;
            var handle = body.Handle;
            Assert.False(PhysicsSystem3D.Instance.IsGravityEnabled(handle));

            PhysicsSystem3D.Instance.RemoveBody(actor);
            Assert.True(PhysicsSystem3D.Instance.IsGravityEnabled(handle));
        }
        finally
        {
            PhysicsSystem3D.Instance.RemoveBody(actor);
            scene.Destroy();
        }
    }

    /// <summary>
    /// A frozen body cannot be spun by a torque, and taking the lock off gives it back the
    /// inertia its shape computed rather than an approximation.
    /// </summary>
    /// <remarks>
    /// The browser skips angular integration entirely while <c>freezeRotation</c> is set —
    /// how a top-down or first-person character stays upright. Without the property, a
    /// character that stood still in the prototype toppled over natively on its first
    /// glancing contact.
    /// </remarks>
    [Fact]
    public void AFrozenBodyCannotBeSpunAndThawsBackToItsOwnInertia()
    {
        var scene = new Scene("FreezeRotation");
        var actor = scene.AddActor(new Actor("Upright"));
        actor.AddComponent<Transform3D>();
        var body = actor.AddComponent<Rigidbody3D>();
        scene.FlushPendingActors();

        try
        {
            var reference = PhysicsSystem3D.Instance.Simulation.Bodies.GetBodyReference(body.Handle);
            var before = reference.LocalInertia.InverseInertiaTensor;

            body.FreezeRotation = true;
            body.AddTorque(new XnaVec3(0f, 5f, 0f));
            PhysicsSystem3D.Instance.FixedStep(1f / 60f);
            Assert.Equal(0f, body.AngularVelocity.Length(), 4);

            body.FreezeRotation = false;
            Assert.Equal(before.YY, reference.LocalInertia.InverseInertiaTensor.YY, 4);

            body.AddTorque(new XnaVec3(0f, 5f, 0f));
            PhysicsSystem3D.Instance.FixedStep(1f / 60f);
            Assert.True(body.AngularVelocity.Y > 0.1f,
                $"an unfrozen body should spin; it turned at {body.AngularVelocity.Y}");
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
