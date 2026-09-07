using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Physics;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// A collider with no Rigidbody2D is scenery, and scenery does not fall.
/// </summary>
/// <remarks>
/// The bundled templates build their world out of actors carrying a
/// SpriteRenderer and a collider and nothing else — walls, crates, parked cars —
/// and their own comments call that "static geometry on both engines". The
/// browser engine makes it true by never integrating a collider that has no body
/// of its own. This engine instead auto-created a Rigidbody2D, and a fresh one is
/// Dynamic, so every piece of scenery in a native build slid off the bottom of
/// the screen while the player and the enemies looked fine — their scripts set a
/// velocity every frame, which overwrites an accumulating fall.
///
/// GravityScale cannot fix it: Aether has no per-body gravity and the property is
/// stored for game logic only. The body has to be kinematic.
/// </remarks>
public class BareColliderTests
{
    /// <summary>An actor with only a collider, as every template's scenery is built.</summary>
    private static (Scene scene, Actor actor) Scenery()
    {
        var scene = new Scene("BareCollider");
        var actor = scene.AddActor(new Actor("Wall"));
        actor.AddComponent<BoxCollider2D>().Size = new Vector2(64, 16);
        scene.FlushPendingActors();
        return (scene, actor);
    }

    [Fact]
    public void ABareColliderGetsAKinematicBodySoItIsNotPulledByGravity()
    {
        var (scene, actor) = Scenery();
        try
        {
            var body = actor.GetComponent<Rigidbody2D>();
            Assert.NotNull(body);   // the collider creates one; the question is which kind

            Assert.True(body!.IsKinematic,
                "a collider with no Rigidbody2D must get a kinematic body, or the scenery falls");
            Assert.Equal(0f, body.GravityScale);
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Fact]
    public void AnExplicitRigidbodyIsLeftExactlyAsTheSceneAskedFor()
    {
        // The fix must not reach past the case it is for: an actor that asks for a
        // dynamic body still gets one, whichever order its components were added.
        var scene = new Scene("BareCollider");
        var actor = scene.AddActor(new Actor("Barrel"));
        var body = actor.AddComponent<Rigidbody2D>();
        actor.AddComponent<BoxCollider2D>();
        scene.FlushPendingActors();

        try
        {
            Assert.False(body.IsKinematic, "an explicitly added Rigidbody2D stays dynamic");
            Assert.Equal(1f, body.GravityScale);
        }
        finally
        {
            scene.Destroy();
        }
    }
}
