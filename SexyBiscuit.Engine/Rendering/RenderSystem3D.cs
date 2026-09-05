using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// Per-frame counters produced by <see cref="RenderSystem3D"/>. Read them from a debug
/// overlay to see what the renderer actually submitted.
/// </summary>
public struct RenderStats
{
    /// <summary>Mesh renderers considered this frame.</summary>
    public int RenderersTotal;

    /// <summary>Mesh renderers rejected by the frustum test.</summary>
    public int RenderersCulled;

    /// <summary>Mesh renderers submitted to the GPU.</summary>
    public int RenderersDrawn;

    /// <summary>Individual indexed draw calls issued, including the shadow pass.</summary>
    public int DrawCalls;

    /// <summary>Triangles submitted, including the shadow pass.</summary>
    public int Triangles;

    /// <summary>Lights considered for shading this frame.</summary>
    public int LightsActive;

    /// <summary>Shadow-casting lights that produced a depth map.</summary>
    public int ShadowCasters;

    internal void Reset() => this = default;
}

/// <summary>
/// The 3D forward renderer: culls, sorts, lights and draws every <see cref="MeshRenderer"/>
/// in the scene, then runs the post-process chain. Called once per frame by
/// <see cref="SBEngine"/> before the 2D sprite pass.
/// </summary>
/// <remarks>
/// <para>
/// The renderer draws through one of two paths per material. When
/// <see cref="Material3D.Shader"/> is null it uses MonoGame's built-in
/// <see cref="BasicEffect"/>, which supports three directional lights and no shadows — enough
/// to see your scene without shipping a content pipeline. When a material supplies a compiled
/// <see cref="Effect"/>, the renderer binds the full lighting and material parameter set
/// described below and the shader decides what to do with it.
/// </para>
/// <para><b>Shader parameter contract.</b> Every parameter is optional; the renderer only
/// sets those your effect declares, so a minimal shader can take just
/// <c>WorldViewProjection</c>.</para>
/// <list type="table">
///   <listheader><term>Parameter</term><description>Meaning</description></listheader>
///   <item><term><c>World</c>, <c>View</c>, <c>Projection</c></term><description>float4x4 transforms</description></item>
///   <item><term><c>WorldViewProjection</c></term><description>float4x4, the three combined</description></item>
///   <item><term><c>WorldInverseTranspose</c></term><description>float4x4 for correct normals under non-uniform scale</description></item>
///   <item><term><c>CameraPosition</c></term><description>float3 world-space eye position</description></item>
///   <item><term><c>AmbientColor</c></term><description>float3 linear ambient contribution</description></item>
///   <item><term><c>LightCount</c></term><description>int, number of valid entries in the light arrays</description></item>
///   <item><term><c>LightDirections</c></term><description>float3[N] world-space direction, for directional and spot lights</description></item>
///   <item><term><c>LightPositions</c></term><description>float3[N] world-space position, for point and spot lights</description></item>
///   <item><term><c>LightColors</c></term><description>float3[N] colour premultiplied by intensity</description></item>
///   <item><term><c>LightParams</c></term><description>float4[N] as (type, range, cosSpotAngle, unused); type 0 = directional, 1 = point, 2 = spot</description></item>
///   <item><term><c>ShadowMap</c></term><description>texture2D depth map for the primary shadow-casting light</description></item>
///   <item><term><c>LightViewProjection</c></term><description>float4x4 that transforms world space into shadow-map space</description></item>
/// </list>
/// <para>
/// Material properties (<c>AlbedoMap</c>, <c>Metallic</c>, and the rest) are pushed by
/// <see cref="Material3D.Apply"/> before these.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // The engine builds and drives one of these for you:
/// SBEngine.Instance.Renderer3D.AmbientLight = new Color(40, 44, 52);
/// SBEngine.Instance.Renderer3D.EnableShadows = true;
/// SBEngine.Instance.Renderer3D.ShadowDepthEffect = content.Load&lt;Effect&gt;("ShadowDepth");
/// </code>
/// </example>
public sealed class RenderSystem3D : IDisposable
{
    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------

    /// <summary>Skips the whole 3D pass when false.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Render through this camera instead of <see cref="Camera3D.Main"/>.</summary>
    public Camera3D? OverrideCamera { get; set; }

    /// <summary>Ambient light added to every surface regardless of the light list.</summary>
    public Color AmbientLight { get; set; } = new(38, 38, 44);

    /// <summary>Rejects renderers outside the camera frustum before submitting them.</summary>
    public bool EnableFrustumCulling { get; set; } = true;

    /// <summary>
    /// Lights bound per draw. The renderer picks the most influential ones for each object.
    /// The <see cref="BasicEffect"/> path caps this at 3 regardless of the value here.
    /// </summary>
    public int MaxLightsPerObject { get; set; } = 4;

    /// <summary>Sorts opaque geometry front-to-back so the depth test rejects hidden pixels early.</summary>
    public bool SortOpaqueFrontToBack { get; set; } = true;

    /// <summary>Renders the scene's <see cref="Skybox"/> before opaque geometry.</summary>
    public bool RenderSkybox { get; set; } = true;

    /// <summary>
    /// Enables the shadow depth pass. Requires <see cref="ShadowDepthEffect"/>; without one
    /// the pass is skipped and a warning is logged once.
    /// </summary>
    public bool EnableShadows { get; set; }

    /// <summary>
    /// Compiled depth-only effect used for the shadow pass. It must accept a
    /// <c>WorldViewProjection</c> float4x4 and write linear depth to the red channel.
    /// </summary>
    public Effect? ShadowDepthEffect { get; set; }

    /// <summary>Half-width of the orthographic shadow volume around the camera, in world units.</summary>
    public float ShadowDistance { get; set; } = 50f;

    /// <summary>Post-process passes run over the composited 3D image, in order.</summary>
    public List<PostProcessPass3D> PostProcess { get; } = new();

    /// <summary>Counters for the frame that was last rendered.</summary>
    public RenderStats Stats => _stats;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    private GraphicsDevice  _gd = null!;
    private RenderStats     _stats;
    private bool            _warnedNoShadowEffect;

    private RenderTarget2D? _shadowMap;
    private Matrix          _lightViewProjection = Matrix.Identity;

    private RenderTarget2D? _sceneTarget;
    private RenderTarget2D? _ppPing;
    private SpriteBatch?    _compositeBatch;

    private readonly List<MeshRenderer> _opaque      = new();
    private readonly List<MeshRenderer> _transparent = new();
    private readonly List<Light3D>      _frameLights = new();

    // Reused per-draw scratch so a frame allocates nothing.
    private readonly Vector3[] _lightDirections = new Vector3[8];
    private readonly Vector3[] _lightPositions  = new Vector3[8];
    private readonly Vector3[] _lightColors     = new Vector3[8];
    private readonly Vector4[] _lightParams     = new Vector4[8];

    /// <summary>Binds the renderer to a device. Called once by <see cref="SBEngine"/>.</summary>
    public void Initialize(GraphicsDevice gd)
    {
        _gd = gd ?? throw new ArgumentNullException(nameof(gd));
        _compositeBatch = new SpriteBatch(gd);
    }

    // -------------------------------------------------------------------------
    // Frame
    // -------------------------------------------------------------------------

    /// <summary>
    /// Renders every visible mesh in <paramref name="scene"/> through the active camera.
    /// Does nothing when the renderer is disabled or no <see cref="Camera3D"/> exists.
    /// </summary>
    public void Render(Core.Scene scene)
    {
        _stats.Reset();
        if (!Enabled || _gd == null) return;

        var camera = OverrideCamera ?? Camera3D.Main;
        if (camera == null) return;

        float aspect = _gd.Viewport.AspectRatio;
        var   view   = camera.GetViewMatrix();
        var   proj   = camera.GetProjectionMatrix(aspect);
        var   camPos = camera.GetTransform3D().Position;

        GatherLights();
        GatherVisible(camera, view, proj, camPos);

        bool usePostProcess = PostProcess.Any(p => p.Enabled && p.Shader != null);
        var  previousTargets = usePostProcess ? _gd.GetRenderTargets() : Array.Empty<RenderTargetBinding>();

        if (EnableShadows) RenderShadowPass(camPos);

        if (usePostProcess)
        {
            EnsureSceneTarget();
            _gd.SetRenderTarget(_sceneTarget);
            _gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Transparent, 1f, 0);
        }

        DrawScene(scene, camera, view, proj, camPos);

        if (usePostProcess)
        {
            var result = RunPostProcess(_sceneTarget!);
            _gd.SetRenderTargets(previousTargets.Length > 0 ? previousTargets : null);
            Blit(result);
        }
    }

    private void DrawScene(Core.Scene scene, Camera3D camera, Matrix view, Matrix proj, Vector3 camPos)
    {
        // Skybox draws first with depth writes off so everything else overwrites it.
        if (RenderSkybox)
        {
            var sky = FindSkybox(scene);
            if (sky != null)
            {
                _gd.DepthStencilState = DepthStencilState.DepthRead;
                _gd.RasterizerState   = RasterizerState.CullNone;
                sky.Draw(_gd, camera);
            }
        }

        _gd.DepthStencilState = DepthStencilState.Default;
        _gd.RasterizerState   = RasterizerState.CullCounterClockwise;
        _gd.BlendState        = BlendState.Opaque;

        foreach (var r in _opaque) DrawRenderer(r, view, proj, camPos);

        // Skinned meshes go through SkinnedEffect and are not part of the material path.
        foreach (var skin in SkinnedMeshRenderer.All)
        {
            if (!skin.Enabled || !skin.Actor.IsActive) continue;
            skin.Draw(_gd, view, proj);
            _stats.RenderersDrawn++;
            _stats.DrawCalls += skin.SubMeshes.Count;
        }

        // Transparent geometry: read depth but do not write it, so overlapping
        // surfaces blend instead of occluding each other in submission order.
        _gd.DepthStencilState = DepthStencilState.DepthRead;
        _gd.BlendState        = BlendState.AlphaBlend;

        foreach (var r in _transparent) DrawRenderer(r, view, proj, camPos);

        // Particles are always transparent and draw last, over everything else.
        foreach (var emitter in ParticleSystem3D.All)
        {
            if (!emitter.Enabled || !emitter.Actor.IsActive || emitter.AliveCount == 0) continue;
            emitter.Draw(_gd, camera, view, proj);
            _stats.DrawCalls++;
        }

        _gd.DepthStencilState = DepthStencilState.Default;
        _gd.BlendState        = BlendState.Opaque;
    }

    // -------------------------------------------------------------------------
    // Gathering
    // -------------------------------------------------------------------------

    private void GatherLights()
    {
        _frameLights.Clear();
        foreach (var l in Light3D.All)
        {
            if (!l.Enabled || !l.Actor.IsActive) continue;
            if (l.Intensity <= 0f) continue;
            _frameLights.Add(l);
        }
        _stats.LightsActive = _frameLights.Count;
    }

    private void GatherVisible(Camera3D camera, Matrix view, Matrix proj, Vector3 camPos)
    {
        _opaque.Clear();
        _transparent.Clear();

        var frustum = new BoundingFrustum(view * proj);

        foreach (var r in MeshRenderer.All)
        {
            _stats.RenderersTotal++;

            if (!r.Enabled || !r.Actor.IsActive) continue;

            // LOD groups pick which of their renderers is enabled before the cull test.
            if (EnableFrustumCulling && !r.IgnoreCulling)
            {
                if (frustum.Contains(r.WorldBounds.ToBoundingBox()) == ContainmentType.Disjoint)
                {
                    _stats.RenderersCulled++;
                    continue;
                }
            }

            (r.IsTransparent ? _transparent : _opaque).Add(r);
        }

        // Update LOD selection for everything that survived the cull.
        foreach (var lod in LODGroup.All)
        {
            if (lod.Enabled && lod.Actor.IsActive) lod.Update(camera);
        }

        if (SortOpaqueFrontToBack)
            _opaque.Sort((a, b) => DistanceSq(a, camPos).CompareTo(DistanceSq(b, camPos)));

        // Back-to-front is required for correct alpha blending.
        _transparent.Sort((a, b) => DistanceSq(b, camPos).CompareTo(DistanceSq(a, camPos)));

        _stats.RenderersDrawn = _opaque.Count + _transparent.Count;
    }

    private static float DistanceSq(MeshRenderer r, Vector3 from)
        => Vector3.DistanceSquared(r.GetTransform3D().Position, from);

    private static Skybox? FindSkybox(Core.Scene scene)
    {
        foreach (var layer in scene.Layers)
            foreach (var actor in layer.Actors)
            {
                if (!actor.IsActive) continue;
                var sky = actor.GetComponent<Skybox>();
                if (sky is { Enabled: true }) return sky;
            }
        return null;
    }

    // -------------------------------------------------------------------------
    // Drawing one renderer
    // -------------------------------------------------------------------------

    private void DrawRenderer(MeshRenderer renderer, Matrix view, Matrix proj, Vector3 camPos)
    {
        var world = renderer.GetTransform3D().GetWorldMatrix();
        int lightCount = SelectLights(renderer.GetTransform3D().Position);

        var subMeshes = renderer.SubMeshes;

        if (subMeshes.Count == 0)
        {
            // Fallback cube path — MeshRenderer owns the geometry and its own BasicEffect.
            renderer.Draw(_gd, view, proj);
            _stats.DrawCalls++;
            _stats.Triangles += 12;
            return;
        }

        foreach (var sub in subMeshes)
        {
            var material = sub.MaterialIndex < renderer.Materials.Count
                ? renderer.Materials[sub.MaterialIndex]
                : Material3D.Default;

            _gd.SetVertexBuffer(sub.VertexBuffer);
            _gd.Indices = sub.IndexBuffer;

            if (material.Shader != null)
                DrawWithCustomShader(material, sub, world, view, proj, camPos, lightCount);
            else
                DrawWithBasicEffect(renderer, material, sub, world, view, proj, lightCount);

            _stats.DrawCalls++;
            _stats.Triangles += sub.PrimitiveCount;
        }
    }

    private void DrawWithCustomShader(Material3D material, MeshRenderer.SubMesh sub,
        Matrix world, Matrix view, Matrix proj, Vector3 camPos, int lightCount)
    {
        var fx = material.Shader!;
        material.Apply(fx);

        SetIfPresent(fx, "World",      world);
        SetIfPresent(fx, "View",       view);
        SetIfPresent(fx, "Projection", proj);
        SetIfPresent(fx, "WorldViewProjection", world * view * proj);
        SetIfPresent(fx, "WorldInverseTranspose", Matrix.Transpose(Matrix.Invert(world)));

        fx.Parameters["CameraPosition"]?.SetValue(camPos);
        fx.Parameters["AmbientColor"]?.SetValue(AmbientLight.ToVector3());
        fx.Parameters["LightCount"]?.SetValue(lightCount);

        if (lightCount > 0)
        {
            fx.Parameters["LightDirections"]?.SetValue(_lightDirections);
            fx.Parameters["LightPositions"]?.SetValue(_lightPositions);
            fx.Parameters["LightColors"]?.SetValue(_lightColors);
            fx.Parameters["LightParams"]?.SetValue(_lightParams);
        }

        if (_shadowMap != null)
        {
            fx.Parameters["ShadowMap"]?.SetValue(_shadowMap);
            fx.Parameters["LightViewProjection"]?.SetValue(_lightViewProjection);
        }

        foreach (var pass in fx.CurrentTechnique.Passes)
        {
            pass.Apply();
            _gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, sub.PrimitiveCount);
        }
    }

    private readonly Dictionary<MeshRenderer, BasicEffect> _basicEffects = new();

    private void DrawWithBasicEffect(MeshRenderer renderer, Material3D material,
        MeshRenderer.SubMesh sub, Matrix world, Matrix view, Matrix proj, int lightCount)
    {
        if (!_basicEffects.TryGetValue(renderer, out var fx))
        {
            fx = new BasicEffect(_gd) { PreferPerPixelLighting = true };
            _basicEffects[renderer] = fx;
        }

        fx.World      = world;
        fx.View       = view;
        fx.Projection = proj;

        if (material.AlbedoMap != null)
        {
            fx.TextureEnabled = true;
            fx.Texture        = material.AlbedoMap;
            fx.DiffuseColor   = Vector3.One;
        }
        else
        {
            fx.TextureEnabled = false;
            fx.DiffuseColor   = material.AlbedoColor.ToVector3();
        }

        fx.Alpha           = material.AlbedoColor.A / 255f;
        fx.EmissiveColor   = material.EmissiveIntensity > 0f
            ? material.AlbedoColor.ToVector3() * material.EmissiveIntensity
            : Vector3.Zero;

        // BasicEffect has no roughness term; map it onto specular sharpness so the
        // material's roughness at least reads as duller or shinier.
        fx.SpecularPower = MathHelper.Lerp(64f, 2f, MathHelper.Clamp(material.Roughness, 0f, 1f));
        fx.SpecularColor = Vector3.One * (1f - MathHelper.Clamp(material.Roughness, 0f, 1f));

        fx.AmbientLightColor = AmbientLight.ToVector3();
        fx.LightingEnabled   = true;

        // BasicEffect exposes exactly three directional lights.
        ApplyBasicLight(fx.DirectionalLight0, 0, lightCount);
        ApplyBasicLight(fx.DirectionalLight1, 1, lightCount);
        ApplyBasicLight(fx.DirectionalLight2, 2, lightCount);

        foreach (var pass in fx.CurrentTechnique.Passes)
        {
            pass.Apply();
            _gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, sub.PrimitiveCount);
        }
    }

    private void ApplyBasicLight(DirectionalLight slot, int index, int lightCount)
    {
        if (index >= lightCount)
        {
            slot.Enabled = false;
            return;
        }

        slot.Enabled        = true;
        slot.Direction      = _lightDirections[index];
        slot.DiffuseColor   = _lightColors[index];
        slot.SpecularColor  = _lightColors[index] * 0.5f;
    }

    /// <summary>
    /// Fills the light scratch arrays with the most influential lights for a point in space
    /// and returns how many were written.
    /// </summary>
    /// <remarks>
    /// Influence is intensity for directional lights and intensity scaled by inverse-square
    /// falloff for point and spot lights, so a bright distant lamp can still outrank a dim
    /// nearby one. Point and spot lights are also written as a direction pointing from the
    /// light toward the shaded object, which lets the <see cref="BasicEffect"/> path
    /// approximate them without per-pixel attenuation.
    /// </remarks>
    private int SelectLights(Vector3 worldPosition)
    {
        int capacity = Math.Clamp(MaxLightsPerObject, 0, _lightDirections.Length);
        if (capacity == 0 || _frameLights.Count == 0) return 0;

        Span<float> scores = stackalloc float[8];
        Span<int>   picked = stackalloc int[8];
        int count = 0;

        for (int i = 0; i < _frameLights.Count; i++)
        {
            var light = _frameLights[i];
            float score = ComputeInfluence(light, worldPosition);
            if (score <= 0f) continue;

            if (count < capacity)
            {
                picked[count] = i;
                scores[count] = score;
                count++;
            }
            else
            {
                // Replace the weakest entry when this light beats it.
                int weakest = 0;
                for (int k = 1; k < count; k++) if (scores[k] < scores[weakest]) weakest = k;
                if (score > scores[weakest]) { picked[weakest] = i; scores[weakest] = score; }
            }
        }

        // Strongest first so the three BasicEffect slots get the lights that matter most.
        for (int a = 0; a < count - 1; a++)
            for (int b = a + 1; b < count; b++)
                if (scores[b] > scores[a])
                {
                    (scores[a], scores[b]) = (scores[b], scores[a]);
                    (picked[a], picked[b]) = (picked[b], picked[a]);
                }

        for (int i = 0; i < count; i++)
        {
            var light = _frameLights[picked[i]];
            var lightPos = light.Actor.GetComponent<Transform3D>()?.Position ?? Vector3.Zero;

            Vector3 direction = light.Type == LightType.Directional
                ? SBMath.SafeNormalize(light.GetDirection())
                : SBMath.SafeNormalize(worldPosition - lightPos);

            _lightDirections[i] = direction;
            _lightPositions[i]  = lightPos;
            _lightColors[i]     = light.Color.ToVector3() * light.Intensity * Attenuation(light, worldPosition);
            _lightParams[i]     = new Vector4(
                (float)light.Type,
                light.Range,
                MathF.Cos(MathHelper.ToRadians(light.SpotAngle)),
                0f);
        }

        return count;
    }

    private static float ComputeInfluence(Light3D light, Vector3 worldPosition)
        => light.Type == LightType.Directional
            ? light.Intensity
            : light.Intensity * Attenuation(light, worldPosition);

    private static float Attenuation(Light3D light, Vector3 worldPosition)
    {
        if (light.Type == LightType.Directional) return 1f;

        var lightPos = light.Actor.GetComponent<Transform3D>()?.Position ?? Vector3.Zero;
        float dist   = Vector3.Distance(lightPos, worldPosition);
        if (light.Range <= 0f || dist >= light.Range) return 0f;

        // Inverse-square with a smooth cutoff at Range so lights do not pop out abruptly.
        float normalized = dist / light.Range;
        float falloff    = 1f - normalized * normalized;
        return MathHelper.Clamp(falloff * falloff, 0f, 1f);
    }

    // -------------------------------------------------------------------------
    // Shadow pass
    // -------------------------------------------------------------------------

    private void RenderShadowPass(Vector3 focusPoint)
    {
        var caster = _frameLights.FirstOrDefault(l => l.CastsShadows && l.Type == LightType.Directional);
        if (caster == null) return;

        if (ShadowDepthEffect == null)
        {
            if (!_warnedNoShadowEffect)
            {
                Console.Error.WriteLine(
                    "[RenderSystem3D] EnableShadows is on but ShadowDepthEffect is null. " +
                    "Assign a compiled depth-only Effect (see the RenderSystem3D docs) or turn shadows off.");
                _warnedNoShadowEffect = true;
            }
            return;
        }

        int size = Math.Max(256, caster.ShadowMapSize);
        if (_shadowMap == null || _shadowMap.Width != size)
        {
            _shadowMap?.Dispose();
            _shadowMap = new RenderTarget2D(_gd, size, size, false,
                SurfaceFormat.Single, DepthFormat.Depth24);
        }

        // Fit an orthographic volume around the camera, looking down the light direction.
        var dir      = SBMath.SafeNormalize(caster.GetDirection());
        if (dir == Vector3.Zero) dir = Vector3.Down;

        var eye      = focusPoint - dir * ShadowDistance;
        var up       = MathF.Abs(Vector3.Dot(dir, Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
        var lightView = Matrix.CreateLookAt(eye, focusPoint, up);
        var lightProj = Matrix.CreateOrthographic(
            ShadowDistance * 2f, ShadowDistance * 2f, 0.1f, ShadowDistance * 4f);

        _lightViewProjection = lightView * lightProj;

        var previousTargets = _gd.GetRenderTargets();
        _gd.SetRenderTarget(_shadowMap);
        _gd.Clear(Color.White);
        _gd.DepthStencilState = DepthStencilState.Default;
        _gd.RasterizerState   = RasterizerState.CullCounterClockwise;

        var wvp = ShadowDepthEffect.Parameters["WorldViewProjection"];

        foreach (var r in _opaque)
        {
            if (!r.CastShadows || r.SubMeshes.Count == 0) continue;

            var world = r.GetTransform3D().GetWorldMatrix();
            wvp?.SetValue(world * _lightViewProjection);

            foreach (var sub in r.SubMeshes)
            {
                _gd.SetVertexBuffer(sub.VertexBuffer);
                _gd.Indices = sub.IndexBuffer;

                foreach (var pass in ShadowDepthEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, sub.PrimitiveCount);
                }

                _stats.DrawCalls++;
                _stats.Triangles += sub.PrimitiveCount;
            }
        }

        _stats.ShadowCasters = 1;
        _gd.SetRenderTargets(previousTargets.Length > 0 ? previousTargets : null);
    }

    // -------------------------------------------------------------------------
    // Post-processing
    // -------------------------------------------------------------------------

    private void EnsureSceneTarget()
    {
        var pp = _gd.PresentationParameters;
        if (_sceneTarget != null && _sceneTarget.Width == pp.BackBufferWidth
                                 && _sceneTarget.Height == pp.BackBufferHeight) return;

        _sceneTarget?.Dispose();
        _ppPing?.Dispose();

        _sceneTarget = new RenderTarget2D(_gd, pp.BackBufferWidth, pp.BackBufferHeight,
            false, SurfaceFormat.Color, DepthFormat.Depth24);
        _ppPing = new RenderTarget2D(_gd, pp.BackBufferWidth, pp.BackBufferHeight,
            false, SurfaceFormat.Color, DepthFormat.None);
    }

    private RenderTarget2D RunPostProcess(RenderTarget2D source)
    {
        var from = source;
        var to   = _ppPing!;

        foreach (var pass in PostProcess)
        {
            if (!pass.Enabled || pass.Shader == null) continue;

            _gd.SetRenderTarget(to);
            _gd.Clear(Color.Transparent);

            _compositeBatch!.Begin(SpriteSortMode.Immediate, BlendState.Opaque,
                SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, pass.Shader);
            _compositeBatch.Draw(from, Vector2.Zero, Color.White);
            _compositeBatch.End();

            (from, to) = (to, from);
        }

        return from;
    }

    private void Blit(Texture2D texture)
    {
        _compositeBatch!.Begin(SpriteSortMode.Deferred, BlendState.Opaque,
            SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
        _compositeBatch.Draw(texture, Vector2.Zero, Color.White);
        _compositeBatch.End();
    }

    private static void SetIfPresent(Effect fx, string name, Matrix value)
        => fx.Parameters[name]?.SetValue(value);

    // -------------------------------------------------------------------------
    // Teardown
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        foreach (var fx in _basicEffects.Values) fx.Dispose();
        _basicEffects.Clear();

        _shadowMap?.Dispose();
        _sceneTarget?.Dispose();
        _ppPing?.Dispose();
        _compositeBatch?.Dispose();

        _shadowMap = null;
        _sceneTarget = null;
        _ppPing = null;
        _compositeBatch = null;
    }
}
