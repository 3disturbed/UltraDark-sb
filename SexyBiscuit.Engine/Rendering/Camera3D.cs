using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// 3D perspective or orthographic camera component.
/// Reads position/orientation from the Actor's Transform3D.
/// Tag the actor "MainCamera3D" to make it the static Main camera.
/// </summary>
public sealed class Camera3D : Component
{
    // -------------------------------------------------------------------------
    // Static registry
    // -------------------------------------------------------------------------
    private static readonly List<Camera3D> _all = new();

    /// <summary>
    /// The camera the local player looks through, set by <see cref="Gameplay.PlayerController"/>
    /// when the pawn it possesses carries one. While it is alive, enabled and on an active actor
    /// it is what <see cref="Main"/> returns, whatever the scene's tags say: the possessed
    /// player's view must win over a preview camera left in the level.
    /// </summary>
    public static Camera3D? PlayerView { get; set; }

    /// <summary>
    /// The player's view camera when there is one, otherwise the first active Camera3D whose
    /// actor is tagged "MainCamera3D".
    /// </summary>
    public static Camera3D? Main
    {
        get
        {
            if (PlayerView is { Enabled: true } view && view.Actor != null && view.Actor.IsActive && _all.Contains(view))
                return view;

            foreach (var cam in _all)
                if (cam.Enabled && cam.Actor.IsActive && cam.Actor.Tag == "MainCamera3D")
                    return cam;
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Projection properties
    // -------------------------------------------------------------------------
    /// <summary>Vertical field of view in degrees. Default 60°.</summary>
    public float FieldOfView { get; set; } = 60f;

    public float NearClip { get; set; } = 0.1f;
    public float FarClip  { get; set; } = 1000f;

    public bool  IsOrthographic { get; set; } = false;

    /// <summary>Half-height of the orthographic view volume in world units.</summary>
    public float OrthoSize { get; set; } = 5f;

    // -------------------------------------------------------------------------
    // Cached Transform3D
    // -------------------------------------------------------------------------
    private Transform3D? _t3d;

    /// <summary>The transform this camera views from, created on the actor if absent.</summary>
    public Transform3D GetTransform3D()
    {
        if (_t3d != null) return _t3d;
        _t3d = Actor.GetComponent<Transform3D>();
        if (_t3d == null)
            _t3d = Actor.AddComponent<Transform3D>();
        return _t3d;
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------
    public override void Awake()
    {
        _all.Add(this);
    }

    public override void OnDestroy()
    {
        _all.Remove(this);
        if (ReferenceEquals(PlayerView, this)) PlayerView = null;
    }

    // -------------------------------------------------------------------------
    // View / Projection
    // -------------------------------------------------------------------------
    /// <summary>Returns the view matrix derived from this camera's Transform3D.</summary>
    public Matrix GetViewMatrix()
    {
        var t = GetTransform3D();
        return Matrix.CreateLookAt(t.Position, t.Position + t.Forward, t.Up);
    }

    /// <summary>Returns the projection matrix for the given aspect ratio.</summary>
    public Matrix GetProjectionMatrix(float aspectRatio)
    {
        if (IsOrthographic)
        {
            float halfH = OrthoSize;
            float halfW = halfH * aspectRatio;
            return Matrix.CreateOrthographicOffCenter(-halfW, halfW, -halfH, halfH, NearClip, FarClip);
        }

        return Matrix.CreatePerspectiveFieldOfView(
            MathHelper.ToRadians(FieldOfView),
            aspectRatio,
            NearClip,
            FarClip);
    }

    // -------------------------------------------------------------------------
    // Screen-to-world ray
    // -------------------------------------------------------------------------
    /// <summary>
    /// Unprojcts a screen-space position into a world-space ray.
    /// Returns (origin, direction) — direction is normalised.
    /// </summary>
    public (Vector3 origin, Vector3 direction) ScreenToWorldRay(Vector2 screenPos, GraphicsDevice gd)
    {
        var vp     = gd.Viewport;
        float ar   = vp.AspectRatio;
        var view   = GetViewMatrix();
        var proj   = GetProjectionMatrix(ar);

        // Near and far unproject points
        var nearPt = vp.Unproject(
            new Vector3(screenPos.X, screenPos.Y, 0f),
            proj, view, Matrix.Identity);

        var farPt = vp.Unproject(
            new Vector3(screenPos.X, screenPos.Y, 1f),
            proj, view, Matrix.Identity);

        var dir = farPt - nearPt;
        if (dir.LengthSquared() > 0f)
            dir = Vector3.Normalize(dir);

        return (nearPt, dir);
    }
}
