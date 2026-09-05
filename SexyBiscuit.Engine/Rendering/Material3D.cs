using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// PBR-lite material definition for 3D meshes.
/// Holds texture maps and scalar properties, and can push them into any Effect.
/// </summary>
public sealed class Material3D
{
    // -------------------------------------------------------------------------
    // Texture maps
    // -------------------------------------------------------------------------
    public Texture2D? AlbedoMap    { get; set; }
    public Texture2D? NormalMap    { get; set; }
    public Texture2D? MetallicMap  { get; set; }
    public Texture2D? RoughnessMap { get; set; }
    public Texture2D? EmissiveMap  { get; set; }

    // -------------------------------------------------------------------------
    // Scalar / colour properties
    // -------------------------------------------------------------------------
    public Color AlbedoColor       { get; set; } = Color.White;
    public float Metallic          { get; set; } = 0f;
    public float Roughness         { get; set; } = 0.5f;
    public float EmissiveIntensity { get; set; } = 0f;

    // -------------------------------------------------------------------------
    // Asset paths
    // -------------------------------------------------------------------------

    /// <summary>Asset path the albedo map was loaded from, recorded so it can be reloaded.</summary>
    /// <remarks>
    /// A scene file cannot store a <see cref="Texture2D"/> — it is a GPU resource. It
    /// stores the path and the loader rebuilds it. Without somewhere to keep that path a
    /// material's textures are simply lost on save, which is what used to happen.
    /// </remarks>
    public string? AlbedoMapPath { get; set; }

    /// <inheritdoc cref="AlbedoMapPath"/>
    public string? NormalMapPath { get; set; }

    /// <inheritdoc cref="AlbedoMapPath"/>
    public string? MetallicMapPath { get; set; }

    /// <inheritdoc cref="AlbedoMapPath"/>
    public string? RoughnessMapPath { get; set; }

    /// <inheritdoc cref="AlbedoMapPath"/>
    public string? EmissiveMapPath { get; set; }

    /// <summary>Name of the compiled effect to load for <see cref="Shader"/>, if any.</summary>
    public string? ShaderPath { get; set; }

    /// <summary>
    /// Loads the named textures through the running host's asset manager, doing nothing when
    /// no host is running (headless tools keep the paths and resolve them later).
    /// </summary>
    public void ResolveTextures()
    {
        var assets = Assets.AssetManager.Current;
        if (assets != null) ResolveTextures(assets);
    }

    /// <summary>
    /// A copy with the same scalars, paths and texture references. Used before writing to a
    /// material that might be the shared <see cref="Default"/>.
    /// </summary>
    public Material3D Clone() => new()
    {
        AlbedoMap         = AlbedoMap,
        NormalMap         = NormalMap,
        MetallicMap       = MetallicMap,
        RoughnessMap      = RoughnessMap,
        EmissiveMap       = EmissiveMap,
        AlbedoColor       = AlbedoColor,
        Metallic          = Metallic,
        Roughness         = Roughness,
        EmissiveIntensity = EmissiveIntensity,
        AlbedoMapPath     = AlbedoMapPath,
        NormalMapPath     = NormalMapPath,
        MetallicMapPath   = MetallicMapPath,
        RoughnessMapPath  = RoughnessMapPath,
        EmissiveMapPath   = EmissiveMapPath,
        ShaderPath        = ShaderPath,
        Shader            = Shader,
    };

    /// <summary>
    /// Loads every texture named by the path properties through an asset manager.
    /// </summary>
    /// <remarks>
    /// Called after a scene loads. Separate from deserialisation because loading needs a
    /// graphics device, and a scene is deserialised long before one is in reach.
    /// </remarks>
    public void ResolveTextures(Assets.AssetManager assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        AlbedoMap    = Load(AlbedoMapPath)    ?? AlbedoMap;
        NormalMap    = Load(NormalMapPath)    ?? NormalMap;
        MetallicMap  = Load(MetallicMapPath)  ?? MetallicMap;
        RoughnessMap = Load(RoughnessMapPath) ?? RoughnessMap;
        EmissiveMap  = Load(EmissiveMapPath)  ?? EmissiveMap;

        Texture2D? Load(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                return assets.Load<Texture2D>(path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Material3D] Could not load '{path}': {ex.Message}");
                return null;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Custom HLSL effect — null means use the renderer's default
    // -------------------------------------------------------------------------
    public Effect? Shader { get; set; }

    // -------------------------------------------------------------------------
    // Default instance
    // -------------------------------------------------------------------------
    private static Material3D? _default;
    public static Material3D Default => _default ??= new Material3D();

    // -------------------------------------------------------------------------
    // Apply to an effect
    // -------------------------------------------------------------------------
    /// <summary>
    /// Pushes all material properties into the given effect.
    /// Only sets parameters that actually exist on the effect to avoid runtime errors.
    /// </summary>
    public void Apply(Effect effect)
    {
        TrySetTexture(effect, "AlbedoMap",    AlbedoMap);
        TrySetTexture(effect, "NormalMap",    NormalMap);
        TrySetTexture(effect, "MetallicMap",  MetallicMap);
        TrySetTexture(effect, "RoughnessMap", RoughnessMap);
        TrySetTexture(effect, "EmissiveMap",  EmissiveMap);

        TrySetVector4(effect, "AlbedoColor",
            new Vector4(AlbedoColor.R / 255f,
                        AlbedoColor.G / 255f,
                        AlbedoColor.B / 255f,
                        AlbedoColor.A / 255f));

        TrySetFloat(effect, "Metallic",          Metallic);
        TrySetFloat(effect, "Roughness",         Roughness);
        TrySetFloat(effect, "EmissiveIntensity", EmissiveIntensity);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private static void TrySetTexture(Effect effect, string name, Texture2D? texture)
    {
        if (texture == null) return;
        var p = effect.Parameters[name];
        if (p != null) p.SetValue(texture);
    }

    private static void TrySetFloat(Effect effect, string name, float value)
    {
        var p = effect.Parameters[name];
        if (p != null) p.SetValue(value);
    }

    private static void TrySetVector4(Effect effect, string name, Vector4 value)
    {
        var p = effect.Parameters[name];
        if (p != null) p.SetValue(value);
    }
}
