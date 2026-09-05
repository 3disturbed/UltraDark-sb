namespace SexyBiscuit.Engine.Gameplay;

/// <summary>
/// Base class for engine subsystems — long-lived singletons with a managed lifetime,
/// modelled on Unreal's <c>USubsystem</c> family.
/// </summary>
/// <remarks>
/// A subsystem is discovered by type and instantiated automatically by its owning
/// collection, so gameplay code never has to construct or register one. Derive from
/// <see cref="GameInstanceSubsystem"/> for state that outlives the level, or
/// <see cref="WorldSubsystem"/> for state that is torn down with the scene.
/// </remarks>
public abstract class Subsystem
{
    /// <summary>True between <see cref="Initialize"/> and <see cref="Deinitialize"/>.</summary>
    public bool IsInitialized { get; internal set; }

    /// <summary>
    /// Return false to prevent this subsystem from being created — useful for a subsystem
    /// that only applies to, say, dedicated servers or a particular platform.
    /// </summary>
    public virtual bool ShouldCreate() => true;

    /// <summary>Called once when the subsystem is created. Acquire resources here.</summary>
    public virtual void Initialize() { }

    /// <summary>Called once when the owning collection is torn down. Release resources here.</summary>
    public virtual void Deinitialize() { }

    /// <summary>Called once per frame with the scaled frame delta. Default does nothing.</summary>
    public virtual void Tick(float dt) { }
}

/// <summary>
/// A subsystem owned by the <see cref="GameInstance"/>. Created at startup and kept alive
/// for the whole session, across every scene load.
/// </summary>
/// <example>
/// <code>
/// public class AchievementSubsystem : GameInstanceSubsystem
/// {
///     public override void Initialize() =&gt; Load();
/// }
///
/// // Anywhere:
/// var achievements = GameInstance.Current.GetSubsystem&lt;AchievementSubsystem&gt;();
/// </code>
/// </example>
public abstract class GameInstanceSubsystem : Subsystem
{
    /// <summary>The game instance that owns this subsystem.</summary>
    public GameInstance GameInstance { get; internal set; } = null!;
}

/// <summary>
/// A subsystem scoped to the active scene. Created when a scene becomes active and
/// deinitialised when it is unloaded, so per-level state cannot leak between levels.
/// </summary>
public abstract class WorldSubsystem : Subsystem
{
    /// <summary>The scene this subsystem belongs to.</summary>
    public Core.Scene World { get; internal set; } = null!;
}

/// <summary>
/// Type-keyed store of subsystem instances. One collection lives on the
/// <see cref="GameInstance"/>; another is created per scene.
/// </summary>
public sealed class SubsystemCollection
{
    private readonly Dictionary<Type, Subsystem> _subsystems = new();

    /// <summary>Every live subsystem in creation order.</summary>
    public IEnumerable<Subsystem> All => _subsystems.Values;

    /// <summary>
    /// Returns the subsystem of type <typeparamref name="T"/>, creating and initialising it
    /// on first request. Returns null when the type's <see cref="Subsystem.ShouldCreate"/> declined.
    /// </summary>
    public T? Get<T>() where T : Subsystem, new()
    {
        if (_subsystems.TryGetValue(typeof(T), out var existing))
            return (T)existing;

        var instance = new T();
        if (!instance.ShouldCreate()) return null;

        _subsystems[typeof(T)] = instance;
        Configure?.Invoke(instance);

        instance.Initialize();
        instance.IsInitialized = true;
        return instance;
    }

    /// <summary>
    /// Runs after a subsystem is constructed but before <see cref="Subsystem.Initialize"/>,
    /// giving the owner a chance to inject itself. Set by <see cref="GameInstance"/> / scene setup.
    /// </summary>
    internal Action<Subsystem>? Configure { get; set; } = _ => { };

    /// <summary>Ticks every initialised subsystem.</summary>
    internal void Tick(float dt)
    {
        foreach (var s in _subsystems.Values)
        {
            if (!s.IsInitialized) continue;
            try { s.Tick(dt); }
            catch (Exception ex) { Console.Error.WriteLine($"[Subsystem] {s.GetType().Name}.Tick threw: {ex}"); }
        }
    }

    /// <summary>Deinitialises and drops every subsystem, in reverse creation order.</summary>
    internal void Shutdown()
    {
        foreach (var s in _subsystems.Values.Reverse())
        {
            try { s.Deinitialize(); }
            catch (Exception ex) { Console.Error.WriteLine($"[Subsystem] {s.GetType().Name}.Deinitialize threw: {ex}"); }
            s.IsInitialized = false;
        }
        _subsystems.Clear();
    }
}
