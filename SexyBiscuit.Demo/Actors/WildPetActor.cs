using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Physics;
using SexyBiscuit.Engine.Scripting;
using SexyBiscuit.Demo.Systems;

namespace SexyBiscuit.Demo.Actors;

/// <summary>
/// An overworld representation of a wild pet. Wanders randomly, detects the
/// player through a trigger zone and initiates a battle encounter.
/// </summary>
public class WildPetActor : Actor
{
    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    /// <summary>The underlying pet data this overworld actor represents.</summary>
    public PetActor PetData { get; }

    /// <summary>Set to true once the player has captured this pet — the actor then destroys itself.</summary>
    public bool AlreadyCaptured { get; set; } = false;

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fired when the player walks into the encounter trigger zone.
    /// Arguments: (player, this).
    /// </summary>
    public event Action<PlayerActor, WildPetActor>? OnPlayerEncounter;

    // -------------------------------------------------------------------------
    // Wander state
    // -------------------------------------------------------------------------
    private Vector2 _wanderDirection = Vector2.UnitX;
    private float   _wanderTimer;
    private const float WanderSpeed      = 1.5f;
    private const float WanderMinTime    = 1.5f;
    private const float WanderMaxTime    = 3.5f;
    private bool        _encounterFired  = false;

    // -------------------------------------------------------------------------
    // Components
    // -------------------------------------------------------------------------
    private CircleCollider2D _triggerZone = null!;
    private ScriptComponent  _aiScript    = null!;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public WildPetActor(PetActor petData) : base($"Wild_{petData.PetName}")
    {
        PetData = petData;
        Tag     = "WildPet";

        // Wander trigger zone
        _triggerZone           = AddComponent<CircleCollider2D>();
        _triggerZone.Radius    = 3f;
        _triggerZone.IsTrigger = true;

        // Optional JS wander script (same behaviour is also implemented in C# below)
        _aiScript            = AddComponent<ScriptComponent>();
        _aiScript.ScriptPath = "Scripts/ai_wander.js";
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------
    protected override void OnStart()
    {
        PickNewWanderDirection();
    }

    protected override void Update(float dt)
    {
        if (AlreadyCaptured)
        {
            Destroy();
            return;
        }

        UpdateWander(dt);
    }

    // -------------------------------------------------------------------------
    // Wander logic
    // -------------------------------------------------------------------------
    private void UpdateWander(float dt)
    {
        _wanderTimer -= dt;
        if (_wanderTimer <= 0f)
            PickNewWanderDirection();

        var currentPos = Transform.Position;
        Transform.Position = currentPos + _wanderDirection * WanderSpeed * dt;
    }

    private void PickNewWanderDirection()
    {
        float angle = (float)(Random.Shared.NextDouble() * Math.PI * 2.0);
        _wanderDirection = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        _wanderTimer     = WanderMinTime + (float)(Random.Shared.NextDouble() * (WanderMaxTime - WanderMinTime));
    }

    // -------------------------------------------------------------------------
    // Trigger — battle encounter
    // -------------------------------------------------------------------------
    public override void OnTriggerEnter(Actor other)
    {
        base.OnTriggerEnter(other);

        if (_encounterFired) return;
        if (AlreadyCaptured)  return;

        if (other is PlayerActor player)
        {
            _encounterFired = true;
            OnPlayerEncounter?.Invoke(player, this);
            BattleManager.StartBattle(player, this);
        }
    }
}
