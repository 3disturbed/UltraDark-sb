using System.Collections;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Gameplay;
using Xunit;

namespace SexyBiscuit.Tests;

public class TimerManagerTests
{
    [Fact]
    public void SetTimer_FiresOnceAfterTheDelay()
    {
        var timers = new TimerManager();
        int fired = 0;

        timers.SetTimer(1f, () => fired++);

        timers.Tick(0.5f, 0.5f);
        Assert.Equal(0, fired);

        timers.Tick(0.6f, 0.6f);
        Assert.Equal(1, fired);

        timers.Tick(5f, 5f);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void LoopingTimer_FiresRepeatedlyWithoutDrift()
    {
        var timers = new TimerManager();
        int fired = 0;

        timers.SetTimer(0.1f, () => fired++, looping: true);

        // Ten ticks of exactly one interval should give exactly ten fires;
        // overshoot must carry forward rather than accumulate as drift.
        for (int i = 0; i < 10; i++) timers.Tick(0.1f, 0.1f);

        Assert.Equal(10, fired);
    }

    [Fact]
    public void Clear_StopsATimerBeforeItFires()
    {
        var timers = new TimerManager();
        int fired = 0;

        var handle = timers.SetTimer(1f, () => fired++);
        Assert.True(timers.Clear(handle));

        timers.Tick(2f, 2f);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void PausedTimer_DoesNotAdvance()
    {
        var timers = new TimerManager();
        int fired = 0;

        var handle = timers.SetTimer(1f, () => fired++);
        timers.Tick(0.1f, 0.1f);
        timers.Pause(handle);
        timers.Tick(5f, 5f);
        Assert.Equal(0, fired);

        timers.Resume(handle);
        timers.Tick(1f, 1f);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void UnscaledTimer_IgnoresTheScaledDelta()
    {
        var timers = new TimerManager();
        int scaled = 0, unscaled = 0;

        timers.SetTimer(1f, () => scaled++);
        timers.SetTimer(1f, () => unscaled++, useUnscaledTime: true);

        // Game is paused: scaled delta is zero, real time still passes.
        timers.Tick(0f, 1.5f);

        Assert.Equal(0, scaled);
        Assert.Equal(1, unscaled);
    }

    [Fact]
    public void AThrowingCallbackDoesNotStopOtherTimers()
    {
        var timers = new TimerManager();
        bool second = false;

        timers.SetTimer(0.1f, () => throw new InvalidOperationException("boom"));
        timers.SetTimer(0.1f, () => second = true);

        timers.Tick(0.2f, 0.2f);
        Assert.True(second);
    }
}

public class CoroutineTests
{
    [Fact]
    public void Coroutine_ResumesOneStepPerTick()
    {
        var runner = new CoroutineRunner();
        var log = new List<int>();

        IEnumerator Routine()
        {
            log.Add(1);
            yield return null;
            log.Add(2);
            yield return null;
            log.Add(3);
        }

        runner.Start(Routine());

        runner.Tick(0.016f, 0.016f); Assert.Equal(new[] { 1 }, log);
        runner.Tick(0.016f, 0.016f); Assert.Equal(new[] { 1, 2 }, log);
        runner.Tick(0.016f, 0.016f); Assert.Equal(new[] { 1, 2, 3 }, log);
    }

    [Fact]
    public void WaitForSeconds_SuspendsForTheGivenScaledTime()
    {
        var runner = new CoroutineRunner();
        bool done = false;

        IEnumerator Routine()
        {
            yield return new WaitForSeconds(1f);
            done = true;
        }

        runner.Start(Routine());

        runner.Tick(0.5f, 0.5f);   // first step: reaches the yield
        runner.Tick(0.4f, 0.4f);
        Assert.False(done);

        runner.Tick(0.7f, 0.7f);
        Assert.True(done);
    }

    [Fact]
    public void WaitUntil_BlocksUntilThePredicateHolds()
    {
        var runner = new CoroutineRunner();
        bool gate = false, done = false;

        IEnumerator Routine()
        {
            yield return new WaitUntil(() => gate);
            done = true;
        }

        runner.Start(Routine());
        runner.Tick(0.016f, 0.016f);
        runner.Tick(0.016f, 0.016f);
        Assert.False(done);

        gate = true;
        runner.Tick(0.016f, 0.016f);
        runner.Tick(0.016f, 0.016f);
        Assert.True(done);
    }

    [Fact]
    public void StopAllFor_CancelsOnlyThatOwnersRoutines()
    {
        var runner = new CoroutineRunner();
        var ownerA = new object();
        var ownerB = new object();
        int a = 0, b = 0;

        IEnumerator Forever(Action tick)
        {
            while (true) { tick(); yield return null; }
        }

        runner.Start(Forever(() => a++), ownerA);
        runner.Start(Forever(() => b++), ownerB);

        runner.Tick(0.016f, 0.016f);
        runner.StopAllFor(ownerA);
        runner.Tick(0.016f, 0.016f);

        Assert.Equal(1, a);
        Assert.Equal(2, b);
    }

    [Fact]
    public void AThrowingCoroutineIsStoppedNotPropagated()
    {
        var runner = new CoroutineRunner();

        IEnumerator Bad()
        {
            yield return null;
            throw new InvalidOperationException("boom");
        }

        runner.Start(Bad());
        runner.Tick(0.016f, 0.016f);
        runner.Tick(0.016f, 0.016f);   // must not throw

        Assert.Equal(0, runner.ActiveCount);
    }
}

public class ObjectPoolTests
{
    private sealed class Thing { public bool InUse; }

    [Fact]
    public void Rent_ReusesReturnedInstances()
    {
        var pool = new ObjectPool<Thing>(() => new Thing());

        var first = pool.Rent();
        pool.Return(first);
        var second = pool.Rent();

        Assert.Same(first, second);
        Assert.Equal(1, pool.CountCreated);
    }

    [Fact]
    public void Prewarm_CreatesInstancesUpFront()
    {
        var pool = new ObjectPool<Thing>(() => new Thing(), prewarm: 5);
        Assert.Equal(5, pool.CountInactive);
        Assert.Equal(5, pool.CountCreated);
    }

    [Fact]
    public void RentAndReturn_RunTheCallbacks()
    {
        var pool = new ObjectPool<Thing>(
            () => new Thing(),
            onRent:   t => t.InUse = true,
            onReturn: t => t.InUse = false);

        var thing = pool.Rent();
        Assert.True(thing.InUse);

        pool.Return(thing);
        Assert.False(thing.InUse);
    }

    [Fact]
    public void MaxRetained_CapsTheRetainedInstances()
    {
        var pool = new ObjectPool<Thing>(() => new Thing(), maxRetained: 2);

        var items = new[] { pool.Rent(), pool.Rent(), pool.Rent(), pool.Rent() };
        foreach (var i in items) pool.Return(i);

        Assert.Equal(2, pool.CountInactive);
    }

    [Fact]
    public void ReturningTheSameInstanceTwiceThrowsInDebug()
    {
        var pool = new ObjectPool<Thing>(() => new Thing());
        var thing = pool.Rent();
        pool.Return(thing);
#if DEBUG
        Assert.Throws<InvalidOperationException>(() => pool.Return(thing));
#endif
    }
}

public class SBEventTests
{
    [Fact]
    public void Broadcast_CallsEveryHandlerInOrder()
    {
        var evt = new SBEvent();
        var log = new List<int>();

        evt.Add(() => log.Add(1));
        evt.Add(() => log.Add(2));
        evt.Broadcast();

        Assert.Equal(new[] { 1, 2 }, log);
    }

    [Fact]
    public void RemovingAHandlerDuringBroadcastStillDeliversTheCurrentCall()
    {
        var evt = new SBEvent();
        int calls = 0;
        Action? handler = null;

        handler = () => { calls++; evt.Remove(handler!); };
        evt.Add(handler);

        evt.Broadcast();
        evt.Broadcast();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void AThrowingHandlerDoesNotBlockTheRest()
    {
        var evt = new SBEvent<int>();
        int received = 0;

        evt.Add(_ => throw new InvalidOperationException("boom"));
        evt.Add(v => received = v);

        evt.Broadcast(42);
        Assert.Equal(42, received);
    }
}

public class PossessionTests
{
    [Fact]
    public void Possess_LinksControllerAndPawnBothWays()
    {
        var controller = new PlayerController();
        var pawn = new Pawn("Body");

        controller.Possess(pawn);

        Assert.Same(pawn, controller.ControlledPawn);
        Assert.Same(controller, pawn.Controller);
        Assert.True(pawn.IsPlayerControlled);
    }

    [Fact]
    public void PossessingASecondPawnReleasesTheFirst()
    {
        var controller = new PlayerController();
        var first  = new Pawn("First");
        var second = new Pawn("Second");

        controller.Possess(first);
        controller.Possess(second);

        Assert.Null(first.Controller);
        Assert.Same(second, controller.ControlledPawn);
    }

    [Fact]
    public void PossessingAnAlreadyPossessedPawnEvictsTheOtherController()
    {
        var a = new PlayerController();
        var b = new PlayerController();
        var pawn = new Pawn();

        a.Possess(pawn);
        b.Possess(pawn);

        Assert.Null(a.ControlledPawn);
        Assert.Same(b, pawn.Controller);
    }

    [Fact]
    public void MovementInput_AccumulatesThenDrains()
    {
        var pawn = new Pawn();
        pawn.AddMovementInput(Vector3.Forward, 0.5f);
        pawn.AddMovementInput(Vector3.Right,   0.5f);

        var consumed = pawn.ConsumeMovementInput();
        Assert.True(consumed.Length() > 0f);
        Assert.Equal(Vector3.Zero, pawn.ConsumeMovementInput());
    }

    [Fact]
    public void MovementInput_IsClampedToUnitLength()
    {
        var pawn = new Pawn();
        pawn.AddMovementInput(Vector3.Forward, 5f);
        Assert.Equal(1f, pawn.ConsumeMovementInput().Length(), 3);
    }

    [Fact]
    public void ControllerPitch_IsClampedToAvoidFlippingOver()
    {
        var pawn = new Pawn();
        pawn.AddControllerPitchInput(1000f);
        Assert.Equal(89f, pawn.ControlRotation.X, 3);
    }
}

public class SubsystemTests
{
    private sealed class Tracked : GameInstanceSubsystem
    {
        public int Inits, Deinits, Ticks;
        public override void Initialize()   => Inits++;
        public override void Deinitialize() => Deinits++;
        public override void Tick(float dt) => Ticks++;
    }

    private sealed class Declined : GameInstanceSubsystem
    {
        public override bool ShouldCreate() => false;
    }

    [Fact]
    public void GetSubsystem_CreatesOnceAndCaches()
    {
        var instance = new GameInstance();
        instance.InternalInit();

        var a = instance.GetSubsystem<Tracked>();
        var b = instance.GetSubsystem<Tracked>();

        Assert.Same(a, b);
        Assert.Equal(1, a!.Inits);
        Assert.Same(instance, a.GameInstance);
    }

    [Fact]
    public void ASubsystemThatDeclinesIsNotCreated()
    {
        var instance = new GameInstance();
        instance.InternalInit();
        Assert.Null(instance.GetSubsystem<Declined>());
    }
}

public class PlayModeGateTests
{
    [Fact]
    public void AGameModeDoesNothingWhilePlayModeIsInactive()
    {
        // Why: the editor starts actors at edit time so they render. A GameMode that spawned its
        // game state and players then put them into every loaded, undone or hot-reloaded scene,
        // and the junk was saved with the level.
        PlayMode.IsActive = false;
        var scene = new Scene("EditTime");
        try
        {
            var mode = new GameMode();
            scene.AddActor(mode);
            scene.FlushPendingActors();

            Assert.Empty(mode.Controllers);
            Assert.Single(scene.Layers.SelectMany(l => l.Actors));   // the mode itself, nothing spawned
        }
        finally
        {
            PlayMode.IsActive = true;
            scene.Destroy();
        }
    }

    [Fact]
    public void AGameModeStartsTheMatchWhenPlayModeIsActive()
    {
        var scene = new Scene("Playing");
        try
        {
            var mode = new GameMode();
            scene.AddActor(mode);
            scene.FlushPendingActors();

            Assert.Single(mode.Controllers);
            Assert.NotNull(mode.GameState);
        }
        finally
        {
            scene.Destroy();
        }
    }
}
