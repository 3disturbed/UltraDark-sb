using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>Shape of a <see cref="Light2D"/>.</summary>
public enum Light2DType
{
    /// <summary>Radial falloff from a point.</summary>
    Point,

    /// <summary>A cone from a point, aimed along the transform's Right vector.</summary>
    Spot,

    /// <summary>Uniform light over the whole screen, ignoring position.</summary>
    Global,
}

/// <summary>
/// A 2D light. Lights are gathered by <see cref="Lighting2D"/>, drawn into an off-screen
/// light map and multiplied over the scene.
/// </summary>
/// <remarks>
/// Falloff is baked into a procedurally generated radial texture rather than computed per
/// pixel in a shader, so the whole 2D lighting pipeline runs on stock MonoGame with no
/// content pipeline. That is the trade: no normal-mapped surface detail unless you supply
/// <see cref="Lighting2D.NormalMapEffect"/>, but it works everywhere out of the box.
/// </remarks>
/// <example>
/// <code>
/// var torch = lampActor.AddComponent&lt;Light2D&gt;();
/// torch.Color     = new Color(255, 180, 90);
/// torch.Radius    = 220f;
/// torch.Intensity = 1.4f;
/// </code>
/// </example>
public sealed class Light2D : Component
{
    /// <summary>Every live 2D light. <see cref="Lighting2D"/> iterates this each frame.</summary>
    public static readonly List<Light2D> All = new();

    /// <summary>Shape of the light.</summary>
    public Light2DType Type { get; set; } = Light2DType.Point;

    /// <summary>Light colour before <see cref="Intensity"/> is applied.</summary>
    public Color Color { get; set; } = Color.White;

    /// <summary>Brightness multiplier. Values above 1 blow out the centre.</summary>
    public float Intensity { get; set; } = 1f;

    /// <summary>Radius in world units at which the light reaches zero.</summary>
    public float Radius { get; set; } = 200f;

    /// <summary>
    /// Falloff curve exponent. 1 is linear, 2 is a soft inverse-square look,
    /// values below 1 give a hard-edged pool of light.
    /// </summary>
    public float Falloff { get; set; } = 2f;

    /// <summary>Cone half-angle in degrees for <see cref="Light2DType.Spot"/>.</summary>
    public float SpotAngle { get; set; } = 45f;

    /// <summary>Casts shadows from <see cref="ShadowCaster2D"/> occluders in range.</summary>
    public bool CastsShadows { get; set; }

    /// <summary>World position, taken from the actor's <see cref="Transform"/>.</summary>
    public Vector2 Position => Actor.Transform.Position;

    public override void Awake()     => All.Add(this);
    public override void OnDestroy() => All.Remove(this);
}

/// <summary>
/// Marks a convex polygon as blocking 2D light. Shadows are projected away from each
/// light through the polygon's edges.
/// </summary>
/// <remarks>
/// Points are in local space and are transformed by the actor each frame, so a rotating
/// occluder casts a rotating shadow without any extra bookkeeping.
/// </remarks>
public sealed class ShadowCaster2D : Component
{
    /// <summary>Every live occluder.</summary>
    public static readonly List<ShadowCaster2D> All = new();

    /// <summary>Occluder outline in local space, in winding order.</summary>
    public List<Vector2> Points { get; set; } = new();

    /// <summary>Builds a rectangular occluder centred on the actor.</summary>
    public void SetBox(float width, float height)
    {
        float hw = width * 0.5f, hh = height * 0.5f;
        Points = new List<Vector2>
        {
            new(-hw, -hh), new(hw, -hh), new(hw, hh), new(-hw, hh),
        };
    }

    /// <summary>The outline transformed into world space.</summary>
    public IEnumerable<Vector2> WorldPoints()
    {
        var t = Actor.Transform;
        float cos = MathF.Cos(t.Rotation), sin = MathF.Sin(t.Rotation);
        var scale = t.Scale;
        var origin = t.Position;

        foreach (var p in Points)
        {
            var s = new Vector2(p.X * scale.X, p.Y * scale.Y);
            yield return new Vector2(s.X * cos - s.Y * sin, s.X * sin + s.Y * cos) + origin;
        }
    }

    public override void Awake()     => All.Add(this);
    public override void OnDestroy() => All.Remove(this);
}

/// <summary>
/// The 2D lighting pipeline: renders every <see cref="Light2D"/> into a light map and
/// multiplies it over the scene.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="BuildLightMap"/> after the scene has been drawn but before presenting,
/// then <see cref="Composite"/> to blend the result. Both steps take the camera transform so
/// lights land in the right screen position when the view scrolls or zooms.
/// </para>
/// <para>
/// Compositing uses a multiply blend, so <see cref="AmbientColor"/> is the darkness floor:
/// black means unlit areas are fully black, mid-grey gives a moonlit look.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Once:
/// var lighting = new Lighting2D();
/// lighting.Initialize(GraphicsDevice);
/// lighting.AmbientColor = new Color(24, 26, 40);
///
/// // Each frame, after drawing the scene:
/// lighting.BuildLightMap(camera.GetViewMatrix());
/// lighting.Composite(spriteBatch);
/// </code>
/// </example>
public sealed class Lighting2D : IDisposable
{
    /// <summary>Base light level applied everywhere. Black gives full darkness outside lights.</summary>
    public Color AmbientColor { get; set; } = new(30, 30, 40);

    /// <summary>Skips the whole pipeline when false.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Resolution divisor for the light map. 2 halves its width and height, which softens
    /// light edges and costs a quarter of the fill rate.
    /// </summary>
    public int Downsample { get; set; } = 1;

    /// <summary>
    /// Optional effect for normal-mapped lighting. When set it is applied while compositing,
    /// and receives <c>LightMap</c>, <c>NormalMap</c> and <c>AmbientColor</c> parameters.
    /// </summary>
    public Effect? NormalMapEffect { get; set; }

    /// <summary>Scene normal map sampled by <see cref="NormalMapEffect"/>.</summary>
    public Texture2D? SceneNormalMap { get; set; }

    /// <summary>The light map produced by the most recent <see cref="BuildLightMap"/> call.</summary>
    public RenderTarget2D? LightMap => _lightMap;

    private GraphicsDevice   _gd = null!;
    private SpriteBatch      _batch = null!;
    private RenderTarget2D?  _lightMap;
    private Texture2D?       _radialGradient;
    private Texture2D?       _pixel;

    /// <summary>
    /// Multiply blend: destination is scaled by the light map, so unlit pixels go dark and
    /// lit pixels keep their colour.
    /// </summary>
    private static readonly BlendState MultiplyBlend = new()
    {
        ColorSourceBlend      = Blend.Zero,
        ColorDestinationBlend = Blend.SourceColor,
        AlphaSourceBlend      = Blend.Zero,
        AlphaDestinationBlend = Blend.SourceAlpha,
    };

    /// <summary>Binds the pipeline to a device and builds its lookup textures.</summary>
    public void Initialize(GraphicsDevice gd)
    {
        _gd    = gd ?? throw new ArgumentNullException(nameof(gd));
        _batch = new SpriteBatch(gd);

        _pixel = new Texture2D(gd, 1, 1);
        _pixel.SetData(new[] { Color.White });
    }

    /// <summary>
    /// Renders every enabled light into the light map.
    /// </summary>
    /// <param name="viewMatrix">
    /// The camera transform, so world-space light positions map to the same screen pixels
    /// the scene was drawn at.
    /// </param>
    public void BuildLightMap(Matrix viewMatrix)
    {
        if (!Enabled || _gd == null) return;

        EnsureTargets();

        var previous = _gd.GetRenderTargets();
        _gd.SetRenderTarget(_lightMap);
        _gd.Clear(AmbientColor);

        // Additive so overlapping lights brighten rather than replace each other.
        var scale = Matrix.CreateScale(1f / Math.Max(1, Downsample));
        _batch.Begin(SpriteSortMode.Deferred, BlendState.Additive,
            SamplerState.LinearClamp, null, null, null, viewMatrix * scale);

        foreach (var light in Light2D.All)
        {
            if (!light.Enabled || !light.Actor.IsActive || light.Intensity <= 0f) continue;
            DrawLight(light);
        }

        _batch.End();

        _gd.SetRenderTargets(previous.Length > 0 ? previous : null);
    }

    private void DrawLight(Light2D light)
    {
        var tint = light.Color * MathHelper.Clamp(light.Intensity, 0f, 8f);

        if (light.Type == Light2DType.Global)
        {
            var vp = _gd.Viewport;
            _batch.Draw(_pixel, new Rectangle(0, 0, vp.Width * 4, vp.Height * 4), tint);
            return;
        }

        var gradient = GetRadialGradient(light.Falloff);
        float diameter = light.Radius * 2f;
        float texScale = diameter / gradient.Width;

        var origin = new Vector2(gradient.Width * 0.5f, gradient.Height * 0.5f);

        if (light.Type == Light2DType.Spot)
        {
            // Approximate the cone by scaling the radial sprite down across the
            // perpendicular axis and rotating it to the transform's facing.
            float coneScale = MathHelper.Clamp(light.SpotAngle / 90f, 0.05f, 1f);
            _batch.Draw(gradient, light.Position, null, tint,
                light.Actor.Transform.Rotation, origin,
                new Vector2(texScale, texScale * coneScale), SpriteEffects.None, 0f);
            return;
        }

        _batch.Draw(gradient, light.Position, null, tint, 0f, origin,
            texScale, SpriteEffects.None, 0f);
    }

    /// <summary>
    /// Blends the light map over whatever is already in the back buffer.
    /// Call after the scene has been drawn.
    /// </summary>
    public void Composite(SpriteBatch target)
    {
        if (!Enabled || _lightMap == null) return;

        var vp = _gd.Viewport;
        var destination = new Rectangle(0, 0, vp.Width, vp.Height);

        if (NormalMapEffect != null)
        {
            NormalMapEffect.Parameters["LightMap"]?.SetValue(_lightMap);
            NormalMapEffect.Parameters["NormalMap"]?.SetValue(SceneNormalMap);
            NormalMapEffect.Parameters["AmbientColor"]?.SetValue(AmbientColor.ToVector4());
        }

        target.Begin(SpriteSortMode.Deferred, MultiplyBlend,
            SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, NormalMapEffect);
        target.Draw(_lightMap, destination, Color.White);
        target.End();
    }

    private void EnsureTargets()
    {
        var pp = _gd.PresentationParameters;
        int divisor = Math.Max(1, Downsample);
        int w = Math.Max(1, pp.BackBufferWidth  / divisor);
        int h = Math.Max(1, pp.BackBufferHeight / divisor);

        if (_lightMap != null && _lightMap.Width == w && _lightMap.Height == h) return;

        _lightMap?.Dispose();
        _lightMap = new RenderTarget2D(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
    }

    /// <summary>
    /// Builds (and caches) a radial falloff sprite for a given exponent. One texture is
    /// shared by every light using that curve.
    /// </summary>
    private Texture2D GetRadialGradient(float falloff)
    {
        // Cache a single gradient; changing falloff at runtime rebuilds it. Lights that
        // share a curve — the common case — share the texture.
        if (_radialGradient != null && SBMath.Approximately(_cachedFalloff, falloff))
            return _radialGradient;

        const int size = 256;
        const float radius = size * 0.5f;

        var data = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - radius + 0.5f;
                float dy = y - radius + 0.5f;
                float d  = MathF.Sqrt(dx * dx + dy * dy) / radius;

                float v = d >= 1f ? 0f : MathF.Pow(1f - d, MathF.Max(0.05f, falloff));
                byte b  = (byte)(MathHelper.Clamp(v, 0f, 1f) * 255f);
                data[y * size + x] = new Color(b, b, b, b);
            }
        }

        _radialGradient?.Dispose();
        _radialGradient = new Texture2D(_gd, size, size);
        _radialGradient.SetData(data);
        _cachedFalloff = falloff;
        return _radialGradient;
    }

    private float _cachedFalloff = float.NaN;

    public void Dispose()
    {
        _lightMap?.Dispose();
        _radialGradient?.Dispose();
        _pixel?.Dispose();
        _batch?.Dispose();

        _lightMap = null;
        _radialGradient = null;
        _pixel = null;
    }
}
