using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>Discriminates between the three supported light archetypes.</summary>
public enum LightType
{
    Directional,
    Point,
    Spot
}

/// <summary>
/// 3D light component. Auto-registers in a static list so renderers can iterate lights.
/// Reads direction from the actor's Transform3D.Forward.
/// </summary>
public sealed class Light3D : Component
{
    // -------------------------------------------------------------------------
    // Static registry — all currently-alive Light3D instances
    // -------------------------------------------------------------------------
    public static readonly List<Light3D> All = new();

    // -------------------------------------------------------------------------
    // Light properties
    // -------------------------------------------------------------------------
    public LightType Type       { get; set; } = LightType.Directional;
    public Color     Color      { get; set; } = Color.White;
    public float     Intensity  { get; set; } = 1f;

    /// <summary>Effective radius for Point and Spot lights (world units).</summary>
    public float Range          { get; set; } = 10f;

    /// <summary>Half-angle of the spot cone in degrees.</summary>
    public float SpotAngle      { get; set; } = 30f;

    // -------------------------------------------------------------------------
    // Shadow map
    // -------------------------------------------------------------------------
    public bool CastsShadows    { get; set; } = false;
    public int  ShadowMapSize   { get; set; } = 1024;

    // -------------------------------------------------------------------------
    // Cached Transform3D
    // -------------------------------------------------------------------------
    private Transform3D? _t3d;

    private Transform3D GetTransform3D()
    {
        if (_t3d != null) return _t3d;
        _t3d = Actor.GetComponent<Transform3D>();
        if (_t3d == null)
            _t3d = Actor.AddComponent<Transform3D>();
        return _t3d;
    }

    // -------------------------------------------------------------------------
    // Direction
    // -------------------------------------------------------------------------
    /// <summary>Returns the world-space forward direction of this light.</summary>
    public Vector3 GetDirection() => GetTransform3D().Forward;

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------
    public override void Awake()
    {
        All.Add(this);
    }

    public override void OnDestroy()
    {
        All.Remove(this);
    }
}
