using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// A sprite is skipped only when it provably cannot touch the viewport.
/// </summary>
/// <remarks>
/// Culling is the one optimisation whose bug is invisible to every other gate
/// here: a scene that culls too eagerly still loads, still steps, still reports
/// no errors, and simply has a hole in the picture that nothing but an eye can
/// see. So the arithmetic it rests on is pinned rather than trusted.
///
/// The case that matters is the pivot. A 2.5D game extrudes a wall out of a
/// single sprite by hanging it upward from its footprint — pivot (0.5, 1) — so
/// the drawn rectangle is entirely above the actor's position. Cull that from
/// the centre and the wall disappears while the part you can see is still on
/// screen, which reads as flickering geometry rather than as a culling bug.
/// </remarks>
public class ViewCullingTests
{
    private static SpriteRenderer Sprite(Vector2 size, Vector2 pivot)
    {
        var scene = new Scene("Cull");
        var actor = scene.AddActor(new Actor("Thing"));
        var sr = actor.AddComponent<SpriteRenderer>();
        sr.Size = size;
        sr.Pivot = pivot;
        scene.FlushPendingActors();
        return sr;
    }

    [Fact]
    public void ACentredSpriteReachesHalfItsDiagonal()
    {
        var sr = Sprite(new Vector2(60f, 80f), new Vector2(0.5f, 0.5f));
        try
        {
            // 30 and 40 from the centre: the corner is at 50.
            Assert.Equal(50f, sr.CullRadius(), 3);
        }
        finally { sr.Actor.Scene!.Destroy(); }
    }

    [Fact]
    public void ASpriteHangingAboveItsActorReachesItsWholeHeight()
    {
        // The extruded wall: pivot y = 1, so nothing is drawn below the actor and
        // the whole 80 is drawn above it. Measuring from the centre would say 50
        // and cut the wall off 30px early.
        var sr = Sprite(new Vector2(60f, 80f), new Vector2(0.5f, 1f));
        try
        {
            Assert.Equal(MathF.Sqrt(30f * 30f + 80f * 80f), sr.CullRadius(), 3);
        }
        finally { sr.Actor.Scene!.Destroy(); }
    }

    [Fact]
    public void ScaleCountsAndSoDoesANegativeOne()
    {
        var sr = Sprite(new Vector2(10f, 10f), new Vector2(0.5f, 0.5f));
        try
        {
            // A flipped actor draws exactly as far as an unflipped one; taking the
            // signed size would return a negative radius and cull it everywhere.
            sr.Actor.Transform.Scale = new Vector2(-3f, 3f);
            Assert.Equal(MathF.Sqrt(15f * 15f + 15f * 15f), sr.CullRadius(), 3);
        }
        finally { sr.Actor.Scene!.Destroy(); }
    }

    [Fact]
    public void WithNoCameraNothingIsCulled()
    {
        // A tool or a test draws with an identity transform and no camera. There
        // is no way to know what is on screen, so everything must be submitted:
        // quietly dropping sprites would be far worse than drawing too many.
        Assert.True(RenderSystem2D.IsVisible(new Vector2(1_000_000f, 1_000_000f), 1f),
            "a pass with no camera must not cull anything");
    }
}
