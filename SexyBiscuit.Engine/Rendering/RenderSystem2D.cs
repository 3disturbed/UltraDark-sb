using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// Manages the 2D render pipeline for a scene.
/// Wraps SpriteBatch begin/end with camera transform, optional per-layer render targets,
/// and a post-process effect chain applied after the scene is fully drawn.
/// </summary>
public class RenderSystem2D
{
    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------

    /// <summary>
    /// When true each named layer gets its own RenderTarget2D. They are composited
    /// back to the screen (or the final post-process target) in layer order.
    /// When false a single SpriteBatch pass is used for the whole scene.
    /// </summary>
    public bool UsePerLayerRenderTargets { get; set; } = false;

    /// <summary>
    /// SpriteSortMode used for the main scene batch. Default BackToFront lets
    /// SpriteRenderer.LayerDepth control painter ordering.
    /// </summary>
    /// <remarks>
    /// FrontToBack, despite the name, is the mode that matches the browser engine: MonoGame's
    /// BackToFront draws the HIGHEST layerDepth first, and the browser's SpriteBatch sorts
    /// ascending so the highest is drawn LAST. Two engines with opposite conventions means a
    /// project laid out for one draws its ground over its whole world in the other, so this
    /// side is the one that moves — every game and every cookie is written against the
    /// browser's "higher is nearer". Verified with Games/DepthProbe, not reasoned about.
    /// </remarks>
    public SpriteSortMode SortMode { get; set; } = SpriteSortMode.FrontToBack;

    /// <summary>BlendState used for the main scene batch.</summary>
    /// <remarks>
    /// <b>NonPremultiplied</b>, not AlphaBlend. MonoGame's AlphaBlend expects colours
    /// whose RGB has already been multiplied by their alpha; the engine hands it
    /// straight <see cref="Color"/> values from a scene file or a script, so a
    /// translucent sprite ADDED its full colour and merely attenuated what was
    /// behind it. A night overlay tinted (6, 8, 20) at alpha ZERO therefore lifted
    /// every pixel in the game by exactly (6, 8, 20) — visible only as "the palette
    /// is slightly off", which is how it survived.
    ///
    /// The browser engine composites with <c>ctx.globalAlpha</c>, which is straight
    /// alpha, so this is also what makes the two agree: before this, any sprite with
    /// an alpha looked different natively than it did in the prototype.
    /// </remarks>
    public BlendState BlendState { get; set; } = BlendState.NonPremultiplied;

    /// <summary>SamplerState used for the main scene batch. Default is PointClamp for pixel art.</summary>
    public SamplerState SamplerState { get; set; } = SamplerState.PointClamp;

    // -------------------------------------------------------------------------
    // Post-process chain
    // -------------------------------------------------------------------------

    /// <summary>
    /// Ordered list of post-process passes. Each effect is applied in sequence
    /// using a ping-pong RenderTarget2D strategy.
    /// Each entry is an (Effect, optional parameter callback) tuple.
    /// </summary>
    public List<PostProcessPass> PostProcessPasses { get; } = new();

    // -------------------------------------------------------------------------
    // Internal state
    // -------------------------------------------------------------------------

    private GraphicsDevice _gd = null!;

    // Ping-pong targets for post-processing
    private RenderTarget2D? _pingTarget;
    private RenderTarget2D? _pongTarget;

    // Per-layer render targets keyed by layer name
    private readonly Dictionary<string, RenderTarget2D> _layerTargets = new();

    // Tracks whether we are currently inside a Begin/End pair
    private bool _inBatch;

    // -------------------------------------------------------------------------
    // Initialisation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Call once, passing the GraphicsDevice. Required before any Begin/End calls.
    /// </summary>
    public void Initialize(GraphicsDevice gd)
    {
        _gd = gd;
    }

    // -------------------------------------------------------------------------
    // Main scene rendering helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Begins a SpriteBatch pass for the scene.
    /// If <paramref name="camera"/> is non-null its view matrix is applied.
    /// </summary>
    public void Begin(SpriteBatch sb, Camera2D? camera = null)
    {
        if (_inBatch)
            throw new InvalidOperationException("RenderSystem2D.Begin called without a matching End.");

        Matrix? viewMatrix = camera?.GetViewMatrix(_gd);
        SetCullBounds(camera);

        sb.Begin(
            sortMode:       SortMode,
            blendState:     BlendState,
            samplerState:   SamplerState,
            depthStencilState: DepthStencilState.None,
            rasterizerState:   RasterizerState.CullNone,
            effect:         null,
            transformMatrix: viewMatrix);

        _inBatch = true;
    }

    /// <summary>
    /// Begins a SpriteBatch pass with an explicit <see cref="Effect"/> (e.g. for lit sprites).
    /// </summary>
    public void Begin(SpriteBatch sb, Camera2D? camera, Effect? effect)
    {
        if (_inBatch)
            throw new InvalidOperationException("RenderSystem2D.Begin called without a matching End.");

        Matrix? viewMatrix = camera?.GetViewMatrix(_gd);
        SetCullBounds(camera);

        sb.Begin(
            sortMode:        SortMode,
            blendState:      BlendState,
            samplerState:    SamplerState,
            depthStencilState: DepthStencilState.None,
            rasterizerState:   RasterizerState.CullNone,
            effect:          effect,
            transformMatrix: viewMatrix);

        _inBatch = true;
    }

    /// <summary>Ends the current SpriteBatch pass.</summary>
    public void End(SpriteBatch sb)
    {
        if (!_inBatch)
            throw new InvalidOperationException("RenderSystem2D.End called without a matching Begin.");

        sb.End();
        _inBatch = false;

        // Off again the moment the pass is over. A cull rectangle that outlives
        // its camera would silently apply to the next batch — the screen-space
        // HUD, say, whose coordinates are nothing to do with the world.
        CullEnabled = false;
    }

    // -------------------------------------------------------------------------
    // Culling
    //
    // Nothing here changes what a scene looks like: a sprite is skipped only when
    // it provably cannot touch the viewport. What it changes is what a big map
    // costs. Without it every actor in the scene is submitted every frame, sorted
    // by MonoGame and handed to the driver, so a city of ten thousand walls pays
    // for all ten thousand to draw the two hundred you can see.
    // -------------------------------------------------------------------------

    /// <summary>Whether the current pass is culling to a camera's view.</summary>
    public static bool CullEnabled { get; private set; }

    private static float _cullMinX, _cullMinY, _cullMaxX, _cullMaxY;

    private void SetCullBounds(Camera2D? camera)
    {
        // No camera means no idea what is on screen, so draw everything: a pass
        // with an identity transform is usually a tool or a test, and quietly
        // dropping its sprites would be far worse than drawing too many.
        if (camera == null) { CullEnabled = false; return; }

        var view = camera.VisibleWorldBounds(_gd);
        _cullMinX = view.X;
        _cullMinY = view.Y;
        _cullMaxX = view.X + view.Width;
        _cullMaxY = view.Y + view.Height;
        CullEnabled = true;
    }

    /// <summary>
    /// True when something of the given radius at the given point could appear in
    /// the pass currently being drawn.
    /// </summary>
    /// <remarks>
    /// The radius is the caller's own conservative bound — it has to cover the
    /// sprite's pivot, scale and rotation, because this test knows none of them.
    /// </remarks>
    public static bool IsVisible(Vector2 centre, float radius)
    {
        if (!CullEnabled) return true;
        return centre.X + radius >= _cullMinX && centre.X - radius <= _cullMaxX
            && centre.Y + radius >= _cullMinY && centre.Y - radius <= _cullMaxY;
    }

    // -------------------------------------------------------------------------
    // Full scene render with optional per-layer targets
    // -------------------------------------------------------------------------

    /// <summary>
    /// Renders a complete scene through all its layers.
    /// If <see cref="UsePerLayerRenderTargets"/> is true each layer is drawn to its own
    /// RenderTarget2D and composited. Post-process passes are applied at the end.
    /// </summary>
    public void RenderScene(SpriteBatch sb, Core.Scene scene, Camera2D? camera)
    {
        EnsurePingPongTargets();

        if (!UsePerLayerRenderTargets)
        {
            // Single pass — route everything to the first ping-pong target if post-processing
            if (PostProcessPasses.Count > 0)
                _gd.SetRenderTarget(_pingTarget);

            Begin(sb, camera);
            scene.Draw(sb);       // Scene.Draw calls Layer.Draw → Actor.InternalDraw → Component.Draw
            End(sb);

            if (PostProcessPasses.Count > 0)
            {
                _gd.SetRenderTarget(null);
                ApplyPostProcessChain(sb);
            }
        }
        else
        {
            // Per-layer pass
            foreach (var layer in scene.Layers)
            {
                if (!layer.Visible) continue;

                var target = GetOrCreateLayerTarget(layer.Name);
                _gd.SetRenderTarget(target);
                _gd.Clear(Color.Transparent);

                Begin(sb, camera);
                layer.Draw(sb);
                End(sb);
            }

            // Composite all layer targets to ping or back-buffer
            bool hasPost = PostProcessPasses.Count > 0;
            _gd.SetRenderTarget(hasPost ? _pingTarget : null);
            if (hasPost) _gd.Clear(Color.Transparent);

            sb.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend,
                SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);

            foreach (var layer in scene.Layers)
            {
                if (!layer.Visible) continue;
                if (_layerTargets.TryGetValue(layer.Name, out var layerTex))
                    sb.Draw(layerTex, Vector2.Zero, Color.White);
            }
            sb.End();

            if (hasPost)
            {
                _gd.SetRenderTarget(null);
                ApplyPostProcessChain(sb);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Post-processing
    // -------------------------------------------------------------------------

    private void ApplyPostProcessChain(SpriteBatch sb)
    {
        if (_pingTarget == null || _pongTarget == null) return;

        RenderTarget2D src  = _pingTarget;
        RenderTarget2D dest = _pongTarget;

        for (int i = 0; i < PostProcessPasses.Count; i++)
        {
            var pass = PostProcessPasses[i];
            bool isLast = i == PostProcessPasses.Count - 1;

            if (isLast)
                _gd.SetRenderTarget(null);   // final pass goes to back-buffer
            else
            {
                _gd.SetRenderTarget(dest);
                _gd.Clear(Color.Transparent);
            }

            // Let the caller configure effect parameters per-pass
            pass.ConfigureEffect?.Invoke(pass.Effect);

            sb.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend,
                SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone,
                pass.Effect);

            sb.Draw(src, new Rectangle(0, 0, _gd.Viewport.Width, _gd.Viewport.Height), Color.White);
            sb.End();

            // Swap
            (src, dest) = (dest, src);
        }

        // If there were no passes (shouldn't reach here) or even number of swaps, blit src to back buffer
        // This is handled above by routing the last pass directly to null render target.
    }

    // -------------------------------------------------------------------------
    // Render target management
    // -------------------------------------------------------------------------

    private void EnsurePingPongTargets()
    {
        int w = _gd.Viewport.Width;
        int h = _gd.Viewport.Height;

        if (_pingTarget == null || _pingTarget.Width != w || _pingTarget.Height != h ||
            _pingTarget.IsDisposed)
        {
            _pingTarget?.Dispose();
            _pongTarget?.Dispose();
            _pingTarget = new RenderTarget2D(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
            _pongTarget = new RenderTarget2D(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        }
    }

    private RenderTarget2D GetOrCreateLayerTarget(string layerName)
    {
        int w = _gd.Viewport.Width;
        int h = _gd.Viewport.Height;

        if (_layerTargets.TryGetValue(layerName, out var existing))
        {
            if (existing.Width == w && existing.Height == h && !existing.IsDisposed)
                return existing;
            existing.Dispose();
        }

        var target = new RenderTarget2D(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        _layerTargets[layerName] = target;
        return target;
    }

    // -------------------------------------------------------------------------
    // Disposal
    // -------------------------------------------------------------------------

    /// <summary>Release all managed render targets.</summary>
    public void Dispose()
    {
        _pingTarget?.Dispose();
        _pongTarget?.Dispose();
        _pingTarget = null;
        _pongTarget = null;

        foreach (var rt in _layerTargets.Values)
            rt.Dispose();
        _layerTargets.Clear();
    }
}

// =========================================================================
// Post-process pass descriptor
// =========================================================================

/// <summary>
/// Describes a single post-process pass: an Effect and an optional callback
/// invoked immediately before the pass is drawn so callers can set parameters.
/// </summary>
public sealed class PostProcessPass
{
    /// <summary>The MonoGame Effect used for this pass. Must not be null.</summary>
    public Effect Effect { get; }

    /// <summary>
    /// Optional delegate called just before this pass is rendered.
    /// Use it to set effect parameters (e.g. blur radius, colour grade LUT).
    /// </summary>
    public Action<Effect>? ConfigureEffect { get; set; }

    public PostProcessPass(Effect effect, Action<Effect>? configure = null)
    {
        Effect          = effect ?? throw new ArgumentNullException(nameof(effect));
        ConfigureEffect = configure;
    }
}
