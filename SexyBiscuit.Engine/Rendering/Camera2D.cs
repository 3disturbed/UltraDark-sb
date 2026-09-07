using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.UI;   // RectangleF

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// 2D camera component. Attach to an Actor to define the viewpoint for a scene.
/// Supports zoom, world-bounds clamping, smooth follow with deadzone, and trauma-based shake.
/// </summary>
public class Camera2D : Component
{
    // -------------------------------------------------------------------------
    // Which camera the 2D pass draws through
    // -------------------------------------------------------------------------

    private static readonly List<Camera2D> _all = new();

    /// <summary>
    /// The first active camera whose actor is tagged "MainCamera", else the first active one
    /// at all.
    /// </summary>
    /// <remarks>
    /// Camera3D has had this since it existed; Camera2D had no equivalent, which is part of
    /// why the 2D pass rendered with no camera at all and every camera-follow script looked
    /// broken in a native build while working in the browser. The fallback matches the
    /// browser's <c>Camera2D.main</c>: a scene with one untagged camera still draws through
    /// it rather than through nothing.
    /// </remarks>
    public static Camera2D? Main
    {
        get
        {
            foreach (var camera in _all)
                if (camera.Actor is { IsActive: true, Tag: "MainCamera" }) return camera;

            foreach (var camera in _all)
                if (camera.Actor is { IsActive: true }) return camera;

            return null;
        }
    }

    /// <inheritdoc />
    public override void Awake() => _all.Add(this);

    /// <inheritdoc />
    public override void OnDestroy() => _all.Remove(this);

    // -------------------------------------------------------------------------
    // Zoom
    // -------------------------------------------------------------------------

    private float _zoom = 1f;

    /// <summary>Current zoom level. Clamped between MinZoom and MaxZoom.</summary>
    public float Zoom
    {
        get => _zoom;
        set => _zoom = Math.Clamp(value, MinZoom, MaxZoom);
    }

    public float MinZoom { get; set; } = 0.1f;
    public float MaxZoom { get; set; } = 10f;

    // -------------------------------------------------------------------------
    // World bounds clamping
    // -------------------------------------------------------------------------

    /// <summary>
    /// Optional world-space rectangle the camera centre is clamped within.
    /// When null no clamping is applied.
    /// </summary>
    public Rectangle? Bounds { get; set; }

    // -------------------------------------------------------------------------
    // Follow behaviour
    // -------------------------------------------------------------------------

    /// <summary>Inner class managing smooth camera following of an Actor target.</summary>
    public CameraFollow Follow { get; } = new CameraFollow();

    // -------------------------------------------------------------------------
    // Shake (trauma-based)
    // -------------------------------------------------------------------------

    private float _trauma;        // 0..1, decays over shake duration
    private float _shakeDuration;
    private float _shakeTimer;

    // Random number generator for shake offsets
    private static readonly Random _rng = new Random();

    /// <summary>Current raw shake offset applied on top of the camera position.</summary>
    private Vector2 _shakeOffset;
    private float   _shakeAngleOffset;

    /// <summary>Maximum pixel displacement at full trauma.</summary>
    public float ShakeMaxOffset { get; set; } = 20f;

    /// <summary>Maximum rotational displacement (radians) at full trauma.</summary>
    public float ShakeMaxAngle { get; set; } = 0.05f;

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public override void Update(float dt)
    {
        // Tick shake
        if (_shakeTimer > 0f)
        {
            _shakeTimer -= dt;
            _trauma = Math.Clamp(_shakeTimer / _shakeDuration, 0f, 1f);

            float shake = _trauma * _trauma; // squaring gives more punch
            _shakeOffset = new Vector2(
                ((float)_rng.NextDouble() * 2f - 1f) * ShakeMaxOffset * shake,
                ((float)_rng.NextDouble() * 2f - 1f) * ShakeMaxOffset * shake);
            _shakeAngleOffset = ((float)_rng.NextDouble() * 2f - 1f) * ShakeMaxAngle * shake;
        }
        else
        {
            _trauma = 0f;
            _shakeOffset = Vector2.Zero;
            _shakeAngleOffset = 0f;
        }
    }

    public override void LateUpdate(float dt)
    {
        Follow.Apply(this, dt);
        ClampToBounds();
    }

    // -------------------------------------------------------------------------
    // View matrix
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the view matrix suitable for passing to SpriteBatch.Begin as the transformMatrix.
    /// Encodes camera position (with shake), rotation and zoom centred on the viewport.
    /// </summary>
    public Matrix GetViewMatrix(GraphicsDevice gd)
    {
        Vector2 camPos = Actor.Transform.Position + _shakeOffset;
        float   camRot = Actor.Transform.Rotation + _shakeAngleOffset;

        // gd.Viewport, deliberately: when a post-process pass has a render target bound this
        // is that target's size, which is the rectangle the world should be centred in.
        Vector2 viewport = new Vector2(gd.Viewport.Width, gd.Viewport.Height);

        // Translate so camera position maps to screen centre, then rotate and zoom
        return Matrix.CreateTranslation(-camPos.X, -camPos.Y, 0f)
             * Matrix.CreateRotationZ(-camRot)
             * Matrix.CreateScale(_zoom, _zoom, 1f)
             * Matrix.CreateTranslation(viewport.X * 0.5f, viewport.Y * 0.5f, 0f);
    }

    /// <summary>
    /// The world-space rectangle this camera can currently see, with a margin.
    /// </summary>
    /// <remarks>
    /// Conservative on purpose, and in three ways: the half-extent is the
    /// viewport's own diagonal rather than its width and height, so any camera
    /// rotation or shake angle is already covered; the zoom divides it; and a
    /// margin is added on top. Nothing that could appear on screen may ever fall
    /// outside this rectangle — a cull that is slightly too generous costs a few
    /// sprites, and one that is slightly too tight makes the world flicker at the
    /// edge of the window, which is far harder to recognise as a culling bug.
    /// </remarks>
    /// <param name="gd">The device whose viewport is being drawn into.</param>
    /// <param name="margin">Extra world units on every side.</param>
    public RectangleF VisibleWorldBounds(GraphicsDevice gd, float margin = 64f)
    {
        Vector2 camPos = Actor.Transform.Position + _shakeOffset;

        float halfW = gd.Viewport.Width * 0.5f;
        float halfH = gd.Viewport.Height * 0.5f;
        float reach = MathF.Sqrt(halfW * halfW + halfH * halfH) / MathF.Max(_zoom, 0.0001f) + margin;

        return new RectangleF(camPos.X - reach, camPos.Y - reach, reach * 2f, reach * 2f);
    }

    // -------------------------------------------------------------------------
    // Coordinate conversion
    // -------------------------------------------------------------------------

    /// <summary>Converts a screen-space position to world-space coordinates.</summary>
    public Vector2 ScreenToWorld(Vector2 screenPos, GraphicsDevice gd)
    {
        Matrix inv = Matrix.Invert(GetViewMatrix(gd));
        return Vector2.Transform(screenPos, inv);
    }

    /// <summary>Converts a world-space position to screen-space coordinates.</summary>
    public Vector2 WorldToScreen(Vector2 worldPos, GraphicsDevice gd)
    {
        return Vector2.Transform(worldPos, GetViewMatrix(gd));
    }

    // -------------------------------------------------------------------------
    // Shake API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Trigger a screen shake.
    /// </summary>
    /// <param name="intensity">Scalar 0..1 controlling maximum displacement. Values &gt;1 are accepted but clamped in feel.</param>
    /// <param name="duration">How long (seconds) the shake lasts.</param>
    public void Shake(float intensity, float duration)
    {
        float clamped  = Math.Clamp(intensity, 0f, 1f);
        _shakeDuration = MathF.Max(duration, 0.01f);
        _shakeTimer    = _shakeDuration;
        _trauma        = clamped;
    }

    // -------------------------------------------------------------------------
    // Bounds clamping
    // -------------------------------------------------------------------------

    private void ClampToBounds()
    {
        if (Bounds == null) return;

        var pos = Actor.Transform.Position;
        pos.X = Math.Clamp(pos.X, Bounds.Value.Left, Bounds.Value.Right);
        pos.Y = Math.Clamp(pos.Y, Bounds.Value.Top,  Bounds.Value.Bottom);
        Actor.Transform.Position = pos;
    }

    // =========================================================================
    // Inner class: CameraFollow
    // =========================================================================

    /// <summary>
    /// Drives smooth camera following of a target Actor with deadzone and lerp speed.
    /// </summary>
    public sealed class CameraFollow
    {
        /// <summary>Actor the camera tracks. Null disables following.</summary>
        public Actor? Target { get; set; }

        /// <summary>
        /// Lerp speed (units per second, conceptually). Higher = snappier.
        /// The actual interpolation factor per frame is: 1 - exp(-LerpSpeed * dt).
        /// </summary>
        public float LerpSpeed { get; set; } = 5f;

        /// <summary>Constant world-space offset added to the target position before lerping.</summary>
        public Vector2 Offset { get; set; } = Vector2.Zero;

        /// <summary>
        /// Rectangular half-extents (world units) of the deadzone centred on the current camera position.
        /// The camera only starts moving when the target leaves this area.
        /// Set to Vector2.Zero to disable the deadzone.
        /// </summary>
        public Vector2 Deadzone { get; set; } = Vector2.Zero;

        /// <summary>Called by Camera2D.LateUpdate to apply the follow logic.</summary>
        internal void Apply(Camera2D camera, float dt)
        {
            if (Target == null) return;

            Vector2 targetPos = Target.Transform.Position + Offset;
            Vector2 camPos    = camera.Actor.Transform.Position;

            // Deadzone: if the target is within the deadzone, don't move
            if (Deadzone.X > 0f || Deadzone.Y > 0f)
            {
                Vector2 delta = targetPos - camPos;

                float clampedX = Math.Clamp(delta.X, -Deadzone.X, Deadzone.X);
                float clampedY = Math.Clamp(delta.Y, -Deadzone.Y, Deadzone.Y);

                // Desired position is as close as possible while keeping target inside deadzone
                targetPos = camPos + (delta - new Vector2(clampedX, clampedY));

                // If target is fully inside the deadzone, no movement needed
                if (Vector2.DistanceSquared(targetPos, camPos) < 0.01f)
                    return;
            }

            // Exponential lerp — frame-rate independent
            float t = 1f - MathF.Exp(-LerpSpeed * dt);
            camera.Actor.Transform.Position = Vector2.Lerp(camPos, targetPos, t);
        }
    }
}
