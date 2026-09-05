using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.AI;
using SexyBiscuit.Engine.Core;
using Xunit;

namespace SexyBiscuit.Tests;

public class BlackboardTests
{
    [Fact]
    public void SetThenGet_RoundTripsTheValue()
    {
        var bb = new Blackboard();
        bb.Set("hp", 42);
        Assert.Equal(42, bb.Get<int>("hp"));
    }

    [Fact]
    public void TryGet_WithTheWrongTypeFailsInsteadOfThrowing()
    {
        var bb = new Blackboard();
        bb.Set("hp", "not a number");
        Assert.False(bb.TryGet<int>("hp", out _));
    }

    [Fact]
    public void Get_ReturnsTheFallbackForAMissingKey()
        => Assert.Equal(7, new Blackboard().Get("missing", 7));

    [Fact]
    public void TryGetPosition_AcceptsEitherAVectorOrAnActor()
    {
        var bb = new Blackboard();

        bb.Set("spot", new Vector3(1, 2, 3));
        Assert.True(bb.TryGetPosition("spot", out var direct));
        Assert.Equal(new Vector3(1, 2, 3), direct);

        var actor = new Actor("Target");
        actor.AddComponent<Transform3D>().LocalPosition = new Vector3(4, 5, 6);
        bb.Set("who", actor);

        Assert.True(bb.TryGetPosition("who", out var fromActor));
        Assert.Equal(new Vector3(4, 5, 6), fromActor);
    }

    [Fact]
    public void ValueChanged_FiresOnWrite()
    {
        var bb = new Blackboard();
        string? seen = null;
        bb.ValueChanged.Add(k => seen = k);

        bb.Set("alert", true);
        Assert.Equal("alert", seen);
    }
}

public class BehaviorTreeTests
{
    private static BehaviorContext Ctx(float dt = 0.016f)
        => new() { Blackboard = new Blackboard(), DeltaTime = dt };

    [Fact]
    public void Sequence_SucceedsOnlyWhenEveryChildSucceeds()
    {
        var seq = new Sequence(
            new ConditionNode(_ => true),
            new ConditionNode(_ => true));

        Assert.Equal(NodeStatus.Success, seq.Tick(Ctx()));
    }

    [Fact]
    public void Sequence_FailsAtTheFirstFailingChild()
    {
        int reached = 0;
        var seq = new Sequence(
            new ConditionNode(_ => false),
            new ActionNode(_ => { reached++; return NodeStatus.Success; }));

        Assert.Equal(NodeStatus.Failure, seq.Tick(Ctx()));
        Assert.Equal(0, reached);
    }

    [Fact]
    public void Selector_StopsAtTheFirstSuccess()
    {
        int reached = 0;
        var sel = new Selector(
            new ConditionNode(_ => true),
            new ActionNode(_ => { reached++; return NodeStatus.Success; }));

        Assert.Equal(NodeStatus.Success, sel.Tick(Ctx()));
        Assert.Equal(0, reached);
    }

    [Fact]
    public void Sequence_RemembersItsPlaceAcrossTicks()
    {
        int firstRuns = 0;
        var seq = new Sequence(
            new ActionNode(_ => { firstRuns++; return NodeStatus.Success; }),
            new WaitNode(1f));

        var ctx = Ctx(0.1f);
        for (int i = 0; i < 5; i++) seq.Tick(ctx);

        // The first child must not re-run while the second is still going.
        Assert.Equal(1, firstRuns);
    }

    [Fact]
    public void Inverter_SwapsSuccessAndFailure()
    {
        Assert.Equal(NodeStatus.Failure, new Inverter(new ConditionNode(_ => true)).Tick(Ctx()));
        Assert.Equal(NodeStatus.Success, new Inverter(new ConditionNode(_ => false)).Tick(Ctx()));
    }

    [Fact]
    public void WaitNode_RunsUntilTheDurationElapses()
    {
        var wait = new WaitNode(0.3f);
        var ctx = Ctx(0.1f);

        Assert.Equal(NodeStatus.Running, wait.Tick(ctx));
        Assert.Equal(NodeStatus.Running, wait.Tick(ctx));
        Assert.Equal(NodeStatus.Success, wait.Tick(ctx));
    }

    [Fact]
    public void Cooldown_BlocksTheChildUntilItExpires()
    {
        int runs = 0;
        var node = new Cooldown(new ActionNode(_ => { runs++; return NodeStatus.Success; }), 1f);
        var ctx = Ctx(0.25f);

        Assert.Equal(NodeStatus.Success, node.Tick(ctx));
        Assert.Equal(1, runs);

        for (int i = 0; i < 3; i++) Assert.Equal(NodeStatus.Failure, node.Tick(ctx));
        Assert.Equal(1, runs);

        node.Tick(ctx);                       // fourth tick clears the cooldown
        Assert.Equal(NodeStatus.Success, node.Tick(ctx));
        Assert.Equal(2, runs);
    }

    [Fact]
    public void EnterAndExit_RunExactlyOncePerRun()
    {
        var probe = new LifecycleProbe();
        var ctx = Ctx();

        probe.Tick(ctx);   // Running
        probe.Tick(ctx);   // Running
        probe.Finish = true;
        probe.Tick(ctx);   // Success

        Assert.Equal(1, probe.Enters);
        Assert.Equal(1, probe.Exits);
    }

    private sealed class LifecycleProbe : BehaviorNode
    {
        public int Enters, Exits;
        public bool Finish;

        protected override void OnEnter(BehaviorContext c) => Enters++;
        protected override void OnExit(BehaviorContext c, NodeStatus s) => Exits++;
        protected override NodeStatus OnTick(BehaviorContext c)
            => Finish ? NodeStatus.Success : NodeStatus.Running;
    }
}

public class NavMeshTests
{
    /// <summary>
    /// A flat 4x4 grid of quads on the XZ plane spanning (0,0) to (4,4), split into triangles.
    /// </summary>
    private static NavMesh BuildFlatGrid(int size = 4)
    {
        var vertices = new List<Vector3>();
        var indices  = new List<int>();

        for (int z = 0; z <= size; z++)
            for (int x = 0; x <= size; x++)
                vertices.Add(new Vector3(x, 0f, z));

        int stride = size + 1;
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int bl = z * stride + x;
                int br = bl + 1;
                int tl = bl + stride;
                int tr = tl + 1;

                indices.AddRange(new[] { bl, tl, tr });
                indices.AddRange(new[] { bl, tr, br });
            }
        }

        return NavMesh.Build(vertices, indices);
    }

    [Fact]
    public void Build_KeepsEveryFlatTriangle()
        => Assert.Equal(32, BuildFlatGrid().TriangleCount);

    [Fact]
    public void Build_DropsTrianglesSteeperThanTheSlopeLimit()
    {
        // A vertical wall triangle should be rejected at the default slope limit.
        var vertices = new List<Vector3> { new(0, 0, 0), new(0, 2, 0), new(0, 0, 2) };
        var mesh = NavMesh.Build(vertices, new[] { 0, 1, 2 });
        Assert.Equal(0, mesh.TriangleCount);
    }

    [Fact]
    public void SamplePosition_SnapsAPointOntoTheSurface()
    {
        var mesh = BuildFlatGrid();
        Assert.True(mesh.SamplePosition(new Vector3(2f, 3f, 2f), 5f, out var nearest));
        Assert.Equal(0f, nearest.Y, 3);
    }

    [Fact]
    public void SamplePosition_FailsWhenTheMeshIsOutOfRange()
        => Assert.False(BuildFlatGrid().SamplePosition(new Vector3(100, 0, 100), 1f, out _));

    [Fact]
    public void FindPath_ConnectsOppositeCornersOfTheGrid()
    {
        var mesh = BuildFlatGrid();
        var path = new List<Vector3>();

        Assert.True(mesh.FindPath(new Vector3(0.5f, 0f, 0.5f), new Vector3(3.5f, 0f, 3.5f), path));
        Assert.True(path.Count >= 2);

        // Endpoints must actually be the requested endpoints.
        Assert.Equal(0.5f, path[0].X, 2);
        Assert.Equal(3.5f, path[^1].X, 2);
    }

    [Fact]
    public void FindPath_OnAnOpenPlaneProducesAStraightLine()
    {
        // With nothing in the way, string-pulling should collapse the corridor
        // down to just the two endpoints.
        var mesh = BuildFlatGrid();
        var path = new List<Vector3>();

        Assert.True(mesh.FindPath(new Vector3(0.5f, 0f, 2f), new Vector3(3.5f, 0f, 2f), path));
        Assert.Equal(2, path.Count);
    }

    [Fact]
    public void FindPath_FailsWhenAnEndpointIsOffTheMesh()
    {
        var mesh = BuildFlatGrid();
        var path = new List<Vector3>();
        Assert.False(mesh.FindPath(new Vector3(0.5f, 0f, 0.5f), new Vector3(50f, 0f, 50f), path, snapDistance: 1f));
    }

    [Fact]
    public void FindPath_AcrossTwoDisconnectedIslandsFails()
    {
        var vertices = new List<Vector3>
        {
            // Island A around the origin
            new(0, 0, 0), new(1, 0, 0), new(0, 0, 1),
            // Island B far away
            new(20, 0, 20), new(21, 0, 20), new(20, 0, 21),
        };
        var mesh = NavMesh.Build(vertices, new[] { 0, 2, 1, 3, 5, 4 });

        var path = new List<Vector3>();
        Assert.False(mesh.FindPath(new Vector3(0.2f, 0, 0.2f), new Vector3(20.2f, 0, 20.2f), path, snapDistance: 5f));
    }

    [Fact]
    public void ClosestPointOnTriangle_ClampsToTheNearestEdge()
    {
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(1, 0, 0);
        var c = new Vector3(0, 0, 1);

        var outside = NavMesh.ClosestPointOnTriangle(new Vector3(-1, 0, -1), a, b, c);
        Assert.Equal(a, outside);

        var inside = NavMesh.ClosestPointOnTriangle(new Vector3(0.2f, 5f, 0.2f), a, b, c);
        Assert.Equal(0f, inside.Y, 4);
    }
}
