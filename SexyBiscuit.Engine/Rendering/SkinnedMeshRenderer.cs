using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Core;
using Assimp;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// Renders a skinned mesh deformed by a <see cref="SkeletalAnimator"/>'s bone palette.
/// </summary>
/// <remarks>
/// <para>
/// Skinning runs on the GPU through MonoGame's built-in <see cref="SkinnedEffect"/>, which
/// takes up to 72 bone matrices and 4 weights per vertex — enough for a typical humanoid.
/// A mesh with more than 72 bones is rejected at load with a clear message rather than
/// silently rendering wrong.
/// </para>
/// <para>
/// Bone indices come from the same Assimp import that builds the skeleton, so the palette
/// produced by <see cref="SkeletalAnimator"/> lines up with the vertex weights without any
/// remapping step.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var animator = actor.AddComponent&lt;SkeletalAnimator&gt;();
/// var skin     = actor.AddComponent&lt;SkinnedMeshRenderer&gt;();
/// skin.LoadModel("Assets/hero.fbx", GraphicsDevice);
/// animator.Play("Run");
/// </code>
/// </example>
public sealed class SkinnedMeshRenderer : Component
{
    /// <summary>Hard limit imposed by <see cref="SkinnedEffect"/>.</summary>
    public const int MaxBones = 72;

    /// <summary>Every live skinned renderer. <see cref="RenderSystem3D"/> iterates this.</summary>
    public static readonly List<SkinnedMeshRenderer> All = new();

    /// <summary>One drawable chunk of the skinned mesh.</summary>
    public sealed class SkinnedSubMesh
    {
        public VertexBuffer VertexBuffer   { get; init; } = null!;
        public IndexBuffer  IndexBuffer    { get; init; } = null!;
        public int          PrimitiveCount { get; init; }
        public int          MaterialIndex  { get; init; }
    }

    /// <summary>Path of the loaded model, or null when nothing is loaded.</summary>
    public string? ModelPath { get; private set; }

    /// <summary>Per-submesh materials, indexed by <see cref="SkinnedSubMesh.MaterialIndex"/>.</summary>
    public List<Material3D> Materials { get; set; } = new();

    /// <summary>Object-space bounds of the mesh in its bind pose.</summary>
    public Bounds LocalBounds { get; private set; } = new(Vector3.Zero, Vector3.One);

    /// <summary>World-space bounds, conservative because it ignores the current pose.</summary>
    public Bounds WorldBounds => LocalBounds.Transform(GetTransform3D().GetWorldMatrix());

    /// <summary>Submeshes uploaded to the GPU.</summary>
    public IReadOnlyList<SkinnedSubMesh> SubMeshes => _subMeshes;

    /// <summary>Number of bones the loaded mesh is weighted to.</summary>
    public int BoneCount { get; private set; }

    private readonly List<SkinnedSubMesh> _subMeshes = new();
    private SkinnedEffect?  _effect;
    private SkeletalAnimator? _animator;
    private Transform3D?    _t3d;
    private Matrix[]        _identityPalette = Array.Empty<Matrix>();

    /// <summary>The transform this renderer draws at, created on the actor if absent.</summary>
    public Transform3D GetTransform3D()
        => _t3d ??= Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();

    public override void Awake() => All.Add(this);

    public override void Start() => _animator = Actor.GetComponent<SkeletalAnimator>();

    // -------------------------------------------------------------------------
    // Loading
    // -------------------------------------------------------------------------

    /// <summary>
    /// Imports a rigged model and uploads its skinned geometry. Call once the graphics
    /// device is available — from Start or later.
    /// </summary>
    /// <param name="path">Model file understood by Assimp (FBX, glTF, DAE, …).</param>
    /// <param name="gd">Device the buffers are created on.</param>
    /// <returns>False when the file could not be read or exceeds <see cref="MaxBones"/>.</returns>
    public bool LoadModel(string path, GraphicsDevice gd)
    {
        DisposeBuffers();
        _subMeshes.Clear();
        Materials.Clear();
        ModelPath = path;

        using var ctx = new AssimpContext();

        Assimp.Scene scene;
        try
        {
            scene = ctx.ImportFile(path,
                PostProcessSteps.Triangulate         |
                PostProcessSteps.GenerateNormals     |
                PostProcessSteps.GenerateUVCoords    |
                PostProcessSteps.LimitBoneWeights    |   // caps influences at 4, matching SkinnedEffect
                PostProcessSteps.FlipUVs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SkinnedMeshRenderer] Failed to load '{path}': {ex.Message}");
            return false;
        }

        var boneIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool anyVertex = false;

        foreach (var mesh in scene.Meshes)
        {
            if (!mesh.HasBones)
            {
                Console.Error.WriteLine(
                    $"[SkinnedMeshRenderer] Mesh '{mesh.Name}' in '{path}' has no bones; " +
                    "use MeshRenderer for static geometry.");
                continue;
            }

            // Accumulate up to four influences per vertex.
            var indices = new Vector4[mesh.VertexCount];
            var weights = new Vector4[mesh.VertexCount];
            var counts  = new int[mesh.VertexCount];

            foreach (var bone in mesh.Bones)
            {
                if (!boneIndexByName.TryGetValue(bone.Name, out int boneIndex))
                {
                    boneIndex = boneIndexByName.Count;
                    boneIndexByName[bone.Name] = boneIndex;
                }

                if (boneIndex >= MaxBones)
                {
                    Console.Error.WriteLine(
                        $"[SkinnedMeshRenderer] '{path}' needs more than {MaxBones} bones, " +
                        "which SkinnedEffect cannot bind. Reduce the skeleton or supply a custom shader.");
                    return false;
                }

                foreach (var weight in bone.VertexWeights)
                {
                    int v = weight.VertexID;
                    int slot = counts[v];
                    if (slot >= 4) continue;   // LimitBoneWeights should prevent this

                    SetComponent(ref indices[v], slot, boneIndex);
                    SetComponent(ref weights[v], slot, weight.Weight);
                    counts[v] = slot + 1;
                }
            }

            var vertices = new VertexPositionNormalTextureBlend[mesh.VertexCount];
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var p = mesh.Vertices[i];
                var n = mesh.HasNormals ? mesh.Normals[i] : new Vector3D(0, 1, 0);
                var uv = mesh.HasTextureCoords(0) ? mesh.TextureCoordinateChannels[0][i] : new Vector3D(0, 0, 0);

                var position = new Vector3(p.X, p.Y, p.Z);
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
                anyVertex = true;

                vertices[i] = new VertexPositionNormalTextureBlend(
                    position,
                    new Vector3(n.X, n.Y, n.Z),
                    new Vector2(uv.X, uv.Y),
                    indices[i],
                    Normalize(weights[i]));
            }

            var flatIndices = new List<int>(mesh.FaceCount * 3);
            foreach (var face in mesh.Faces)
                flatIndices.AddRange(face.Indices);

            var vb = new VertexBuffer(gd, VertexPositionNormalTextureBlend.VertexDeclaration,
                vertices.Length, BufferUsage.WriteOnly);
            vb.SetData(vertices);

            IndexBuffer ib;
            if (vertices.Length <= ushort.MaxValue)
            {
                var i16 = flatIndices.Select(i => (ushort)i).ToArray();
                ib = new IndexBuffer(gd, IndexElementSize.SixteenBits, i16.Length, BufferUsage.WriteOnly);
                ib.SetData(i16);
            }
            else
            {
                var i32 = flatIndices.ToArray();
                ib = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, i32.Length, BufferUsage.WriteOnly);
                ib.SetData(i32);
            }

            _subMeshes.Add(new SkinnedSubMesh
            {
                VertexBuffer   = vb,
                IndexBuffer    = ib,
                PrimitiveCount = flatIndices.Count / 3,
                MaterialIndex  = mesh.MaterialIndex,
            });

            while (Materials.Count <= mesh.MaterialIndex) Materials.Add(Material3D.Default);
        }

        BoneCount   = boneIndexByName.Count;
        LocalBounds = anyVertex ? Bounds.FromMinMax(min, max) : new Bounds(Vector3.Zero, Vector3.One);

        _identityPalette = new Matrix[Math.Max(1, BoneCount)];
        Array.Fill(_identityPalette, Matrix.Identity);

        return _subMeshes.Count > 0;
    }

    private static void SetComponent(ref Vector4 v, int index, float value)
    {
        switch (index)
        {
            case 0: v.X = value; break;
            case 1: v.Y = value; break;
            case 2: v.Z = value; break;
            default: v.W = value; break;
        }
    }

    /// <summary>Rescales weights to sum to one so the skin does not shrink or bloat.</summary>
    private static Vector4 Normalize(Vector4 weights)
    {
        float sum = weights.X + weights.Y + weights.Z + weights.W;
        return sum > SBMath.Epsilon ? weights / sum : new Vector4(1f, 0f, 0f, 0f);
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Draws the mesh with the animator's current pose. Call from a 3D render pass,
    /// outside any <see cref="SpriteBatch"/> block.
    /// </summary>
    public void Draw(GraphicsDevice gd, Matrix view, Matrix projection)
    {
        if (_subMeshes.Count == 0) return;

        _effect ??= new SkinnedEffect(gd) { WeightsPerVertex = 4 };

        var palette = _animator?.BonePalette;
        if (palette == null || palette.Length == 0) palette = _identityPalette;

        // SkinnedEffect requires exactly its configured bone count, so pad or trim.
        if (palette.Length != MaxBones)
        {
            var padded = new Matrix[MaxBones];
            Array.Fill(padded, Matrix.Identity);
            Array.Copy(palette, padded, Math.Min(palette.Length, MaxBones));
            palette = padded;
        }

        _effect.SetBoneTransforms(palette);
        _effect.World      = GetTransform3D().GetWorldMatrix();
        _effect.View       = view;
        _effect.Projection = projection;
        _effect.EnableDefaultLighting();

        foreach (var sub in _subMeshes)
        {
            var material = sub.MaterialIndex < Materials.Count ? Materials[sub.MaterialIndex] : Material3D.Default;

            // SkinnedEffect always samples a texture, so substitute a white one when the
            // material is colour-only.
            _effect.Texture      = material.AlbedoMap ?? GetWhitePixel(gd);
            _effect.DiffuseColor = material.AlbedoColor.ToVector3();
            _effect.Alpha        = material.AlbedoColor.A / 255f;

            gd.SetVertexBuffer(sub.VertexBuffer);
            gd.Indices = sub.IndexBuffer;

            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawIndexedPrimitives(Microsoft.Xna.Framework.Graphics.PrimitiveType.TriangleList, 0, 0, sub.PrimitiveCount);
            }
        }
    }

    private static Texture2D? _whitePixel;
    private static Texture2D GetWhitePixel(GraphicsDevice gd)
    {
        if (_whitePixel is { IsDisposed: false }) return _whitePixel;
        _whitePixel = new Texture2D(gd, 1, 1);
        _whitePixel.SetData(new[] { Color.White });
        return _whitePixel;
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
        _effect?.Dispose();
        _effect = null;
    }
}

/// <summary>
/// Vertex layout for GPU skinning: position, normal, UV, four bone indices and four weights.
/// Matches what <see cref="SkinnedEffect"/> expects.
/// </summary>
public struct VertexPositionNormalTextureBlend : IVertexType
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TextureCoordinate;
    public Vector4 BlendIndices;
    public Vector4 BlendWeights;

    public VertexPositionNormalTextureBlend(Vector3 position, Vector3 normal,
        Vector2 uv, Vector4 blendIndices, Vector4 blendWeights)
    {
        Position          = position;
        Normal            = normal;
        TextureCoordinate = uv;
        BlendIndices      = blendIndices;
        BlendWeights      = blendWeights;
    }

    /// <summary>Declaration describing this layout to the graphics device.</summary>
    public static readonly VertexDeclaration VertexDeclaration = new(
        new VertexElement(0,  VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
        new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(32, VertexElementFormat.Vector4, VertexElementUsage.BlendIndices, 0),
        new VertexElement(48, VertexElementFormat.Vector4, VertexElementUsage.BlendWeight, 0));

    readonly VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;
}
