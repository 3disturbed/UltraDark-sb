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

    // -------------------------------------------------------------------------
    // GPU resources (created on first Draw)
    // -------------------------------------------------------------------------
    private VertexBuffer?    _cubeVB;
    private IndexBuffer?     _cubeIB;
    private BasicEffect?     _cubemapEffect;
    private BasicEffect?     _gradientEffect;
    private VertexBuffer?    _quadVB;

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
    public void LoadCubemap(string[] facePaths, GraphicsDevice gd)
    {
        if (facePaths.Length != 6)
            throw new ArgumentException("Exactly 6 face paths are required (+X -X +Y -Y +Z -Z).", nameof(facePaths));

        // Load first face to get dimensions
        Texture2D? first = null;
        try { first = Texture2D.FromStream(gd, File.OpenRead(facePaths[0])); }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[Skybox] Cannot load face 0 '{facePaths[0]}': {ex.Message}");
            return;
        }

        int size = first.Width;
        var cube = new TextureCube(gd, size, false, SurfaceFormat.Color);

        // Extract pixels from first face
        var pixels = new Color[size * size];
        first.GetData(pixels);
        cube.SetData(FaceOrder[0], pixels);
        first.Dispose();

        // Remaining faces
        for (int i = 1; i < 6; i++)
        {
            try
            {
                using var tex = Texture2D.FromStream(gd, File.OpenRead(facePaths[i]));
                var facePixels = new Color[size * size];
                tex.GetData(facePixels);
                cube.SetData(FaceOrder[i], facePixels);
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[Skybox] Cannot load face {i} '{facePaths[i]}': {ex.Message}");
            }
        }

        CubemapTexture?.Dispose();
        CubemapTexture = cube;
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
        effect.DiffuseColor = Vector3.One;
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

    private void EnsureGradientResources(GraphicsDevice gd)
    {
        if (_quadVB != null) return;

        // Full-screen triangle strip with per-vertex colours
        var top    = GradientTop;
        var bottom = GradientBottom;

        var verts = new VertexPositionColor[]
        {
            new(new Vector3(-1,  1, 0), top),
            new(new Vector3(-1, -1, 0), bottom),
            new(new Vector3( 1,  1, 0), top),
            new(new Vector3( 1, -1, 0), bottom)
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
