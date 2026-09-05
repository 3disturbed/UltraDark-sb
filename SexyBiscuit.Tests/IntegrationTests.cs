using System.Collections;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.AI;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Gameplay;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Drives the whole gameplay stack through hundreds of simulated frames without a
/// graphics device.
/// </summary>
/// <remarks>
/// The unit tests elsewhere check pieces in isolation; these check that the pieces
/// survive being run together and repeatedly. Most of what breaks an engine update path
/// is a null reference or a collection modified during iteration, and neither shows up
/// until something ticks a few hundred times with actors coming and going.
///
/// Rendering, audio and physics stepping are excluded — all three need a device or a
/// live simulation. What is covered is the part that runs every frame regardless.
/// </remarks>
public class GameplayLoopTests
{
    /// <summary>A test harness that pumps the same order SBEngine.Update does.</summary>
    /// <remarks>
    /// Disposable, and every test disposes it. Components register in static registries
    /// (PlayerStart.All, Light3D.All, MeshRenderer.All) and only leave them on destroy, so
    /// a scene that is merely dropped stays visible to the next test — which showed up as
    /// a spawn landing at a previous test's PlayerStart.
    /// </remarks>
    private sealed class Harness : IDisposable
    {
        public Scene            Scene      { get; }
        public TimerManager     Timers     { get; }
        public CoroutineRunner  Coroutines { get; }
        public GameInstance     Instance   { get; }

        public Harness(string sceneName = "Test")
        {
            Scene      = new Scene(sceneName);
            Timers     = TimerManager.Instance    = new TimerManager();
            Coroutines = CoroutineRunner.Instance = new CoroutineRunner();
            Instance   = new GameInstance();
            Instance.InternalInit();
            Instance.InternalStart();
        }

        public void Frame(float dt = 1f / 60f)
        {
            Instance.InternalTick(dt);
            Scene.FixedUpdate(dt);
            Scene.Update(dt);
            Timers.Tick(dt, dt);
            Coroutines.Tick(dt, dt);
        }

        public void Frames(int count, float dt = 1f / 60f)
        {
            for (int i = 0; i < count; i++) Frame(dt);
        }

        public void Dispose()
        {
            Scene.Destroy();
            Instance.InternalShutdown();
            Coroutines.StopAll();
            Timers.ClearAll();
        }
    }

    private sealed class Ticker : Component
    {
        public int Updates, FixedUpdates, LateUpdates;
        public override void Update(float dt)      => Updates++;
        public override void FixedUpdate(float dt) => FixedUpdates++;
        public override void LateUpdate(float dt)  => LateUpdates++;
    }

    [Fact]
    public void AnActorTicksEveryFrameOnceItIsLive()
    {
        using var h = new Harness();
        var ticker = h.Scene.AddActor(new Actor("A")).AddComponent<Ticker>();

        h.Frames(10);

        // The first frame flushes the pending add, so the actor sees nine.
        Assert.InRange(ticker.Updates, 9, 10);
    }

    [Fact]
    public void SpawningAndDestroyingEveryFrameDoesNotCorruptIteration()
    {
        using var h = new Harness();
        var live = new List<Actor>();

        for (int frame = 0; frame < 200; frame++)
        {
            // Churn hard: two spawns and one destroy per frame, from outside and
            // from inside the update path.
            live.Add(h.Scene.AddActor(new Actor($"spawn{frame}") { LifeSpan = 0.05f }));
            live.Add(h.Scene.AddActor(new Actor($"perm{frame}")));

            if (live.Count > 20)
            {
                live[0].Destroy();
                live.RemoveAt(0);
            }

            h.Frame();
        }

        // The point is that none of that threw. A surviving scene is the assertion.
        Assert.True(h.Scene.Layers.SelectMany(l => l.Actors).Any());
    }

    [Fact]
    public void AGameModeSpawnsAPossessedPawnAndSurvivesRespawn()
    {
        using var h = new Harness();

        var start = h.Scene.AddActor(new Actor("Start"));
        start.AddComponent<Transform3D>().LocalPosition = new Vector3(5, 0, 5);
        start.AddComponent<PlayerStart>();

        var mode = new GameMode
        {
            PawnFactory  = () => new Pawn("Body"),
            RespawnDelay = 0.1f,
        };
        h.Scene.AddActor(mode);

        h.Frames(3);

        var controller = Assert.Single(mode.Controllers);
        Assert.NotNull(controller.ControlledPawn);
        Assert.Equal(MatchState.InProgress, mode.GameState.MatchState);

        // The pawn should have been placed at the player start.
        var pawnT = controller.ControlledPawn!.GetComponent<Transform3D>()!;
        Assert.Equal(5f, pawnT.Position.X, 3);

        var original = controller.ControlledPawn;
        mode.RestartPlayer(controller);
        Assert.Null(controller.ControlledPawn);

        // Respawn is on a timer, so it needs frames to land.
        h.Frames(20);

        Assert.NotNull(controller.ControlledPawn);
        Assert.NotSame(original, controller.ControlledPawn);
    }

    [Fact]
    public void TheMatchEndsWhenTheScoreLimitIsReached()
    {
        using var h = new Harness();
        h.Scene.AddActor(new Actor("Start")).AddComponent<PlayerStart>();

        var mode = new GameMode { ScoreToWin = 3, PawnFactory = () => new Pawn() };
        h.Scene.AddActor(mode);
        h.Frames(3);

        PlayerState? winner = null;
        mode.MatchEnded.Add(w => winner = w);

        mode.Controllers[0].PlayerState!.AddScore(3);
        h.Frames(2);

        Assert.Equal(MatchState.Ended, mode.GameState.MatchState);
        Assert.NotNull(winner);
    }

    [Fact]
    public void TimersAndCoroutinesInterleaveOverManyFrames()
    {
        using var h = new Harness();
        var order = new List<string>();

        h.Timers.SetTimer(0.1f, () => order.Add("timer"));

        IEnumerator Routine()
        {
            yield return new WaitForSeconds(0.2f);
            order.Add("coroutine");
        }

        h.Coroutines.Start(Routine());
        h.Frames(30);

        Assert.Equal(new[] { "timer", "coroutine" }, order);
    }

    [Fact]
    public void ACoroutineOwnedByAnActorStopsWhenTheActorIsDestroyed()
    {
        using var h = new Harness();
        var actor = h.Scene.AddActor(new Actor("Runner"));
        h.Frame();

        int ticks = 0;
        IEnumerator Forever()
        {
            while (true) { ticks++; yield return null; }
        }

        actor.StartCoroutine(Forever());
        h.Frames(5);

        int atDestroy = ticks;
        actor.Destroy();
        h.Frames(5);

        Assert.Equal(atDestroy, ticks);
    }

    [Fact]
    public void AnAiControllerWithANavAgentRunsItsTreeWithoutThrowing()
    {
        using var h = new Harness();

        // A flat 6x6 plane to walk on.
        var verts = new List<Vector3>();
        var idx   = new List<int>();
        const int size = 6;

        for (int z = 0; z <= size; z++)
            for (int x = 0; x <= size; x++)
                verts.Add(new Vector3(x, 0, z));

        for (int z = 0; z < size; z++)
            for (int x = 0; x < size; x++)
            {
                int bl = z * (size + 1) + x;
                idx.AddRange(new[] { bl, bl + size + 1, bl + size + 2 });
                idx.AddRange(new[] { bl, bl + size + 2, bl + 1 });
            }

        NavMesh.Active = NavMesh.Build(verts, idx);

        var pawn = new Pawn("Enemy");
        pawn.AddComponent<Transform3D>().LocalPosition = new Vector3(0.5f, 0, 0.5f);
        var agent = pawn.AddComponent<NavMeshAgent>();
        agent.Speed = 4f;
        h.Scene.AddActor(pawn);

        var ai = new AIController { RequireLineOfSight = false };
        ai.Blackboard.Set("Goal", new Vector3(5.5f, 0, 5.5f));
        ai.BehaviorTree = new BehaviorTree(
            new Sequence(
                new MoveToNode("Goal", acceptanceRadius: 0.6f),
                new WaitNode(0.2f)),
            ai.Blackboard, ai);

        h.Scene.AddActor(ai);
        h.Frame();
        ai.Possess(pawn);

        h.Frames(300);

        // Three seconds at 4 units/second is ample for a 7-unit diagonal.
        var finalPos = pawn.GetComponent<Transform3D>()!.Position;
        Assert.True(Vector3.Distance(finalPos, new Vector3(5.5f, 0, 5.5f)) < 1.5f,
            $"agent ended at {finalPos}, which is not near the goal");
    }

    [Fact]
    public void TimeScaleZeroFreezesScaledWorkButNotUnscaled()
    {
        using var h = new Harness();
        int scaled = 0, unscaled = 0;

        h.Timers.SetTimer(0.1f, () => scaled++, looping: true);
        h.Timers.SetTimer(0.1f, () => unscaled++, looping: true, useUnscaledTime: true);

        // Simulate a paused frame: zero scaled delta, real time still passing.
        for (int i = 0; i < 60; i++) h.Timers.Tick(0f, 1f / 60f);

        Assert.Equal(0, scaled);
        Assert.True(unscaled > 5);
    }

    [Fact]
    public void ADeepTransformHierarchyStaysConsistentAcrossFrames()
    {
        using var h = new Harness();

        var transforms = new List<Transform3D>();
        Transform3D? parent = null;

        for (int i = 0; i < 12; i++)
        {
            var actor = h.Scene.AddActor(new Actor($"link{i}"));
            var t = actor.AddComponent<Transform3D>();
            t.LocalPosition = new Vector3(0, 1, 0);
            if (parent != null) t.SetParent(parent, keepWorldTransform: false);
            transforms.Add(t);
            parent = t;
        }

        h.Frames(5);

        // Each link is one unit above the last, so the tip is eleven above the root.
        Assert.Equal(12f, transforms[^1].Position.Y, 3);

        // Moving the root must carry the whole chain.
        transforms[0].LocalPosition = new Vector3(10, 1, 0);
        h.Frame();

        Assert.Equal(10f, transforms[^1].Position.X, 3);
        Assert.Equal(12f, transforms[^1].Position.Y, 3);
    }
}
