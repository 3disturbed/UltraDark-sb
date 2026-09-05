using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>Shape of the volume new particles are emitted from.</summary>
public enum EmitterShape3D
{
    /// <summary>All particles start at the emitter's origin.</summary>
    Point,

    /// <summary>Particles start at a random point inside a sphere of <see cref="ParticleSystem3D.ShapeRadius"/>.</summary>
    Sphere,

    /// <summary>Particles start inside a box of <see cref="ParticleSystem3D.ShapeSize"/>.</summary>
    Box,

    /// <summary>Particles start inside a cone opening along the emitter's forward axis.</summary>
    Cone,
}

/// <summary>How each particle is drawn.</summary>
public enum ParticleRenderMode3D
{
    /// <summary>A camera-facing textured quad.</summary>
    Billboard,

    /// <summary>A quad that faces the camera but keeps its up axis vertical — good for smoke and fire.</summary>
    VerticalBillboard,

    /// <summary>A copy of <see cref="ParticleSystem3D.ParticleMesh"/> per particle.</summary>
    Mesh,
}

/// <summary>
/// A 3D particle emitter: spawns, simulates and draws camera-facing quads or mesh copies.
/// </summary>
/// <remarks>
/// <para>
/// Particles live in a fixed-size pool sized by <see cref="MaxParticles"/> and are never
/// individually allocated, so a long-running emitter produces no garbage. When the pool is
/// full, new emissions are dropped rather than growing the buffer — a burst that would
/// exceed the budget is visibly clipped instead of quietly costing frame time.
/// </para>
/// <para>
/// Billboards are built into one dynamic vertex buffer and drawn in a single call, so
/// thousands of particles cost one draw. Mesh particles cost one draw each through
/// <see cref="BasicEffect"/>; for large counts supply a hardware-instancing shader and drive
/// it from <see cref="EnumerateParticles"/> instead.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var fx = actor.AddComponent&lt;ParticleSystem3D&gt;();
/// fx.Texture       = smokeTexture;
/// fx.EmissionRate  = 40f;
/// fx.Shape         = EmitterShape3D.Cone;
/// fx.ConeAngle     = 20f;
/// fx.StartColor    = Color.White;
/// fx.EndColor      = Color.Transparent;
/// fx.Play();
/// </code>
/// </example>
public sealed class ParticleSystem3D : Component
{
    /// <summary>Every live 3D emitter.</summary>
    public static readonly List<ParticleSystem3D> All = new();

    /// <summary>One simulated particle. Exposed for custom rendering paths.</summary>
    public struct Particle
    {
        /// <summary>World-space position.</summary>
        public Vector3 Position;

        /// <summary>World-space velocity in units per second.</summary>
        public Vector3 Velocity;

        /// <summary>Seconds this particle has existed.</summary>
        public float Age;

        /// <summary>Total seconds this particle will live.</summary>
        public float Lifetime;

        /// <summary>Rotation about the view axis, in radians.</summary>
        public float Rotation;

        /// <summary>Rotation rate in radians per second.</summary>
        public float AngularVelocity;

        /// <summary>Size at spawn, in world units.</summary>
        public float StartSize;

        /// <summary>Colour at spawn.</summary>
        public Color StartTint;

        /// <summary>False for a free slot in the pool.</summary>
        public bool Alive;

        /// <summary>Fraction of the particle's life elapsed, 0 at spawn and 1 at death.</summary>
        public readonly float NormalizedAge => Lifetime <= 0f ? 1f : MathHelper.Clamp(Age / Lifetime, 0f, 1f);
    }

    // -------------------------------------------------------------------------
    // Emission
    // -------------------------------------------------------------------------

    /// <summary>Upper bound on live particles. Changing it reallocates the pool.</summary>
    public int MaxParticles
    {
        get => _maxParticles;
        set
        {
            int clamped = Math.Clamp(value, 1, 100_000);
            if (clamped == _maxParticles) return;
            _maxParticles = clamped;
            _particles = new Particle[_maxParticles];
            _aliveCount = 0;
        }
    }

    /// <summary>Particles spawned per second while playing.</summary>
    public float EmissionRate { get; set; } = 20f;

    /// <summary>Volume new particles spawn inside.</summary>
    public EmitterShape3D Shape { get; set; } = EmitterShape3D.Point;

    /// <summary>Radius for <see cref="EmitterShape3D.Sphere"/> and the base of a cone.</summary>
    public float ShapeRadius { get; set; } = 0.5f;

    /// <summary>Full extents for <see cref="EmitterShape3D.Box"/>.</summary>
    public Vector3 ShapeSize { get; set; } = Vector3.One;

    /// <summary>Half-angle of the emission cone in degrees.</summary>
    public float ConeAngle { get; set; } = 25f;

    /// <summary>Emitter stops after this many seconds. Zero runs forever.</summary>
    public float Duration { get; set; }

    /// <summary>Restarts the emitter when <see cref="Duration"/> elapses.</summary>
    public bool Looping { get; set; } = true;

    // -------------------------------------------------------------------------
    // Particle properties
    // -------------------------------------------------------------------------

    /// <summary>Shortest particle lifetime in seconds.</summary>
    public float MinLifetime { get; set; } = 1f;

    /// <summary>Longest particle lifetime in seconds.</summary>
    public float MaxLifetime { get; set; } = 2f;

    /// <summary>Slowest launch speed in units per second.</summary>
    public float MinSpeed { get; set; } = 1f;

    /// <summary>Fastest launch speed in units per second.</summary>
    public float MaxSpeed { get; set; } = 3f;

    /// <summary>Smallest spawn size in world units.</summary>
    public float MinSize { get; set; } = 0.2f;

    /// <summary>Largest spawn size in world units.</summary>
    public float MaxSize { get; set; } = 0.5f;

    /// <summary>Size multiplier applied at the end of a particle's life. 0 shrinks to nothing.</summary>
    public float EndSizeMultiplier { get; set; } = 1f;

    /// <summary>Tint at spawn.</summary>
    public Color StartColor { get; set; } = Color.White;

    /// <summary>Tint at death. Alpha 0 fades the particle out.</summary>
    public Color EndColor { get; set; } = Color.Transparent;

    /// <summary>Constant acceleration applied to every particle, in units per second squared.</summary>
    public Vector3 Gravity { get; set; } = new(0f, -2f, 0f);

    /// <summary>Fraction of velocity shed per second. 0 keeps momentum, 1 stops almost immediately.</summary>
    public float Drag { get; set; }

    /// <summary>Maximum spin rate at spawn, in degrees per second.</summary>
    public float MaxSpinDegrees { get; set; } = 90f;

    // -------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------

    /// <summary>How particles are drawn.</summary>
    public ParticleRenderMode3D RenderMode { get; set; } = ParticleRenderMode3D.Billboard;

    /// <summary>Sprite used for billboard particles. A white quad is used when null.</summary>
    public Texture2D? Texture { get; set; }

    /// <summary>Mesh drawn per particle in <see cref="ParticleRenderMode3D.Mesh"/> mode.</summary>
    public MeshRenderer? ParticleMesh { get; set; }

    /// <summary>Additive blending suits fire and sparks; alpha suits smoke and debris.</summary>
    public bool AdditiveBlend { get; set; }

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    /// <summary>True while the emitter is spawning particles.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>Number of particles currently alive.</summary>
    public int AliveCount => _aliveCount;

    private int         _maxParticles = 512;
    private Particle[]  _particles = new Particle[512];
    private int         _aliveCount;
    private float       _emitAccumulator;
    private float       _elapsed;

    private Transform3D?          _t3d;
    private DynamicVertexBuffer?  _billboardVertices;
    private IndexBuffer?          _billboardIndices;
    private BasicEffect?          _billboardEffect;
    private Texture2D?            _whitePixel;
    private VertexPositionColorTexture[] _vertexScratch = Array.Empty<VertexPositionColorTexture>();

    private Transform3D T3D => _t3d ??= Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();

    public override void Awake() => All.Add(this);

    // -------------------------------------------------------------------------
    // Control
    // -------------------------------------------------------------------------

    /// <summary>Starts emitting.</summary>
    public void Play()
    {
        IsPlaying = true;
        _elapsed  = 0f;
    }

    /// <summary>Stops emitting. Particles already alive finish their lifetimes.</summary>
    public void Stop() => IsPlaying = false;

    /// <summary>Stops emitting and kills every live particle immediately.</summary>
    public void Clear()
    {
        IsPlaying   = false;
        _aliveCount = 0;
        Array.Clear(_particles);
    }

    /// <summary>Spawns <paramref name="count"/> particles at once, ignoring <see cref="EmissionRate"/>.</summary>
    public void Burst(int count)
    {
        for (int i = 0; i < count; i++) SpawnOne();
    }

    // -------------------------------------------------------------------------
    // Simulation
    // -------------------------------------------------------------------------

    public override void Update(float dt)
    {
        if (IsPlaying)
        {
            _elapsed += dt;

            if (Duration > 0f && _elapsed >= Duration)
            {
                if (Looping) _elapsed = 0f;
                else         IsPlaying = false;
            }

            if (IsPlaying && EmissionRate > 0f)
            {
                _emitAccumulator += EmissionRate * dt;
                while (_emitAccumulator >= 1f)
                {
                    SpawnOne();
                    _emitAccumulator -= 1f;
                }
            }
        }

        // Compact the pool as we go: swap dead particles with the last live one so the
        // live range stays contiguous and iteration never checks dead slots.
        float dragFactor = Drag > 0f ? MathF.Max(0f, 1f - Drag * dt) : 1f;

        for (int i = 0; i < _aliveCount; i++)
        {
            ref var p = ref _particles[i];

            p.Age += dt;
            if (p.Age >= p.Lifetime)
            {
                _particles[i] = _particles[_aliveCount - 1];
                _aliveCount--;
                i--;
                continue;
            }

            p.Velocity += Gravity * dt;
            if (Drag > 0f) p.Velocity *= dragFactor;

            p.Position += p.Velocity * dt;
            p.Rotation += p.AngularVelocity * dt;
        }
    }

    private void SpawnOne()
    {
        if (_aliveCount >= _maxParticles) return;

        var origin    = T3D.Position;
        var forward   = T3D.Forward;
        var spawnPos  = origin;
        var direction = SBMath.RandomOnUnitSphere();

        switch (Shape)
        {
            case EmitterShape3D.Sphere:
                spawnPos = origin + SBMath.RandomInUnitSphere() * ShapeRadius;
                break;

            case EmitterShape3D.Box:
                spawnPos = origin + new Vector3(
                    SBMath.RandomRange(-ShapeSize.X, ShapeSize.X) * 0.5f,
                    SBMath.RandomRange(-ShapeSize.Y, ShapeSize.Y) * 0.5f,
                    SBMath.RandomRange(-ShapeSize.Z, ShapeSize.Z) * 0.5f);
                break;

            case EmitterShape3D.Cone:
                // Random direction within the cone, then spread the origin across its base.
                var circle = SBMath.RandomOnUnitCircle() * MathF.Tan(MathHelper.ToRadians(ConeAngle));
                var right  = T3D.Right;
                var up     = T3D.Up;
                direction  = SBMath.SafeNormalize(forward + right * circle.X + up * circle.Y);
                spawnPos   = origin + (right * circle.X + up * circle.Y) * ShapeRadius;
                break;
        }

        _particles[_aliveCount] = new Particle
        {
            Position        = spawnPos,
            Velocity        = direction * SBMath.RandomRange(MinSpeed, MaxSpeed),
            Age             = 0f,
            Lifetime        = SBMath.RandomRange(MinLifetime, MaxLifetime),
            Rotation        = SBMath.RandomRange(0f, MathF.Tau),
            AngularVelocity = MathHelper.ToRadians(SBMath.RandomRange(-MaxSpinDegrees, MaxSpinDegrees)),
            StartSize       = SBMath.RandomRange(MinSize, MaxSize),
            StartTint       = StartColor,
            Alive           = true,
        };

        _aliveCount++;
    }

    /// <summary>Iterates the live particles, for custom rendering or gameplay reactions.</summary>
    public IEnumerable<Particle> EnumerateParticles()
    {
        for (int i = 0; i < _aliveCount; i++) yield return _particles[i];
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Draws every live particle. Called by <see cref="RenderSystem3D"/> during the
    /// transparent pass, after opaque geometry.
    /// </summary>
    public void Draw(GraphicsDevice gd, Camera3D camera, Matrix view, Matrix projection)
    {
        if (_aliveCount == 0) return;

        if (RenderMode == ParticleRenderMode3D.Mesh)
        {
            DrawMeshParticles(gd, view, projection);
            return;
        }

        DrawBillboards(gd, camera, view, projection);
    }

    private void DrawBillboards(GraphicsDevice gd, Camera3D camera, Matrix view, Matrix projection)
    {
        EnsureBillboardResources(gd);

        // Build the quads in view space so every particle faces the camera without a
        // per-particle matrix: right and up come straight from the inverse view basis.
        var invView = Matrix.Invert(view);
        var camRight = new Vector3(invView.M11, invView.M12, invView.M13);
        var camUp    = RenderMode == ParticleRenderMode3D.VerticalBillboard
            ? Vector3.Up
            : new Vector3(invView.M21, invView.M22, invView.M23);

        if (_vertexScratch.Length < _aliveCount * 4)
            _vertexScratch = new VertexPositionColorTexture[_maxParticles * 4];

        for (int i = 0; i < _aliveCount; i++)
        {
            ref var p = ref _particles[i];
            float t = p.NormalizedAge;

            float size  = p.StartSize * MathHelper.Lerp(1f, EndSizeMultiplier, t) * 0.5f;
            var   tint  = Color.Lerp(p.StartTint, EndColor, t);

            // Spin the quad basis about the view axis.
            float cos = MathF.Cos(p.Rotation), sin = MathF.Sin(p.Rotation);
            var right = (camRight * cos + camUp * sin) * size;
            var up    = (camUp * cos - camRight * sin) * size;

            int v = i * 4;
            _vertexScratch[v + 0] = new VertexPositionColorTexture(p.Position - right + up, tint, new Vector2(0, 0));
            _vertexScratch[v + 1] = new VertexPositionColorTexture(p.Position + right + up, tint, new Vector2(1, 0));
            _vertexScratch[v + 2] = new VertexPositionColorTexture(p.Position + right - up, tint, new Vector2(1, 1));
            _vertexScratch[v + 3] = new VertexPositionColorTexture(p.Position - right - up, tint, new Vector2(0, 1));
        }

        _billboardVertices!.SetData(_vertexScratch, 0, _aliveCount * 4, SetDataOptions.Discard);

        var previousBlend = gd.BlendState;
        var previousDepth = gd.DepthStencilState;

        gd.BlendState        = AdditiveBlend ? BlendState.Additive : BlendState.AlphaBlend;
        gd.DepthStencilState = DepthStencilState.DepthRead;
        gd.RasterizerState   = RasterizerState.CullNone;

        _billboardEffect!.View       = view;
        _billboardEffect.Projection  = projection;
        _billboardEffect.World       = Matrix.Identity;
        _billboardEffect.Texture     = Texture ?? _whitePixel;

        gd.SetVertexBuffer(_billboardVertices);
        gd.Indices = _billboardIndices;

        foreach (var pass in _billboardEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawIndexedPrimitives(
                Microsoft.Xna.Framework.Graphics.PrimitiveType.TriangleList,
                0, 0, _aliveCount * 2);
        }

        gd.BlendState        = previousBlend;
        gd.DepthStencilState = previousDepth;
    }

    private void DrawMeshParticles(GraphicsDevice gd, Matrix view, Matrix projection)
    {
        if (ParticleMesh == null) return;

        var meshTransform = ParticleMesh.GetTransform3D();
        var originalPos   = meshTransform.LocalPosition;
        var originalScale = meshTransform.LocalScale;

        for (int i = 0; i < _aliveCount; i++)
        {
            ref var p = ref _particles[i];
            float t = p.NormalizedAge;
            float size = p.StartSize * MathHelper.Lerp(1f, EndSizeMultiplier, t);

            meshTransform.LocalPosition = p.Position;
            meshTransform.LocalScale    = new Vector3(size);
            ParticleMesh.Draw(gd, view, projection);
        }

        meshTransform.LocalPosition = originalPos;
        meshTransform.LocalScale    = originalScale;
    }

    private void EnsureBillboardResources(GraphicsDevice gd)
    {
        if (_billboardEffect == null)
        {
            _billboardEffect = new BasicEffect(gd)
            {
                TextureEnabled   = true,
                VertexColorEnabled = true,
                LightingEnabled  = false,
            };

            _whitePixel = new Texture2D(gd, 1, 1);
            _whitePixel.SetData(new[] { Color.White });
        }

        if (_billboardVertices == null || _billboardVertices.VertexCount < _maxParticles * 4)
        {
            _billboardVertices?.Dispose();
            _billboardVertices = new DynamicVertexBuffer(gd,
                VertexPositionColorTexture.VertexDeclaration, _maxParticles * 4, BufferUsage.WriteOnly);

            // The index pattern never changes, so it is built once for the whole pool.
            _billboardIndices?.Dispose();
            var indices = new ushort[_maxParticles * 6];
            for (int i = 0; i < _maxParticles; i++)
            {
                int v = i * 4, o = i * 6;
                indices[o + 0] = (ushort)(v + 0);
                indices[o + 1] = (ushort)(v + 1);
                indices[o + 2] = (ushort)(v + 2);
                indices[o + 3] = (ushort)(v + 0);
                indices[o + 4] = (ushort)(v + 2);
                indices[o + 5] = (ushort)(v + 3);
            }

            _billboardIndices = new IndexBuffer(gd, IndexElementSize.SixteenBits, indices.Length, BufferUsage.WriteOnly);
            _billboardIndices.SetData(indices);
        }
    }

    public override void OnDestroy()
    {
        All.Remove(this);
        _billboardVertices?.Dispose();
        _billboardIndices?.Dispose();
        _billboardEffect?.Dispose();
        _whitePixel?.Dispose();

        _billboardVertices = null;
        _billboardIndices  = null;
        _billboardEffect   = null;
        _whitePixel        = null;
    }
}
