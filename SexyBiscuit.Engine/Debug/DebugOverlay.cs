using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Debug;

/// <summary>
/// Real-time HUD overlay displaying FPS, delta time, draw calls, actor count,
/// GC collections, and managed memory. Toggle with F1.
/// </summary>
public static class DebugOverlay
{
    // -------------------------------------------------------------------------
    // Visibility toggle
    // -------------------------------------------------------------------------
    public static bool Visible { get; set; } = false;

    // -------------------------------------------------------------------------
    // FPS rolling average
    // -------------------------------------------------------------------------
    private const int FpsSampleCount = 60;
    private static readonly float[] _fpsSamples = new float[FpsSampleCount];
    private static int _fpsSampleIndex = 0;
    private static float _averageFps = 0f;
    private static float _lastDeltaMs = 0f;

    // -------------------------------------------------------------------------
    // Draw call counter — reset each frame; callers call IncrementDrawCall()
    // -------------------------------------------------------------------------
    private static int _drawCallsThisFrame = 0;
    private static int _drawCallsLastFrame = 0;

    // -------------------------------------------------------------------------
    // F1 key state (edge detection — no dependency on InputManager)
    // -------------------------------------------------------------------------
    private static bool _f1WasDown = false;

    // -------------------------------------------------------------------------
    // Lazily-created rendering resources
    // -------------------------------------------------------------------------
    private static Texture2D? _pixel = null;

    // Panel layout constants
    private const int PanelX       = 8;
    private const int PanelY       = 8;
    private const int PanelWidth   = 280;
    private const int LineHeight   = 18;
    private const int PanelPadding = 6;
    private const int FontSize     = 1; // fallback pixel-art scale

    // -------------------------------------------------------------------------
    // Public API — frame boundary
    // -------------------------------------------------------------------------

    /// <summary>
    /// Call once per frame from the engine Update. dt is in seconds.
    /// </summary>
    public static void Update(float dt)
    {
        // F1 toggle — manual edge detection so we have no InputManager coupling
        var kb = Keyboard.GetState();
        bool f1Down = kb.IsKeyDown(Keys.F1);
        if (f1Down && !_f1WasDown)
            Visible = !Visible;
        _f1WasDown = f1Down;

        if (!Visible) return;

        // Accumulate FPS samples
        _lastDeltaMs = dt * 1000f;
        float fps = dt > 0f ? 1f / dt : 0f;
        _fpsSamples[_fpsSampleIndex] = fps;
        _fpsSampleIndex = (_fpsSampleIndex + 1) % FpsSampleCount;

        float sum = 0f;
        for (int i = 0; i < FpsSampleCount; i++) sum += _fpsSamples[i];
        _averageFps = sum / FpsSampleCount;
    }

    /// <summary>
    /// Call at the start of each Draw pass to reset the draw call counter.
    /// </summary>
    public static void BeginFrame()
    {
        _drawCallsLastFrame = _drawCallsThisFrame;
        _drawCallsThisFrame = 0;
    }

    /// <summary>
    /// Increment from any code path that issues a SpriteBatch.Begin/End pair.
    /// </summary>
    public static void IncrementDrawCall()
    {
        _drawCallsThisFrame++;
    }

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    /// <summary>
    /// Renders the overlay panel. Call after the scene draw but before
    /// SpriteBatch.End on your UI pass.
    /// </summary>
    public static void Draw(SpriteBatch sb)
    {
        if (!Visible) return;

        EnsurePixel(sb.GraphicsDevice);

        // Gather stats
        int actorCount  = CountActiveActors();
        int gen0        = GC.CollectionCount(0);
        int gen1        = GC.CollectionCount(1);
        int gen2        = GC.CollectionCount(2);
        float memoryMb  = GC.GetTotalMemory(false) / (1024f * 1024f);

        string[] lines =
        {
            $"FPS      : {_averageFps:F1}",
            $"Delta    : {_lastDeltaMs:F2} ms",
            $"Draws    : {_drawCallsLastFrame}",
            $"Actors   : {actorCount}",
            $"GC Gen0  : {gen0}",
            $"GC Gen1  : {gen1}",
            $"GC Gen2  : {gen2}",
            $"Memory   : {memoryMb:F2} MB",
        };

        int panelHeight = PanelPadding * 2 + lines.Length * LineHeight;

        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);

        // Semi-transparent black background panel
        DrawRect(sb, PanelX, PanelY, PanelWidth, panelHeight, new Color(0, 0, 0, 180));

        // Text lines using pixel-bar fallback renderer
        for (int i = 0; i < lines.Length; i++)
        {
            int y = PanelY + PanelPadding + i * LineHeight;
            DrawPixelText(sb, lines[i], PanelX + PanelPadding, y, Color.White);
        }

        sb.End();
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static int CountActiveActors()
    {
        var engine = EngineHost.Current;
        if (engine == null) return 0;

        int count = 0;
        var sm = engine.SceneManager;

        count += CountActorsInScene(sm.ActiveScene);
        foreach (var additive in sm.AdditiveScenes)
            count += CountActorsInScene(additive);

        return count;
    }

    private static int CountActorsInScene(Core.Scene? scene)
    {
        if (scene == null) return 0;
        int c = 0;
        foreach (var layer in scene.Layers)
            c += layer.Actors.Count(a => a.IsActive);
        return c;
    }

    private static void EnsurePixel(GraphicsDevice gd)
    {
        if (_pixel != null) return;
        _pixel = new Texture2D(gd, 1, 1);
        _pixel.SetData(new[] { Color.White });
    }

    private static void DrawRect(SpriteBatch sb, int x, int y, int w, int h, Color color)
    {
        sb.Draw(_pixel!, new Rectangle(x, y, w, h), color);
    }

    /// <summary>
    /// Draws one line of the overlay.
    /// </summary>
    /// <remarks>
    /// Through the shared 5x7 glyph table, which is the engine's one font: the browser
    /// imports the same file and the C# side embeds it, so debug text and game text are
    /// drawn by the same code. This used to be a hand-rolled 3x5 table that only this
    /// file knew about, written when there was no font to reach for.
    /// </remarks>
    private static void DrawPixelText(SpriteBatch sb, string text, int x, int y, Color color)
        => UI.BitmapFont.Draw(sb, text, new Vector2(x, y), color);
}
