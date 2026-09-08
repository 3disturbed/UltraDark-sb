using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// 3D perspective or orthographic camera component.
/// Reads position/orientation from the Actor's Transform3D.
/// Tag the actor "MainCamera3D" to make it the static Main camera; "MainCamera" and
/// then any live camera are the fallbacks, in that order.
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

    /// <summary>Drops every registered camera. A test that leaks one poisons the next.</summary>
    internal static void ClearAll()
    {
        _all.Clear();
        PlayerView = null;
    }

    /// <summary>
    /// The player's view camera when there is one, otherwise the best active Camera3D:
    /// one tagged "MainCamera3D", failing that one tagged "MainCamera", failing that
    /// whichever was added first.
    /// </summary>
    /// <remarks>
    /// The fallback chain is not politeness, it is parity. This used to return null unless
    /// something carried the exact "MainCamera3D" tag while the browser fell back to
    /// "MainCamera" and then to any camera at all, so the bundled <c>3D Scene</c> template —
    /// whose camera is tagged "MainCamera" — rendered in the browser and rendered nothing
    /// natively, because <c>RenderSystem3D.Render</c> returns immediately on a null camera.
    /// A world-space <c>UiCanvas</c> resolves its camera through this property and would have
    /// vanished the same way. <c>CameraParityTests</c> pins the order.
    /// </remarks>
    public static Camera3D? Main
    {
        get
        {
            if (PlayerView is { Enabled: true } view && view.Actor != null && view.Actor.IsActive && _all.Contains(view))
                return view;

            return Tagged("MainCamera3D") ?? Tagged("MainCamera") ?? FirstLive();

            static Camera3D? Tagged(string tag)
            {
                foreach (var cam in _all)
                    if (Live(cam) && cam.Actor!.Tag == tag) return cam;
                return null;
            }

            static Camera3D? FirstLive()
            {
                foreach (var cam in _all)
                    if (Live(cam)) return cam;
                return null;
            }

            static bool Live(Camera3D cam) => cam.Enabled && cam.Actor != null && cam.Actor.IsActive;
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
        => ScreenToWorldRay(screenPos, gd.Viewport.Width, gd.Viewport.Height);

    /// <summary>
    /// A ray from the camera through a point on screen, for picking. The direction is
    /// normalised.
    /// </summary>
    /// <remarks>
    /// Takes the viewport as two numbers rather than a <see cref="GraphicsDevice"/> so it can
    /// be called with no device at all — which is what lets a headless test and the browser
    /// read the same fixture. The device overload is sugar over this one.
    /// <para>
    /// The arithmetic is the browser's, deliberately, rather than <c>Viewport.Unproject</c>.
    /// The two agree to about a pixel for an ordinary camera and drift apart at wide fields of
    /// view, and two implementations of one answer is exactly the thing a shared fixture
    /// cannot pin. One implementation, mirrored.
    /// </para>
    /// </remarks>
    public (Vector3 origin, Vector3 direction) ScreenToWorldRay(Vector2 screenPos, float viewportWidth, float viewportHeight)
    {
        float width  = MathF.Max(1f, viewportWidth);
        float height = MathF.Max(1f, viewportHeight);

        // Screen pixels into normalised device coordinates, where Y is flipped because
        // screen Y grows downwards and clip space Y grows upwards.
        float ndcX = screenPos.X / width * 2f - 1f;
        float ndcY = 1f - screenPos.Y / height * 2f;

        Transform3D t = GetTransform3D();
        float aspect  = width / height;

        if (IsOrthographic)
        {
            float halfHeight = OrthoSize;
            float halfWidth  = halfHeight * aspect;
            Vector3 offset   = t.Right * (ndcX * halfWidth) + t.Up * (ndcY * halfHeight);
            return (t.Position + offset, Normalised(t.Forward));
        }

        float tanHalfFov = MathF.Tan(MathHelper.ToRadians(FieldOfView) * 0.5f);

        Vector3 direction = t.Forward
                          + t.Right * (ndcX * tanHalfFov * aspect)
                          + t.Up    * (ndcY * tanHalfFov);

        return (t.Position, Normalised(direction));
    }

    // -------------------------------------------------------------------------
    // World-to-screen
    // -------------------------------------------------------------------------

    /// <summary>
    /// Projects a world point to screen pixels, or null when it is behind the camera.
    /// </summary>
    /// <remarks>
    /// The null is the point of it. A point behind the camera has a negative <c>w</c>, and the
    /// perspective divide flips it to the opposite side of the screen rather than hiding it —
    /// so a caller that projects without checking draws a nameplate for the enemy standing
    /// behind them. <see cref="Rendering.TextRenderer3D"/> guards this by hand with a view-space
    /// Z test; this returns null instead, which is what the browser has always done.
    /// </remarks>
    public Vector2? WorldToScreen(Vector3 worldPoint, float viewportWidth, float viewportHeight)
    {
        float width  = MathF.Max(1f, viewportWidth);
        float height = MathF.Max(1f, viewportHeight);

        Matrix vp = GetViewMatrix() * GetProjectionMatrix(width / height);

        float w = worldPoint.X * vp.M14 + worldPoint.Y * vp.M24 + worldPoint.Z * vp.M34 + vp.M44;
        if (w <= 0f) return null;

        float clipX = (worldPoint.X * vp.M11 + worldPoint.Y * vp.M21 + worldPoint.Z * vp.M31 + vp.M41) / w;
        float clipY = (worldPoint.X * vp.M12 + worldPoint.Y * vp.M22 + worldPoint.Z * vp.M32 + vp.M42) / w;

        return new Vector2(
            (clipX * 0.5f + 0.5f) * width,
            (1f - (clipY * 0.5f + 0.5f)) * height);
    }

    /// <summary>Projects a world point to screen pixels using the device's viewport.</summary>
    public Vector2? WorldToScreen(Vector3 worldPoint, GraphicsDevice gd)
        => WorldToScreen(worldPoint, gd.Viewport.Width, gd.Viewport.Height);

    private static Vector3 Normalised(Vector3 v)
        => v.LengthSquared() > 0f ? Vector3.Normalize(v) : v;
}
