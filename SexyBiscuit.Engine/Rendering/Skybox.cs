using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// Renders a cubemap skybox (or a procedural gradient when no cubemap is loaded).
/// Draw() should be called at the start of the 3D render pass, before opaque geometry,
/// with depth writes disabled.
/// </summary>
public sealed class Skybox : Component
{
    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------
    public TextureCube? CubemapTexture { get; private set; }
    public Color        GradientTop    { get; set; } = new Color(0.1f, 0.3f, 0.8f);
    public Color        GradientBottom { get; set; } = new Color(0.6f, 0.7f, 0.9f);

    /// <summary>
    /// Multiplier applied to the sky's colour as it is drawn. Default 1.
    /// </summary>
    /// <remarks>
    /// The browser scales the sky in its fragment shader; here it rides on the effect's
    /// diffuse colour, which <see cref="BasicEffect"/> multiplies into both the gradient's
    /// vertex colours and the cubemap. Below 1 darkens the sky under a bright scene; above 1
    /// blows it out. Negative is clamped away rather than inverting the sky.
    /// </remarks>
    public float Exposure { get; set; } = 1f;

    /// <summary>
    /// Folder holding the six cubemap faces. Empty (the default) leaves the gradient in place.
    /// </summary>
    /// <remarks>
    /// The one path a scene file stores, because six of them in a property bag is not
    /// something anyone edits by hand. <see cref="CubemapFacePaths"/> spells out the
    /// convention it stands for — <c>px</c>, <c>nx</c>, <c>py</c>, <c>ny</c>, <c>pz</c>,
    /// <c>nz</c> as .png, the names <c>wiki/05-rendering-3d.md</c> has always used. The faces
    /// are loaded on the first <see cref="Draw"/>, which is the first moment there is a
    /// <see cref="GraphicsDevice"/> to build a <see cref="TextureCube"/> with; setting the
    /// path again asks for another attempt.
    /// </remarks>
    public string CubemapPath
    {
        get => _cubemapPath;
        set
        {
            _cubemapPath  = value ?? "";
            _cubemapTried = false;
        }
    }
    private string _cubemapPath = "";
    private bool   _cubemapTried;

    /// <summary>Face file names in the order <see cref="TextureCube"/> wants them.</summary>
    private static readonly string[] FaceNames = { "px", "nx", "py", "ny", "pz", "nz" };

    /// <summary>
    /// The six face paths a <see cref="CubemapPath"/> folder stands for, +X,-X,+Y,-Y,+Z,-Z.
    /// </summary>
    public static string[] CubemapFacePaths(string folder)
    {
        string root = (folder ?? "").TrimEnd('/', '\\');
        return FaceNames.Select(name => $"{root}/{name}.png").ToArray();
    }

    // -------------------------------------------------------------------------
    // GPU resources (created on first Draw)
    // -------------------------------------------------------------------------
    private VertexBuffer?    _cubeVB;
    private IndexBuffer?     _cubeIB;
    private BasicEffect?     _cubemapEffect;
    private BasicEffect?     _gradientEffect;
    private VertexBuffer?    _quadVB;

    // The colours _quadVB's vertices were built from, so a later edit rebuilds it.
    private Color            _quadTop;
    private Color            _quadBottom;

    // Face order expected by TextureCube: +X, -X, +Y, -Y, +Z, -Z
    private static readonly CubeMapFace[] FaceOrder =
    {
        CubeMapFace.PositiveX, CubeMapFace.NegativeX,
        CubeMapFace.PositiveY, CubeMapFace.NegativeY,
        CubeMapFace.PositiveZ, CubeMapFace.NegativeZ
    };

    // -------------------------------------------------------------------------
    // Cubemap loading
    // -------------------------------------------------------------------------
    /// <summary>
    /// Loads 6 face images in +X,-X,+Y,-Y,+Z,-Z order and assembles a TextureCube.
    /// facePaths must have exactly 6 entries.
    /// </summary>
    /// <remarks>
    /// The first face decides the cube's size; a face that is missing, not square or a
    /// different size is reported and skipped, leaving that side of the sky black rather
    /// than failing the whole load. A first face that cannot be read leaves the gradient in
    /// place, because there is nothing to size a cube from.
    /// </remarks>
    public void LoadCubemap(string[] facePaths, GraphicsDevice gd)
    {
        if (facePaths.Length != 6)
            throw new ArgumentException("Exactly 6 face paths are required (+X -X +Y -Y +Z -Z).", nameof(facePaths));

        var first = ReadFace(facePaths[0], gd);
        if (first == null) return;

        int size = first.Value.Size;
        var cube = new TextureCube(gd, size, false, SurfaceFormat.Color);
        cube.SetData(FaceOrder[0], first.Value.Pixels);

        for (int i = 1; i < 6; i++)
        {
            var face = ReadFace(facePaths[i], gd);
            if (face == null) continue;

            if (face.Value.Size != size)
            {
                Console.Error.WriteLine(
                    $"[Skybox] Face {i} '{facePaths[i]}' is {face.Value.Size}px; the first face is {size}px. Skipped.");
                continue;
            }

            cube.SetData(FaceOrder[i], face.Value.Pixels);
        }

        CubemapTexture?.Dispose();
        CubemapTexture = cube;
    }

    /// <summary>One face's pixels and its edge length, or null when it could not be read.</summary>
    /// <remarks>
    /// The asset manager first, so a cubemap inside an export bundle is found the way every
    /// other texture is — <c>AssetManager.Load</c> checks mounted bundles before the file
    /// system, and going straight to <see cref="File.OpenRead"/> is why a sky that worked
    /// from a source tree silently fell back to the gradient in a bundled build. The file
    /// system after it, so a tool or a test with no running host still loads one.
    /// <para>
    /// A texture the manager hands over is cached and reference-counted by it, so the
    /// reference is given back with <c>Unload</c> rather than disposed; the pixels have been
    /// copied out by then, and disposing it would leave the cache holding a dead texture.
    /// </para>
    /// </remarks>
    private static (Color[] Pixels, int Size)? ReadFace(string path, GraphicsDevice gd)
    {
        var assets = Assets.AssetManager.Current;

        try
        {
            if (assets != null)
            {
                try { return Square(assets.Load<Texture2D>(path), path); }
                finally { assets.Unload(path); }
            }

            using var texture = Texture2D.FromStream(gd, File.OpenRead(path));
            return Square(texture, path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Skybox] Cannot load face '{path}': {ex.Message}");
            return null;
        }

        static (Color[] Pixels, int Size)? Square(Texture2D texture, string path)
        {
            if (texture.Width != texture.Height)
            {
                Console.Error.WriteLine(
                    $"[Skybox] Face '{path}' is {texture.Width}x{texture.Height}; a cube face must be square. Skipped.");
                return null;
            }

            var pixels = new Color[texture.Width * texture.Height];
            texture.GetData(pixels);
            return (pixels, texture.Width);
        }
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------
    /// <summary>
    /// Renders the skybox. Call before drawing any scene geometry.
    /// The depth buffer is not written so the skybox always appears behind everything.
    /// </summary>
    public void Draw(GraphicsDevice gd, Camera3D camera)
    {
        if (CubemapTexture == null && !_cubemapTried && !string.IsNullOrWhiteSpace(_cubemapPath))
        {
            // Once, not once a frame: a missing folder would otherwise reopen six files and
            // print six lines every frame for the life of the scene.
            _cubemapTried = true;
            LoadCubemap(CubemapFacePaths(_cubemapPath), gd);
        }

        // Save render state
        var prevDepthState   = gd.DepthStencilState;
        var prevRastState    = gd.RasterizerState;
        var prevBlendState   = gd.BlendState;

        // Depth: read but do not write
        gd.DepthStencilState = new DepthStencilState
        {
            DepthBufferEnable     = true,
            DepthBufferWriteEnable = false,
            DepthBufferFunction   = CompareFunction.LessEqual
        };
        gd.RasterizerState = new RasterizerState { CullMode = CullMode.CullClockwiseFace };
        gd.BlendState      = BlendState.Opaque;

        if (CubemapTexture != null)
            DrawCubemap(gd, camera);
        else
            DrawGradient(gd, camera);

        // Restore render state
        gd.DepthStencilState  = prevDepthState;
        gd.RasterizerState    = prevRastState;
        gd.BlendState         = prevBlendState;
    }

    // -------------------------------------------------------------------------
    // Cubemap draw (large box centered on camera, no translation in view)
    // -------------------------------------------------------------------------
    private void DrawCubemap(GraphicsDevice gd, Camera3D camera)
    {
        EnsureCubeBuffers(gd);
        var effect = EnsureCubemapEffect(gd);

        float ar     = gd.Viewport.AspectRatio;
        var   proj   = camera.GetProjectionMatrix(ar);

        // Strip translation from view so the skybox is infinite
        var view = camera.GetViewMatrix();
        view.Translation = Vector3.Zero;

        // Scale sky box very large so it encloses the scene
        var world = Matrix.CreateScale(900f);

        effect.World      = world;
        effect.View       = view;
        effect.Projection = proj;
        effect.TextureEnabled = true;

        // BasicEffect doesn't support TextureCube natively; use vertex colours
        // as a gradient stand-in and sample the cube using a custom approach.
        // Because MonoGame BasicEffect doesn't expose a TextureCube parameter,
        // we tint each face by sampling the dominant colour direction instead.
        // For a true cubemap the game should supply a custom HLSL effect.
        effect.DiffuseColor = Vector3.One * MathF.Max(0f, Exposure);
        effect.LightingEnabled = false;

        gd.SetVertexBuffer(_cubeVB!);
        gd.Indices = _cubeIB!;

        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, 12);
        }
    }

    // -------------------------------------------------------------------------
    // Gradient fallback (full-screen quad in clip space)
    // -------------------------------------------------------------------------
    private void DrawGradient(GraphicsDevice gd, Camera3D camera)
    {
        EnsureGradientResources(gd);
        var effect = EnsureGradientEffect(gd);

        // Full-screen quad: no world/view/projection needed (already in NDC)
        effect.World      = Matrix.Identity;
        effect.View       = Matrix.Identity;
        effect.Projection = Matrix.Identity;
        effect.VertexColorEnabled = true;
        effect.LightingEnabled    = false;
        effect.TextureEnabled     = false;

        // BasicEffect multiplies the diffuse colour into each vertex colour, so this scales
        // the whole gradient without rebuilding the quad.
        effect.DiffuseColor = Vector3.One * MathF.Max(0f, Exposure);

        gd.SetVertexBuffer(_quadVB!);

        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
        }
    }

    // -------------------------------------------------------------------------
    // Resource helpers
    // -------------------------------------------------------------------------
    private static readonly VertexPositionNormalTexture[] SkyboxCubeVerts = BuildSkyboxVerts();
    private static readonly short[] SkyboxCubeIndices = BuildSkyboxIndices();

    private static VertexPositionNormalTexture[] BuildSkyboxVerts()
    {
        // 8 unique corners of a unit cube
        var corners = new[]
        {
            new Vector3(-1,-1,-1), new Vector3( 1,-1,-1),
            new Vector3( 1, 1,-1), new Vector3(-1, 1,-1),
            new Vector3(-1,-1, 1), new Vector3( 1,-1, 1),
            new Vector3( 1, 1, 1), new Vector3(-1, 1, 1)
        };
        var verts = new VertexPositionNormalTexture[8];
        for (int i = 0; i < 8; i++)
            verts[i] = new VertexPositionNormalTexture(corners[i], Vector3.Zero, Vector2.Zero);
        return verts;
    }

    private static short[] BuildSkyboxIndices()
    {
        // Inward-facing cube (CW winding from inside)
        return new short[]
        {
            0,2,1, 0,3,2, // -Z
            4,5,6, 4,6,7, // +Z
            3,7,6, 3,6,2, // +Y
            0,1,5, 0,5,4, // -Y
            0,4,7, 0,7,3, // -X
            1,2,6, 1,6,5  // +X
        };
    }

    private void EnsureCubeBuffers(GraphicsDevice gd)
    {
        if (_cubeVB != null) return;

        _cubeVB = new VertexBuffer(gd, VertexPositionNormalTexture.VertexDeclaration,
            SkyboxCubeVerts.Length, BufferUsage.WriteOnly);
        _cubeVB.SetData(SkyboxCubeVerts);

        _cubeIB = new IndexBuffer(gd, IndexElementSize.SixteenBits,
            SkyboxCubeIndices.Length, BufferUsage.WriteOnly);
        _cubeIB.SetData(SkyboxCubeIndices);
    }

    private BasicEffect EnsureCubemapEffect(GraphicsDevice gd)
    {
        if (_cubemapEffect == null)
        {
            _cubemapEffect = new BasicEffect(gd)
            {
                LightingEnabled = false,
                TextureEnabled  = false,
                VertexColorEnabled = false
            };
        }
        return _cubemapEffect;
    }

    /// <summary>
    /// Builds the full-screen gradient quad, and rebuilds it when either colour changes.
    /// </summary>
    /// <remarks>
    /// The colours live in the vertices, so a quad built once and kept for the life of the
    /// component ignores every later edit to <see cref="GradientTop"/> and
    /// <see cref="GradientBottom"/> — and the editor edits both live, while the browser
    /// recomputes its gradient on every draw. The two engines disagreeing on a live edit is
    /// exactly the class of defect the mirror map exists to catch, so the guard compares the
    /// colours the buffer was built from rather than merely checking that a buffer exists.
    /// <see cref="Exposure"/> sidesteps this by riding on the effect's diffuse colour.
    /// </remarks>
    private void EnsureGradientResources(GraphicsDevice gd)
    {
        if (_quadVB != null && _quadTop == GradientTop && _quadBottom == GradientBottom) return;

        _quadVB?.Dispose();
        _quadTop    = GradientTop;
        _quadBottom = GradientBottom;

        // Full-screen triangle strip with per-vertex colours
        var verts = new VertexPositionColor[]
        {
            new(new Vector3(-1,  1, 0), _quadTop),
            new(new Vector3(-1, -1, 0), _quadBottom),
            new(new Vector3( 1,  1, 0), _quadTop),
            new(new Vector3( 1, -1, 0), _quadBottom)
        };

        _quadVB = new VertexBuffer(gd, VertexPositionColor.VertexDeclaration,
            verts.Length, BufferUsage.WriteOnly);
        _quadVB.SetData(verts);
    }

    private BasicEffect EnsureGradientEffect(GraphicsDevice gd)
    {
        if (_gradientEffect == null)
        {
            _gradientEffect = new BasicEffect(gd)
            {
                LightingEnabled    = false,
                TextureEnabled     = false,
                VertexColorEnabled = true
            };
        }
        return _gradientEffect;
    }

    // -------------------------------------------------------------------------
    // Cleanup
    // -------------------------------------------------------------------------
    public override void OnDestroy()
    {
        CubemapTexture?.Dispose();
        _cubeVB?.Dispose();
        _cubeIB?.Dispose();
        _cubemapEffect?.Dispose();
        _gradientEffect?.Dispose();
        _quadVB?.Dispose();
    }
}
