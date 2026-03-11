using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Demo.Actors;

namespace SexyBiscuit.Demo.Systems;

// ---------------------------------------------------------------------------
// Battle state
// ---------------------------------------------------------------------------

public enum BattleState
{
    Inactive,
    PlayerTurn,
    EnemyTurn,
    Capture,
    Victory,
    Defeat
}

// ---------------------------------------------------------------------------
// BattleManager
// ---------------------------------------------------------------------------

/// <summary>
/// Static controller for the ATB battle system.
/// Call <see cref="StartBattle"/> from the overworld when an encounter triggers.
/// </summary>
public static class BattleManager
{
    // -------------------------------------------------------------------------
    // Public state
    // -------------------------------------------------------------------------
    public static BattleState State { get; private set; } = BattleState.Inactive;

    public static List<BattleCombatantActor> Combatants { get; } = new();

    /// <summary>The combatant whose ATB gauge is full and is currently choosing an action.</summary>
    public static BattleCombatantActor? ActiveCombatant { get; set; }

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------
    public static event Action<bool, PetActor?>? OnCaptureResult;
    public static event Action<bool>?            OnBattleEnded;

    // -------------------------------------------------------------------------
    // Internal references
    // -------------------------------------------------------------------------
    private static PlayerActor?  _player;
    private static WildPetActor? _wildPetActor;
    private static bool          _actingInProgress;

    // -------------------------------------------------------------------------
    // Start / End
    // -------------------------------------------------------------------------

    /// <summary>
    /// Freezes the overworld, loads the battle scene additively, and sets up combatants.
    /// </summary>
    public static void StartBattle(PlayerActor player, WildPetActor wildPet)
    {
        if (State != BattleState.Inactive) return;

        _player       = player;
        _wildPetActor = wildPet;
        _actingInProgress = false;

        // Freeze overworld — disable player input by deactivating the actor
        player.IsActive = false;

        // Load battle scene additively so overworld stays in memory
        SBEngine.Instance.SceneManager.LoadSceneAdditive("Scenes/BattleScene");

        // Build combatants from the player's lead party pet and the wild pet
        Combatants.Clear();
        ActiveCombatant = null;

        var leadPet = PartyManager.GetLead();
        if (leadPet != null)
        {
            var playerCombatant = new BattleCombatantActor(leadPet, isPlayer: true);
            playerCombatant.AtbGauge = 0f;
            Combatants.Add(playerCombatant);
        }

        var enemyCombatant = new BattleCombatantActor(wildPet.PetData, isPlayer: false);
        enemyCombatant.AtbGauge = 0f;
        Combatants.Add(enemyCombatant);

        State = BattleState.PlayerTurn;
    }

    /// <summary>
    /// Called every frame by the battle scene to tick ATB gauges and set the active combatant.
    /// </summary>
    public static void Update(float dt)
    {
        if (State == BattleState.Inactive || State == BattleState.Victory || State == BattleState.Defeat)
            return;

        if (_actingInProgress) return;

        // Advance all ATB gauges
        foreach (var combatant in Combatants)
        {
            if (combatant.Pet == null || combatant.Pet.IsFainted) continue;
            if (!combatant.IsReady)
                combatant.AtbGauge = Math.Min(100f, combatant.AtbGauge + combatant.AtbFillRate * dt * 10f);
        }

        // Find the first ready combatant if none is active
        if (ActiveCombatant == null)
        {
            foreach (var combatant in Combatants)
            {
                if (combatant.IsReady && (combatant.Pet == null || !combatant.Pet.IsFainted))
                {
                    ActiveCombatant = combatant;

                    if (combatant.IsPlayer)
                        State = BattleState.PlayerTurn;
                    else
                    {
                        State = BattleState.EnemyTurn;
                        ExecuteEnemyTurnAI();
                    }
                    break;
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Execute actions
    // -------------------------------------------------------------------------

    /// <summary>
    /// Executes the chosen move from <paramref name="attacker"/> against <paramref name="target"/>.
    /// </summary>
    public static void ExecuteMove(PetMove move, BattleCombatantActor attacker, BattleCombatantActor target)
    {
        if (attacker.Pet == null || target.Pet == null) return;
        if (attacker.Pet.Mp < move.MpCost) return;

        _actingInProgress = true;

        // Accuracy check
        if (Random.Shared.NextSingle() > move.Accuracy)
        {
            // Missed — reset gauge and clear active combatant
            attacker.ResetGauge();
            ActiveCombatant = null;
            _actingInProgress = false;
            return;
        }

        attacker.Pet.Mp = Math.Max(0, attacker.Pet.Mp - move.MpCost);

        float rawDamage = CalculateDamage(move, attacker, target);
        int   damage    = Math.Max(1, (int)rawDamage);

        // Play attack animation then apply damage
        attacker.Pet.PlayAttackAnimation(() =>
        {
            target.Pet?.TakeDamage(damage);

            attacker.ResetGauge();
            ActiveCombatant = null;
            _actingInProgress = false;

            CheckBattleEnd();
        });
    }

    /// <summary>
    /// Calculates damage using the standard formula with elemental multiplier.
    /// </summary>
    public static float CalculateDamage(
        PetMove move,
        BattleCombatantActor attacker,
        BattleCombatantActor target)
    {
        if (attacker.Pet == null || target.Pet == null) return 0f;

        float elementMultiplier = GetElementMultiplier(move.Element, target.Pet.Element);
        float randomFactor      = 0.85f + Random.Shared.NextSingle() * 0.15f; // 0.85 – 1.0

        float damage = ((2f * attacker.Pet.Level / 5f + 2f)
                        * move.BasePower
                        * attacker.Pet.Attack
                        / (float)Math.Max(1, target.Pet.Defense)
                        / 50f
                        + 2f)
                       * elementMultiplier
                       * randomFactor;

        return damage;
    }

    /// <summary>
    /// Returns the elemental damage multiplier (2x, 0.5x, or 1x).
    /// </summary>
    public static float GetElementMultiplier(PetElement attacker, PetElement defender)
    {
        return (attacker, defender) switch
        {
            // Fire beats Grass; Water beats Fire; Grass beats Water
            (PetElement.Fire,    PetElement.Grass)    => 2.0f,
            (PetElement.Fire,    PetElement.Water)    => 0.5f,
            (PetElement.Water,   PetElement.Fire)     => 2.0f,
            (PetElement.Water,   PetElement.Grass)    => 0.5f,
            (PetElement.Grass,   PetElement.Water)    => 2.0f,
            (PetElement.Grass,   PetElement.Fire)     => 0.5f,
            // Electric is neutral against most; resisted by itself
            (PetElement.Electric, PetElement.Electric) => 0.5f,
            // Shadow hits Normal for extra damage
            (PetElement.Shadow,  PetElement.Normal)   => 2.0f,
            // Shadow resists Shadow
            (PetElement.Shadow,  PetElement.Shadow)   => 0.5f,
            // Anything vs Shadow deals reduced damage
            (_,                  PetElement.Shadow)   => 0.5f,
            // Default: no modifier
            _                                          => 1.0f
        };
    }

    // -------------------------------------------------------------------------
    // Capture
    // -------------------------------------------------------------------------

    /// <summary>
    /// Attempts to capture the wild pet using the specified capture item's ball multiplier.
    /// </summary>
    public static void AttemptCapture(PlayerActor player, WildPetActor target, float ballMultiplier = 1f)
    {
        if (target.PetData == null) return;

        State = BattleState.Capture;

        int   maxHp        = target.PetData.MaxHp;
        int   currentHp    = target.PetData.Hp;
        float baseCatchRate = 0.5f; // default base rate

        float captureRate =
            (float)(3 * maxHp - 2 * currentHp) / (3f * Math.Max(1, maxHp))
            * baseCatchRate
            * ballMultiplier;

        captureRate = Math.Min(captureRate, 0.95f);

        bool success = Random.Shared.NextSingle() < captureRate;

        if (success)
        {
            target.AlreadyCaptured = true;
            PartyManager.AddToParty(target.PetData);
            OnCaptureResult?.Invoke(true, target.PetData);
            EndBattle(playerWon: true);
        }
        else
        {
            OnCaptureResult?.Invoke(false, null);
            // Return to PlayerTurn after failed capture
            ActiveCombatant = null;
            State = BattleState.PlayerTurn;
        }
    }

    // -------------------------------------------------------------------------
    // Battle end
    // -------------------------------------------------------------------------

    /// <summary>
    /// Cleans up the battle, unloads the battle scene and returns to the overworld.
    /// </summary>
    public static void EndBattle(bool playerWon)
    {
        State = playerWon ? BattleState.Victory : BattleState.Defeat;

        Combatants.Clear();
        ActiveCombatant    = null;
        _actingInProgress  = false;

        // Unload battle scene
        SBEngine.Instance.SceneManager.UnloadScene("BattleScene");

        // Re-enable player
        if (_player != null)
            _player.IsActive = true;

        if (!playerWon)
        {
            // Heal all pets at defeat (equivalent to a game-over clinic visit)
            PartyManager.HealAll();
        }

        OnBattleEnded?.Invoke(playerWon);

        State       = BattleState.Inactive;
        _player     = null;
        _wildPetActor = null;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static void ExecuteEnemyTurnAI()
    {
        var enemy = ActiveCombatant;
        if (enemy?.Pet == null || enemy.Pet.Moves.Count == 0) return;

        // Simple AI: pick a random move, target the player's lead pet
        var playerCombatant = Combatants.FirstOrDefault(c => c.IsPlayer);
        if (playerCombatant == null) { enemy.ResetGauge(); ActiveCombatant = null; return; }

        var move = enemy.Pet.Moves[Random.Shared.Next(enemy.Pet.Moves.Count)];
        ExecuteMove(move, enemy, playerCombatant);
    }

    private static void CheckBattleEnd()
    {
        if (PartyManager.AllFainted())
        {
            EndBattle(playerWon: false);
            return;
        }

        bool enemyFainted = Combatants
            .Where(c => !c.IsPlayer)
            .All(c => c.Pet == null || c.Pet.IsFainted);

        if (enemyFainted)
        {
            EndBattle(playerWon: true);
        }
    }
}
