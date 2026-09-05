using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Gameplay;

/// <summary>
/// Base class for anything that can possess a <see cref="Pawn"/> and drive its behaviour.
/// Modelled on Unreal's <c>AController</c>.
/// </summary>
/// <remarks>
/// Controllers are actors in their own right and are not destroyed when their pawn dies —
/// that separation is what lets a player respawn into a new body while keeping their score,
/// input bindings and camera state.
/// </remarks>
public abstract class Controller : Actor
{
    /// <summary>The pawn this controller currently drives, or null.</summary>
    public Pawn? ControlledPawn { get; private set; }

    /// <summary>Raised after this controller possesses a pawn.</summary>
    public SBEvent<Pawn> Possessed { get; } = new();

    /// <summary>Raised after this controller releases a pawn.</summary>
    public SBEvent<Pawn> UnPossessed { get; } = new();

    protected Controller() { }
    protected Controller(string name) : base(name) { }

    /// <summary>
    /// Takes control of <paramref name="pawn"/>, releasing any pawn already possessed and
    /// evicting <paramref name="pawn"/>'s previous controller if it had one.
    /// </summary>
    public void Possess(Pawn pawn)
    {
        ArgumentNullException.ThrowIfNull(pawn);
        if (ReferenceEquals(ControlledPawn, pawn)) return;

        if (ControlledPawn != null) UnPossess();
        pawn.Controller?.UnPossess();

        ControlledPawn = pawn;
        pawn.Controller = this;

        OnPossess(pawn);
        pawn.OnPossessed(this);
        Possessed.Broadcast(pawn);
    }

    /// <summary>Releases the current pawn. Does nothing when no pawn is possessed.</summary>
    public void UnPossess()
    {
        var pawn = ControlledPawn;
        if (pawn == null) return;

        ControlledPawn = null;
        pawn.Controller = null;

        OnUnPossess(pawn);
        pawn.OnUnPossessed(this);
        UnPossessed.Broadcast(pawn);
    }

    /// <summary>Called after possession. Cache pawn components here.</summary>
    protected virtual void OnPossess(Pawn pawn) { }

    /// <summary>Called after the pawn is released. Drop cached references here.</summary>
    protected virtual void OnUnPossess(Pawn pawn) { }

    protected override void OnDestroy() => UnPossess();
}
