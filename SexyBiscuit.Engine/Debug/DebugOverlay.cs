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
    /// Minimal 3×5 pixel-font renderer using the 1×1 white pixel texture.
    /// Each character is approximated as a solid colored block (4×8) so the
    /// overlay is readable even without a loaded SpriteFont.
    /// </summary>
    private static void DrawPixelText(SpriteBatch sb, string text, int x, int y, Color color)
    {
        // Each glyph is 5px wide, 8px tall with 1px gap
        const int GlyphW = 5;
        const int GlyphH = 8;
        const int Gap    = 1;

        // We render a solid block per character; this is intentionally
        // low-fidelity — the real overlay should replace _pixel rendering
        // with a proper SpriteFont once FontStashSharp is initialised.
        int cx = x;
        foreach (char ch in text)
        {
            if (ch == ' ')
            {
                cx += GlyphW + Gap;
                continue;
            }

            // Draw a tiny coloured rectangle representing the character.
            // Each glyph column bitmap is encoded below for printable ASCII.
            byte[][]? bmp = GetGlyphBitmap(ch);
            if (bmp == null)
            {
                // Fallback: solid block
                sb.Draw(_pixel!, new Rectangle(cx, y, GlyphW - 1, GlyphH), color);
            }
            else
            {
                for (int col = 0; col < bmp.Length; col++)
                {
                    byte colMask = bmp[col][0];
                    for (int row = 0; row < GlyphH; row++)
                    {
                        bool lit = (colMask & (1 << (GlyphH - 1 - row))) != 0;
                        if (lit)
                            sb.Draw(_pixel!, new Rectangle(cx + col, y + row, 1, 1), color);
                    }
                }
            }

            cx += GlyphW + Gap;
        }
    }

    // -------------------------------------------------------------------------
    // Minimal 3-column, 8-row bitmap font for common ASCII characters.
    // Each entry: array of column bitmasks (bit 7 = top row).
    // Only the characters used in the overlay stats are populated;
    // everything else falls back to a solid block.
    // -------------------------------------------------------------------------
    private static byte[][]? GetGlyphBitmap(char ch)
    {
        // Digits 0-9 and common punctuation used in the overlay labels
        return ch switch
        {
            '0' => new byte[][] { new[]{(byte)0x7E}, new[]{(byte)0x81}, new[]{(byte)0x81}, new[]{(byte)0x7E} },
            '1' => new byte[][] { new[]{(byte)0x00}, new[]{(byte)0x82}, new[]{(byte)0xFF}, new[]{(byte)0x80} },
            '2' => new byte[][] { new[]{(byte)0xE2}, new[]{(byte)0x91}, new[]{(byte)0x91}, new[]{(byte)0x8E} },
            '3' => new byte[][] { new[]{(byte)0x42}, new[]{(byte)0x89}, new[]{(byte)0x89}, new[]{(byte)0x76} },
            '4' => new byte[][] { new[]{(byte)0x1F}, new[]{(byte)0x10}, new[]{(byte)0x10}, new[]{(byte)0xFF} },
            '5' => new byte[][] { new[]{(byte)0x4F}, new[]{(byte)0x89}, new[]{(byte)0x89}, new[]{(byte)0x71} },
            '6' => new byte[][] { new[]{(byte)0x7E}, new[]{(byte)0x89}, new[]{(byte)0x89}, new[]{(byte)0x72} },
            '7' => new byte[][] { new[]{(byte)0x01}, new[]{(byte)0xF1}, new[]{(byte)0x09}, new[]{(byte)0x07} },
            '8' => new byte[][] { new[]{(byte)0x76}, new[]{(byte)0x89}, new[]{(byte)0x89}, new[]{(byte)0x76} },
            '9' => new byte[][] { new[]{(byte)0x4E}, new[]{(byte)0x91}, new[]{(byte)0x91}, new[]{(byte)0x7E} },
            '.' => new byte[][] { new[]{(byte)0x00}, new[]{(byte)0x00}, new[]{(byte)0x60}, new[]{(byte)0x60} },
            ':' => new byte[][] { new[]{(byte)0x00}, new[]{(byte)0x66}, new[]{(byte)0x66}, new[]{(byte)0x00} },
            '-' => new byte[][] { new[]{(byte)0x08}, new[]{(byte)0x08}, new[]{(byte)0x08}, new[]{(byte)0x08} },
            _ => null
        };
    }
}
