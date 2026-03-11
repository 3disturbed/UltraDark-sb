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
