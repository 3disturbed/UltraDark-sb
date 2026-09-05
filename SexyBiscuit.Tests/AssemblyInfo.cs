using Xunit;

// The engine is deliberately single-threaded for gameplay: SBMath.Random,
// TimerManager.Instance, CoroutineRunner.Instance, NavMesh.Active, GameInstance.Current
// and the component registries are all process-wide state with no locking, because
// locking them would cost more than it saves on a game thread.
//
// xUnit parallelises test *classes* by default, which turns that design choice into
// flaky tests — a CameraShake constructed in one class draws from the same
// SBMath.Random that another class just seeded. The suite runs in well under a second,
// so serialising it costs nothing and removes a whole category of false failure.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
