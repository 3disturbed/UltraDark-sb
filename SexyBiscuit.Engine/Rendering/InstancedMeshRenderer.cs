using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// Draws many copies of one mesh, each with its own transform and colour.
/// Modelled on Unreal's <c>UInstancedStaticMeshComponent</c>.
/// </summary>
/// <remarks>
/// <para>
/// Grass, rocks, crowds and debris are the same geometry a thousand times over. As
/// separate actors that is a thousand draw calls and a thousand components to tick; here
/// it is one buffer and one draw.
/// </para>
/// <para>
/// Hardware instancing needs a shader that reads the per-instance stream, so a compiled
/// <see cref="InstanceEffect"/> is required for the fast path. Without one the component
/// falls back to drawing each instance separately through <see cref="BasicEffect"/> — the
/// same picture at a fraction of the speed, which keeps a scene visible while you are
/// still setting the content pipeline up rather than showing nothing.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var grass = actor.AddComponent&lt;InstancedMeshRenderer&gt;();
/// grass.MeshType = MeshPrimitive.Quad;
///
/// for (int i = 0; i &lt; 5000; i++)
///     grass.AddInstance(RandomPointOnTerrain(), Quaternion.Identity, Vector3.One);
/// </code>
/// </example>
public sealed class InstancedMeshRenderer : Component
{
    /// <summary>Every live instanced renderer.</summary>
    public static readonly List<InstancedMeshRenderer> All = new();

    /// <summary>Per-instance data uploaded to the GPU.</summary>
    public struct InstanceData : IVertexType
    {
        /// <summary>World transform for this instance, as four rows.</summary>
        public Vector4 Row0, Row1, Row2, Row3;

        /// <summary>Per-instance tint.</summary>
        public Color Tint;

        public static readonly VertexDeclaration VertexDeclaration = new(
            new VertexElement(0,  VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 1),
            new VertexElement(16, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 2),
            new VertexElement(32, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 3),
            new VertexElement(48, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 4),
            new VertexElement(64, VertexElementFormat.Color,   VertexElementUsage.Color,             1));

        readonly VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;
    }

    /// <summary>Built-in shape to instance.</summary>
    public MeshPrimitive MeshType { get; set; } = MeshPrimitive.Cube;

    /// <summary>Material applied to every instance.</summary>
    public Material3D Material { get; set; } = new();

    /// <summary>
    /// Compiled effect that reads the per-instance stream. Without one the component
    /// falls back to one draw per instance.
    /// </summary>
    public Effect? InstanceEffect { get; set; }

    /// <summary>Number of live instances.</summary>
    public int InstanceCount => _instances.Count;

    /// <summary>True when the fast path is in use.</summary>
    public bool IsHardwareInstanced => InstanceEffect != null;

    /// <summary>Bounds covering every instance, for frustum culling.</summary>
    public Bounds WorldBounds { get; private set; }

    private readonly List<InstanceData> _instances = new();
    private DynamicVertexBuffer? _instanceBuffer;
    private bool _instancesDirty = true;
    private bool _warnedNoEffect;

    private Transform3D? _t3d;
    private Transform3D GetTransform3D()
        => _t3d ??= Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();

    public override void Awake() => All.Add(this);

    /// <summary>Adds an instance and returns its index.</summary>
    public int AddInstance(Vector3 position, Quaternion rotation, Vector3 scale, Color? tint = null)
    {
        var world = Matrix.CreateScale(scale)
                  * Matrix.CreateFromQuaternion(rotation)
                  * Matrix.CreateTranslation(position);

        return AddInstance(world, tint);
    }

    /// <summary>Adds an instance from a full transform matrix.</summary>
    public int AddInstance(Matrix world, Color? tint = null)
    {
        _instances.Add(new InstanceData
        {
            // Transposed on upload: a shader reads the matrix as four float4 attributes,
            // and row-major rows are what map onto those cleanly.
            Row0 = new Vector4(world.M11, world.M21, world.M31, world.M41),
            Row1 = new Vector4(world.M12, world.M22, world.M32, world.M42),
            Row2 = new Vector4(world.M13, world.M23, world.M33, world.M43),
            Row3 = new Vector4(world.M14, world.M24, world.M34, world.M44),
            Tint = tint ?? Color.White,
        });

        _instancesDirty = true;
        return _instances.Count - 1;
    }

    /// <summary>Removes every instance.</summary>
    public void ClearInstances()
    {
        _instances.Clear();
        _instancesDirty = true;
    }

    /// <summary>Replaces one instance's transform.</summary>
    public void SetInstanceTransform(int index, Matrix world, Color? tint = null)
    {
        if (index < 0 || index >= _instances.Count) return;

        var data = _instances[index];
        data.Row0 = new Vector4(world.M11, world.M21, world.M31, world.M41);
        data.Row1 = new Vector4(world.M12, world.M22, world.M32, world.M42);
        data.Row2 = new Vector4(world.M13, world.M23, world.M33, world.M43);
        data.Row3 = new Vector4(world.M14, world.M24, world.M34, world.M44);
        if (tint.HasValue) data.Tint = tint.Value;

        _instances[index] = data;
        _instancesDirty = true;
    }

    /// <summary>Draws every instance. Called by <see cref="RenderSystem3D"/>.</summary>
    public void Draw(GraphicsDevice gd, Matrix view, Matrix projection)
    {
        if (_instances.Count == 0) return;

        var geometry = PrimitiveMesh.Get(MeshType, gd);
        if (geometry == null) return;

        if (_instancesDirty) UploadInstances(gd);

        if (InstanceEffect != null) DrawInstanced(gd, geometry, view, projection);
        else                        DrawOneByOne(gd, geometry, view, projection);
    }

    private void UploadInstances(GraphicsDevice gd)
    {
        if (_instanceBuffer == null || _instanceBuffer.VertexCount < _instances.Count)
        {
            _instanceBuffer?.Dispose();
            _instanceBuffer = new DynamicVertexBuffer(gd, InstanceData.VertexDeclaration,
                Math.Max(16, _instances.Count * 2), BufferUsage.WriteOnly);
        }

        _instanceBuffer.SetData(_instances.ToArray(), 0, _instances.Count, SetDataOptions.Discard);
        RecomputeBounds();
        _instancesDirty = false;
    }

    /// <summary>Grows a single box around every instance, for one culling test instead of N.</summary>
    private void RecomputeBounds()
    {
        if (_instances.Count == 0)
        {
            WorldBounds = Bounds.Zero;
            return;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var instance in _instances)
        {
            // Row3 holds the translation after the transpose above.
            var position = new Vector3(instance.Row0.W, instance.Row1.W, instance.Row2.W);
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        var bounds = Bounds.FromMinMax(min, max);

        // Pad by the mesh's own extent — an instance's origin being inside the box does
        // not mean its geometry is.
        var meshExtent = PrimitiveMeshExtent();
        bounds.Expand(meshExtent);
        WorldBounds = bounds;
    }

    private float PrimitiveMeshExtent() => MeshType switch
    {
        MeshPrimitive.Plane or MeshPrimitive.Quad => 0.75f,
        _ => 1f,
    };

    private void DrawInstanced(GraphicsDevice gd, PrimitiveMesh.Geometry geometry,
                               Matrix view, Matrix projection)
    {
        var fx = InstanceEffect!;
        Material.Apply(fx);

        fx.Parameters["View"]?.SetValue(view);
        fx.Parameters["Projection"]?.SetValue(projection);
        fx.Parameters["World"]?.SetValue(GetTransform3D().GetWorldMatrix());

        gd.SetVertexBuffers(
            new VertexBufferBinding(geometry.VertexBuffer, 0, 0),
            new VertexBufferBinding(_instanceBuffer!, 0, 1));

        gd.Indices = geometry.IndexBuffer;

        foreach (var pass in fx.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawInstancedPrimitives(PrimitiveType.TriangleList, 0, 0,
                geometry.PrimitiveCount, _instances.Count);
        }
    }

    /// <summary>
    /// The fallback: one draw per instance through BasicEffect.
    /// </summary>
    /// <remarks>
    /// Slow by design rather than by accident. Showing the scene at a poor frame rate is
    /// more useful than showing nothing while the shader is still being set up, and the
    /// one-time warning says which is happening.
    /// </remarks>
    private void DrawOneByOne(GraphicsDevice gd, PrimitiveMesh.Geometry geometry,
                              Matrix view, Matrix projection)
    {
        if (!_warnedNoEffect)
        {
            Console.Error.WriteLine(
                $"[InstancedMeshRenderer] '{Actor.Name}' has {_instances.Count} instances and no " +
                "InstanceEffect, so each one is drawn separately. Assign a compiled instancing " +
                "shader for the single-draw path.");
            _warnedNoEffect = true;
        }

        var basic = _fallback ??= new BasicEffect(gd) { PreferPerPixelLighting = true };
        basic.EnableDefaultLighting();
        basic.View       = view;
        basic.Projection = projection;
        basic.TextureEnabled = Material.AlbedoMap != null;
        if (Material.AlbedoMap != null) basic.Texture = Material.AlbedoMap;

        gd.SetVertexBuffer(geometry.VertexBuffer);
        gd.Indices = geometry.IndexBuffer;

        foreach (var instance in _instances)
        {
            basic.World = new Matrix(
                instance.Row0.X, instance.Row1.X, instance.Row2.X, instance.Row3.X,
                instance.Row0.Y, instance.Row1.Y, instance.Row2.Y, instance.Row3.Y,
                instance.Row0.Z, instance.Row1.Z, instance.Row2.Z, instance.Row3.Z,
                instance.Row0.W, instance.Row1.W, instance.Row2.W, instance.Row3.W);

            basic.DiffuseColor = instance.Tint.ToVector3();

            foreach (var pass in basic.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geometry.PrimitiveCount);
            }
        }
    }

    private BasicEffect? _fallback;

    public override void OnDestroy()
    {
        All.Remove(this);
        _instanceBuffer?.Dispose();
        _fallback?.Dispose();
        _instanceBuffer = null;
        _fallback = null;
    }
}
