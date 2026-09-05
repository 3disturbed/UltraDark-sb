using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Input;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// Shared plumbing for the built-in camera controllers — device access and the
/// <see cref="Transform3D"/> they drive.
/// </summary>
public abstract class CameraControllerBase : Component
{
    /// <summary>Mouse look sensitivity in degrees per pixel.</summary>
    public float LookSensitivity { get; set; } = 0.15f;

    /// <summary>Inverts vertical look.</summary>
    public bool InvertY { get; set; }

    /// <summary>Stops the controller reading input without disabling the component.</summary>
    public bool InputEnabled { get; set; } = true;

    private Transform3D? _t3d;

    /// <summary>The transform this controller moves, created on the actor if absent.</summary>
    protected Transform3D T3D => _t3d ??= Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();

    /// <summary>The engine input manager, or null before the engine has initialised.</summary>
    protected static InputManager? Input => EngineHost.Current?.Input;

    /// <summary>Vertical look angle in degrees, clamped to avoid flipping over the poles.</summary>
    protected float Pitch { get; set; }

    /// <summary>Horizontal look angle in degrees.</summary>
    protected float Yaw { get; set; }

    public override void Start()
    {
        var euler = T3D.EulerAngles;
        Pitch = euler.X;
        Yaw   = euler.Y;
    }

    /// <summary>Applies mouse delta to <see cref="Pitch"/> and <see cref="Yaw"/>.</summary>
    protected void AccumulateMouseLook(InputManager input, float minPitch = -89f, float maxPitch = 89f)
    {
        var delta = input.MouseDelta;
        if (delta == Vector2.Zero) return;

        Yaw   = SBMath.WrapAngle(Yaw + delta.X * LookSensitivity);
        Pitch = MathHelper.Clamp(Pitch + delta.Y * LookSensitivity * (InvertY ? -1f : 1f), minPitch, maxPitch);
    }
}

/// <summary>
/// Free-flying debug camera: WASD to move, mouse to look, Shift to sprint, Q/E for vertical.
/// Drop it on any actor with a <see cref="Camera3D"/> to inspect a scene.
/// </summary>
/// <remarks>
/// Movement runs on unscaled time, so the camera keeps flying while the game is paused —
/// which is usually what you want from a debug camera.
/// </remarks>
/// <example>
/// <code>
/// var camActor = scene.AddActor(new Actor("DebugCam") { Tag = "MainCamera3D" });
/// camActor.AddComponent&lt;Camera3D&gt;();
/// var fly = camActor.AddComponent&lt;FlyCamController&gt;();
/// fly.MoveSpeed = 20f;
/// </code>
/// </example>
public sealed class FlyCamController : CameraControllerBase
{
    /// <summary>Movement speed in world units per second.</summary>
    public float MoveSpeed { get; set; } = 10f;

    /// <summary>Multiplier applied while the sprint key is held.</summary>
    public float SprintMultiplier { get; set; } = 3f;

    /// <summary>Key that engages sprint.</summary>
    public Keys SprintKey { get; set; } = Keys.LeftShift;

    /// <summary>
    /// When true the controller only looks while the right mouse button is held, leaving the
    /// cursor free otherwise. Handy in an editor viewport.
    /// </summary>
    public bool RequireRightMouseToLook { get; set; }

    public override void Update(float dt)
    {
        var input = Input;
        if (!InputEnabled || input == null) return;

        // Debug cameras should keep working while the game is paused.
        float step = Time.UnscaledDeltaTime;

        bool looking = !RequireRightMouseToLook || input.IsMouseButtonDown(MouseButton.Right);
        if (looking) AccumulateMouseLook(input);

        T3D.EulerAngles = new Vector3(Pitch, Yaw, 0f);

        var move = Vector3.Zero;
        if (input.IsKeyDown(Keys.W)) move += T3D.Forward;
        if (input.IsKeyDown(Keys.S)) move -= T3D.Forward;
        if (input.IsKeyDown(Keys.D)) move += T3D.Right;
        if (input.IsKeyDown(Keys.A)) move -= T3D.Right;
        if (input.IsKeyDown(Keys.E)) move += Vector3.Up;
        if (input.IsKeyDown(Keys.Q)) move -= Vector3.Up;

        if (move == Vector3.Zero) return;

        float speed = MoveSpeed * (input.IsKeyDown(SprintKey) ? SprintMultiplier : 1f);
        T3D.Position += SBMath.SafeNormalize(move) * speed * step;
    }
}

/// <summary>
/// Orbits the camera around a target actor: drag to rotate, scroll to zoom.
/// The standard third-person and model-viewer camera.
/// </summary>
/// <remarks>
/// When <see cref="CollisionAvoidance"/> is on the camera pulls in toward the target if the
/// line between them is blocked, so it never ends up inside a wall. That test needs a
/// <see cref="Physics.PhysicsSystem3D"/> in the scene; without one the setting is ignored.
/// </remarks>
public sealed class OrbitCamController : CameraControllerBase
{
    /// <summary>Actor to orbit. When null the controller orbits <see cref="TargetOffset"/> in world space.</summary>
    public Actor? Target { get; set; }

    /// <summary>Offset from the target's position to the orbit centre — typically head height.</summary>
    public Vector3 TargetOffset { get; set; } = new(0f, 1.5f, 0f);

    /// <summary>Current distance from the orbit centre.</summary>
    public float Distance { get; set; } = 6f;

    /// <summary>Closest the camera may orbit.</summary>
    public float MinDistance { get; set; } = 1.5f;

    /// <summary>Furthest the camera may orbit.</summary>
    public float MaxDistance { get; set; } = 25f;

    /// <summary>World units of zoom per scroll notch.</summary>
    public float ZoomSpeed { get; set; } = 2f;

    /// <summary>Lowest pitch in degrees; negative looks down at the target.</summary>
    public float MinPitch { get; set; } = -30f;

    /// <summary>Highest pitch in degrees.</summary>
    public float MaxPitch { get; set; } = 75f;

    /// <summary>Half-life in seconds for position smoothing. Zero snaps.</summary>
    public float SmoothingHalfLife { get; set; } = 0.06f;

    /// <summary>Pulls the camera in when geometry blocks the view of the target.</summary>
    public bool CollisionAvoidance { get; set; } = true;

    /// <summary>Gap kept between the camera and any surface it pulls in against.</summary>
    public float CollisionPadding { get; set; } = 0.3f;

    /// <summary>Requires the left mouse button to be held before the camera rotates.</summary>
    public bool RequireDragToRotate { get; set; }

    private Vector3 _smoothedPosition;
    private bool    _hasSmoothed;

    public override void Update(float dt)
    {
        var input = Input;
        if (input == null) return;

        if (InputEnabled)
        {
            bool rotating = !RequireDragToRotate || input.IsMouseButtonDown(MouseButton.Left);
            if (rotating) AccumulateMouseLook(input, MinPitch, MaxPitch);

            float scroll = input.ScrollDelta;
            if (scroll != 0f)
                Distance = MathHelper.Clamp(Distance - scroll * ZoomSpeed, MinDistance, MaxDistance);
        }

        var centre = (Target?.GetComponent<Transform3D>()?.Position ?? Vector3.Zero) + TargetOffset;

        var rotation = Quaternion.CreateFromYawPitchRoll(
            MathHelper.ToRadians(Yaw), MathHelper.ToRadians(Pitch), 0f);
        var offset   = Vector3.Transform(Vector3.Backward, rotation) * Distance;
        var desired  = centre + offset;

        if (CollisionAvoidance) desired = PullInOnBlockedView(centre, desired);

        if (!_hasSmoothed)
        {
            _smoothedPosition = desired;
            _hasSmoothed = true;
        }
        else
        {
            _smoothedPosition = SBMath.Damp(_smoothedPosition, desired, SmoothingHalfLife, dt);
        }

        T3D.Position = _smoothedPosition;
        T3D.LookAt(centre);
    }

    private Vector3 PullInOnBlockedView(Vector3 centre, Vector3 desired)
    {
        var toCamera = desired - centre;
        float length = toCamera.Length();
        if (length < SBMath.Epsilon) return desired;

        var direction = toCamera / length;
        if (!Physics.PhysicsSystem3D.Instance.Raycast(centre, direction, length, out var hit))
            return desired;

        float safe = MathF.Max(MinDistance, hit.Distance - CollisionPadding);
        return centre + direction * safe;
    }
}

/// <summary>
/// A first-person camera that rides on a pawn: yaw turns the body, pitch tilts only the view,
/// with optional head-bob while moving.
/// </summary>
/// <remarks>
/// Attach this to the camera actor and point <see cref="Body"/> at the character. Keeping the
/// camera on its own actor is what lets pitch move independently of the character's facing.
/// </remarks>
public sealed class FirstPersonController : CameraControllerBase
{
    /// <summary>The actor whose yaw this controller drives. Usually the character.</summary>
    public Actor? Body { get; set; }

    /// <summary>Camera height above the body's origin.</summary>
    public float EyeHeight { get; set; } = 1.7f;

    /// <summary>Enables the vertical head-bob while the body is moving.</summary>
    public bool HeadBob { get; set; } = true;

    /// <summary>Head-bob travel in world units.</summary>
    public float BobAmplitude { get; set; } = 0.05f;

    /// <summary>Head-bob cycles per second at full speed.</summary>
    public float BobFrequency { get; set; } = 8f;

    /// <summary>Speed above which head-bob reaches full amplitude.</summary>
    public float BobFullSpeed { get; set; } = 4f;

    private float   _bobPhase;
    private Vector3 _lastBodyPosition;
    private bool    _hasLastPosition;

    public override void Update(float dt)
    {
        var input = Input;
        if (InputEnabled && input != null) AccumulateMouseLook(input);

        // Yaw drives the body so the character turns; pitch stays on the camera alone.
        var bodyT = Body?.GetComponent<Transform3D>();
        if (bodyT != null)
            bodyT.EulerAngles = bodyT.EulerAngles with { Y = Yaw };

        T3D.EulerAngles = new Vector3(Pitch, Yaw, 0f);

        var basePosition = (bodyT?.Position ?? T3D.Position) + Vector3.Up * EyeHeight;

        if (HeadBob && bodyT != null)
        {
            float speed = 0f;
            if (_hasLastPosition && dt > 0f)
            {
                var travelled = bodyT.Position - _lastBodyPosition;
                travelled.Y = 0f;
                speed = travelled.Length() / dt;
            }

            _lastBodyPosition = bodyT.Position;
            _hasLastPosition  = true;

            float intensity = SBMath.Clamp01(speed / MathF.Max(SBMath.Epsilon, BobFullSpeed));
            _bobPhase += dt * BobFrequency * intensity;
            basePosition.Y += MathF.Sin(_bobPhase * MathF.Tau) * BobAmplitude * intensity;
        }

        T3D.Position = basePosition;
    }
}
