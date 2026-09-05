using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// Trauma-based camera shake. Add trauma on impacts and it decays on its own.
/// </summary>
/// <remarks>
/// <para>
/// Shake magnitude is <c>trauma^Exponent</c> rather than trauma itself. A linear
/// response makes small hits feel as violent as big ones and makes the tail-off
/// obvious; squaring or cubing gives a sharp initial jolt that fades smoothly,
/// which is the standard trick from Squirrel Eiserloh's "Juicing Your Cameras".
/// </para>
/// <para>
/// Offsets come from value noise sampled along independent seeds per axis, not from
/// <c>Random</c> per frame. Random per frame is uncorrelated between frames, which
/// looks like buzzing static; smooth noise reads as a physical camera being knocked.
/// </para>
/// <para>
/// The component writes an offset on top of whatever else moves the camera, so it
/// composes with a follow camera or a controller: it stores the base pose in
/// LateUpdate, applies the offset for the frame, and restores it at the start of the
/// next one.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var shake = cameraActor.AddComponent&lt;CameraShake&gt;();
/// shake.MaxOffset = 0.6f;
/// shake.MaxRoll   = 4f;
///
/// // On an explosion, scaled by distance:
/// shake.AddTrauma(0.8f * falloff);
/// </code>
/// </example>
public sealed class CameraShake : Component
{
    /// <summary>
    /// Current trauma in 0..1. Set indirectly through <see cref="AddTrauma"/>; decays
    /// at <see cref="DecayPerSecond"/>.
    /// </summary>
    public float Trauma { get; private set; }

    /// <summary>Trauma lost per second. 1 means a full-strength shake lasts one second.</summary>
    public float DecayPerSecond { get; set; } = 1.2f;

    /// <summary>
    /// Exponent applied to trauma to get shake strength. 2 is the usual choice; 3 makes
    /// small hits almost imperceptible and large ones dramatic.
    /// </summary>
    public float Exponent { get; set; } = 2f;

    /// <summary>Maximum positional offset in world units at full trauma.</summary>
    public float MaxOffset { get; set; } = 0.5f;

    /// <summary>Maximum roll in degrees at full trauma. 3D cameras only.</summary>
    public float MaxRoll { get; set; } = 3f;

    /// <summary>How fast the noise is sampled. Higher is more frantic.</summary>
    public float Frequency { get; set; } = 22f;

    /// <summary>Runs on unscaled time, so a hit-stop pause does not freeze the shake.</summary>
    public bool UseUnscaledTime { get; set; } = true;

    /// <summary>The offset applied this frame, for a debug readout.</summary>
    public Vector3 CurrentOffset { get; private set; }

    private Transform3D? _t3d;
    private Transform?   _t2d;

    private Vector3 _basePosition3;
    private float   _baseRoll3;
    private Vector2 _basePosition2;
    private bool    _hasBase;

    private float _noiseTime;

    // Independent seeds so the axes do not move in lockstep, which would read as a
    // single diagonal wobble instead of a shake.
    private readonly float _seedX = SBMath.RandomRange(0f, 1000f);
    private readonly float _seedY = SBMath.RandomRange(0f, 1000f);
    private readonly float _seedZ = SBMath.RandomRange(0f, 1000f);
    private readonly float _seedR = SBMath.RandomRange(0f, 1000f);

    public override void Start()
    {
        _t3d = Actor.GetComponent<Transform3D>();
        _t2d = Actor.Transform;
    }

    /// <summary>
    /// Adds trauma, clamped so repeated hits saturate rather than compound into an
    /// unwatchable screen.
    /// </summary>
    public void AddTrauma(float amount) => Trauma = SBMath.Clamp01(Trauma + amount);

    /// <summary>Stops the shake immediately and restores the camera.</summary>
    public void Reset()
    {
        Trauma = 0f;
        RestoreBase();
    }

    public override void LateUpdate(float dt)
    {
        // Undo last frame's offset before reading the pose, so the base does not
        // drift as offsets accumulate.
        RestoreBase();

        if (Trauma <= 0f)
        {
            CurrentOffset = Vector3.Zero;
            return;
        }

        float step = UseUnscaledTime ? Time.UnscaledDeltaTime : dt;
        _noiseTime += step * Frequency;
        Trauma = MathF.Max(0f, Trauma - DecayPerSecond * step);

        float strength = MathF.Pow(Trauma, Exponent);

        var offset = new Vector3(
            Noise(_seedX) * MaxOffset * strength,
            Noise(_seedY) * MaxOffset * strength,
            Noise(_seedZ) * MaxOffset * strength);

        CurrentOffset = offset;

        if (_t3d != null)
        {
            _basePosition3 = _t3d.LocalPosition;
            _baseRoll3     = _t3d.LocalEulerAngles.Z;
            _hasBase       = true;

            _t3d.LocalPosition = _basePosition3 + offset;
            _t3d.LocalEulerAngles = _t3d.LocalEulerAngles with
            {
                Z = _baseRoll3 + Noise(_seedR) * MaxRoll * strength,
            };
        }
        else if (_t2d != null)
        {
            _basePosition2 = _t2d.LocalPosition;
            _hasBase       = true;
            _t2d.LocalPosition = _basePosition2 + new Vector2(offset.X, offset.Y);
        }
    }

    private void RestoreBase()
    {
        if (!_hasBase) return;
        _hasBase = false;

        if (_t3d != null)
        {
            _t3d.LocalPosition = _basePosition3;
            _t3d.LocalEulerAngles = _t3d.LocalEulerAngles with { Z = _baseRoll3 };
        }
        else if (_t2d != null)
        {
            _t2d.LocalPosition = _basePosition2;
        }
    }

    /// <summary>
    /// Value noise in -1..1: a hash at each integer step, smoothly interpolated between.
    /// </summary>
    /// <remarks>
    /// Deliberately not Perlin — the extra quality buys nothing at this frequency, and a
    /// dozen lines with no lookup tables is easier to reason about than an import.
    /// </remarks>
    private float Noise(float seed)
    {
        float t = _noiseTime + seed;
        int   i = (int)MathF.Floor(t);
        float f = t - i;

        float a = Hash(i);
        float b = Hash(i + 1);

        // Smoothstep the interpolant so the curve has no corner at each integer.
        float u = f * f * (3f - 2f * f);
        return MathHelper.Lerp(a, b, u);
    }

    private static float Hash(int n)
    {
        // Integer bit-mix, then map to -1..1. Deterministic per input, which keeps
        // the noise stable if the same time is sampled twice in a frame.
        unchecked
        {
            uint x = (uint)n * 2654435761u;
            x ^= x >> 15;
            x *= 2246822519u;
            x ^= x >> 13;
            return (x / (float)uint.MaxValue) * 2f - 1f;
        }
    }
}
