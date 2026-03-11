using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.Debug;

/// <summary>
/// Named-section frame profiler with rolling 60-frame averages.
/// Usage:
///   Profiler.BeginFrame();
///   Profiler.Begin("Physics");
///   // ... physics work ...
///   Profiler.End("Physics");
///   Profiler.EndFrame();
///   Profiler.Draw(sb, new Vector2(300, 8));
/// </summary>
public static class Profiler
{
    // -------------------------------------------------------------------------
    // Public sample type
    // -------------------------------------------------------------------------
    public struct ProfileSample
    {
        public string Name;
        public double AvgMs;
        public double LastMs;
    }

    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------
    private const int RollingWindow = 60;

    // -------------------------------------------------------------------------
    // Per-section state
    // -------------------------------------------------------------------------
    private sealed class SectionData
    {
        public Stopwatch          Timer    = new();
        public double             LastMs   = 0.0;
        public readonly Queue<double> History = new(RollingWindow);
        public double             AvgMs    = 0.0;

        public void PushSample(double ms)
        {
            LastMs = ms;
            if (History.Count >= RollingWindow)
                History.Dequeue();
            History.Enqueue(ms);

            double sum = 0.0;
            foreach (double v in History) sum += v;
            AvgMs = sum / History.Count;
        }
    }

    // -------------------------------------------------------------------------
    // Storage
    // -------------------------------------------------------------------------
    private static readonly Dictionary<string, SectionData> _sections = new();

    // Frame-level timing
    private static readonly Stopwatch _frameTimer = new();
    public static double TotalFrameMs { get; private set; } = 0.0;

    // Snapshot returned to callers — rebuilt on EndFrame
    private static readonly List<ProfileSample> _samples = new();
    public static IReadOnlyList<ProfileSample> Samples => _samples;

    // Lazily created 1×1 white pixel for the colour bars in Draw()
    private static Texture2D? _pixel;

    // -------------------------------------------------------------------------
    // Frame boundary
    // -------------------------------------------------------------------------

    /// <summary>Call at the very start of each game frame (before Update).</summary>
    public static void BeginFrame()
    {
        _frameTimer.Restart();
    }

    /// <summary>
    /// Call at the very end of each game frame (after all sections are closed).
    /// Computes TotalFrameMs and rebuilds the Samples snapshot.
    /// </summary>
    public static void EndFrame()
    {
        _frameTimer.Stop();
        TotalFrameMs = _frameTimer.Elapsed.TotalMilliseconds;

        // Rebuild public snapshot, sorted by average time descending
        _samples.Clear();
        foreach (var (name, data) in _sections)
        {
            _samples.Add(new ProfileSample
            {
                Name  = name,
                AvgMs = data.AvgMs,
                LastMs = data.LastMs,
            });
        }
        _samples.Sort((a, b) => b.AvgMs.CompareTo(a.AvgMs));
    }

    // -------------------------------------------------------------------------
    // Section API
    // -------------------------------------------------------------------------

    /// <summary>Begin timing a named section. Nested/overlapping calls are allowed.</summary>
    public static void Begin(string sectionName)
    {
        if (!_sections.TryGetValue(sectionName, out var data))
        {
            data = new SectionData();
            _sections[sectionName] = data;
        }
        data.Timer.Restart();
    }

    /// <summary>Stop timing the named section and record the elapsed time.</summary>
    public static void End(string sectionName)
    {
        if (!_sections.TryGetValue(sectionName, out var data)) return;
        data.Timer.Stop();
        data.PushSample(data.Timer.Elapsed.TotalMilliseconds);
    }

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    /// <summary>
    /// Draws a colour-coded table of profiler sections at the given screen position.
    /// Green = &lt; 2 ms, Yellow = &lt; 8 ms, Red = &ge; 8 ms.
    /// </summary>
    public static void Draw(SpriteBatch sb, Vector2 position)
    {
        if (_samples.Count == 0) return;

        EnsurePixel(sb.GraphicsDevice);

        const int LineHeight    = 16;
        const int PanelPadding  = 6;
        const int NameColWidth  = 160;
        const int ValueColWidth = 80;
        const int BarMaxWidth   = 60;
        const int PanelWidth    = NameColWidth + ValueColWidth + BarMaxWidth + PanelPadding * 3;

        int panelHeight = PanelPadding * 2 + (_samples.Count + 1) * LineHeight;

        int x = (int)position.X;
        int y = (int)position.Y;

        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);

        // Background
        sb.Draw(_pixel!, new Rectangle(x, y, PanelWidth, panelHeight), new Color(0, 0, 0, 180));

        int cx = x + PanelPadding;
        int cy = y + PanelPadding;

        // Header row
        DrawLabel(sb, "SECTION",  cx,                           cy, Color.LightGray);
        DrawLabel(sb, "AVG",      cx + NameColWidth,            cy, Color.LightGray);
        DrawLabel(sb, "LAST",     cx + NameColWidth + 50,       cy, Color.LightGray);
        cy += LineHeight;

        // Data rows
        foreach (var sample in _samples)
        {
            Color rowColor = SampleColor(sample.AvgMs);

            DrawLabel(sb, Truncate(sample.Name, 18), cx, cy, rowColor);
            DrawLabel(sb, $"{sample.AvgMs:F2}",  cx + NameColWidth,       cy, rowColor);
            DrawLabel(sb, $"{sample.LastMs:F2}", cx + NameColWidth + 50,  cy, rowColor);

            // Bar graph (avg ms clamped to 16 ms = one frame budget)
            int barW = (int)Math.Clamp(sample.AvgMs / 16.0 * BarMaxWidth, 1, BarMaxWidth);
            sb.Draw(_pixel!,
                    new Rectangle(cx + NameColWidth + ValueColWidth, cy + 2, barW, LineHeight - 4),
                    rowColor * 0.6f);

            cy += LineHeight;
        }

        // Total frame row
        DrawLabel(sb, $"TOTAL: {TotalFrameMs:F2} ms", cx, cy, Color.White);

        sb.End();
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static Color SampleColor(double avgMs) =>
        avgMs >= 8.0 ? Color.Red :
        avgMs >= 2.0 ? Color.Yellow :
                       Color.LimeGreen;

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen];

    /// <summary>
    /// Renders a string as a row of solid-coloured pixel rectangles.
    /// Each character is rendered as a 4×8 block; spacing is 5px.
    /// This is a very simple fallback; replace with FontStashSharp for proper text.
    /// </summary>
    private static void DrawLabel(SpriteBatch sb, string text, int x, int y, Color color)
    {
        const int GlyphW = 5;
        const int GlyphH = 8;

        int cx = x;
        foreach (char ch in text)
        {
            if (ch != ' ')
                sb.Draw(_pixel!, new Rectangle(cx, y, GlyphW - 1, GlyphH), color);
            cx += GlyphW;
        }
    }

    private static void EnsurePixel(GraphicsDevice gd)
    {
        if (_pixel != null) return;
        _pixel = new Texture2D(gd, 1, 1);
        _pixel.SetData(new[] { Color.White });
    }
}
