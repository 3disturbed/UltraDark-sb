using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Demo.Actors;

/// <summary>
/// Wraps either a player's active pet or a wild pet as a combatant in an
/// ATB battle. Tracks the ATB gauge and exposes the acting flag.
/// </summary>
public class BattleCombatantActor : Actor
{
    // -------------------------------------------------------------------------
    // ATB
    // -------------------------------------------------------------------------

    /// <summary>Current ATB fill level (0–100).</summary>
    public float AtbGauge { get; set; } = 0f;

    /// <summary>
    /// Units of ATB filled per second (= pet Speed * 0.5).
    /// Set when the combatant is created.
    /// </summary>
    public float AtbFillRate { get; set; } = 5f;

    /// <summary>True when the ATB gauge is at 100 — the combatant is ready to act.</summary>
    public bool IsReady => AtbGauge >= 100f;

    // -------------------------------------------------------------------------
    // References
    // -------------------------------------------------------------------------

    /// <summary>True when this combatant is controlled by the player.</summary>
    public bool IsPlayer { get; set; } = false;

    /// <summary>The pet whose stats back this combatant. May be null for the player's stand-alone turn.</summary>
    public PetActor? Pet { get; set; }

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public BattleCombatantActor(PetActor pet, bool isPlayer) : base($"Combatant_{pet.PetName}")
    {
        Pet          = pet;
        IsPlayer     = isPlayer;
        AtbFillRate  = pet.Speed * 0.5f;
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------
    protected override void Update(float dt)
    {
        if (!IsReady && (Pet == null || !Pet.IsFainted))
        {
            AtbGauge = Math.Min(100f, AtbGauge + AtbFillRate * dt * 10f);
        }
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resets the ATB gauge to zero after the combatant takes an action.
    /// </summary>
    public void ResetGauge()
    {
        AtbGauge = 0f;
    }
}
