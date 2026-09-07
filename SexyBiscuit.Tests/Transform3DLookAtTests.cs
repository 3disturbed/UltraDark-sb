using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// A transform told to look at a point ends up facing that point.
/// </summary>
/// <remarks>
/// The twin of <c>html5/tests/lookRotation.test.js</c>, and it exists because
/// the browser's version of this was wrong for as long as it had existed. Its
/// <c>Quaternion.lookRotation</c> built a LEFT-handed basis — determinant -1 for
/// every input — so the extraction read a non-unit quaternion out of a
/// reflection and returned a 180-degree roll that ignored the direction
/// entirely. A camera asked to look down at the ground kept staring at the
/// horizon, with nothing thrown and nothing logged.
///
/// This side was always right, because it defers to MonoGame's
/// <c>Matrix.CreateWorld</c> rather than doing the algebra itself. That is
/// precisely why nobody found the other one: there was no test holding the two
/// to the same answer, and nothing in the repository had ever aimed a 3D camera
/// — the bundled 3D template carries a comment where the call should be.
/// </remarks>
public class Transform3DLookAtTests
{
    private static (Scene scene, Transform3D t) Subject()
    {
        var scene = new Scene("LookAt");
        var actor = scene.AddActor(new Actor("Camera"));
        var t = actor.AddComponent<Transform3D>();
        scene.FlushPendingActors();
        return (scene, t);
    }

    [Theory]
    [InlineData(0f, 0f, -1f)]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, -0.903f, -0.429f)]
    [InlineData(0.5f, -0.707f, -0.5f)]
    [InlineData(0.2f, 0.9f, 0.35f)]
    public void LookingAlongADirectionLeavesForwardPointingThatWay(float x, float y, float z)
    {
        var (scene, t) = Subject();
        try
        {
            var want = Vector3.Normalize(new Vector3(x, y, z));
            t.Position = Vector3.Zero;
            t.LookAt(want);

            var got = t.Forward;
            Assert.True(Vector3.Distance(got, want) < 1e-3f,
                $"forward should be {want} but is {got}");
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void ACameraAboveTheGroundLooksDownAtTheSpotItWasGiven()
    {
        // The case the browser got wrong, in the numbers Jake01 actually uses.
        var (scene, t) = Subject();
        try
        {
            t.Position = new Vector3(4565f, 400f, 4755f);
            t.LookAt(new Vector3(4565f, 0f, 4565f));

            var f = t.Forward;
            Assert.True(f.Y < -0.5f, $"a camera above the ground must look DOWN at it, not along y={f.Y}");

            // Follow the ray to the ground and see where it lands.
            float k = -t.Position.Y / f.Y;
            Assert.Equal(4565f, t.Position.X + f.X * k, 1);
            Assert.Equal(4565f, t.Position.Z + f.Z * k, 1);
        }
        finally { scene.Destroy(); }
    }
}
