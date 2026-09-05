using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Gameplay;

/// <summary>
/// The one object that outlives every scene — the natural home for save data, the active
/// player profile, matchmaking state and anything else that must survive a level change.
/// Modelled on Unreal's <c>UGameInstance</c>.
/// </summary>
/// <remarks>
/// Exactly one game instance exists per process. <see cref="SBEngine"/> creates a plain
/// <see cref="GameInstance"/> at startup unless the game supplies its own subclass through
/// <see cref="EngineConfig.GameInstanceFactory"/>.
/// </remarks>
/// <example>
/// <code>
/// public class MyGameInstance : GameInstance
/// {
///     public string PlayerName = "Player";
///     public override void OnStart() =&gt; Console.WriteLine("Session begins");
/// }
///
/// SBEngine.Run(new EngineConfig { GameInstanceFactory = () =&gt; new MyGameInstance() });
/// </code>
/// </example>
public class GameInstance
{
    /// <summary>The active game instance. Never null once the engine has initialised.</summary>
    public static GameInstance Current { get; internal set; } = null!;

    /// <summary>Subsystems scoped to the whole session.</summary>
    public SubsystemCollection Subsystems { get; } = new();

    /// <summary>Raised after <see cref="OnStart"/> completes.</summary>
    public SBEvent Started { get; } = new();

    /// <summary>Raised just before <see cref="OnShutdown"/> runs.</summary>
    public SBEvent ShuttingDown { get; } = new();

    /// <summary>Raised whenever a scene finishes loading, with the new scene.</summary>
    public SBEvent<Core.Scene> WorldChanged { get; } = new();

    internal void InternalInit()
    {
        Current = this;
        Subsystems.Configure = s =>
        {
            if (s is GameInstanceSubsystem gis) gis.GameInstance = this;
        };
    }

    /// <summary>Shorthand for <c>Subsystems.Get&lt;T&gt;()</c>.</summary>
    public T? GetSubsystem<T>() where T : GameInstanceSubsystem, new() => Subsystems.Get<T>();

    /// <summary>Called once when the engine has finished initialising, before the first scene loads.</summary>
    public virtual void OnStart() { }

    /// <summary>Called once as the engine shuts down. Flush saves here.</summary>
    public virtual void OnShutdown() { }

    /// <summary>Called every frame with the scaled delta, before scenes tick.</summary>
    public virtual void Tick(float dt) { }

    internal void InternalStart()
    {
        OnStart();
        Started.Broadcast();
    }

    internal void InternalTick(float dt)
    {
        Tick(dt);
        Subsystems.Tick(dt);
    }

    internal void InternalShutdown()
    {
        ShuttingDown.Broadcast();
        OnShutdown();
        Subsystems.Shutdown();
    }
}
