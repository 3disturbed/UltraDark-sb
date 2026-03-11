using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;
using Assimp;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// Renders a 3D mesh loaded via AssimpNet.
/// Supports multiple submeshes, per-submesh materials, and falls back to a
/// unit-cube BasicEffect draw when no model is loaded.
/// </summary>
public sealed class MeshRenderer : Component
{
    // -------------------------------------------------------------------------
    // Sub-mesh container
    // -------------------------------------------------------------------------
    public sealed class SubMesh
    {
        public VertexBuffer  VertexBuffer   { get; init; } = null!;
        public IndexBuffer   IndexBuffer    { get; init; } = null!;
        public int           PrimitiveCount { get; init; }
        public int           MaterialIndex  { get; init; }
    }

    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------
    public string?          ModelPath  { get; private set; }
    public List<Material3D> Materials  { get; set; } = new();

    private List<SubMesh>  _subMeshes  = new();
    private BasicEffect?   _fallback;

    // -------------------------------------------------------------------------
    // Cached Transform3D
    // -------------------------------------------------------------------------
    private Transform3D? _t3d;
    private Transform3D GetTransform3D()
    {
        if (_t3d != null) return _t3d;
        _t3d = Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();
        return _t3d;
    }

    // -------------------------------------------------------------------------
    // Model loading
    // -------------------------------------------------------------------------
    /// <summary>
    /// Loads a 3D model from disk using AssimpNet and uploads geometry to GPU buffers.
    /// Call this after the GraphicsDevice is ready (i.e. from Start or later).
    /// </summary>
    public void LoadModel(string path, GraphicsDevice gd)
    {
        // Dispose previous geometry
        DisposeBuffers();
        _subMeshes.Clear();
        Materials.Clear();
        ModelPath = path;

        using var ctx = new AssimpContext();

        var postProcess =
            PostProcessSteps.Triangulate         |
            PostProcessSteps.GenerateNormals      |
            PostProcessSteps.GenerateUVCoords     |
            PostProcessSteps.CalculateTangentSpace|
            PostProcessSteps.FlipUVs;

        Assimp.Scene scene;
        try
        {
            scene = ctx.ImportFile(path, postProcess);
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[MeshRenderer] Failed to load '{path}': {ex.Message}");
            return;
        }

        // Build one SubMesh per Assimp mesh
        foreach (var mesh in scene.Meshes)
        {
            var vertices = new VertexPositionNormalTexture[mesh.VertexCount];

            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var pos = mesh.Vertices[i];
                var nor = mesh.HasNormals ? mesh.Normals[i] : new Vector3D(0, 1, 0);
                var uv  = mesh.HasTextureCoords(0)
                    ? mesh.TextureCoordinateChannels[0][i]
                    : new Vector3D(0, 0, 0);

                vertices[i] = new VertexPositionNormalTexture(
                    new Microsoft.Xna.Framework.Vector3(pos.X, pos.Y, pos.Z),
                    new Microsoft.Xna.Framework.Vector3(nor.X, nor.Y, nor.Z),
                    new Microsoft.Xna.Framework.Vector2(uv.X,  uv.Y));
            }

            // Build flat index list from Assimp faces (already triangulated)
            var indices = new List<int>(mesh.FaceCount * 3);
            foreach (var face in mesh.Faces)
                foreach (var idx in face.Indices)
                    indices.Add(idx);

            var vb = new VertexBuffer(gd, VertexPositionNormalTexture.VertexDeclaration,
                vertices.Length, BufferUsage.WriteOnly);
            vb.SetData(vertices);

            // Choose 16- or 32-bit indices based on vertex count
            IndexBuffer ib;
            int primCount = indices.Count / 3;

            if (vertices.Length <= ushort.MaxValue)
            {
                var idx16 = indices.Select(i => (ushort)i).ToArray();
                ib = new IndexBuffer(gd, IndexElementSize.SixteenBits, idx16.Length, BufferUsage.WriteOnly);
                ib.SetData(idx16);
            }
            else
            {
                var idx32 = indices.ToArray();
                ib = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, idx32.Length, BufferUsage.WriteOnly);
                ib.SetData(idx32);
            }

            _subMeshes.Add(new SubMesh
            {
                VertexBuffer   = vb,
                IndexBuffer    = ib,
                PrimitiveCount = primCount,
                MaterialIndex  = mesh.MaterialIndex
            });

            // Ensure Materials list has a slot for every material index referenced
            while (Materials.Count <= mesh.MaterialIndex)
                Materials.Add(Material3D.Default);
        }
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------
    /// <summary>
    /// Draws all submeshes. Call from a 3D render pass (outside SpriteBatch).
    /// </summary>
    public void Draw(GraphicsDevice gd, Microsoft.Xna.Framework.Matrix view, Microsoft.Xna.Framework.Matrix projection)
    {
        var world = GetTransform3D().GetWorldMatrix();

        if (_subMeshes.Count == 0)
        {
            DrawFallbackCube(gd, world, view, projection);
            return;
        }

        foreach (var sub in _subMeshes)
        {
            var mat    = sub.MaterialIndex < Materials.Count ? Materials[sub.MaterialIndex] : Material3D.Default;
            var shader = mat.Shader;

            if (shader != null)
            {
                // Custom HLSL path
                mat.Apply(shader);

                var wp = shader.Parameters["World"];
                var vp = shader.Parameters["View"];
                var pp = shader.Parameters["Projection"];
                var wvp = shader.Parameters["WorldViewProjection"];

                wp?.SetValue(world);
                vp?.SetValue(view);
                pp?.SetValue(projection);
                wvp?.SetValue(world * view * projection);

                gd.SetVertexBuffer(sub.VertexBuffer);
                gd.Indices = sub.IndexBuffer;

                foreach (var pass in shader.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawIndexedPrimitives(Microsoft.Xna.Framework.Graphics.PrimitiveType.TriangleList, 0, 0, sub.PrimitiveCount);
                }
            }
            else
            {
                // Default BasicEffect path
                var basic = GetOrCreateFallback(gd);
                basic.World      = world;
                basic.View       = view;
                basic.Projection = projection;

                if (mat.AlbedoMap != null)
                {
                    basic.TextureEnabled = true;
                    basic.Texture        = mat.AlbedoMap;
                }
                else
                {
                    basic.TextureEnabled = false;
                    basic.DiffuseColor   = mat.AlbedoColor.ToVector3();
                }

                gd.SetVertexBuffer(sub.VertexBuffer);
                gd.Indices = sub.IndexBuffer;

                foreach (var pass in basic.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawIndexedPrimitives(Microsoft.Xna.Framework.Graphics.PrimitiveType.TriangleList, 0, 0, sub.PrimitiveCount);
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Fallback unit cube
    // -------------------------------------------------------------------------
    private static readonly VertexPositionNormalTexture[] CubeVerts = BuildCubeVerts();
    private static readonly short[] CubeIndices = BuildCubeIndices();

    private static VertexPositionNormalTexture[] BuildCubeVerts()
    {
        // 24 vertices (4 per face, 6 faces) for correct normals
        var n = new[]
        {
            Microsoft.Xna.Framework.Vector3.Forward,  Microsoft.Xna.Framework.Vector3.Backward,
            Microsoft.Xna.Framework.Vector3.Up,        Microsoft.Xna.Framework.Vector3.Down,
            Microsoft.Xna.Framework.Vector3.Left,      Microsoft.Xna.Framework.Vector3.Right
        };

        // corners per face (CCW when viewed from outside)
        var fc = new[]
        {
            new[]{new Microsoft.Xna.Framework.Vector3(-1,-1,-1), new Microsoft.Xna.Framework.Vector3( 1,-1,-1), new Microsoft.Xna.Framework.Vector3( 1, 1,-1), new Microsoft.Xna.Framework.Vector3(-1, 1,-1)}, // forward (-Z)
            new[]{new Microsoft.Xna.Framework.Vector3( 1,-1, 1), new Microsoft.Xna.Framework.Vector3(-1,-1, 1), new Microsoft.Xna.Framework.Vector3(-1, 1, 1), new Microsoft.Xna.Framework.Vector3( 1, 1, 1)}, // backward (+Z)
            new[]{new Microsoft.Xna.Framework.Vector3(-1, 1,-1), new Microsoft.Xna.Framework.Vector3( 1, 1,-1), new Microsoft.Xna.Framework.Vector3( 1, 1, 1), new Microsoft.Xna.Framework.Vector3(-1, 1, 1)}, // up
            new[]{new Microsoft.Xna.Framework.Vector3(-1,-1, 1), new Microsoft.Xna.Framework.Vector3( 1,-1, 1), new Microsoft.Xna.Framework.Vector3( 1,-1,-1), new Microsoft.Xna.Framework.Vector3(-1,-1,-1)}, // down
            new[]{new Microsoft.Xna.Framework.Vector3(-1,-1, 1), new Microsoft.Xna.Framework.Vector3(-1,-1,-1), new Microsoft.Xna.Framework.Vector3(-1, 1,-1), new Microsoft.Xna.Framework.Vector3(-1, 1, 1)}, // left
            new[]{new Microsoft.Xna.Framework.Vector3( 1,-1,-1), new Microsoft.Xna.Framework.Vector3( 1,-1, 1), new Microsoft.Xna.Framework.Vector3( 1, 1, 1), new Microsoft.Xna.Framework.Vector3( 1, 1,-1)}, // right
        };

        var uv = new[]
        {
            new Microsoft.Xna.Framework.Vector2(0,1),
            new Microsoft.Xna.Framework.Vector2(1,1),
            new Microsoft.Xna.Framework.Vector2(1,0),
            new Microsoft.Xna.Framework.Vector2(0,0)
        };

        var verts = new VertexPositionNormalTexture[24];
        for (int f = 0; f < 6; f++)
            for (int v = 0; v < 4; v++)
                verts[f * 4 + v] = new VertexPositionNormalTexture(fc[f][v] * 0.5f, n[f], uv[v]);

        return verts;
    }

    private static short[] BuildCubeIndices()
    {
        var idx = new short[36];
        for (int f = 0; f < 6; f++)
        {
            int b = f * 6;
            short v = (short)(f * 4);
            idx[b+0] = v;  idx[b+1] = (short)(v+1); idx[b+2] = (short)(v+2);
            idx[b+3] = v;  idx[b+4] = (short)(v+2); idx[b+5] = (short)(v+3);
        }
        return idx;
    }

    private void DrawFallbackCube(GraphicsDevice gd,
        Microsoft.Xna.Framework.Matrix world,
        Microsoft.Xna.Framework.Matrix view,
        Microsoft.Xna.Framework.Matrix projection)
    {
        var basic = GetOrCreateFallback(gd);
        basic.World         = world;
        basic.View          = view;
        basic.Projection    = projection;
        basic.TextureEnabled = false;
        basic.DiffuseColor  = Microsoft.Xna.Framework.Vector3.One;

        foreach (var pass in basic.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserIndexedPrimitives(
                Microsoft.Xna.Framework.Graphics.PrimitiveType.TriangleList,
                CubeVerts, 0, CubeVerts.Length,
                CubeIndices, 0, 12);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private BasicEffect GetOrCreateFallback(GraphicsDevice gd)
    {
        if (_fallback == null)
        {
            _fallback = new BasicEffect(gd)
            {
                LightingEnabled = true,
                PreferPerPixelLighting = false
            };
            _fallback.EnableDefaultLighting();
        }
        return _fallback;
    }

    private void DisposeBuffers()
    {
        foreach (var s in _subMeshes)
        {
            s.VertexBuffer?.Dispose();
            s.IndexBuffer?.Dispose();
        }
    }

    public override void OnDestroy()
    {
        DisposeBuffers();
        _fallback?.Dispose();
        _fallback = null;
    }
}
