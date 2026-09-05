using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.AI;

/// <summary>
/// A walkable surface expressed as a triangle mesh, with A* pathfinding and funnel
/// string-pulling on top of it.
/// </summary>
/// <remarks>
/// <para>
/// A nav mesh is built from raw geometry — usually a simplified version of the level's floor —
/// via <see cref="Build"/>. Triangles that share an edge become neighbours, so the search
/// walks the surface rather than a grid, and paths follow diagonal openings naturally.
/// </para>
/// <para>
/// The search runs A* over triangle centroids to find the corridor, then the funnel algorithm
/// pulls the path taut against the corridor's edges. Without that second step paths visibly
/// zig-zag between triangle centres; with it they hug corners the way a person would.
/// </para>
/// <para>
/// Heights are handled by projecting onto the XZ plane for the funnel and interpolating Y from
/// the triangles the path crosses, which suits walkable terrain and ramps. It is not a
/// multi-storey solution — for overlapping floors, build one nav mesh per storey and link them
/// with your own logic.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var navMesh = NavMesh.Build(floorVertices, floorIndices);
/// NavMesh.Active = navMesh;
///
/// var path = new List&lt;Vector3&gt;();
/// if (navMesh.FindPath(agentPos, targetPos, path))
///     agent.SetPath(path);
/// </code>
/// </example>
public sealed class NavMesh
{
    /// <summary>The nav mesh agents use when none is assigned explicitly.</summary>
    public static NavMesh? Active { get; set; }

    private readonly Vector3[] _vertices;
    private readonly int[]     _indices;
    private readonly int[][]   _neighbours;   // per triangle: neighbour triangle per edge, -1 for a border
    private readonly Vector3[] _centroids;

    /// <summary>Number of walkable triangles.</summary>
    public int TriangleCount => _indices.Length / 3;

    /// <summary>Axis-aligned bounds of the whole surface.</summary>
    public Bounds Bounds { get; }

    private NavMesh(Vector3[] vertices, int[] indices, int[][] neighbours, Vector3[] centroids, Bounds bounds)
    {
        _vertices   = vertices;
        _indices    = indices;
        _neighbours = neighbours;
        _centroids  = centroids;
        Bounds      = bounds;
    }

    // -------------------------------------------------------------------------
    // Building
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a nav mesh from a triangle soup. Vertices closer than
    /// <paramref name="weldEpsilon"/> are treated as the same point when finding shared edges,
    /// which is what lets separately authored floor pieces connect.
    /// </summary>
    /// <param name="vertices">Triangle vertices in world space.</param>
    /// <param name="indices">Triangle indices, three per triangle.</param>
    /// <param name="maxSlopeDegrees">
    /// Triangles steeper than this are dropped as unwalkable. 90 keeps everything.
    /// </param>
    /// <param name="weldEpsilon">Distance under which two vertices count as coincident.</param>
    public static NavMesh Build(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices,
                                float maxSlopeDegrees = 50f, float weldEpsilon = 0.01f)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Count % 3 != 0)
            throw new ArgumentException("Index count must be a multiple of three.", nameof(indices));

        var verts = vertices.ToArray();
        float cosLimit = MathF.Cos(MathHelper.ToRadians(MathHelper.Clamp(maxSlopeDegrees, 0f, 90f)));

        // Drop triangles that are too steep to stand on, or degenerate.
        var kept = new List<int>(indices.Count);
        for (int t = 0; t < indices.Count; t += 3)
        {
            var a = verts[indices[t]];
            var b = verts[indices[t + 1]];
            var c = verts[indices[t + 2]];

            var normal = Vector3.Cross(b - a, c - a);
            if (normal.LengthSquared() < 1e-8f) continue;

            normal.Normalize();
            if (MathF.Abs(Vector3.Dot(normal, Vector3.Up)) < cosLimit) continue;

            // Normalise winding so every triangle is counter-clockwise in the XZ
            // projection. The funnel algorithm derives "left" and "right" from vertex
            // order, so a mesh with mixed winding makes it emit a corner at every
            // portal instead of pulling the path straight.
            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
            if (SignedAreaXZ(verts[i0], verts[i1], verts[i2]) < 0f)
                (i1, i2) = (i2, i1);

            kept.Add(i0);
            kept.Add(i1);
            kept.Add(i2);
        }

        var idx        = kept.ToArray();
        int triCount   = idx.Length / 3;
        var centroids  = new Vector3[triCount];
        var neighbours = new int[triCount][];

        for (int t = 0; t < triCount; t++)
        {
            centroids[t]  = (verts[idx[t * 3]] + verts[idx[t * 3 + 1]] + verts[idx[t * 3 + 2]]) / 3f;
            neighbours[t] = new[] { -1, -1, -1 };
        }

        // Map each welded edge to the triangles that use it. Two triangles sharing an
        // edge become neighbours; an edge used once is a border.
        var edgeOwners = new Dictionary<(long, long), (int tri, int edge)>();
        long Quantize(float v) => (long)MathF.Round(v / MathF.Max(weldEpsilon, 1e-6f));
        long Key(Vector3 v) => Quantize(v.X) * 73856093L ^ Quantize(v.Y) * 19349663L ^ Quantize(v.Z) * 83492791L;

        for (int t = 0; t < triCount; t++)
        {
            for (int e = 0; e < 3; e++)
            {
                long k0 = Key(verts[idx[t * 3 + e]]);
                long k1 = Key(verts[idx[t * 3 + (e + 1) % 3]]);
                var key = k0 < k1 ? (k0, k1) : (k1, k0);

                if (edgeOwners.TryGetValue(key, out var other))
                {
                    neighbours[t][e]                  = other.tri;
                    neighbours[other.tri][other.edge] = t;
                }
                else
                {
                    edgeOwners[key] = (t, e);
                }
            }
        }

        var bounds = Bounds.FromPoints(verts);
        return new NavMesh(verts, idx, neighbours, centroids, bounds);
    }

    // -------------------------------------------------------------------------
    // Queries
    // -------------------------------------------------------------------------

    /// <summary>
    /// Finds the closest point on the nav mesh to <paramref name="position"/> within
    /// <paramref name="maxDistance"/>. Use it to snap a spawn point or a click target onto
    /// walkable ground before pathing to it.
    /// </summary>
    public bool SamplePosition(Vector3 position, float maxDistance, out Vector3 nearest, out int triangle)
    {
        nearest  = position;
        triangle = -1;

        float bestSq = maxDistance * maxDistance;
        bool  found  = false;

        for (int t = 0; t < TriangleCount; t++)
        {
            var p = ClosestPointOnTriangle(position, t);
            float dSq = Vector3.DistanceSquared(p, position);
            if (dSq >= bestSq) continue;

            bestSq   = dSq;
            nearest  = p;
            triangle = t;
            found    = true;
        }

        return found;
    }

    /// <inheritdoc cref="SamplePosition(Vector3,float,out Vector3,out int)"/>
    public bool SamplePosition(Vector3 position, float maxDistance, out Vector3 nearest)
        => SamplePosition(position, maxDistance, out nearest, out _);

    /// <summary>True when a straight line between two points stays on the nav mesh.</summary>
    /// <remarks>
    /// Sampled rather than analytic — cheap, and accurate enough to decide whether an agent
    /// can cut a corner. <paramref name="stepLength"/> trades accuracy for cost.
    /// </remarks>
    public bool IsStraightPathClear(Vector3 from, Vector3 to, float stepLength = 0.5f, float tolerance = 0.5f)
    {
        float distance = Vector3.Distance(from, to);
        int steps = Math.Max(1, (int)MathF.Ceiling(distance / MathF.Max(0.05f, stepLength)));

        for (int i = 1; i < steps; i++)
        {
            var p = Vector3.Lerp(from, to, i / (float)steps);
            if (!SamplePosition(p, tolerance, out _)) return false;
        }

        return true;
    }

    // -------------------------------------------------------------------------
    // Pathfinding
    // -------------------------------------------------------------------------

    /// <summary>
    /// Finds a walkable path between two world positions. Both endpoints are snapped onto the
    /// mesh first, so callers do not need to be exactly on the surface.
    /// </summary>
    /// <param name="start">Where the agent is.</param>
    /// <param name="goal">Where it wants to be.</param>
    /// <param name="result">Receives the corner points, starting at the snapped start and ending at the snapped goal.</param>
    /// <param name="snapDistance">How far the endpoints may be from the mesh and still snap onto it.</param>
    /// <returns>False when either endpoint is off the mesh or no corridor connects them.</returns>
    public bool FindPath(Vector3 start, Vector3 goal, List<Vector3> result, float snapDistance = 2f)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.Clear();

        if (!SamplePosition(start, snapDistance, out var startPoint, out int startTri)) return false;
        if (!SamplePosition(goal,  snapDistance, out var goalPoint,  out int goalTri))  return false;

        if (startTri == goalTri)
        {
            result.Add(startPoint);
            result.Add(goalPoint);
            return true;
        }

        var corridor = FindCorridor(startTri, goalTri);
        if (corridor == null) return false;

        StringPull(startPoint, goalPoint, corridor, result);
        return result.Count > 0;
    }

    /// <summary>A* over triangle adjacency. Returns the triangle corridor, or null when unreachable.</summary>
    private List<int>? FindCorridor(int startTri, int goalTri)
    {
        int count = TriangleCount;
        var cameFrom = new int[count];
        var gScore   = new float[count];
        var closed   = new bool[count];

        Array.Fill(cameFrom, -1);
        Array.Fill(gScore, float.PositiveInfinity);
        gScore[startTri] = 0f;

        // PriorityQueue keeps the open set ordered by f-score without a manual heap.
        var open = new PriorityQueue<int, float>();
        open.Enqueue(startTri, Heuristic(startTri, goalTri));

        while (open.TryDequeue(out int current, out _))
        {
            if (current == goalTri) return Reconstruct(cameFrom, current);
            if (closed[current]) continue;
            closed[current] = true;

            foreach (int neighbour in _neighbours[current])
            {
                if (neighbour < 0 || closed[neighbour]) continue;

                float tentative = gScore[current] + Vector3.Distance(_centroids[current], _centroids[neighbour]);
                if (tentative >= gScore[neighbour]) continue;

                cameFrom[neighbour] = current;
                gScore[neighbour]   = tentative;
                open.Enqueue(neighbour, tentative + Heuristic(neighbour, goalTri));
            }
        }

        return null;
    }

    private float Heuristic(int from, int to) => Vector3.Distance(_centroids[from], _centroids[to]);

    private static List<int> Reconstruct(int[] cameFrom, int current)
    {
        var path = new List<int> { current };
        while (cameFrom[current] >= 0)
        {
            current = cameFrom[current];
            path.Add(current);
        }
        path.Reverse();
        return path;
    }

    // -------------------------------------------------------------------------
    // Funnel (simple stupid funnel algorithm)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pulls a triangle corridor taut into a minimal list of corner points.
    /// </summary>
    /// <remarks>
    /// Works in the XZ plane: it advances a funnel bounded by the left and right vertices of
    /// each shared edge, emitting a corner whenever the funnel would turn inside out. Y is
    /// restored afterwards by sampling the mesh under each corner.
    /// </remarks>
    private void StringPull(Vector3 start, Vector3 goal, List<int> corridor, List<Vector3> result)
    {
        // Collect the shared edge between each pair of corridor triangles, oriented
        // consistently so "left" and "right" mean the same thing along the whole corridor.
        var portalLeft  = new List<Vector3> { start };
        var portalRight = new List<Vector3> { start };

        for (int i = 0; i < corridor.Count - 1; i++)
        {
            if (!TryGetSharedEdge(corridor[i], corridor[i + 1], out var left, out var right))
                continue;

            portalLeft.Add(left);
            portalRight.Add(right);
        }

        portalLeft.Add(goal);
        portalRight.Add(goal);

        var apex      = start;
        var funnelL   = start;
        var funnelR   = start;
        int apexIndex = 0, leftIndex = 0, rightIndex = 0;

        result.Add(start);

        for (int i = 1; i < portalLeft.Count; i++)
        {
            var newL = portalLeft[i];
            var newR = portalRight[i];

            // Tighten the right side.
            if (TriArea2(apex, funnelR, newR) <= 0f)
            {
                if (Vector2Equal(apex, funnelR) || TriArea2(apex, funnelL, newR) > 0f)
                {
                    funnelR    = newR;
                    rightIndex = i;
                }
                else
                {
                    // Right side crossed the left: the left vertex is a corner.
                    result.Add(funnelL);
                    apex      = funnelL;
                    apexIndex = leftIndex;
                    funnelL   = apex;
                    funnelR   = apex;
                    leftIndex = rightIndex = apexIndex;
                    i         = apexIndex;
                    continue;
                }
            }

            // Tighten the left side.
            if (TriArea2(apex, funnelL, newL) >= 0f)
            {
                if (Vector2Equal(apex, funnelL) || TriArea2(apex, funnelR, newL) < 0f)
                {
                    funnelL   = newL;
                    leftIndex = i;
                }
                else
                {
                    result.Add(funnelR);
                    apex      = funnelR;
                    apexIndex = rightIndex;
                    funnelL   = apex;
                    funnelR   = apex;
                    leftIndex = rightIndex = apexIndex;
                    i         = apexIndex;
                }
            }
        }

        if (result.Count == 0 || !Vector2Equal(result[^1], goal)) result.Add(goal);

        // Restore heights: the funnel ran flat, so lift each corner back onto the surface.
        for (int i = 0; i < result.Count; i++)
        {
            if (SamplePosition(result[i], 5f, out var onMesh)) result[i] = onMesh;
        }
    }

    /// <summary>
    /// Returns the edge shared by two triangles, with <paramref name="left"/> and
    /// <paramref name="right"/> ordered by the winding of <paramref name="triA"/>.
    /// </summary>
    private bool TryGetSharedEdge(int triA, int triB, out Vector3 left, out Vector3 right)
    {
        for (int e = 0; e < 3; e++)
        {
            if (_neighbours[triA][e] != triB) continue;
            left  = _vertices[_indices[triA * 3 + e]];
            right = _vertices[_indices[triA * 3 + (e + 1) % 3]];
            return true;
        }

        left = right = Vector3.Zero;
        return false;
    }

    /// <summary>Twice the signed area of a triangle projected onto XZ. Negative means clockwise.</summary>
    private static float TriArea2(Vector3 a, Vector3 b, Vector3 c)
        => (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);

    /// <summary>Signed area in the XZ plane, used to detect and fix triangle winding at build time.</summary>
    private static float SignedAreaXZ(Vector3 a, Vector3 b, Vector3 c) => TriArea2(a, b, c);

    private static bool Vector2Equal(Vector3 a, Vector3 b)
        => MathF.Abs(a.X - b.X) < 1e-4f && MathF.Abs(a.Z - b.Z) < 1e-4f;

    // -------------------------------------------------------------------------
    // Geometry
    // -------------------------------------------------------------------------

    private Vector3 ClosestPointOnTriangle(Vector3 p, int triangle)
    {
        var a = _vertices[_indices[triangle * 3]];
        var b = _vertices[_indices[triangle * 3 + 1]];
        var c = _vertices[_indices[triangle * 3 + 2]];
        return ClosestPointOnTriangle(p, a, b, c);
    }

    /// <summary>
    /// Closest point on triangle <c>abc</c> to <paramref name="p"/>, using the standard
    /// Voronoi-region test from Ericson's <i>Real-Time Collision Detection</i>.
    /// </summary>
    public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;

        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;

        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            return a + ab * (d1 / (d1 - d3));

        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            return a + ac * (d2 / (d2 - d6));

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    // -------------------------------------------------------------------------
    // Debug
    // -------------------------------------------------------------------------

    /// <summary>Yields the three corners of each triangle, for drawing the mesh in a debug view.</summary>
    public IEnumerable<(Vector3 a, Vector3 b, Vector3 c)> EnumerateTriangles()
    {
        for (int t = 0; t < TriangleCount; t++)
            yield return (_vertices[_indices[t * 3]],
                          _vertices[_indices[t * 3 + 1]],
                          _vertices[_indices[t * 3 + 2]]);
    }
}
