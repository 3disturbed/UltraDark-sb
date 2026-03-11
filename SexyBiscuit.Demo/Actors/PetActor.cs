using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Rendering;

namespace SexyBiscuit.Demo.Actors;

// ---------------------------------------------------------------------------
// Element enum
// ---------------------------------------------------------------------------

public enum PetElement
{
    Fire,
    Water,
    Grass,
    Electric,
    Normal,
    Shadow
}

// ---------------------------------------------------------------------------
// PetMove record
// ---------------------------------------------------------------------------

/// <summary>
/// Describes a single move a pet can use in battle.
/// </summary>
public record PetMove(
    string     Name,
    PetElement Element,
    int        BasePower,
    int        MpCost,
    float      Accuracy);

// ---------------------------------------------------------------------------
// PetActor
// ---------------------------------------------------------------------------

/// <summary>
/// Represents a pet — either a party member or a wild encounter.
/// Drives its own sprite + animation and exposes the combat API used by
/// <see cref="SexyBiscuit.Demo.Systems.BattleManager"/>.
/// </summary>
public class PetActor : Actor
{
    // -------------------------------------------------------------------------
    // Stats
    // -------------------------------------------------------------------------
    public string     PetName  { get; set; } = "Unknown";
    public string     Species  { get; set; } = "Unknown";
    public int        Level    { get; set; } = 1;
    public int        Hp       { get; set; } = 30;
    public int        MaxHp    { get; set; } = 30;
    public int        Mp       { get; set; } = 15;
    public int        MaxMp    { get; set; } = 15;
    public int        Attack   { get; set; } = 10;
    public int        Defense  { get; set; } = 8;
    public int        Speed    { get; set; } = 10;
    public PetElement Element  { get; set; } = PetElement.Normal;

    /// <summary>Whether this pet is from the player's party (false) or a wild encounter (true).</summary>
    public bool IsWild { get; set; } = false;

    public bool IsFainted => Hp <= 0;

    // -------------------------------------------------------------------------
    // Move list (maximum 4)
    // -------------------------------------------------------------------------
    public List<PetMove> Moves { get; } = new(4);

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------
    public event Action<int>? OnHpChanged;
    public event Action?      OnFainted;

    // -------------------------------------------------------------------------
    // Components
    // -------------------------------------------------------------------------
    private SpriteRenderer _renderer  = null!;
    private SpriteAnimator _animator  = null!;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public PetActor() : base("Pet")
    {
        _renderer = AddComponent<SpriteRenderer>();
        _renderer.Tint = Color.White;

        _animator = AddComponent<SpriteAnimator>();
        RegisterDefaultClips();
    }

    public PetActor(string petName, string species, PetElement element, int level = 1)
        : this()
    {
        PetName  = petName;
        Species  = species;
        Element  = element;
        Level    = level;
        Name     = petName;

        // Scale base stats with level
        MaxHp    = 20 + level * 5;
        Hp       = MaxHp;
        MaxMp    = 10 + level * 2;
        Mp       = MaxMp;
        Attack   = 8  + level * 2;
        Defense  = 6  + level * 2;
        Speed    = 8  + level * 2;
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------
    protected override void OnStart()
    {
        _animator.Play("idle");
    }

    // -------------------------------------------------------------------------
    // Animation
    // -------------------------------------------------------------------------
    private void RegisterDefaultClips()
    {
        _animator.AddClip(new AnimationClip
        {
            Name        = "idle",
            StartFrame  = 0,
            EndFrame    = 3,
            FrameWidth  = 48,
            FrameHeight = 48,
            Fps         = 6f,
            Loop        = true
        });

        _animator.AddClip(new AnimationClip
        {
            Name        = "attack",
            StartFrame  = 4,
            EndFrame    = 7,
            FrameWidth  = 48,
            FrameHeight = 48,
            Fps         = 10f,
            Loop        = false
        });

        _animator.AddClip(new AnimationClip
        {
            Name        = "hurt",
            StartFrame  = 8,
            EndFrame    = 10,
            FrameWidth  = 48,
            FrameHeight = 48,
            Fps         = 10f,
            Loop        = false
        });

        _animator.AddClip(new AnimationClip
        {
            Name        = "faint",
            StartFrame  = 11,
            EndFrame    = 15,
            FrameWidth  = 48,
            FrameHeight = 48,
            Fps         = 8f,
            Loop        = false
        });
    }

    // -------------------------------------------------------------------------
    // Combat API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reduces HP by <paramref name="amount"/>, plays the hurt animation.
    /// Triggers <see cref="Faint"/> when HP reaches zero.
    /// </summary>
    public void TakeDamage(int amount)
    {
        if (IsFainted || amount <= 0) return;

        Hp = Math.Max(0, Hp - amount);
        OnHpChanged?.Invoke(Hp);

        if (Hp <= 0)
        {
            Faint();
            return;
        }

        _animator.PlayOnce("hurt", () => _animator.Play("idle"));
    }

    /// <summary>
    /// Plays the faint animation and marks this pet as defeated.
    /// </summary>
    public void Faint()
    {
        Hp = 0;
        _animator.PlayOnce("faint", () =>
        {
            IsActive = false;
            OnFainted?.Invoke();
        });
    }

    /// <summary>
    /// Plays the attack animation. Caller handles damage calculation separately.
    /// </summary>
    public void PlayAttackAnimation(Action? onComplete = null)
    {
        _animator.PlayOnce("attack", () =>
        {
            _animator.Play("idle");
            onComplete?.Invoke();
        });
    }

    /// <summary>
    /// Adds a move if the pet has fewer than 4 moves. Returns true on success.
    /// </summary>
    public bool LearnMove(PetMove move)
    {
        if (Moves.Count >= 4) return false;
        Moves.Add(move);
        return true;
    }
}
