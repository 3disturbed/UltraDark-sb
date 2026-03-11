using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Audio;
using SexyBiscuit.Engine.Networking;
using SexyBiscuit.Engine.Physics;
using SexyBiscuit.Engine.Rendering;

namespace SexyBiscuit.Demo.Actors;

/// <summary>
/// The player-controlled character. Handles 3D movement, jumping, interaction,
/// animation, and combat damage/healing.
/// </summary>
public class PlayerActor : Actor
{
    // -------------------------------------------------------------------------
    // Stats
    // -------------------------------------------------------------------------
    public float MoveSpeed { get; set; } = 5f;
    public float JumpForce { get; set; } = 8f;
    public int   MaxHp     { get; set; } = 100;
    public int   Hp        { get; set; } = 100;
    public int   MaxMp     { get; set; } = 50;
    public int   Mp        { get; set; } = 50;

    // -------------------------------------------------------------------------
    // Components
    // -------------------------------------------------------------------------
    private CharacterController3D _controller = null!;
    private SpriteAnimator        _animator   = null!;
    private AudioSource           _audio      = null!;
    private NetworkObject         _netObj     = null!;

    // -------------------------------------------------------------------------
    // Internal state
    // -------------------------------------------------------------------------
    private bool  _isMoving;
    private float _interactCooldown;

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------
    public event Action<int>? OnHpChanged;
    public event Action<int>? OnMpChanged;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public PlayerActor() : base("Player")
    {
        Tag = "Player";

        // CharacterController3D requires Rigidbody3D — engine auto-adds it.
        _controller = AddComponent<CharacterController3D>();
        _controller.MoveSpeed = MoveSpeed;
        _controller.JumpSpeed = JumpForce;

        // Shadow sprite animator (flat ground-shadow quad)
        var shadowRenderer = AddComponent<SpriteRenderer>();
        shadowRenderer.Tint = new Color(0, 0, 0, 80);

        _animator = AddComponent<SpriteAnimator>();
        RegisterAnimationClips();

        _audio  = AddComponent<AudioSource>();
        _netObj = AddComponent<NetworkObject>();
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------
    protected override void OnStart()
    {
        _animator.Play("idle");
    }

    protected override void Update(float dt)
    {
        if (_interactCooldown > 0f)
            _interactCooldown -= dt;

        HandleMovement(dt);
        HandleJump();
        HandleInteract();
    }

    // -------------------------------------------------------------------------
    // Movement
    // -------------------------------------------------------------------------
    private void HandleMovement(float dt)
    {
        var input = SBEngine.Instance.Input;

        float moveX = input.GetAxis("MoveX");
        float moveY = input.GetAxis("MoveY");

        var move = new Vector3(moveX, 0f, moveY) * MoveSpeed;
        _controller.Move(move);

        _isMoving = move.LengthSquared() > 0.01f;
        UpdateAnimation();
    }

    private void HandleJump()
    {
        var input = SBEngine.Instance.Input;
        if (input.IsPressed("Jump") && _controller.IsGrounded)
        {
            _controller.Jump();
        }
    }

    // -------------------------------------------------------------------------
    // Interaction
    // -------------------------------------------------------------------------
    private void HandleInteract()
    {
        var input = SBEngine.Instance.Input;
        if (!input.IsPressed("Interact") || _interactCooldown > 0f)
            return;

        _interactCooldown = 0.3f;

        // Find interactable actors within a short radius ahead of the player.
        var scene = Scene;
        if (scene == null) return;

        Vector2 playerPos   = Transform.Position;
        Vector2 forwardDir  = Transform.Right;
        float   reachRadius = 2f;

        foreach (var actor in scene.FindByTag("Interactable"))
        {
            float dist = Vector2.Distance(playerPos, actor.Transform.Position);
            if (dist <= reachRadius)
            {
                // Raise event on the interactable — it handles its own logic.
                actor.OnTriggerEnter(this);
                break;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Animation
    // -------------------------------------------------------------------------
    private void UpdateAnimation()
    {
        string clip = _isMoving ? "run" : "idle";
        if (_animator.CurrentClipName != clip)
            _animator.Play(clip);
    }

    private void RegisterAnimationClips()
    {
        // Clips reference spritesheet frames. Without a real texture the
        // SpriteAnimator gracefully skips draw — clips are registered anyway
        // so Play() calls don't throw.
        _animator.AddClip(new AnimationClip
        {
            Name        = "idle",
            StartFrame  = 0,
            EndFrame    = 3,
            FrameWidth  = 64,
            FrameHeight = 64,
            Fps         = 6f,
            Loop        = true
        });

        _animator.AddClip(new AnimationClip
        {
            Name        = "run",
            StartFrame  = 4,
            EndFrame    = 11,
            FrameWidth  = 64,
            FrameHeight = 64,
            Fps         = 12f,
            Loop        = true
        });

        _animator.AddClip(new AnimationClip
        {
            Name        = "hurt",
            StartFrame  = 12,
            EndFrame    = 15,
            FrameWidth  = 64,
            FrameHeight = 64,
            Fps         = 10f,
            Loop        = false
        });
    }

    // -------------------------------------------------------------------------
    // Public combat API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies damage to the player, triggers a camera shake and hurt animation.
    /// </summary>
    public void TakeDamage(int amount)
    {
        if (amount <= 0) return;

        Hp = Math.Max(0, Hp - amount);
        OnHpChanged?.Invoke(Hp);

        // Play hurt sound
        _audio.Play();

        // Camera shake — find Camera2D in scene
        var scene = Scene;
        if (scene != null)
        {
            foreach (var camActor in scene.FindByTag("Camera"))
            {
                var cam2d = camActor.GetComponent<SexyBiscuit.Engine.Rendering.Camera2D>();
                cam2d?.Shake(0.4f, 0.35f);
                break;
            }
        }

        // Play hurt animation (one-shot), then return to idle
        _animator.PlayOnce("hurt", () =>
        {
            if (_isMoving) _animator.Play("run");
            else           _animator.Play("idle");
        });
    }

    /// <summary>
    /// Heals the player up to MaxHp.
    /// </summary>
    public void Heal(int amount)
    {
        if (amount <= 0) return;
        Hp = Math.Min(MaxHp, Hp + amount);
        OnHpChanged?.Invoke(Hp);
    }

    // -------------------------------------------------------------------------
    // Collision
    // -------------------------------------------------------------------------
    public override void OnCollisionEnter(CollisionData data)
    {
        base.OnCollisionEnter(data);

        // Battle trigger zones are tagged "BattleTrigger"
        if (data.Other.Tag == "BattleTrigger")
        {
            // The WildPetActor trigger handles the actual battle start.
        }
    }
}
