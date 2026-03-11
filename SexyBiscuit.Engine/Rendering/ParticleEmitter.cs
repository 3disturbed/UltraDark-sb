using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

// =============================================================================
// Particle struct
// =============================================================================

/// <summary>
/// Data for a single live particle. Kept as a struct so the pool stays
/// contiguous in memory and avoids GC pressure.
/// </summary>
public struct Particle
{
    public Vector2 Position;
    public Vector2 Velocity;
    public Vector2 Acceleration;

    public Color StartColor;
    public Color EndColor;

    /// <summary>Total lifespan in seconds.</summary>
    public float Lifetime;

    /// <summary>How long the particle has been alive (seconds).</summary>
    public float Age;

    /// <summary>Pixel size at spawn.</summary>
    public float StartSize;

    /// <summary>Pixel size at end-of-life.</summary>
    public float EndSize;

    /// <summary>Current rotation angle in radians.</summary>
    public float Rotation;

    /// <summary>Rotation speed in radians per second.</summary>
    public float AngularVelocity;

    /// <summary>True while this slot is occupied by a live particle.</summary>
    public bool IsAlive;

    /// <summary>Normalised age 0..1 — cached each frame for convenience.</summary>
    public readonly float NormalizedAge => Lifetime > 0f ? Math.Clamp(Age / Lifetime, 0f, 1f) : 1f;
}

// =============================================================================
// ParticleEmitter component
// =============================================================================

/// <summary>
/// Component that spawns, updates and draws a pool of 2D particles.
/// Supports continuous emission, burst mode, per-particle colour and size
/// interpolation, angular velocity, gravity, and custom textures.
/// </summary>
public class ParticleEmitter : Component
{
    // -------------------------------------------------------------------------
    // Pool
    // -------------------------------------------------------------------------

    private Particle[] _pool = Array.Empty<Particle>();
    private int _liveCount;
    private float _emitAccumulator;

    // -------------------------------------------------------------------------
    // Emitter settings — pool size
    // -------------------------------------------------------------------------

    private int _maxParticles = 500;

    /// <summary>Maximum number of simultaneously live particles. Resizes the pool when changed.</summary>
    public int MaxParticles
    {
        get => _maxParticles;
        set
        {
            if (value == _maxParticles) return;
            _maxParticles = Math.Max(1, value);
            ResizePool();
        }
    }

    // -------------------------------------------------------------------------
    // Emission settings
    // -------------------------------------------------------------------------

    /// <summary>Particles spawned per second in continuous mode.</summary>
    public float EmitRate { get; set; } = 20f;

    /// <summary>Whether the emitter loops continuously.</summary>
    public bool Loop { get; set; } = true;

    /// <summary>Whether to emit all BurstCount particles at once on Play/enable.</summary>
    public bool BurstMode { get; set; } = false;

    /// <summary>Number of particles emitted in a single burst.</summary>
    public int BurstCount { get; set; } = 50;

    /// <summary>True while the emitter is actively spawning particles.</summary>
    public bool IsPlaying { get; private set; } = true;

    // -------------------------------------------------------------------------
    // Per-particle settings
    // -------------------------------------------------------------------------

    public float MinLifetime { get; set; } = 0.5f;
    public float MaxLifetime { get; set; } = 2f;

    public Vector2 MinVelocity { get; set; } = new Vector2(-50f, -100f);
    public Vector2 MaxVelocity { get; set; } = new Vector2( 50f, -200f);

    public Color StartColor { get; set; } = Color.White;
    public Color EndColor   { get; set; } = Color.Transparent;

    public float MinStartSize { get; set; } = 4f;
    public float MaxStartSize { get; set; } = 8f;
    public float EndSize      { get; set; } = 0f;

    /// <summary>Gravity multiplier. 0 = no gravity. Uses world gravity of 980 px/s².</summary>
    public float GravityScale { get; set; } = 0f;

    /// <summary>Constant world gravity in pixels/second². Scaled by GravityScale per particle.</summary>
    public float WorldGravity { get; set; } = 980f;

    /// <summary>Texture used to draw particles. If null a 1×1 white pixel is simulated via a unit rectangle.</summary>
    public Texture2D? ParticleTexture { get; set; }

    // -------------------------------------------------------------------------
    // Randomness
    // -------------------------------------------------------------------------

    private static readonly Random _rng = new Random();

    // -------------------------------------------------------------------------
    // Awake / initialisation
    // -------------------------------------------------------------------------

    public override void Awake()
    {
        ResizePool();

        if (BurstMode && IsPlaying)
            Burst();
    }

    // -------------------------------------------------------------------------
    // Public control API
    // -------------------------------------------------------------------------

    /// <summary>Start or resume emission.</summary>
    public void Play()
    {
        IsPlaying = true;
        if (BurstMode)
            Burst();
    }

    /// <summary>Stop emitting new particles (live particles continue to update).</summary>
    public void Stop() => IsPlaying = false;

    /// <summary>Stop emission and kill all live particles immediately.</summary>
    public void Clear()
    {
        IsPlaying = false;
        for (int i = 0; i < _pool.Length; i++)
            _pool[i].IsAlive = false;
        _liveCount = 0;
        _emitAccumulator = 0f;
    }

    /// <summary>Emit <see cref="BurstCount"/> particles immediately at the actor's current position.</summary>
    public void Burst()
    {
        for (int i = 0; i < BurstCount; i++)
            SpawnParticle();
    }

    // -------------------------------------------------------------------------
    // Update
    // -------------------------------------------------------------------------

    public override void Update(float dt)
    {
        Vector2 gravity = new Vector2(0f, WorldGravity * GravityScale);

        // Update live particles
        _liveCount = 0;
        for (int i = 0; i < _pool.Length; i++)
        {
            ref Particle p = ref _pool[i];
            if (!p.IsAlive) continue;

            p.Age += dt;
            if (p.Age >= p.Lifetime)
            {
                p.IsAlive = false;
                continue;
            }

            // Physics
            p.Velocity   += (p.Acceleration + gravity) * dt;
            p.Position   += p.Velocity * dt;
            p.Rotation   += p.AngularVelocity * dt;

            _liveCount++;
        }

        // Continuous emission
        if (IsPlaying && !BurstMode && Loop)
        {
            _emitAccumulator += EmitRate * dt;
            int toSpawn = (int)_emitAccumulator;
            _emitAccumulator -= toSpawn;

            for (int i = 0; i < toSpawn; i++)
                SpawnParticle();
        }
    }

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    public override void Draw(SpriteBatch sb)
    {
        if (ParticleTexture == null) return;

        for (int i = 0; i < _pool.Length; i++)
        {
            ref Particle p = ref _pool[i];
            if (!p.IsAlive) continue;

            float t    = p.NormalizedAge;
            Color col  = Color.Lerp(p.StartColor, p.EndColor, t);
            float size = MathHelper.Lerp(p.StartSize, p.EndSize, t);

            if (size <= 0f || col.A == 0) continue;

            // Scale is uniform; origin is centre of the texture
            float scale = size / MathF.Max(ParticleTexture.Width, ParticleTexture.Height);
            Vector2 origin = new Vector2(ParticleTexture.Width * 0.5f, ParticleTexture.Height * 0.5f);

            sb.Draw(
                ParticleTexture,
                p.Position,
                null,
                col,
                p.Rotation,
                origin,
                scale,
                SpriteEffects.None,
                0f);
        }
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private void SpawnParticle()
    {
        // Find a dead slot
        for (int i = 0; i < _pool.Length; i++)
        {
            if (_pool[i].IsAlive) continue;

            ref Particle p = ref _pool[i];

            p.IsAlive          = true;
            p.Position         = Actor.Transform.Position;
            p.Age              = 0f;
            p.Lifetime         = MathHelper.Lerp(MinLifetime, MaxLifetime, (float)_rng.NextDouble());
            p.StartColor       = StartColor;
            p.EndColor         = EndColor;
            p.StartSize        = MathHelper.Lerp(MinStartSize, MaxStartSize, (float)_rng.NextDouble());
            p.EndSize          = EndSize;
            p.Rotation         = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            p.AngularVelocity  = (float)((_rng.NextDouble() - 0.5) * MathHelper.TwoPi);
            p.Acceleration     = Vector2.Zero;

            // Random velocity within the min/max range
            p.Velocity = new Vector2(
                MathHelper.Lerp(MinVelocity.X, MaxVelocity.X, (float)_rng.NextDouble()),
                MathHelper.Lerp(MinVelocity.Y, MaxVelocity.Y, (float)_rng.NextDouble()));

            return; // one particle per call
        }
        // Pool is full — silently drop the spawn request
    }

    private void ResizePool()
    {
        int oldLen = _pool.Length;
        Array.Resize(ref _pool, _maxParticles);

        // Initialise any newly allocated slots
        for (int i = oldLen; i < _maxParticles; i++)
            _pool[i] = new Particle { IsAlive = false };
    }
}
