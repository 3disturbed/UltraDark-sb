using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scene;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Actor-level attachment: the parent/child relationship a game reasons about, as opposed to
/// the transform parenting it is built on.
/// </summary>
public class ActorHierarchyTests
{
    private static Actor Add(Engine.Core.Scene scene, string name, float x = 0, float y = 0)
    {
        var actor = new Actor(name);
        actor.Transform.Position = new Vector2(x, y);
        scene.AddActor(actor);
        return actor;
    }

    [Fact]
    public void AttachingKeepsTheChildWhereItStands()
    {
        // The default has to be "do not move me". A pickup snapped onto a moving player should
        // stay in the player's hand, not teleport to the player's origin the instant it is
        // attached -- which is what keepWorldTransform:false would do.
        var scene = new Engine.Core.Scene("attach");
        try
        {
            var parent = Add(scene, "Player", 100, 50);
            var child  = Add(scene, "Pickup", 110, 50);
            scene.FlushPendingActors();

            child.AttachTo(parent);

            Assert.Equal(110f, child.Transform.Position.X, 3);
            Assert.Equal(10f,  child.Transform.LocalPosition.X, 3);
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void AttachingWithoutKeepingTheWorldTransformTreatsTheOffsetAsLocal()
    {
        // The socket case, and the one the scene loader takes: the file already says "the
        // muzzle sits 20 units along the barrel", so that number is the local offset and must
        // be applied as written rather than rebased.
        var scene = new Engine.Core.Scene("socket");
        try
        {
            var barrel = Add(scene, "Barrel", 100, 0);
            var muzzle = Add(scene, "Muzzle", 20, 0);
            scene.FlushPendingActors();

            muzzle.AttachTo(barrel, keepWorldTransform: false);

            Assert.Equal(20f,  muzzle.Transform.LocalPosition.X, 3);
            Assert.Equal(120f, muzzle.Transform.Position.X, 3);
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void BothTransformsFollowTheAttachment()
    {
        // The reason attachment lives on the Actor at all. A 3D actor parented through the 2D
        // transform alone inherits nothing, because no 3D renderer ever reads that transform --
        // the child would sit at the world origin however far the parent moved.
        var scene = new Engine.Core.Scene("both");
        try
        {
            var parent = Add(scene, "Ship");
            var child  = Add(scene, "Turret");
            var parentSpatial = parent.AddComponent<Transform3D>();
            var childSpatial  = child.AddComponent<Transform3D>();
            scene.FlushPendingActors();

            child.AttachTo(parent, keepWorldTransform: false);
            parentSpatial.LocalPosition = new Vector3(0, 10, 0);
            childSpatial.LocalPosition  = new Vector3(0, 2, 0);

            Assert.Same(parent.Transform, child.Transform.Parent);
            Assert.Same(parentSpatial, childSpatial.Parent);
            Assert.Equal(12f, childSpatial.Position.Y, 3);
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void A3DChildOfA2DParentIsNotGivenAnInventedTransform()
    {
        // Attaching must never add a Transform3D to the parent behind the game's back: that
        // would change what the parent serialises as, and turn a 2D actor into a 3D one.
        var scene = new Engine.Core.Scene("mixed");
        try
        {
            var parent = Add(scene, "Flat");
            var child  = Add(scene, "Solid");
            var childSpatial = child.AddComponent<Transform3D>();
            scene.FlushPendingActors();

            child.AttachTo(parent);

            Assert.Null(parent.GetComponent<Transform3D>());
            Assert.Null(childSpatial.Parent);
            Assert.Same(parent, child.Parent);
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void ACycleIsRefusedRatherThanHanging()
    {
        // Every walk in the engine -- Root, IsActiveInHierarchy, Descendants, HierarchyPath --
        // is an unguarded loop up or down the tree. A cycle is not a wrong answer, it is a
        // frozen process, so it has to be impossible to create.
        var scene = new Engine.Core.Scene("cycle");
        try
        {
            var a = Add(scene, "A");
            var b = Add(scene, "B");
            var c = Add(scene, "C");
            scene.FlushPendingActors();

            b.AttachTo(a);
            c.AttachTo(b);

            Assert.Throws<InvalidOperationException>(() => a.AttachTo(c));
            Assert.Throws<InvalidOperationException>(() => a.AttachTo(a));
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void DestroyingAParentDestroysItsWholeSubtree()
    {
        // A tank's turret must not be left hanging in the air when the tank dies. The
        // alternative -- every caller walking the subtree first -- is the rule everybody
        // forgets exactly once.
        var scene = new Engine.Core.Scene("destroy");
        try
        {
            var tank   = Add(scene, "Tank");
            var turret = Add(scene, "Turret");
            var muzzle = Add(scene, "Muzzle");
            scene.FlushPendingActors();

            turret.AttachTo(tank);
            muzzle.AttachTo(turret);

            tank.Destroy();
            scene.FlushPendingActors();

            Assert.True(tank.IsDestroyed);
            Assert.True(turret.IsDestroyed);
            Assert.True(muzzle.IsDestroyed);
            Assert.Null(scene.FindByName("Muzzle"));
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void DetachedChildrenSurviveTheirParent()
    {
        // The documented escape hatch from the rule above. Debris that outlives the thing it
        // fell off has to be expressible.
        var scene = new Engine.Core.Scene("detach");
        try
        {
            var ship   = Add(scene, "Ship");
            var debris = Add(scene, "Debris");
            scene.FlushPendingActors();

            debris.AttachTo(ship);
            ship.DetachChildren();
            ship.Destroy();
            scene.FlushPendingActors();

            Assert.True(ship.IsDestroyed);
            Assert.False(debris.IsDestroyed);
            Assert.Null(debris.Parent);
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void ADestroyedChildLeavesItsParentsChildList()
    {
        // Otherwise a surviving parent holds a destroyed shell forever, and every
        // `foreach (var c in actor.Children)` in a game has to null-check it.
        var scene = new Engine.Core.Scene("orphan");
        try
        {
            var parent = Add(scene, "Parent");
            var child  = Add(scene, "Child");
            scene.FlushPendingActors();

            child.AttachTo(parent);
            child.Destroy();
            scene.FlushPendingActors();

            Assert.Empty(parent.Children);
            Assert.False(parent.IsDestroyed);
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void IsActiveInHierarchyFollowsEveryAncestor()
    {
        // IsActive answers "was this actor switched off", which is not the question gameplay
        // code asks. A child of a disabled parent is not running either.
        var scene = new Engine.Core.Scene("active");
        try
        {
            var root  = Add(scene, "Root");
            var mid   = Add(scene, "Mid");
            var leaf  = Add(scene, "Leaf");
            scene.FlushPendingActors();

            mid.AttachTo(root);
            leaf.AttachTo(mid);

            Assert.True(leaf.IsActiveInHierarchy);
            root.IsActive = false;
            Assert.True(leaf.IsActive);
            Assert.False(leaf.IsActiveInHierarchy);
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void FindChildAndPathsAgreeWithEachOther()
    {
        var scene = new Engine.Core.Scene("find");
        try
        {
            var tank   = Add(scene, "Tank");
            var turret = Add(scene, "Turret");
            var muzzle = Add(scene, "Muzzle");
            scene.FlushPendingActors();

            turret.AttachTo(tank);
            muzzle.AttachTo(turret);

            Assert.Same(turret, tank.FindChild("Turret"));
            Assert.Null(tank.FindChild("Muzzle"));
            Assert.Same(muzzle, tank.FindChild("Muzzle", recursive: true));
            Assert.Same(muzzle, tank.FindChildByPath("Turret/Muzzle"));
            Assert.Equal("Tank/Turret/Muzzle", muzzle.HierarchyPath);
            Assert.Same(tank, muzzle.Root);
            Assert.Equal(new[] { "Turret", "Muzzle" }, tank.Descendants().Select(a => a.Name));
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void ARoundTripPreservesTheHierarchyAndItsLocalOffsets()
    {
        // The point of nesting children in the scene file rather than writing a parent id: a
        // saved subtree reloads with the same shape and the same local offsets, and a child is
        // never loaded twice because it appeared both in the layer and in its parent.
        var scene = new Engine.Core.Scene("roundtrip");
        try
        {
            var tank   = Add(scene, "Tank", 100, 0);
            var turret = Add(scene, "Turret", 100, 10);
            var muzzle = Add(scene, "Muzzle", 100, 25);
            scene.FlushPendingActors();

            turret.AttachTo(tank);
            muzzle.AttachTo(turret);

            var reloaded = SceneSerializer.Deserialize(SceneSerializer.Serialize(scene));
            reloaded.FlushPendingActors();
            try
            {
                var loadedTank = reloaded.FindByName("Tank")!;
                Assert.Single(loadedTank.Children);

                var loadedTurret = loadedTank.FindChild("Turret")!;
                var loadedMuzzle = loadedTank.FindChild("Muzzle", recursive: true)!;

                Assert.Equal(10f,  loadedTurret.Transform.LocalPosition.Y, 3);
                Assert.Equal(15f,  loadedMuzzle.Transform.LocalPosition.Y, 3);
                Assert.Equal(25f,  loadedMuzzle.Transform.Position.Y, 3);
                Assert.Equal(100f, loadedMuzzle.Transform.Position.X, 3);

                // Each actor appears exactly once in the scene, not once per level it was
                // written at.
                Assert.Equal(3, reloaded.Layers.Sum(l => l.Actors.Count));
            }
            finally { reloaded.Destroy(); }
        }
        finally { scene.Destroy(); }
    }
}
