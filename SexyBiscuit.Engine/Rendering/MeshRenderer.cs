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
    // Registry — RenderSystem3D iterates this instead of walking the scene graph
    // -------------------------------------------------------------------------

    /// <summary>Every live mesh renderer, in creation order. Maintained automatically.</summary>
    public static readonly List<MeshRenderer> All = new();

    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------
    /// <summary>
    /// The model file to draw, relative to the project root. Setting it queues a load for the
    /// next draw, when a <see cref="GraphicsDevice"/> is in hand; setting it to null returns
    /// to <see cref="MeshType"/> or the fallback cube.
    /// </summary>
    /// <remarks>
    /// The setter used to be private, so a scene file's ModelPath round-tripped as a dead
    /// string that nothing acted on. Deferring the load to the draw is what lets a headless
    /// tool set the path and a renderer pick it up when the editor draws the frame.
    /// </remarks>
    public string? ModelPath
    {
        get => _modelPath;
        set
        {
            if (_modelPath == value) return;
            _modelPath  = value;
            _modelDirty = true;
        }
    }
    private string? _modelPath;
    private bool    _modelDirty;

    public List<Material3D> Materials  { get; set; } = new();

    // -------------------------------------------------------------------------
    // Material proxies — the first material's headline values, for inspectors and tools.
    // Not serialised: Materials already is, and two copies of one value drift apart.
    // -------------------------------------------------------------------------

    /// <summary>The first material's albedo colour. Writing it never touches the shared default material.</summary>
    [SceneIgnore]
    public Color AlbedoColor
    {
        get => PrimaryMaterial.AlbedoColor;
        set => EnsureOwnMaterial(0).AlbedoColor = value;
    }

    [SceneIgnore]
    public float Metallic
    {
        get => PrimaryMaterial.Metallic;
        set => EnsureOwnMaterial(0).Metallic = value;
    }

    [SceneIgnore]
    public float Roughness
    {
        get => PrimaryMaterial.Roughness;
        set => EnsureOwnMaterial(0).Roughness = value;
    }

    [SceneIgnore]
    public float EmissiveIntensity
    {
        get => PrimaryMaterial.EmissiveIntensity;
        set => EnsureOwnMaterial(0).EmissiveIntensity = value;
    }

    /// <summary>The first material's albedo texture path, relative to the project root.</summary>
    [SceneIgnore]
    public string? AlbedoTexturePath
    {
        get => PrimaryMaterial.AlbedoMapPath;
        set
        {
            var material = EnsureOwnMaterial(0);
            material.AlbedoMapPath = value;
            material.ResolveTextures();
        }
    }

    private Material3D PrimaryMaterial => Materials.Count > 0 ? Materials[0] : Material3D.Default;

    /// <summary>
    /// The material at <paramref name="index"/>, guaranteed to belong to this renderer: missing
    /// slots are created and the shared <see cref="Material3D.Default"/> is replaced by a clone
    /// before it is handed out for writing.
    /// </summary>
    public Material3D EnsureOwnMaterial(int index)
    {
        while (Materials.Count <= index)
            Materials.Add(new Material3D());

        if (ReferenceEquals(Materials[index], Material3D.Default))
            Materials[index] = Material3D.Default.Clone();

        return Materials[index];
    }

    /// <summary>
    /// Object-space bounds of the loaded geometry, used for frustum culling and LOD sizing.
    /// Computed on load; defaults to a unit cube matching the fallback geometry.
    /// </summary>
    public Bounds LocalBounds { get; private set; } = new(Microsoft.Xna.Framework.Vector3.Zero, Microsoft.Xna.Framework.Vector3.One);

    /// <summary>World-space bounds, recomputed from <see cref="LocalBounds"/> and the current transform.</summary>
    public Bounds WorldBounds => LocalBounds.Transform(GetTransform3D().GetWorldMatrix());

    /// <summary>Excludes this renderer from the frustum-culling test. Use for skyboxes and huge meshes.</summary>
    public bool IgnoreCulling { get; set; }

    /// <summary>
    /// Draw order bucket. Renderers marked transparent are drawn after all opaque geometry,
    /// sorted back-to-front, with depth writes disabled.
    /// </summary>
    public bool IsTransparent { get; set; }

    /// <summary>Renderer is skipped entirely when false. Set by <see cref="LODGroup"/>.</summary>
    public bool CastShadows { get; set; } = true;

    /// <summary>
    /// A built-in shape to draw instead of a loaded model — cube, sphere, plane and the
    /// rest.
    /// </summary>
    /// <remarks>
    /// Geometry is built on the first draw rather than when this is set, because a scene
    /// file is deserialised long before a <see cref="GraphicsDevice"/> is in reach. That
    /// is what lets a scene say <c>"MeshType": "Plane"</c> and get a floor with no asset
    /// on disk. Setting <see cref="LoadModel"/> afterwards takes precedence.
    /// </remarks>
    public MeshPrimitive MeshType
    {
        get => _meshType;
        set
        {
            if (_meshType == value) return;
            _meshType = value;
            _primitive = null;      // geometry is rebuilt on the next draw

            // Bounds cannot wait for the draw. Culling and picking read them first, and a
            // scaled-up mesh still carrying the default unit cube swallows every ray.
            LocalBounds = PrimitiveMesh.GetBounds(value);
        }
    }
    private MeshPrimitive _meshType = MeshPrimitive.None;

    private PrimitiveMesh.Geometry? _primitive;

    /// <summary>Total triangles across every submesh. Zero until a model is loaded.</summary>
    public int TriangleCount { get; private set; }

    /// <summary>Submeshes uploaded to the GPU. Empty while the fallback cube is in use.</summary>
    public IReadOnlyList<SubMesh> SubMeshes => _subMeshes;

    private List<SubMesh>  _subMeshes  = new();
    private BasicEffect?   _fallback;

    // -------------------------------------------------------------------------
    // Cached Transform3D
    // -------------------------------------------------------------------------
    private Transform3D? _t3d;

    /// <summary>The transform this renderer draws at, created on the actor if absent.</summary>
    public Transform3D GetTransform3D()
    {
        if (_t3d != null) return _t3d;
        _t3d = Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();
        return _t3d;
    }

    // -------------------------------------------------------------------------
    // Registration
    // -------------------------------------------------------------------------

    public override void Awake() => All.Add(this);

    /// <summary>Loads the textures the materials name, now that a host may be running.</summary>
    public override void Start()
    {
        foreach (var material in Materials)
            material.ResolveTextures();
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
        // Dispose previous geometry. Materials are kept: they were authored in the scene and
        // padding below adds slots for any index the model references beyond them.
        DisposeBuffers();
        _subMeshes.Clear();
        _modelPath  = path;
        _modelDirty = false;

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

        var boundsMin = new Microsoft.Xna.Framework.Vector3(float.MaxValue);
        var boundsMax = new Microsoft.Xna.Framework.Vector3(float.MinValue);
        int totalTris = 0;
        bool anyVertex = false;

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

                var xnaPos = new Microsoft.Xna.Framework.Vector3(pos.X, pos.Y, pos.Z);
                boundsMin  = Microsoft.Xna.Framework.Vector3.Min(boundsMin, xnaPos);
                boundsMax  = Microsoft.Xna.Framework.Vector3.Max(boundsMax, xnaPos);
                anyVertex  = true;

                vertices[i] = new VertexPositionNormalTexture(
                    xnaPos,
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

            totalTris += primCount;

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

        TriangleCount = totalTris;
        LocalBounds   = anyVertex
            ? Bounds.FromMinMax(boundsMin, boundsMax)
            : new Bounds(Microsoft.Xna.Framework.Vector3.Zero, Microsoft.Xna.Framework.Vector3.One);
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------
    /// <summary>
    /// Draws all submeshes. Call from a 3D render pass (outside SpriteBatch).
    /// </summary>
    public void Draw(GraphicsDevice gd, Microsoft.Xna.Framework.Matrix view, Microsoft.Xna.Framework.Matrix projection)
    {
        if (_modelDirty)
        {
            _modelDirty = false;
            string? requested = _modelPath;

            if (string.IsNullOrWhiteSpace(requested))
            {
                DisposeBuffers();
                _subMeshes.Clear();
                TriangleCount = 0;
                LocalBounds   = PrimitiveMesh.GetBounds(_meshType);
            }
            else
            {
                LoadModel(ProjectPaths.Resolve(requested), gd);
                _modelPath = requested;      // keep the project-relative form for saving
            }
        }

        var world = GetTransform3D().GetWorldMatrix();

        if (_subMeshes.Count == 0)
        {
            if (TryGetPrimitive(gd) is { } primitive)
            {
                DrawPrimitive(gd, primitive, world, view, projection);
                return;
            }

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

    /// <summary>
    /// Resolves the generated geometry for <see cref="MeshType"/>, building it on first
    /// use, and updates the renderer's bounds to match.
    /// </summary>
    internal PrimitiveMesh.Geometry? TryGetPrimitive(GraphicsDevice gd)
    {
        if (_meshType == MeshPrimitive.None) return null;
        if (_primitive is { } cached && !cached.VertexBuffer.IsDisposed) return cached;

        _primitive = PrimitiveMesh.Get(_meshType, gd);

        if (_primitive != null)
        {
            // Culling and LOD both read LocalBounds, so a generated shape has to publish
            // its own rather than keeping the default unit cube.
            LocalBounds   = _primitive.Bounds;
            TriangleCount = _primitive.PrimitiveCount;
        }

        return _primitive;
    }

    private void DrawPrimitive(GraphicsDevice gd, PrimitiveMesh.Geometry geometry,
        Microsoft.Xna.Framework.Matrix world,
        Microsoft.Xna.Framework.Matrix view,
        Microsoft.Xna.Framework.Matrix projection)
    {
        var material = Materials.Count > 0 ? Materials[0] : Material3D.Default;
        var basic = GetOrCreateFallback(gd);

        basic.World      = world;
        basic.View       = view;
        basic.Projection = projection;

        if (material.AlbedoMap != null)
        {
            basic.TextureEnabled = true;
            basic.Texture        = material.AlbedoMap;
            basic.DiffuseColor   = Microsoft.Xna.Framework.Vector3.One;
        }
        else
        {
            basic.TextureEnabled = false;
            basic.DiffuseColor   = material.AlbedoColor.ToVector3();
        }

        gd.SetVertexBuffer(geometry.VertexBuffer);
        gd.Indices = geometry.IndexBuffer;

        foreach (var pass in basic.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawIndexedPrimitives(
                Microsoft.Xna.Framework.Graphics.PrimitiveType.TriangleList,
                0, 0, geometry.PrimitiveCount);
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
        All.Remove(this);
        DisposeBuffers();
        _subMeshes.Clear();
        _fallback?.Dispose();
        _fallback = null;
    }
}
