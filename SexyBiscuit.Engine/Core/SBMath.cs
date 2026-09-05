using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.Core;

/// <summary>
/// Gameplay maths helpers that MonoGame's <see cref="MathHelper"/> does not provide —
/// framerate-independent damping, angle wrapping, remapping and easing-friendly curves.
/// </summary>
public static class SBMath
{
    /// <summary>A small value suitable for float equality and length comparisons.</summary>
    public const float Epsilon = 1e-5f;

    public const float Deg2Rad = MathF.PI / 180f;
    public const float Rad2Deg = 180f / MathF.PI;

    // -------------------------------------------------------------------------
    // Comparison
    // -------------------------------------------------------------------------

    /// <summary>True when two floats are within <paramref name="tolerance"/> of each other.</summary>
    public static bool Approximately(float a, float b, float tolerance = Epsilon)
        => MathF.Abs(a - b) <= tolerance;

    // -------------------------------------------------------------------------
    // Ranges
    // -------------------------------------------------------------------------

    public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>Fraction of the way <paramref name="value"/> lies between <paramref name="a"/> and <paramref name="b"/>, clamped to 0..1.</summary>
    public static float InverseLerp(float a, float b, float value)
        => Approximately(a, b) ? 0f : Clamp01((value - a) / (b - a));

    /// <summary>Maps <paramref name="value"/> from the range <c>inMin..inMax</c> onto <c>outMin..outMax</c>.</summary>
    public static float Remap(float value, float inMin, float inMax, float outMin, float outMax)
        => Approximately(inMin, inMax)
            ? outMin
            : outMin + (value - inMin) * (outMax - outMin) / (inMax - inMin);

    /// <summary>Hermite interpolation between 0 and 1 — an S-curve with zero derivative at both ends.</summary>
    public static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = InverseLerp(edge0, edge1, x);
        return t * t * (3f - 2f * t);
    }

    // -------------------------------------------------------------------------
    // Framerate-independent interpolation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Exponential smoothing that produces the same result regardless of framerate.
    /// <paramref name="halfLife"/> is the time in seconds for the gap to halve;
    /// a half-life of 0 snaps immediately.
    /// </summary>
    /// <remarks>
    /// Prefer this over <c>Lerp(current, target, 0.1f)</c> in an Update — that idiom
    /// converges faster at high framerates and slower at low ones.
    /// </remarks>
    public static float Damp(float current, float target, float halfLife, float dt)
    {
        if (halfLife <= 0f) return target;
        return target + (current - target) * MathF.Exp(-MathF.Log(2f) * dt / halfLife);
    }

    /// <inheritdoc cref="Damp(float,float,float,float)"/>
    public static Vector2 Damp(Vector2 current, Vector2 target, float halfLife, float dt)
    {
        if (halfLife <= 0f) return target;
        float k = MathF.Exp(-MathF.Log(2f) * dt / halfLife);
        return target + (current - target) * k;
    }

    /// <inheritdoc cref="Damp(float,float,float,float)"/>
    public static Vector3 Damp(Vector3 current, Vector3 target, float halfLife, float dt)
    {
        if (halfLife <= 0f) return target;
        float k = MathF.Exp(-MathF.Log(2f) * dt / halfLife);
        return target + (current - target) * k;
    }

    /// <summary>Moves <paramref name="current"/> toward <paramref name="target"/> by at most <paramref name="maxDelta"/>.</summary>
    public static float MoveTowards(float current, float target, float maxDelta)
    {
        float diff = target - current;
        return MathF.Abs(diff) <= maxDelta ? target : current + MathF.Sign(diff) * maxDelta;
    }

    /// <inheritdoc cref="MoveTowards(float,float,float)"/>
    public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDelta)
    {
        var diff = target - current;
        float len = diff.Length();
        return len <= maxDelta || len < Epsilon ? target : current + diff / len * maxDelta;
    }

    // -------------------------------------------------------------------------
    // Angles
    // -------------------------------------------------------------------------

    /// <summary>Wraps an angle in degrees into the range -180..180.</summary>
    public static float WrapAngle(float degrees)
    {
        degrees %= 360f;
        if (degrees > 180f)  degrees -= 360f;
        if (degrees < -180f) degrees += 360f;
        return degrees;
    }

    /// <summary>Shortest signed difference between two angles in degrees, in the range -180..180.</summary>
    public static float DeltaAngle(float from, float to) => WrapAngle(to - from);

    /// <summary>Interpolates between two angles in degrees, taking the shortest path around the circle.</summary>
    public static float LerpAngle(float from, float to, float t)
        => from + DeltaAngle(from, to) * Clamp01(t);

    /// <summary>Rotates <paramref name="from"/> toward <paramref name="to"/> by at most <paramref name="maxDelta"/> degrees.</summary>
    public static float MoveTowardsAngle(float from, float to, float maxDelta)
        => from + MathF.Min(MathF.Abs(DeltaAngle(from, to)), maxDelta) * MathF.Sign(DeltaAngle(from, to));

    // -------------------------------------------------------------------------
    // Vectors
    // -------------------------------------------------------------------------

    /// <summary>Normalises a vector, returning <see cref="Vector3.Zero"/> rather than NaN for a zero-length input.</summary>
    public static Vector3 SafeNormalize(Vector3 v)
    {
        float lenSq = v.LengthSquared();
        return lenSq < Epsilon * Epsilon ? Vector3.Zero : v / MathF.Sqrt(lenSq);
    }

    /// <inheritdoc cref="SafeNormalize(Vector3)"/>
    public static Vector2 SafeNormalize(Vector2 v)
    {
        float lenSq = v.LengthSquared();
        return lenSq < Epsilon * Epsilon ? Vector2.Zero : v / MathF.Sqrt(lenSq);
    }

    /// <summary>Signed angle in degrees from <paramref name="from"/> to <paramref name="to"/> about <paramref name="axis"/>.</summary>
    public static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis)
    {
        float unsigned = MathF.Acos(MathHelper.Clamp(
            Vector3.Dot(SafeNormalize(from), SafeNormalize(to)), -1f, 1f)) * Rad2Deg;
        return unsigned * MathF.Sign(Vector3.Dot(axis, Vector3.Cross(from, to)));
    }

    /// <summary>Projects <paramref name="v"/> onto the plane defined by <paramref name="planeNormal"/>.</summary>
    public static Vector3 ProjectOnPlane(Vector3 v, Vector3 planeNormal)
    {
        var n = SafeNormalize(planeNormal);
        return v - n * Vector3.Dot(v, n);
    }

    // -------------------------------------------------------------------------
    // Random
    // -------------------------------------------------------------------------

    /// <summary>
    /// Shared random source. Seed it via <see cref="SetSeed"/> for reproducible runs.
    /// </summary>
    /// <remarks>
    /// Not thread-safe, by design. Gameplay runs on one thread, and locking every roll
    /// would cost more than it saves. Draw from it on the game thread; a background job
    /// that needs randomness should own its own <see cref="System.Random"/>.
    /// </remarks>
    public static Random Random { get; private set; } = new();

    /// <summary>Reseeds <see cref="Random"/> so a session can be replayed deterministically.</summary>
    public static void SetSeed(int seed) => Random = new Random(seed);

    /// <summary>Uniform float in <c>[min, max)</c>.</summary>
    public static float RandomRange(float min, float max)
        => min + (float)Random.NextDouble() * (max - min);

    /// <summary>Uniform int in <c>[minInclusive, maxExclusive)</c>.</summary>
    public static int RandomRange(int minInclusive, int maxExclusive)
        => Random.Next(minInclusive, maxExclusive);

    /// <summary>A uniformly distributed point on the surface of the unit sphere.</summary>
    public static Vector3 RandomOnUnitSphere()
    {
        float z     = RandomRange(-1f, 1f);
        float theta = RandomRange(0f, MathF.Tau);
        float r     = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(r * MathF.Cos(theta), r * MathF.Sin(theta), z);
    }

    /// <summary>A uniformly distributed point inside the unit sphere.</summary>
    public static Vector3 RandomInUnitSphere()
        => RandomOnUnitSphere() * MathF.Cbrt((float)Random.NextDouble());

    /// <summary>A uniformly distributed point on the unit circle in the XY plane.</summary>
    public static Vector2 RandomOnUnitCircle()
    {
        float a = RandomRange(0f, MathF.Tau);
        return new Vector2(MathF.Cos(a), MathF.Sin(a));
    }
}
