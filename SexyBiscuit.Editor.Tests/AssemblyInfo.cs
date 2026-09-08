using Xunit;

// The engine's process-wide state (SBMath.Random, the component registries, GameInstance.Current)
// is single-threaded by design, and building a catalogue touches the registries. Serialise, as
// SexyBiscuit.Tests does: the suite runs in well under a second.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
