using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using Xunit;

namespace SexyBiscuit.Tests;

public class SBMathTests
{
    [Theory]
    [InlineData(370f, 10f)]
    [InlineData(-190f, 170f)]
    [InlineData(180f, 180f)]
    [InlineData(-180f, -180f)]
    public void WrapAngle_KeepsAnglesInRange(float input, float expected)
        => Assert.Equal(expected, SBMath.WrapAngle(input), 3);

    [Fact]
    public void DeltaAngle_TakesTheShortWayRound()
    {
        Assert.Equal(20f,  SBMath.DeltaAngle(350f, 10f), 3);
        Assert.Equal(-20f, SBMath.DeltaAngle(10f, 350f), 3);
    }

    [Fact]
    public void Damp_ConvergesAtTheSameRateRegardlessOfFramerate()
    {
        // One second of damping must land in the same place whether it is
        // simulated in one step or sixty. That is the whole point of Damp.
        const float halfLife = 0.25f;

        float coarse = SBMath.Damp(0f, 10f, halfLife, 1f);

        float fine = 0f;
        for (int i = 0; i < 60; i++) fine = SBMath.Damp(fine, 10f, halfLife, 1f / 60f);

        Assert.Equal(coarse, fine, 3);
    }

    [Fact]
    public void Damp_WithZeroHalfLife_Snaps()
        => Assert.Equal(10f, SBMath.Damp(0f, 10f, 0f, 0.016f));

    [Fact]
    public void SafeNormalize_ReturnsZeroForZeroVector()
        => Assert.Equal(Vector3.Zero, SBMath.SafeNormalize(Vector3.Zero));

    [Fact]
    public void Remap_MapsBetweenRanges()
        => Assert.Equal(50f, SBMath.Remap(0.5f, 0f, 1f, 0f, 100f), 3);

    [Fact]
    public void Remap_WithDegenerateInputRange_ReturnsOutputMinimum()
        => Assert.Equal(7f, SBMath.Remap(5f, 3f, 3f, 7f, 9f), 3);

    [Fact]
    public void SetSeed_MakesRandomReproducible()
    {
        SBMath.SetSeed(1234);
        var first = Enumerable.Range(0, 8).Select(_ => SBMath.RandomRange(0f, 1f)).ToArray();

        SBMath.SetSeed(1234);
        var second = Enumerable.Range(0, 8).Select(_ => SBMath.RandomRange(0f, 1f)).ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public void RandomOnUnitSphere_AlwaysHasUnitLength()
    {
        SBMath.SetSeed(7);
        for (int i = 0; i < 100; i++)
            Assert.Equal(1f, SBMath.RandomOnUnitSphere().Length(), 3);
    }
}

public class BoundsTests
{
    [Fact]
    public void FromPoints_TightlyContainsEveryPoint()
    {
        var bounds = Bounds.FromPoints(new[]
        {
            new Vector3(-1, 0, 2), new Vector3(3, 4, -5), new Vector3(0, -2, 1),
        });

        Assert.Equal(new Vector3(-1, -2, -5), bounds.Min);
        Assert.Equal(new Vector3(3, 4, 2), bounds.Max);
    }

    [Fact]
    public void FromPoints_WithNoPoints_ReturnsZero()
        => Assert.Equal(Bounds.Zero, Bounds.FromPoints(Array.Empty<Vector3>()));

    [Fact]
    public void Contains_DistinguishesInsideFromOutside()
    {
        var b = new Bounds(Vector3.Zero, new Vector3(2, 2, 2));
        Assert.True(b.Contains(new Vector3(0.9f, 0f, 0f)));
        Assert.False(b.Contains(new Vector3(1.1f, 0f, 0f)));
    }

    [Fact]
    public void Transform_UnderRotation_StaysConservative()
    {
        // A unit cube rotated 45 degrees about Y needs a wider AABB to contain it.
        var box = new Bounds(Vector3.Zero, Vector3.One);
        var rotated = box.Transform(Matrix.CreateRotationY(MathHelper.PiOver4));

        Assert.True(rotated.Extents.X > 0.5f);
        Assert.Equal(0.5f, rotated.Extents.Y, 4);
    }

    [Fact]
    public void Encapsulate_GrowsToIncludeThePoint()
    {
        var b = new Bounds(Vector3.Zero, Vector3.One);
        b.Encapsulate(new Vector3(5, 0, 0));
        Assert.True(b.Contains(new Vector3(5, 0, 0)));
    }

    [Fact]
    public void ClosestPoint_ClampsOntoTheSurface()
    {
        var b = new Bounds(Vector3.Zero, new Vector3(2, 2, 2));
        Assert.Equal(new Vector3(1, 1, 1), b.ClosestPoint(new Vector3(10, 10, 10)));
    }
}

public class TransformTests
{
    [Fact]
    public void Transform3D_ChildInheritsParentTranslation()
    {
        var parent = new Actor("Parent").AddComponent<Transform3D>();
        var child  = new Actor("Child").AddComponent<Transform3D>();

        parent.LocalPosition = new Vector3(10, 0, 0);
        child.SetParent(parent, keepWorldTransform: false);
        child.LocalPosition = new Vector3(0, 5, 0);

        Assert.Equal(new Vector3(10, 5, 0), child.Position);
    }

    [Fact]
    public void Transform3D_ChildInheritsParentRotation()
    {
        var parent = new Actor("Parent").AddComponent<Transform3D>();
        var child  = new Actor("Child").AddComponent<Transform3D>();

        parent.LocalRotation = Quaternion.CreateFromYawPitchRoll(MathHelper.PiOver2, 0f, 0f);
        child.SetParent(parent, keepWorldTransform: false);
        child.LocalPosition = new Vector3(0, 0, 1);

        // A 90-degree yaw maps local +Z onto world +X.
        var world = child.Position;
        Assert.Equal(1f, world.X, 3);
        Assert.Equal(0f, world.Z, 3);
    }

    [Fact]
    public void SetParent_KeepingWorldTransform_LeavesThePositionAlone()
    {
        var parent = new Actor("Parent").AddComponent<Transform3D>();
        var child  = new Actor("Child").AddComponent<Transform3D>();

        parent.LocalPosition = new Vector3(10, 0, 0);
        child.LocalPosition  = new Vector3(3, 0, 0);
        child.SetParent(parent, keepWorldTransform: true);

        Assert.Equal(3f, child.Position.X, 3);
    }

    [Fact]
    public void InverseTransformPoint_UndoesTransformPoint()
    {
        var t = new Actor("A").AddComponent<Transform3D>();
        t.LocalPosition = new Vector3(4, -2, 7);
        t.LocalRotation = Quaternion.CreateFromYawPitchRoll(0.6f, 0.2f, -0.3f);
        t.LocalScale    = new Vector3(2, 2, 2);

        var point = new Vector3(1, 2, 3);
        var round = t.InverseTransformPoint(t.TransformPoint(point));

        Assert.Equal(point.X, round.X, 3);
        Assert.Equal(point.Y, round.Y, 3);
        Assert.Equal(point.Z, round.Z, 3);
    }
}

public class ActorTests
{
    private sealed class Counter : Component
    {
        public int Awakes, Starts, Updates, Destroys;
        public override void Awake()      => Awakes++;
        public override void Start()      => Starts++;
        public override void Update(float dt) => Updates++;
        public override void OnDestroy()  => Destroys++;
    }

    [RequireComponent(typeof(Counter))]
    private sealed class NeedsCounter : Component { }

    [Fact]
    public void AddComponent_RunsAwakeImmediately()
    {
        var counter = new Actor().AddComponent<Counter>();
        Assert.Equal(1, counter.Awakes);
    }

    [Fact]
    public void RequireComponent_AutoAddsTheDependency()
    {
        var actor = new Actor();
        actor.AddComponent<NeedsCounter>();
        Assert.NotNull(actor.GetComponent<Counter>());
    }

    [Fact]
    public void GetComponents_ReturnsEveryMatch()
    {
        var actor = new Actor();
        actor.AddComponent<Counter>();
        actor.AddComponent<Counter>();
        Assert.Equal(2, actor.GetComponents<Counter>().Count());
    }

    [Fact]
    public void EveryActorGetsAUniqueId()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => new Actor().Id).ToHashSet();
        Assert.Equal(50, ids.Count);
    }

    [Fact]
    public void LifeSpan_DestroysTheActorWhenItExpires()
    {
        var scene = new Scene("test");
        var actor = scene.AddActor(new Actor("Temp"));
        actor.LifeSpan = 0.1f;

        scene.Update(0.05f);
        Assert.False(actor.IsDestroyed);

        // Two more ticks: one expires the lifespan, the next flushes the destroy queue.
        scene.Update(0.06f);
        scene.Update(0.01f);

        Assert.DoesNotContain(actor, scene.Layers.SelectMany(l => l.Actors));
    }

    [Fact]
    public void Destroy_IsDeferredToTheEndOfTheFrame()
    {
        var scene = new Scene("test");
        var actor = scene.AddActor(new Actor("Doomed"));

        scene.Update(0.016f);            // flush the pending add
        actor.Destroy();

        // Still present until the destroy queue flushes.
        Assert.Contains(actor, scene.Layers.SelectMany(l => l.Actors));

        scene.Update(0.016f);
        scene.Update(0.016f);
        Assert.DoesNotContain(actor, scene.Layers.SelectMany(l => l.Actors));
    }
}

public class SceneTests
{
    [Fact]
    public void NewScene_HasTheFourDefaultLayersInDrawOrder()
    {
        var scene = new Scene();
        Assert.Equal(new[] { "background", "default", "foreground", "ui" },
                     scene.Layers.Select(l => l.Name).ToArray());
    }

    [Fact]
    public void AddLayer_KeepsLayersSortedByOrder()
    {
        var scene = new Scene();
        scene.AddLayer("between", 50);

        // Order 50 puts it after default (0) and before foreground (100).
        Assert.Equal(new[] { "background", "default", "between", "foreground", "ui" },
                     scene.Layers.Select(l => l.Name).ToArray());
    }

    [Fact]
    public void FindByTag_SearchesEveryLayer()
    {
        var scene = new Scene();
        scene.AddActor(new Actor("A") { Tag = "enemy" }, "background");
        scene.AddActor(new Actor("B") { Tag = "enemy" }, "ui");
        scene.Update(0.016f);

        Assert.Equal(2, scene.FindByTag("enemy").Count());
    }
}
