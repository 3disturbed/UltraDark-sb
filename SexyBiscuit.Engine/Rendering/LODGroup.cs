using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// One level of detail entry: a screen-relative transition height threshold and
/// the MeshRenderer to activate at that level.
/// </summary>
public sealed class LODLevel
{
    /// <summary>
    /// Normalised screen height at which to switch to this LOD (0 = tiny / far, 1 = full screen).
    /// Levels should be sorted descending (highest detail first).
    /// </summary>
    public float          ScreenRelativeTransitionHeight { get; set; }

    /// <summary>The MeshRenderer to enable when this LOD is active. May be null to hide the mesh.</summary>
    public MeshRenderer?  Renderer { get; set; }
}

/// <summary>
/// Manages a list of <see cref="LODLevel"/> entries and switches between them each frame
/// based on how large the actor's bounding sphere appears on screen relative to the camera.
/// </summary>
public sealed class LODGroup : Component
{
    /// <summary>Every live LOD group. <see cref="RenderSystem3D"/> evaluates these each frame.</summary>
    public static readonly List<LODGroup> All = new();

    /// <summary>LOD levels, sorted descending by ScreenRelativeTransitionHeight for correct evaluation.</summary>
    public List<LODLevel> Levels { get; set; } = new();

    /// <summary>World-space radius used for screen-size estimation. Default 1 m.</summary>
    public float BoundingRadius { get; set; } = 1f;

    // -------------------------------------------------------------------------
    // Registration
    // -------------------------------------------------------------------------
    public override void Awake()     => All.Add(this);
    public override void OnDestroy() => All.Remove(this);

    // -------------------------------------------------------------------------
    // Cached Transform3D
    // -------------------------------------------------------------------------
    private Transform3D? _t3d;
    private Transform3D GetTransform3D()
    {
        if (_t3d != null) return _t3d;
        _t3d = Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();
        return _t3d;
    }

    // -------------------------------------------------------------------------
    // Update
    // -------------------------------------------------------------------------
    /// <summary>
    /// Evaluate the correct LOD level and toggle renderer visibility accordingly.
    /// Call once per frame from a scene update or the 3D render loop.
    /// </summary>
    public void Update(Camera3D cam)
    {
        if (Levels.Count == 0) return;

        float screenRelSize = ComputeScreenRelativeSize(cam);

        // Walk levels from highest detail (largest threshold) to lowest
        int activeLod = Levels.Count - 1; // default: lowest detail
        for (int i = 0; i < Levels.Count; i++)
        {
            if (screenRelSize >= Levels[i].ScreenRelativeTransitionHeight)
            {
                activeLod = i;
                break;
            }
        }

        for (int i = 0; i < Levels.Count; i++)
        {
            var renderer = Levels[i].Renderer;
            if (renderer == null) continue;
            renderer.Enabled = (i == activeLod);
        }
    }

    // -------------------------------------------------------------------------
    // Screen-size calculation
    // -------------------------------------------------------------------------
    private float ComputeScreenRelativeSize(Camera3D cam)
    {
        // Get camera position from its Transform3D
        var camT3d = cam.Actor.GetComponent<Transform3D>();
        if (camT3d == null) return 0f;

        var actorPos = GetTransform3D().Position;
        float dist   = Vector3.Distance(camT3d.Position, actorPos);

        if (dist < 1e-4f) return 1f; // camera is inside the bounds

        // Angular size: 2 * atan(r / d), normalised by vertical FOV
        float angularSize = 2f * MathF.Atan(BoundingRadius / dist);
        float fovRad      = MathHelper.ToRadians(cam.FieldOfView);

        // screenRelSize = 1 when the object fills the full screen height
        return angularSize / fovRad;
    }
}
