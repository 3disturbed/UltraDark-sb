using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>Built-in shapes a <see cref="MeshRenderer"/> can generate without an asset.</summary>
public enum MeshPrimitive
{
    /// <summary>No generated geometry — the renderer draws a loaded model, if any.</summary>
    None,

    /// <summary>A unit cube centred on the origin.</summary>
    Cube,

    /// <summary>A UV sphere of radius 0.5.</summary>
    Sphere,

    /// <summary>A 1x1 horizontal plane in the XZ axis — a floor.</summary>
    Plane,

    /// <summary>A 1x1 vertical quad in the XY axis — a wall or a billboard.</summary>
    Quad,

    /// <summary>A cylinder of radius 0.5 and height 1, along Y.</summary>
    Cylinder,

    /// <summary>A cone of base radius 0.5 and height 1, along Y.</summary>
    Cone,
}

/// <summary>
/// Generates the built-in primitive shapes.
/// </summary>
/// <remarks>
/// <para>
/// Blocking out a level, or just checking that lighting works, should not require
/// exporting a cube from Blender first. These are the shapes Unreal keeps in its Basic
/// palette, generated at runtime so a scene file can name one and get geometry.
/// </para>
/// <para>
/// Buffers are cached per shape and per device. A scene with two hundred crates in it
/// creates one cube, not two hundred — the transform differs per renderer, the geometry
/// does not.
/// </para>
/// </remarks>
public static class PrimitiveMesh
{
    /// <summary>Vertex and index buffers for one generated shape.</summary>
    public sealed record Geometry(VertexBuffer VertexBuffer, IndexBuffer IndexBuffer, int PrimitiveCount, Bounds Bounds);

    private static readonly Dictionary<(GraphicsDevice, MeshPrimitive), Geometry> _cache = new();

    /// <summary>Segments around the axis for a sphere, cylinder or cone.</summary>
    public static int RadialSegments { get; set; } = 24;

    /// <summary>Segments from pole to pole on a sphere.</summary>
    public static int RingSegments { get; set; } = 16;

    /// <summary>
    /// Returns the geometry for a shape, building and caching it on first request.
    /// Returns null for <see cref="MeshPrimitive.None"/>.
    /// </summary>
    public static Geometry? Get(MeshPrimitive shape, GraphicsDevice device)
    {
        if (shape == MeshPrimitive.None) return null;
        ArgumentNullException.ThrowIfNull(device);

        var key = (device, shape);
        if (_cache.TryGetValue(key, out var cached) && !cached.VertexBuffer.IsDisposed)
            return cached;

        var built = Build(shape, device);
        _cache[key] = built;
        return built;
    }

    /// <summary>
    /// The object-space bounds of a shape, without needing a graphics device.
    /// </summary>
    /// <remarks>
    /// Bounds have to be known before the first draw. Culling and picking both read them,
    /// and both run against a scene that may not have rendered yet — a mesh whose bounds
    /// are still the default unit cube is culled wrongly and, once scaled up, swallows
    /// every ray cast at the scene. These are analytic, so no buffers are needed to
    /// answer the question.
    /// </remarks>
    public static Bounds GetBounds(MeshPrimitive shape) => shape switch
    {
        MeshPrimitive.Plane => new Bounds(Vector3.Zero, new Vector3(1f, 0f, 1f)),
        MeshPrimitive.Quad  => new Bounds(Vector3.Zero, new Vector3(1f, 1f, 0f)),
        MeshPrimitive.None  => new Bounds(Vector3.Zero, Vector3.One),
        _                   => new Bounds(Vector3.Zero, Vector3.One),
    };

    /// <summary>Releases every cached buffer. Call when tearing down a graphics device.</summary>
    public static void ClearCache()
    {
        foreach (var geometry in _cache.Values)
        {
            geometry.VertexBuffer.Dispose();
            geometry.IndexBuffer.Dispose();
        }

        _cache.Clear();
    }

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    private static Geometry Build(MeshPrimitive shape, GraphicsDevice device)
    {
        var (vertices, indices) = shape switch
        {
            MeshPrimitive.Cube     => BuildCube(),
            MeshPrimitive.Sphere   => BuildSphere(),
            MeshPrimitive.Plane    => BuildPlane(),
            MeshPrimitive.Quad     => BuildQuad(),
            MeshPrimitive.Cylinder => BuildCylinder(),
            MeshPrimitive.Cone     => BuildCone(),
            _                      => BuildCube(),
        };

        var vb = new VertexBuffer(device, VertexPositionNormalTexture.VertexDeclaration,
            vertices.Length, BufferUsage.WriteOnly);
        vb.SetData(vertices);

        var ib = new IndexBuffer(device, IndexElementSize.SixteenBits, indices.Length, BufferUsage.WriteOnly);
        ib.SetData(indices);

        var bounds = Bounds.FromPoints(vertices.Select(v => v.Position));
        return new Geometry(vb, ib, indices.Length / 3, bounds);
    }

    /// <summary>
    /// A unit cube with four vertices per face.
    /// </summary>
    /// <remarks>
    /// Faces cannot share corner vertices: a shared corner would have to carry one normal
    /// for three perpendicular faces, which rounds off the edges under lighting. 24
    /// vertices instead of 8 is what makes a cube look like a cube.
    /// </remarks>
    private static (VertexPositionNormalTexture[], short[]) BuildCube()
    {
        var normals = new[]
        {
            Vector3.Backward, Vector3.Forward, Vector3.Up,
            Vector3.Down,     Vector3.Left,    Vector3.Right,
        };

        var faces = new[]
        {
            new[] { new Vector3(-1,-1, 1), new Vector3( 1,-1, 1), new Vector3( 1, 1, 1), new Vector3(-1, 1, 1) },
            new[] { new Vector3( 1,-1,-1), new Vector3(-1,-1,-1), new Vector3(-1, 1,-1), new Vector3( 1, 1,-1) },
            new[] { new Vector3(-1, 1, 1), new Vector3( 1, 1, 1), new Vector3( 1, 1,-1), new Vector3(-1, 1,-1) },
            new[] { new Vector3(-1,-1,-1), new Vector3( 1,-1,-1), new Vector3( 1,-1, 1), new Vector3(-1,-1, 1) },
            new[] { new Vector3(-1,-1,-1), new Vector3(-1,-1, 1), new Vector3(-1, 1, 1), new Vector3(-1, 1,-1) },
            new[] { new Vector3( 1,-1, 1), new Vector3( 1,-1,-1), new Vector3( 1, 1,-1), new Vector3( 1, 1, 1) },
        };

        var uvs = new[] { new Vector2(0,1), new Vector2(1,1), new Vector2(1,0), new Vector2(0,0) };

        var vertices = new VertexPositionNormalTexture[24];
        var indices  = new short[36];

        for (int f = 0; f < 6; f++)
        {
            for (int v = 0; v < 4; v++)
                vertices[f * 4 + v] = new VertexPositionNormalTexture(faces[f][v] * 0.5f, normals[f], uvs[v]);

            int i = f * 6;
            short b = (short)(f * 4);
            indices[i]     = b;
            indices[i + 1] = (short)(b + 1);
            indices[i + 2] = (short)(b + 2);
            indices[i + 3] = b;
            indices[i + 4] = (short)(b + 2);
            indices[i + 5] = (short)(b + 3);
        }

        return (vertices, indices);
    }

    private static (VertexPositionNormalTexture[], short[]) BuildSphere()
    {
        int rings   = Math.Max(3, RingSegments);
        int radials = Math.Max(3, RadialSegments);

        var vertices = new List<VertexPositionNormalTexture>((rings + 1) * (radials + 1));
        var indices  = new List<short>(rings * radials * 6);

        for (int ring = 0; ring <= rings; ring++)
        {
            float v     = ring / (float)rings;
            float phi   = v * MathF.PI;              // 0 at the north pole, PI at the south
            float y     = MathF.Cos(phi) * 0.5f;
            float ringR = MathF.Sin(phi) * 0.5f;

            for (int radial = 0; radial <= radials; radial++)
            {
                float u     = radial / (float)radials;
                float theta = u * MathF.Tau;

                var position = new Vector3(MathF.Cos(theta) * ringR, y, MathF.Sin(theta) * ringR);

                // On a sphere centred at the origin the normal is the direction from the
                // centre, so it is the position normalised.
                vertices.Add(new VertexPositionNormalTexture(
                    position, Vector3.Normalize(position), new Vector2(u, v)));
            }
        }

        int stride = radials + 1;
        for (int ring = 0; ring < rings; ring++)
        {
            for (int radial = 0; radial < radials; radial++)
            {
                int a = ring * stride + radial;
                int b = a + stride;

                indices.Add((short)a);
                indices.Add((short)b);
                indices.Add((short)(a + 1));

                indices.Add((short)(a + 1));
                indices.Add((short)b);
                indices.Add((short)(b + 1));
            }
        }

        return (vertices.ToArray(), indices.ToArray());
    }

    /// <summary>A 1x1 floor in the XZ plane, facing up.</summary>
    private static (VertexPositionNormalTexture[], short[]) BuildPlane()
    {
        var vertices = new[]
        {
            new VertexPositionNormalTexture(new Vector3(-0.5f, 0f, -0.5f), Vector3.Up, new Vector2(0, 0)),
            new VertexPositionNormalTexture(new Vector3( 0.5f, 0f, -0.5f), Vector3.Up, new Vector2(1, 0)),
            new VertexPositionNormalTexture(new Vector3( 0.5f, 0f,  0.5f), Vector3.Up, new Vector2(1, 1)),
            new VertexPositionNormalTexture(new Vector3(-0.5f, 0f,  0.5f), Vector3.Up, new Vector2(0, 1)),
        };

        return (vertices, new short[] { 0, 3, 2, 0, 2, 1 });
    }

    /// <summary>A 1x1 upright quad in the XY plane, facing +Z.</summary>
    private static (VertexPositionNormalTexture[], short[]) BuildQuad()
    {
        var vertices = new[]
        {
            new VertexPositionNormalTexture(new Vector3(-0.5f, -0.5f, 0f), Vector3.Backward, new Vector2(0, 1)),
            new VertexPositionNormalTexture(new Vector3( 0.5f, -0.5f, 0f), Vector3.Backward, new Vector2(1, 1)),
            new VertexPositionNormalTexture(new Vector3( 0.5f,  0.5f, 0f), Vector3.Backward, new Vector2(1, 0)),
            new VertexPositionNormalTexture(new Vector3(-0.5f,  0.5f, 0f), Vector3.Backward, new Vector2(0, 0)),
        };

        return (vertices, new short[] { 0, 1, 2, 0, 2, 3 });
    }

    private static (VertexPositionNormalTexture[], short[]) BuildCylinder()
    {
        int radials = Math.Max(3, RadialSegments);
        var vertices = new List<VertexPositionNormalTexture>();
        var indices  = new List<short>();

        // Side wall: its normals point outward, so it cannot share vertices with the caps,
        // whose normals point along Y.
        for (int i = 0; i <= radials; i++)
        {
            float u     = i / (float)radials;
            float theta = u * MathF.Tau;
            float x     = MathF.Cos(theta) * 0.5f;
            float z     = MathF.Sin(theta) * 0.5f;
            var normal  = Vector3.Normalize(new Vector3(x, 0f, z));

            vertices.Add(new VertexPositionNormalTexture(new Vector3(x,  0.5f, z), normal, new Vector2(u, 0)));
            vertices.Add(new VertexPositionNormalTexture(new Vector3(x, -0.5f, z), normal, new Vector2(u, 1)));
        }

        for (int i = 0; i < radials; i++)
        {
            int a = i * 2;
            indices.AddRange(new[] { (short)a, (short)(a + 1), (short)(a + 2) });
            indices.AddRange(new[] { (short)(a + 2), (short)(a + 1), (short)(a + 3) });
        }

        AddCap(vertices, indices, radials, y:  0.5f, normal: Vector3.Up,   clockwise: false);
        AddCap(vertices, indices, radials, y: -0.5f, normal: Vector3.Down, clockwise: true);

        return (vertices.ToArray(), indices.ToArray());
    }

    private static (VertexPositionNormalTexture[], short[]) BuildCone()
    {
        int radials = Math.Max(3, RadialSegments);
        var vertices = new List<VertexPositionNormalTexture>();
        var indices  = new List<short>();

        var apex = new Vector3(0f, 0.5f, 0f);

        for (int i = 0; i < radials; i++)
        {
            float t0 = i / (float)radials * MathF.Tau;
            float t1 = (i + 1) / (float)radials * MathF.Tau;

            var p0 = new Vector3(MathF.Cos(t0) * 0.5f, -0.5f, MathF.Sin(t0) * 0.5f);
            var p1 = new Vector3(MathF.Cos(t1) * 0.5f, -0.5f, MathF.Sin(t1) * 0.5f);

            // One flat normal per side triangle: a cone's apex has no single normal, so
            // faceting is the honest result rather than a smoothed artefact.
            var normal = Vector3.Normalize(Vector3.Cross(p1 - p0, apex - p0));

            short b = (short)vertices.Count;
            vertices.Add(new VertexPositionNormalTexture(p0,   normal, new Vector2(i / (float)radials, 1)));
            vertices.Add(new VertexPositionNormalTexture(p1,   normal, new Vector2((i + 1) / (float)radials, 1)));
            vertices.Add(new VertexPositionNormalTexture(apex, normal, new Vector2(0.5f, 0)));

            indices.AddRange(new[] { b, (short)(b + 1), (short)(b + 2) });
        }

        AddCap(vertices, indices, radials, y: -0.5f, normal: Vector3.Down, clockwise: true);
        return (vertices.ToArray(), indices.ToArray());
    }

    /// <summary>Adds a triangle-fan cap at the given height.</summary>
    private static void AddCap(List<VertexPositionNormalTexture> vertices, List<short> indices,
                               int radials, float y, Vector3 normal, bool clockwise)
    {
        short centre = (short)vertices.Count;
        vertices.Add(new VertexPositionNormalTexture(new Vector3(0f, y, 0f), normal, new Vector2(0.5f, 0.5f)));

        for (int i = 0; i <= radials; i++)
        {
            float theta = i / (float)radials * MathF.Tau;
            float x = MathF.Cos(theta) * 0.5f;
            float z = MathF.Sin(theta) * 0.5f;

            vertices.Add(new VertexPositionNormalTexture(
                new Vector3(x, y, z), normal,
                new Vector2(x + 0.5f, z + 0.5f)));
        }

        for (int i = 0; i < radials; i++)
        {
            short a = (short)(centre + 1 + i);
            short b = (short)(centre + 2 + i);

            if (clockwise) indices.AddRange(new[] { centre, b, a });
            else           indices.AddRange(new[] { centre, a, b });
        }
    }
}
