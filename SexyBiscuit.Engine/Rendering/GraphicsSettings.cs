using System.Text.Json;
using System.Text.Json.Serialization;

namespace SexyBiscuit.Engine.Rendering;

// =============================================================================
// Vocabulary
// =============================================================================

/// <summary>How much a shadow costs. The browser engine knows the same list, in order.</summary>
public enum ShadowQuality { Off, Low, Medium, High, Ultra }

/// <summary>How a texture is sampled. Anisotropic falls back where it is unsupported.</summary>
public enum TextureFiltering { Point, Bilinear, Trilinear, Anisotropic }

/// <summary>How much the 2D light map costs, which is mostly its resolution.</summary>
public enum Lighting2DQuality { Off, Low, Medium, High }

// =============================================================================
// The settings
// =============================================================================

/// <summary>
/// Every quality knob the engine has, in one place. The mirror of
/// <c>html5/src/rendering/GraphicsSettings.js</c>.
/// </summary>
/// <remarks>
/// <para>
/// One object rather than a scattering of properties on the renderer, the config and the
/// device manager, because a settings menu has to be able to read them all, write them all
/// and put them in a file. <see cref="Apply"/> is the only place that knows where each one
/// actually lives.
/// </para>
/// <para>
/// The native-only and web-only groups both exist on both engines on purpose. A web export
/// is produced on a desktop, and a preset that lost its <c>MaxPixelRatio</c> on the way
/// through would ship a phone build at three times the resolution it can fill. The
/// capability probe, not this type, decides what a player is shown.
/// </para>
/// </remarks>
public sealed class GraphicsSettings
{
    // -------------------------------------------------------------------------
    // Shared — both engines honour these, or hide them when they cannot
    // -------------------------------------------------------------------------

    /// <summary>The preset these values came from, or "custom" once one is changed.</summary>
    public string Preset { get; set; } = "medium";

    /// <summary>Fraction of the back buffer the world is rendered at, then upscaled.</summary>
    public float RenderScale { get; set; } = 1f;

    public bool VSync { get; set; } = true;

    /// <summary>Frames a second, or 0 for uncapped. Ignored while VSync is on.</summary>
    public int FrameCap { get; set; }

    public ShadowQuality Shadows { get; set; } = ShadowQuality.Low;
    public float ShadowDistance { get; set; } = 50f;

    /// <summary>Lights per draw. The HLSL array is four long and the GLSL one is eight.</summary>
    public int MaxLightsPerObject { get; set; } = 4;

    public int TessellationRadial { get; set; } = 24;
    public int TessellationRings  { get; set; } = 16;

    /// <summary>Far clip, in world units.</summary>
    public float DrawDistance { get; set; } = 1000f;

    public bool FrustumCulling { get; set; } = true;
    public bool SortOpaqueFrontToBack { get; set; } = true;
    public bool Skybox { get; set; } = true;

    /// <summary>Multiplies every emitter's rate, so a preset thins particles rather than killing them.</summary>
    public float ParticleDensity { get; set; } = 1f;

    public bool PostProcessing { get; set; } = true;
    public bool Bloom       { get; set; } = true;
    public bool Vignette    { get; set; }
    public bool ColourGrade { get; set; } = true;
    public bool Scanline    { get; set; }

    public Lighting2DQuality Lighting2D { get; set; } = Lighting2DQuality.Medium;

    public TextureFiltering TextureFiltering { get; set; } = TextureFiltering.Trilinear;
    public int Anisotropy { get; set; } = 4;

    public int StreamingRadius  { get; set; } = 2;
    public int MaxLoadsPerFrame { get; set; } = 1;

    // -------------------------------------------------------------------------
    // Native only
    // -------------------------------------------------------------------------

    /// <summary>Multisample count. 1 is off; the device clamps to what it supports.</summary>
    public int Msaa { get; set; } = 2;

    /// <summary>Square shadow map edge, in texels.</summary>
    public int ShadowMapSize { get; set; } = 1024;

    public bool Fullscreen { get; set; }

    /// <summary>Back-buffer size, or 0 to leave the window alone.</summary>
    public int ResolutionWidth  { get; set; }
    public int ResolutionHeight { get; set; }

    // -------------------------------------------------------------------------
    // Web only
    // -------------------------------------------------------------------------

    /// <summary>Ceiling on devicePixelRatio. Phones report 3 or 4 and cannot fill it.</summary>
    public float MaxPixelRatio { get; set; } = 2f;

    public bool HighDpi { get; set; } = true;

    /// <summary>Context-creation flags. Changing either needs the canvas rebuilt.</summary>
    public bool   Antialias       { get; set; } = true;
    public string PowerPreference { get; set; } = "high-performance";

    /// <summary>Float precision the fragment shaders are compiled at.</summary>
    public string ShaderPrecision { get; set; } = "highp";

    /// <summary>Whether a hidden tab stops rendering, which is most of a laptop's battery.</summary>
    public bool ThrottleWhenHidden { get; set; } = true;

    // -------------------------------------------------------------------------
    // Presets
    // -------------------------------------------------------------------------

    /// <summary>The preset table, shared with the browser engine as one JSON file.</summary>
    public static GraphicsPresetTable Presets => GraphicsPresetTable.Shared;

    /// <summary>Overwrites every field from a preset. Unknown ids are ignored.</summary>
    public bool ApplyPreset(string id)
    {
        GraphicsPreset? preset = Presets.Find(id);
        if (preset is null) return false;

        preset.CopyTo(this);
        Preset = preset.Id;
        return true;
    }

    /// <summary>A copy, for previewing a change without committing to it.</summary>
    public GraphicsSettings Clone() => (GraphicsSettings)MemberwiseClone();

    // -------------------------------------------------------------------------
    // Applying
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pushes every setting to whatever actually owns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one place that knows where each knob lives, which is the reason this type
    /// exists: they are otherwise spread across the two renderers, the mesh cache, the
    /// camera, every emitter and the device manager.
    /// </para>
    /// <para>
    /// Back-buffer settings — resolution, fullscreen, vsync, MSAA — are not applied here.
    /// They need the <c>GraphicsDeviceManager</c>, which belongs to whatever owns the
    /// window, so they go out through <see cref="Core.EngineHost.ApplyDisplaySettings"/>.
    /// Nothing in this repository ever called <c>ApplyChanges</c> before, so changing a
    /// resolution at runtime was not merely unimplemented but impossible.
    /// </para>
    /// </remarks>
    public void Apply(Core.EngineHost? host)
    {
        ApplyGlobals();

        if (host is null) return;

        ApplyTo(host.Renderer3D);
        ApplyTo(host.Renderer2D);
        host.ApplyDisplaySettings?.Invoke(this);
    }

    /// <summary>The settings that live on statics, and so apply with no host at all.</summary>
    public void ApplyGlobals()
    {
        // The primitive cache is keyed on the shape, not on the tessellation, so meshes
        // built at the old segment counts have to be dropped or the change is invisible
        // until something asks for a shape nothing has requested yet.
        if (PrimitiveMesh.RadialSegments != TessellationRadial ||
            PrimitiveMesh.RingSegments   != TessellationRings)
        {
            PrimitiveMesh.RadialSegments = Math.Max(3, TessellationRadial);
            PrimitiveMesh.RingSegments   = Math.Max(2, TessellationRings);
            PrimitiveMesh.ClearCache();
        }

        ParticleSystem3D.DensityScale = ParticleDensity;
        ParticleEmitter.DensityScale  = ParticleDensity;

        foreach (Light3D light in Light3D.All)
        {
            light.CastsShadows  = light.CastsShadows && Shadows != ShadowQuality.Off;
            light.ShadowMapSize = ShadowMapSize;
        }

        if (Camera3D.Main is { } camera) camera.FarClip = DrawDistance;
    }

    private void ApplyTo(RenderSystem3D renderer)
    {
        renderer.EnableShadows        = Shadows != ShadowQuality.Off;
        renderer.ShadowDistance       = ShadowDistance;
        renderer.MaxLightsPerObject   = Math.Clamp(MaxLightsPerObject, 1, 8);
        renderer.EnableFrustumCulling = FrustumCulling;
        renderer.SortOpaqueFrontToBack = SortOpaqueFrontToBack;
        renderer.RenderSkybox         = Skybox;
        renderer.RenderScale          = Math.Clamp(RenderScale, 0.25f, 2f);

        foreach (PostProcessPass3D pass in renderer.PostProcess)
            pass.Enabled = PostProcessing && IsPassEnabled(pass.Name);
    }

    private void ApplyTo(RenderSystem2D renderer)
    {
        renderer.SamplerState = TextureFiltering switch
        {
            TextureFiltering.Point       => Microsoft.Xna.Framework.Graphics.SamplerState.PointClamp,
            TextureFiltering.Anisotropic => Anisotropic(Anisotropy),
            _                            => Microsoft.Xna.Framework.Graphics.SamplerState.LinearClamp,
        };
    }

    /// <summary>Whether a named post-processing pass survives this quality level.</summary>
    private bool IsPassEnabled(string name) => name switch
    {
        "Bloom"       => Bloom,
        "Vignette"    => Vignette,
        "ColourGrade" => ColourGrade,
        "Scanline"    => Scanline,
        _             => true,
    };

    /// <summary>
    /// A cached anisotropic sampler. Cached because a new SamplerState per frame is a
    /// device object per frame, and the renderer reads this every draw.
    /// </summary>
    private static Microsoft.Xna.Framework.Graphics.SamplerState Anisotropic(int level)
    {
        int clamped = Math.Clamp(level, 1, 16);
        if (_anisotropic?.MaxAnisotropy == clamped) return _anisotropic;

        return _anisotropic = new Microsoft.Xna.Framework.Graphics.SamplerState
        {
            Filter        = Microsoft.Xna.Framework.Graphics.TextureFilter.Anisotropic,
            MaxAnisotropy = clamped,
            AddressU      = Microsoft.Xna.Framework.Graphics.TextureAddressMode.Clamp,
            AddressV      = Microsoft.Xna.Framework.Graphics.TextureAddressMode.Clamp,
        };
    }

    private static Microsoft.Xna.Framework.Graphics.SamplerState? _anisotropic;

    // -------------------------------------------------------------------------
    // Persistence
    // -------------------------------------------------------------------------

    /// <summary>The key the whole object is stored under, as one JSON blob.</summary>
    public const string PrefsKey = "Graphics";

    /// <summary>Writes the settings to <see cref="Save.PlayerPrefs"/>.</summary>
    public void Save()
    {
        Engine.Save.PlayerPrefs.SetString(PrefsKey, JsonSerializer.Serialize(this, JsonOptions));
        Engine.Save.PlayerPrefs.Save();
    }

    /// <summary>
    /// Reads the settings back, or returns null when the player has never chosen any.
    /// </summary>
    /// <remarks>
    /// Null rather than defaults, because "never chosen" is what triggers the capability
    /// probe on a first run — and a probe that ran every launch would silently undo the
    /// choice of anybody who had deliberately turned something down.
    /// </remarks>
    public static GraphicsSettings? Load()
    {
        string json = Engine.Save.PlayerPrefs.GetString(PrefsKey, "");
        if (string.IsNullOrWhiteSpace(json)) return null;

        try { return JsonSerializer.Deserialize<GraphicsSettings>(json, JsonOptions); }
        catch (JsonException) { return null; }   // A corrupt blob is a first run, not a crash.
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
