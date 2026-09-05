using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.Core;

/// <summary>
/// An axis-aligned bounding box stored as centre plus half-extents.
/// Used for frustum culling, broad-phase queries and LOD sizing.
/// </summary>
public struct Bounds : IEquatable<Bounds>
{
    /// <summary>Centre of the box in world (or local) space.</summary>
    public Vector3 Center;

    /// <summary>Half the size of the box along each axis. Always non-negative.</summary>
    public Vector3 Extents;

    /// <summary>Full size of the box along each axis.</summary>
    public Vector3 Size
    {
        readonly get => Extents * 2f;
        set => Extents = new Vector3(MathF.Abs(value.X), MathF.Abs(value.Y), MathF.Abs(value.Z)) * 0.5f;
    }

    /// <summary>Corner of the box with the smallest coordinate on every axis.</summary>
    public readonly Vector3 Min => Center - Extents;

    /// <summary>Corner of the box with the largest coordinate on every axis.</summary>
    public readonly Vector3 Max => Center + Extents;

    /// <summary>Radius of the sphere that fully contains this box.</summary>
    public readonly float BoundingRadius => Extents.Length();

    public Bounds(Vector3 center, Vector3 size)
    {
        Center  = center;
        Extents = new Vector3(MathF.Abs(size.X), MathF.Abs(size.Y), MathF.Abs(size.Z)) * 0.5f;
    }

    /// <summary>A degenerate box at the origin. Grow it with <see cref="Encapsulate(Vector3)"/>.</summary>
    public static Bounds Zero => new(Vector3.Zero, Vector3.Zero);

    /// <summary>Builds the tightest box containing every point in <paramref name="points"/>.</summary>
    public static Bounds FromPoints(IEnumerable<Vector3> points)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;

        foreach (var p in points)
        {
            any = true;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        if (!any) return Zero;
        return FromMinMax(min, max);
    }

    /// <summary>Builds a box from opposite corners.</summary>
    public static Bounds FromMinMax(Vector3 min, Vector3 max)
        => new() { Center = (min + max) * 0.5f, Extents = (max - min) * 0.5f };

    /// <summary>Grows the box so that it contains <paramref name="point"/>.</summary>
    public void Encapsulate(Vector3 point)
        => this = FromMinMax(Vector3.Min(Min, point), Vector3.Max(Max, point));

    /// <summary>Grows the box so that it contains <paramref name="other"/>.</summary>
    public void Encapsulate(Bounds other)
        => this = FromMinMax(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

    /// <summary>Expands the box by <paramref name="amount"/> on every side.</summary>
    public void Expand(float amount) => Extents += new Vector3(amount);

    public readonly bool Contains(Vector3 p)
    {
        var min = Min; var max = Max;
        return p.X >= min.X && p.X <= max.X
            && p.Y >= min.Y && p.Y <= max.Y
            && p.Z >= min.Z && p.Z <= max.Z;
    }

    public readonly bool Intersects(Bounds other)
    {
        var aMin = Min; var aMax = Max;
        var bMin = other.Min; var bMax = other.Max;
        return aMin.X <= bMax.X && aMax.X >= bMin.X
            && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y
            && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;
    }

    /// <summary>Converts to a MonoGame <see cref="BoundingBox"/> for use with its intersection helpers.</summary>
    public readonly BoundingBox ToBoundingBox() => new(Min, Max);

    /// <summary>The smallest sphere fully containing this box.</summary>
    public readonly BoundingSphere ToBoundingSphere() => new(Center, BoundingRadius);

    /// <summary>
    /// Transforms the box by <paramref name="matrix"/> and returns the axis-aligned box
    /// containing the result. The returned box is conservative — it may be larger than
    /// the tightest fit when the matrix contains rotation.
    /// </summary>
    public readonly Bounds Transform(Matrix matrix)
    {
        // Transform the centre, then accumulate the extents against the absolute
        // value of the rotation/scale basis — the standard conservative AABB transform.
        var center = Vector3.Transform(Center, matrix);

        var extents = new Vector3(
            MathF.Abs(matrix.M11) * Extents.X + MathF.Abs(matrix.M21) * Extents.Y + MathF.Abs(matrix.M31) * Extents.Z,
            MathF.Abs(matrix.M12) * Extents.X + MathF.Abs(matrix.M22) * Extents.Y + MathF.Abs(matrix.M32) * Extents.Z,
            MathF.Abs(matrix.M13) * Extents.X + MathF.Abs(matrix.M23) * Extents.Y + MathF.Abs(matrix.M33) * Extents.Z);

        return new Bounds { Center = center, Extents = extents };
    }

    /// <summary>Closest point on or inside the box to <paramref name="point"/>.</summary>
    public readonly Vector3 ClosestPoint(Vector3 point) => Vector3.Clamp(point, Min, Max);

    /// <summary>Shortest distance from <paramref name="point"/> to the box surface; 0 when inside.</summary>
    public readonly float DistanceTo(Vector3 point) => Vector3.Distance(ClosestPoint(point), point);

    public readonly bool Equals(Bounds other) => Center == other.Center && Extents == other.Extents;
    public readonly override bool Equals(object? obj) => obj is Bounds b && Equals(b);
    public readonly override int GetHashCode() => HashCode.Combine(Center, Extents);
    public static bool operator ==(Bounds a, Bounds b) => a.Equals(b);
    public static bool operator !=(Bounds a, Bounds b) => !a.Equals(b);

    public readonly override string ToString() => $"Bounds(centre {Center}, size {Size})";
}
