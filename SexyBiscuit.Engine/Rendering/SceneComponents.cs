using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// Scene-wide ambient light, optionally tinted differently from above and below.
/// Modelled on Unreal's <c>USkyLightComponent</c>.
/// </summary>
/// <remarks>
/// <para>
/// A single ambient colour makes every unlit surface the same flat tone regardless of
/// which way it faces. A real sky is bright overhead and bounces ground colour upward, so
/// splitting ambient into a sky tint and a ground tint and blending by surface normal
/// costs nothing and removes most of that flatness.
/// </para>
/// <para>
/// The <see cref="BasicEffect"/> path can only take one ambient colour, so it receives the
/// blend at the horizon. A shader reading <c>SkyColor</c> and <c>GroundColor</c> gets the
/// full effect.
/// </para>
/// </remarks>
public sealed class SkyLight : Component
{
    /// <summary>The sky light in the scene, or null. The first enabled one wins.</summary>
    public static SkyLight? Active { get; private set; }

    /// <summary>Ambient contribution on upward-facing surfaces.</summary>
    public Color SkyColor { get; set; } = new(96, 118, 156);

    /// <summary>Ambient contribution on downward-facing surfaces — bounce from the ground.</summary>
    public Color GroundColor { get; set; } = new(60, 54, 46);

    /// <summary>Overall strength.</summary>
    public float Intensity { get; set; } = 1f;

    /// <summary>Optional cubemap for image-based lighting, sampled by a capable shader.</summary>
    public TextureCube? Cubemap { get; set; }

    /// <summary>The single colour to hand a renderer that supports only flat ambient.</summary>
    public Color GetAverageAmbient()
        => Color.Lerp(GroundColor, SkyColor, 0.5f) * MathHelper.Clamp(Intensity, 0f, 8f);

    public override void Awake()  => Active ??= this;
    public override void OnDestroy() { if (ReferenceEquals(Active, this)) Active = null; }
}

/// <summary>
/// Text rendered in world space as camera-facing geometry.
/// Modelled on Unreal's <c>UTextRenderComponent</c>.
/// </summary>
/// <remarks>
/// Draws through <see cref="SpriteBatch"/> with a billboard transform rather than
/// generating text meshes, which keeps it to one code path shared with the rest of the 2D
/// text in the engine. Good for nameplates, damage numbers and debug labels; not the tool
/// for text that has to be occluded by geometry it sits behind.
/// </remarks>
public sealed class TextRenderer3D : Component
{
    /// <summary>Every live world-space label.</summary>
    public static readonly List<TextRenderer3D> All = new();

    /// <summary>The text to draw.</summary>
    public string Text { get; set; } = "";

    /// <summary>Font used. Falls back to nothing drawn when null.</summary>
    public SpriteFont? Font { get; set; }

    /// <summary>Text colour.</summary>
    public Color Color { get; set; } = Color.White;

    /// <summary>World units per pixel of glyph height.</summary>
    public float WorldSize { get; set; } = 0.01f;

    /// <summary>Offset from the actor's origin, in world units.</summary>
    public Vector3 Offset { get; set; } = new(0f, 1f, 0f);

    /// <summary>Horizontal alignment about the anchor point, 0 left to 1 right.</summary>
    public float HorizontalAlign { get; set; } = 0.5f;

    /// <summary>Beyond this distance the label is not drawn. Zero means always.</summary>
    public float MaxDrawDistance { get; set; } = 40f;

    /// <summary>Keeps the label the same size on screen regardless of distance.</summary>
    public bool ConstantScreenSize { get; set; }

    private Transform3D? _t3d;
    private Transform3D GetTransform3D()
        => _t3d ??= Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();

    public override void Awake()     => All.Add(this);
    public override void OnDestroy() => All.Remove(this);

    /// <summary>
    /// Projects the anchor and draws the text. Called from the 2D pass, after the 3D scene,
    /// with an open sprite batch.
    /// </summary>
    public void Draw(SpriteBatch batch, Camera3D camera, GraphicsDevice gd)
    {
        if (Font == null || string.IsNullOrEmpty(Text)) return;

        var anchor = GetTransform3D().Position + Offset;
        var camPos = camera.GetTransform3D().Position;

        float distance = Vector3.Distance(camPos, anchor);
        if (MaxDrawDistance > 0f && distance > MaxDrawDistance) return;

        var view = camera.GetViewMatrix();
        var proj = camera.GetProjectionMatrix(gd.Viewport.AspectRatio);

        // Reject anything behind the camera before projecting: the perspective divide
        // flips those to the opposite side of the screen rather than hiding them.
        var viewSpace = Vector3.Transform(anchor, view);
        if (viewSpace.Z >= -camera.NearClip) return;

        var screen = gd.Viewport.Project(anchor, proj, view, Matrix.Identity);

        var size  = Font.MeasureString(Text);
        float scale = ConstantScreenSize
            ? 1f
            : WorldSize * gd.Viewport.Height / MathF.Max(distance, 0.01f) * 0.1f;

        var origin = new Vector2(size.X * HorizontalAlign, size.Y * 0.5f);

        batch.DrawString(Font, Text, new Vector2(screen.X, screen.Y), Color,
            0f, origin, scale, SpriteEffects.None, 0f);
    }
}

/// <summary>
/// A texture projected onto whatever geometry sits under it — bullet holes, scorch marks,
/// footprints. Modelled on Unreal's <c>UDecalComponent</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is a <em>placed quad</em>, not a projected decal. A true deferred decal needs a
/// depth prepass and a shader that reconstructs world position from depth, neither of
/// which the forward renderer has. A quad offset slightly along the surface normal covers
/// the common case — a mark stuck to a flat wall or floor — and is honest about not
/// wrapping around corners.
/// </para>
/// <para>
/// <see cref="SurfaceOffset"/> exists because a quad exactly coplanar with a wall
/// z-fights with it. A millimetre of separation is invisible and fixes it.
/// </para>
/// </remarks>
public sealed class Decal3D : Component
{
    /// <summary>Every live decal.</summary>
    public static readonly List<Decal3D> All = new();

    /// <summary>The image to project.</summary>
    public Texture2D? Texture { get; set; }

    /// <summary>Tint and opacity.</summary>
    public Color Color { get; set; } = Color.White;

    /// <summary>Size of the decal quad in world units.</summary>
    public Vector2 Size { get; set; } = Vector2.One;

    /// <summary>Distance lifted off the surface, to avoid z-fighting.</summary>
    public float SurfaceOffset { get; set; } = 0.01f;

    /// <summary>Seconds before the decal fades out and destroys its actor. Zero means forever.</summary>
    public float LifeSpan { get; set; }

    /// <summary>Fraction of the lifespan spent fading. 0.25 fades over the last quarter.</summary>
    public float FadeFraction { get; set; } = 0.25f;

    /// <summary>Current opacity after fading, 0 to 1.</summary>
    public float Opacity { get; private set; } = 1f;

    private float _age;
    private Transform3D? _t3d;

    private Transform3D GetTransform3D()
        => _t3d ??= Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();

    public override void Awake()     => All.Add(this);
    public override void OnDestroy() => All.Remove(this);

    /// <summary>
    /// Places the decal flat against a surface, facing out along its normal.
    /// </summary>
    /// <param name="point">Contact point, typically from a raycast hit.</param>
    /// <param name="normal">Surface normal at that point.</param>
    /// <param name="rollDegrees">Spin about the normal, so repeated marks are not identical.</param>
    public void PlaceOnSurface(Vector3 point, Vector3 normal, float rollDegrees = 0f)
    {
        var transform = GetTransform3D();
        var safeNormal = SBMath.SafeNormalize(normal);
        if (safeNormal == Vector3.Zero) safeNormal = Vector3.Up;

        transform.Position = point + safeNormal * SurfaceOffset;

        // LookAt needs an up vector that is not parallel to the direction it is facing.
        var up = MathF.Abs(Vector3.Dot(safeNormal, Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
        transform.LookAt(transform.Position + safeNormal, up);

        if (rollDegrees == 0f) return;

        var roll = Quaternion.CreateFromAxisAngle(safeNormal, MathHelper.ToRadians(rollDegrees));
        transform.Rotation = roll * transform.Rotation;
    }

    public override void Update(float dt)
    {
        if (LifeSpan <= 0f) return;

        _age += dt;

        float fadeStart = LifeSpan * (1f - SBMath.Clamp01(FadeFraction));
        Opacity = _age <= fadeStart
            ? 1f
            : 1f - SBMath.Clamp01((_age - fadeStart) / MathF.Max(LifeSpan - fadeStart, 0.0001f));

        if (_age >= LifeSpan) Actor.Destroy();
    }

    /// <summary>Draws the decal quad. Called by <see cref="RenderSystem3D"/> in the transparent pass.</summary>
    public void Draw(GraphicsDevice gd, Matrix view, Matrix projection)
    {
        if (Texture == null || Opacity <= 0f) return;

        var geometry = PrimitiveMesh.Get(MeshPrimitive.Quad, gd);
        if (geometry == null) return;

        var transform = GetTransform3D();
        var world = Matrix.CreateScale(Size.X, Size.Y, 1f)
                  * Matrix.CreateFromQuaternion(transform.Rotation)
                  * Matrix.CreateTranslation(transform.Position);

        _effect ??= new BasicEffect(gd) { TextureEnabled = true, LightingEnabled = false };
        _effect.World      = world;
        _effect.View       = view;
        _effect.Projection = projection;
        _effect.Texture    = Texture;
        _effect.Alpha      = Opacity * (Color.A / 255f);
        _effect.DiffuseColor = Color.ToVector3();

        gd.SetVertexBuffer(geometry.VertexBuffer);
        gd.Indices = geometry.IndexBuffer;

        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geometry.PrimitiveCount);
        }
    }

    private BasicEffect? _effect;
}

/// <summary>
/// Marks the point 3D audio is heard from. Modelled on Unreal's <c>UAudioListener</c>
/// behaviour on the player camera.
/// </summary>
/// <remarks>
/// Without one, spatialised audio has no reference point. Put it on the camera rather
/// than the player: what the listener hears should match what the view shows, and in
/// third person those are metres apart.
/// </remarks>
public sealed class AudioListener3D : Component
{
    /// <summary>The active listener. The first enabled one wins.</summary>
    public static AudioListener3D? Active { get; private set; }

    /// <summary>World position audio is heard from.</summary>
    public Vector3 Position => GetTransform3D().Position;

    /// <summary>Facing, used for left/right panning.</summary>
    public Vector3 Forward => GetTransform3D().Forward;

    /// <summary>Up axis, used to resolve the panning basis.</summary>
    public Vector3 Up => GetTransform3D().Up;

    /// <summary>Velocity for Doppler, measured between frames.</summary>
    public Vector3 Velocity { get; private set; }

    /// <summary>Enables Doppler shift from <see cref="Velocity"/>.</summary>
    public bool EnableDoppler { get; set; } = true;

    private Vector3 _lastPosition;
    private bool    _hasLastPosition;

    private Transform3D? _t3d;
    private Transform3D GetTransform3D()
        => _t3d ??= Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();

    public override void Awake()  => Active ??= this;
    public override void OnDestroy() { if (ReferenceEquals(Active, this)) Active = null; }

    public override void Update(float dt)
    {
        if (dt <= 0f) return;

        var current = Position;

        // Measured rather than taken from a rigidbody: the listener usually rides a camera,
        // which is moved by a controller and has no physics velocity to read.
        Velocity = _hasLastPosition ? (current - _lastPosition) / dt : Vector3.Zero;

        _lastPosition    = current;
        _hasLastPosition = true;
    }
}
