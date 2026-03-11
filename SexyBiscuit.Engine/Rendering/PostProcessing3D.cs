using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// A single post-process pass: an Effect (custom HLSL) and an enabled flag.
/// </summary>
public class PostProcessPass3D
{
    public Effect? Shader  { get; set; }
    public bool    Enabled { get; set; } = true;

    /// <summary>Human-readable name for debugging.</summary>
    public string  Name    { get; set; } = "Pass";
}

/// <summary>
/// Component that manages a chain of fullscreen post-process passes.
/// Place on the same actor as a Camera3D.
/// Call <see cref="Process"/> after the scene has been rendered to a RenderTarget2D.
/// </summary>
public sealed class PostProcessing3D : Component
{
    public List<PostProcessPass3D> Passes { get; } = new();

    // Ping-pong render targets (created / resized on demand)
    private RenderTarget2D? _rtA;
    private RenderTarget2D? _rtB;
    private SpriteBatch?    _sb;

    // -------------------------------------------------------------------------
    // Static pass factories
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a Bloom pass.
    /// Relies on a "Bloom" named effect with Threshold and Intensity parameters.
    /// Returns a software-defined PostProcessPass3D — caller must supply the shader
    /// via <see cref="PostProcessPass3D.Shader"/> if they have a compiled effect.
    /// The factory sets a sentinel name and stores parameters on a wrapped BasicEffect
    /// so they travel with the pass even before a real shader is attached.
    /// </summary>
    public static PostProcessPass3D Bloom(float threshold, float intensity)
    {
        var pass = new PostProcessPass3D { Name = "Bloom", Enabled = true };
        // Parameters will be pushed to pass.Shader when it is set by the game code.
        // Store them in a simple wrapper so the pipeline knows what to do.
        pass = new BloomPass(threshold, intensity);
        return pass;
    }

    public static PostProcessPass3D Vignette(float radius, float softness)
    {
        return new VignettePass(radius, softness);
    }

    public static PostProcessPass3D ColourGrade(float brightness, float contrast, float saturation)
    {
        return new ColourGradePass(brightness, contrast, saturation);
    }

    // -------------------------------------------------------------------------
    // Processing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs all enabled passes over <paramref name="source"/> using ping-pong render targets.
    /// Returns the final <see cref="RenderTarget2D"/> containing the composited image.
    /// If no passes are enabled, returns <paramref name="source"/> unchanged.
    /// </summary>
    public RenderTarget2D Process(GraphicsDevice gd, RenderTarget2D source)
    {
        // Collect active passes
        var active = Passes.Where(p => p.Enabled && p.Shader != null).ToList();
        if (active.Count == 0) return source;

        EnsureTargets(gd, source.Width, source.Height);
        EnsureSpriteBatch(gd);

        RenderTarget2D current = source;
        bool toggle = false;

        foreach (var pass in active)
        {
            var dest = toggle ? _rtA! : _rtB!;

            // Skip if dest == source (first pass: source is the game RT, not one of ours)
            gd.SetRenderTarget(dest);
            gd.Clear(Color.Transparent);

            // Push built-in parameters if the pass has them
            PushBuiltInParameters(pass, gd, source.Width, source.Height);

            _sb!.Begin(SpriteSortMode.Immediate, BlendState.Opaque,
                SamplerState.LinearClamp, DepthStencilState.None,
                RasterizerState.CullNone, pass.Shader);

            _sb.Draw(current,
                new Rectangle(0, 0, source.Width, source.Height),
                Color.White);

            _sb.End();

            current = dest;
            toggle  = !toggle;
        }

        gd.SetRenderTarget(null);
        return current;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private void EnsureTargets(GraphicsDevice gd, int w, int h)
    {
        if (_rtA != null && _rtA.Width == w && _rtA.Height == h) return;

        _rtA?.Dispose();
        _rtB?.Dispose();

        _rtA = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        _rtB = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
    }

    private void EnsureSpriteBatch(GraphicsDevice gd)
    {
        _sb ??= new SpriteBatch(gd);
    }

    private static void PushBuiltInParameters(PostProcessPass3D pass, GraphicsDevice gd, int w, int h)
    {
        var effect = pass.Shader!;

        // Push resolution to any effect that wants it
        TrySet(effect, "Resolution",  new Vector2(w, h));
        TrySet(effect, "TexelSize",   new Vector2(1f / w, 1f / h));

        switch (pass)
        {
            case BloomPass bloom:
                TrySet(effect, "Threshold", bloom.Threshold);
                TrySet(effect, "Intensity",  bloom.Intensity);
                break;

            case VignettePass vig:
                TrySet(effect, "VignetteRadius",   vig.Radius);
                TrySet(effect, "VignetteSoftness",  vig.Softness);
                break;

            case ColourGradePass cg:
                TrySet(effect, "Brightness",  cg.Brightness);
                TrySet(effect, "Contrast",    cg.Contrast);
                TrySet(effect, "Saturation",  cg.Saturation);
                break;
        }
    }

    private static void TrySet(Effect e, string name, float v)
    {
        var p = e.Parameters[name]; if (p != null) p.SetValue(v);
    }

    private static void TrySet(Effect e, string name, Vector2 v)
    {
        var p = e.Parameters[name]; if (p != null) p.SetValue(v);
    }

    // -------------------------------------------------------------------------
    // Cleanup
    // -------------------------------------------------------------------------
    public override void OnDestroy()
    {
        _rtA?.Dispose();
        _rtB?.Dispose();
        _sb?.Dispose();
        _rtA = null;
        _rtB = null;
        _sb  = null;
    }

    // -------------------------------------------------------------------------
    // Built-in pass subtypes (carry their parameters)
    // -------------------------------------------------------------------------
    private sealed class BloomPass : PostProcessPass3D
    {
        public float Threshold { get; }
        public float Intensity { get; }
        public BloomPass(float threshold, float intensity)
        {
            Threshold = threshold;
            Intensity  = intensity;
            Name       = "Bloom";
        }
    }

    private sealed class VignettePass : PostProcessPass3D
    {
        public float Radius   { get; }
        public float Softness { get; }
        public VignettePass(float radius, float softness)
        {
            Radius   = radius;
            Softness = softness;
            Name     = "Vignette";
        }
    }

    private sealed class ColourGradePass : PostProcessPass3D
    {
        public float Brightness  { get; }
        public float Contrast    { get; }
        public float Saturation  { get; }
        public ColourGradePass(float brightness, float contrast, float saturation)
        {
            Brightness = brightness;
            Contrast   = contrast;
            Saturation = saturation;
            Name       = "ColourGrade";
        }
    }
}
