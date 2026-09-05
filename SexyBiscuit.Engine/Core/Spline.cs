using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.Core;

/// <summary>How a spline behaves at its ends.</summary>
public enum SplineLoopMode
{
    /// <summary>Stops at the last point.</summary>
    Once,

    /// <summary>Jumps back to the start.</summary>
    Loop,

    /// <summary>Reverses direction at each end.</summary>
    PingPong,
}

/// <summary>
/// A smooth curve through a list of points, with constant-speed traversal.
/// Modelled on Unreal's <c>USplineComponent</c>.
/// </summary>
/// <remarks>
/// <para>
/// Uses a Catmull-Rom spline, which passes exactly through every control point. Bezier
/// curves do not, and a patrol route or a camera rail that misses the waypoints you placed
/// is a constant source of "why is it going there".
/// </para>
/// <para>
/// A spline is not parameterised by distance: equal steps in <c>t</c> cover unequal
/// ground, so anything moving at "speed" along a raw spline speeds up on straights and
/// crawls round corners. <see cref="GetPointAtDistance"/> resolves that against an
/// arc-length table built once on <see cref="Rebuild"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var spline = actor.AddComponent&lt;Spline&gt;();
/// spline.Points.AddRange(patrolWaypoints);
/// spline.LoopMode = SplineLoopMode.Loop;
/// spline.Rebuild();
///
/// var follower = guard.AddComponent&lt;SplineFollower&gt;();
/// follower.Spline = spline;
/// follower.Speed  = 2.5f;
/// </code>
/// </example>
public sealed class Spline : Component
{
    /// <summary>Control points in world space. Call <see cref="Rebuild"/> after editing.</summary>
    public List<Vector3> Points { get; set; } = new();

    /// <summary>End behaviour, which also decides whether the curve closes.</summary>
    public SplineLoopMode LoopMode { get; set; } = SplineLoopMode.Once;

    /// <summary>
    /// Samples per segment used to measure arc length. Higher is more accurate and costs
    /// more only at <see cref="Rebuild"/> time.
    /// </summary>
    public int SamplesPerSegment { get; set; } = 16;

    /// <summary>Total curve length in world units. Zero until <see cref="Rebuild"/> runs.</summary>
    public float Length { get; private set; }

    /// <summary>True once the arc-length table has been built.</summary>
    public bool IsBuilt => _samples.Count > 0;

    // Cumulative distance at each sample, paired with its curve parameter.
    private readonly List<(float distance, float t)> _samples = new();

    public override void Start()
    {
        if (!IsBuilt && Points.Count >= 2) Rebuild();
    }

    /// <summary>
    /// Measures the curve and builds the arc-length table. Call after changing
    /// <see cref="Points"/> or <see cref="LoopMode"/>.
    /// </summary>
    public void Rebuild()
    {
        _samples.Clear();
        Length = 0f;

        if (Points.Count < 2) return;

        int segments = LoopMode == SplineLoopMode.Loop ? Points.Count : Points.Count - 1;
        int steps    = Math.Max(2, SamplesPerSegment) * segments;

        var previous = Evaluate(0f);
        _samples.Add((0f, 0f));

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            var point = Evaluate(t);

            Length += Vector3.Distance(previous, point);
            _samples.Add((Length, t));
            previous = point;
        }
    }

    /// <summary>
    /// Position at curve parameter <paramref name="t"/> in 0..1. Not constant-speed — use
    /// <see cref="GetPointAtDistance"/> for movement.
    /// </summary>
    public Vector3 Evaluate(float t)
    {
        if (Points.Count == 0) return Vector3.Zero;
        if (Points.Count == 1) return Points[0];

        bool closed  = LoopMode == SplineLoopMode.Loop;
        int segments = closed ? Points.Count : Points.Count - 1;

        float scaled = SBMath.Clamp01(t) * segments;
        int   index  = Math.Min((int)scaled, segments - 1);
        float local  = scaled - index;

        // Catmull-Rom needs a point either side of the segment. At an open end the
        // endpoint is duplicated, which makes the curve start and finish straight rather
        // than overshooting.
        var p0 = PointAt(index - 1, closed);
        var p1 = PointAt(index,     closed);
        var p2 = PointAt(index + 1, closed);
        var p3 = PointAt(index + 2, closed);

        return CatmullRom(p0, p1, p2, p3, local);
    }

    /// <summary>Unit tangent at curve parameter <paramref name="t"/>.</summary>
    public Vector3 GetDirection(float t)
    {
        const float step = 0.001f;
        var ahead  = Evaluate(MathF.Min(1f, t + step));
        var behind = Evaluate(MathF.Max(0f, t - step));
        return SBMath.SafeNormalize(ahead - behind);
    }

    /// <summary>
    /// Position a given distance along the curve, so equal distances cover equal ground.
    /// </summary>
    public Vector3 GetPointAtDistance(float distance) => Evaluate(DistanceToParameter(distance));

    /// <summary>Unit tangent a given distance along the curve.</summary>
    public Vector3 GetDirectionAtDistance(float distance) => GetDirection(DistanceToParameter(distance));

    /// <summary>
    /// Converts a distance along the curve into a curve parameter, interpolating between
    /// entries in the arc-length table.
    /// </summary>
    public float DistanceToParameter(float distance)
    {
        if (_samples.Count == 0) return 0f;
        if (Length <= 0f) return 0f;

        distance = Math.Clamp(distance, 0f, Length);

        // The table is sorted by distance, so a binary search beats walking it — a long
        // route sampled every frame would otherwise be linear in waypoint count.
        int low = 0, high = _samples.Count - 1;
        while (low < high - 1)
        {
            int mid = (low + high) / 2;
            if (_samples[mid].distance <= distance) low = mid;
            else                                    high = mid;
        }

        var (d0, t0) = _samples[low];
        var (d1, t1) = _samples[high];

        float span = d1 - d0;
        return span <= SBMath.Epsilon ? t0 : MathHelper.Lerp(t0, t1, (distance - d0) / span);
    }

    /// <summary>Closest point on the curve to a world position, by sampling the table.</summary>
    public Vector3 FindClosestPoint(Vector3 worldPosition)
    {
        if (_samples.Count == 0) return Evaluate(0f);

        var best = Evaluate(0f);
        float bestDistanceSq = Vector3.DistanceSquared(best, worldPosition);

        foreach (var (_, t) in _samples)
        {
            var candidate = Evaluate(t);
            float d = Vector3.DistanceSquared(candidate, worldPosition);
            if (d >= bestDistanceSq) continue;

            bestDistanceSq = d;
            best = candidate;
        }

        return best;
    }

    private Vector3 PointAt(int index, bool closed)
    {
        if (closed)
        {
            int wrapped = ((index % Points.Count) + Points.Count) % Points.Count;
            return Points[wrapped];
        }

        return Points[Math.Clamp(index, 0, Points.Count - 1)];
    }

    /// <summary>The standard Catmull-Rom basis, which interpolates p1 to p2.</summary>
    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;

        return 0.5f * ((2f * p1)
                     + (-p0 + p2) * t
                     + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                     + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }
}

/// <summary>
/// Moves an actor along a <see cref="Spline"/> at a constant speed.
/// </summary>
/// <remarks>
/// Travels by distance rather than by curve parameter, so the actor does not accelerate
/// through corners. Useful for patrol routes, moving platforms, camera rails and tower
/// defence paths.
/// </remarks>
public sealed class SplineFollower : Component
{
    /// <summary>The curve to follow. Falls back to a <see cref="Spline"/> on the same actor.</summary>
    public Spline? Spline { get; set; }

    /// <summary>Travel speed in world units per second.</summary>
    public float Speed { get; set; } = 3f;

    /// <summary>Turns the actor to face along the curve.</summary>
    public bool OrientToPath { get; set; } = true;

    /// <summary>Distance travelled along the curve.</summary>
    public float Distance { get; set; }

    /// <summary>False once a non-looping follower reaches the end.</summary>
    public bool IsMoving { get; private set; } = true;

    /// <summary>Raised when a <see cref="SplineLoopMode.Once"/> follower reaches the end.</summary>
    public SBEvent Completed { get; } = new();

    private Transform3D _transform = null!;
    private int _direction = 1;

    public override void Start()
    {
        _transform = Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();
        Spline   ??= Actor.GetComponent<Spline>();
    }

    public override void Update(float dt)
    {
        if (!IsMoving || Spline is not { IsBuilt: true } spline || spline.Length <= 0f) return;

        Distance += Speed * _direction * dt;

        switch (spline.LoopMode)
        {
            case SplineLoopMode.Loop:
                // Modulo rather than clamp, so a loop does not stutter at the seam.
                Distance = ((Distance % spline.Length) + spline.Length) % spline.Length;
                break;

            case SplineLoopMode.PingPong:
                if (Distance > spline.Length) { Distance = spline.Length; _direction = -1; }
                else if (Distance < 0f)       { Distance = 0f;            _direction =  1; }
                break;

            default:
                if (Distance >= spline.Length)
                {
                    Distance = spline.Length;
                    IsMoving = false;
                    Completed.Broadcast();
                }
                break;
        }

        _transform.Position = spline.GetPointAtDistance(Distance);

        if (!OrientToPath) return;

        var forward = spline.GetDirectionAtDistance(Distance) * _direction;
        if (forward.LengthSquared() > SBMath.Epsilon)
            _transform.LookAt(_transform.Position + forward);
    }
}
