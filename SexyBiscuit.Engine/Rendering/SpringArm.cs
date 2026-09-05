using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// A camera boom: holds a child at a fixed distance behind its owner, pulls in when
/// geometry blocks the view, and smooths the result. Modelled on Unreal's
/// <c>USpringArmComponent</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is how a third-person camera is built. The pawn owns the arm, the arm owns the
/// camera, and the camera never needs to know about collision or lag — which is what lets
/// you swap cameras without reimplementing the follow behaviour every time.
/// </para>
/// <para>
/// Position lag and rotation lag are separate on purpose. Lagging position gives weight
/// to the camera without making aiming feel mushy; lagging rotation as well is what you
/// want for a cinematic feel and exactly what you do not want in a shooter.
/// </para>
/// <para>
/// The collision test is a ray from the pivot outward. It runs from the pivot rather than
/// from the camera because a camera already inside a wall has nothing useful to cast
/// against — the question is "how far along this arm can I get", and only the pivot end is
/// reliably in open space.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var arm = character.AddComponent&lt;SpringArm&gt;();
/// arm.TargetArmLength = 4.5f;
/// arm.SocketOffset    = new Vector3(0.6f, 0.4f, 0f);   // over the shoulder
/// arm.EnableCameraLag = true;
///
/// var camActor = scene.AddActor(new Actor("Camera") { Tag = "MainCamera3D" });
/// camActor.AddComponent&lt;Camera3D&gt;();
/// arm.AttachCamera(camActor.AddComponent&lt;Transform3D&gt;());
/// </code>
/// </example>
public sealed class SpringArm : Component
{
    /// <summary>Distance from the pivot to the camera, in world units.</summary>
    public float TargetArmLength { get; set; } = 5f;

    /// <summary>Offset of the pivot from the actor's origin — usually head height.</summary>
    public Vector3 TargetOffset { get; set; } = new(0f, 1.6f, 0f);

    /// <summary>
    /// Offset applied at the camera end, in the arm's own space. X shifts the camera to
    /// the side for an over-the-shoulder view without changing what it orbits.
    /// </summary>
    public Vector3 SocketOffset { get; set; }

    /// <summary>Pulls the camera in when something blocks the line from the pivot.</summary>
    public bool DoCollisionTest { get; set; } = true;

    /// <summary>Radius kept clear of the blocking surface.</summary>
    public float ProbeSize { get; set; } = 0.25f;

    /// <summary>Closest the arm may retract to. Stops the camera ending up inside the pawn.</summary>
    public float MinArmLength { get; set; } = 0.6f;

    /// <summary>Smooths the camera's position rather than snapping it.</summary>
    public bool EnableCameraLag { get; set; }

    /// <summary>Half-life in seconds for position smoothing. Larger is heavier.</summary>
    public float CameraLagHalfLife { get; set; } = 0.08f;

    /// <summary>Smooths the arm's rotation as well as its position.</summary>
    public bool EnableCameraRotationLag { get; set; }

    /// <summary>Half-life in seconds for rotation smoothing.</summary>
    public float RotationLagHalfLife { get; set; } = 0.06f;

    /// <summary>
    /// Uses the owning pawn's <see cref="Gameplay.Pawn.ControlRotation"/> instead of the
    /// actor's own, so the camera follows where the player is looking rather than where
    /// the body is facing.
    /// </summary>
    public bool UseControlRotation { get; set; } = true;

    /// <summary>The transform this arm positions. Set through <see cref="AttachCamera"/>.</summary>
    public Transform3D? AttachedTransform { get; private set; }

    /// <summary>Arm length after collision, for a debug readout.</summary>
    public float CurrentArmLength { get; private set; }

    private Transform3D _transform = null!;
    private Gameplay.Pawn? _pawn;

    private Vector3    _smoothedPosition;
    private Quaternion _smoothedRotation = Quaternion.Identity;
    private bool       _initialised;

    /// <summary>Attaches a transform — normally a camera's — to the far end of the arm.</summary>
    public void AttachCamera(Transform3D cameraTransform) => AttachedTransform = cameraTransform;

    public override void Start()
    {
        _transform = Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();
        _pawn      = Actor as Gameplay.Pawn;
        CurrentArmLength = TargetArmLength;
    }

    // LateUpdate, so the arm sees the pawn's final position for the frame rather than
    // trailing it by one.
    public override void LateUpdate(float dt)
    {
        if (AttachedTransform == null) return;

        var pivot = _transform.Position + TargetOffset;

        var desiredRotation = UseControlRotation && _pawn != null
            ? Quaternion.CreateFromYawPitchRoll(
                MathHelper.ToRadians(_pawn.ControlRotation.Y),
                MathHelper.ToRadians(_pawn.ControlRotation.X),
                0f)
            : _transform.Rotation;

        if (!_initialised)
        {
            _smoothedRotation = desiredRotation;
            _smoothedPosition = pivot;
            _initialised = true;
        }

        _smoothedRotation = EnableCameraRotationLag
            ? DampRotation(_smoothedRotation, desiredRotation, RotationLagHalfLife, dt)
            : desiredRotation;

        _smoothedPosition = EnableCameraLag
            ? SBMath.Damp(_smoothedPosition, pivot, CameraLagHalfLife, dt)
            : pivot;

        var back  = Vector3.Transform(Vector3.Backward, _smoothedRotation);
        float arm = DoCollisionTest ? ProbeArmLength(_smoothedPosition, back) : TargetArmLength;
        CurrentArmLength = arm;

        var socket = Vector3.Transform(SocketOffset, _smoothedRotation);

        AttachedTransform.Position = _smoothedPosition + back * arm + socket;
        AttachedTransform.Rotation = _smoothedRotation;
    }

    /// <summary>Returns how far along the arm the camera can sit without clipping.</summary>
    private float ProbeArmLength(Vector3 pivot, Vector3 direction)
    {
        if (!Physics.PhysicsSystem3D.Instance.Raycast(pivot, direction, TargetArmLength, out var hit))
            return TargetArmLength;

        return MathF.Max(MinArmLength, hit.Distance - ProbeSize);
    }

    /// <summary>
    /// Framerate-independent rotational smoothing, matching <see cref="SBMath.Damp(float,float,float,float)"/>.
    /// </summary>
    private static Quaternion DampRotation(Quaternion current, Quaternion target, float halfLife, float dt)
    {
        if (halfLife <= 0f) return target;

        float t = 1f - MathF.Exp(-MathF.Log(2f) * dt / halfLife);
        return Quaternion.Slerp(current, target, MathHelper.Clamp(t, 0f, 1f));
    }
}
