using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Assets;

namespace SexyBiscuit.Engine.Debug;

/// <summary>
/// Snapshot of managed-memory and asset statistics at a point in time.
/// </summary>
public record MemorySnapshot(
    long TotalManagedBytes,
    int  Gen0,
    int  Gen1,
    int  Gen2,
    int  AssetCount,
    long AssetEstimatedBytes);

/// <summary>
/// Static class that polls GC memory statistics and renders them as a table.
/// </summary>
public static class MemoryViewer
{
    // -------------------------------------------------------------------------
    // Visibility
    // -------------------------------------------------------------------------
    public static bool Visible { get; set; } = false;

    // -------------------------------------------------------------------------
    // Cached stats (updated each Update() call)
    // -------------------------------------------------------------------------
    private static long _totalManagedBytes = 0L;
    private static int  _gen0              = 0;
    private static int  _gen1              = 0;
    private static int  _gen2              = 0;
    private static int  _assetCount        = 0;

    // History for a simple sparkline (last 60 samples, MB)
    private const int HistoryLength = 60;
    private static readonly float[] _memoryHistory = new float[HistoryLength];
    private static int _historyIndex = 0;

    // Lazily created rendering resource
    private static Texture2D? _pixel;

    // Average bytes per asset used for the estimated footprint
    private const long AverageBytesPerAsset = 256 * 1024; // 256 KB

    // -------------------------------------------------------------------------
    // Update — call once per frame
    // -------------------------------------------------------------------------

    /// <summary>
    /// Polls GC stats. Call this once per engine Update frame.
    /// </summary>
    public static void Update()
    {
        _totalManagedBytes = GC.GetTotalMemory(false);
        _gen0              = GC.CollectionCount(0);
        _gen1              = GC.CollectionCount(1);
        _gen2              = GC.CollectionCount(2);

        // Asset count via direct reference to AssetManager through SBEngine
        _assetCount = TryGetAssetCount();

        // Record memory history
        float mbNow = _totalManagedBytes / (1024f * 1024f);
        _memoryHistory[_historyIndex] = mbNow;
        _historyIndex = (_historyIndex + 1) % HistoryLength;
    }

    // -------------------------------------------------------------------------
    // Snapshot API
    // -------------------------------------------------------------------------

    /// <summary>Returns a snapshot of the current memory state.</summary>
    public static MemorySnapshot GetSnapshot()
    {
        int count = TryGetAssetCount();
        long estimated = count * AverageBytesPerAsset;

        return new MemorySnapshot(
            TotalManagedBytes: GC.GetTotalMemory(false),
            Gen0:  GC.CollectionCount(0),
            Gen1:  GC.CollectionCount(1),
            Gen2:  GC.CollectionCount(2),
            AssetCount: count,
            AssetEstimatedBytes: estimated);
    }

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    /// <summary>
    /// Renders the memory statistics table at the given screen position.
    /// </summary>
    public static void Draw(SpriteBatch sb, Vector2 position)
    {
        if (!Visible) return;

        EnsurePixel(sb.GraphicsDevice);

        var snap  = GetSnapshot();
        float mb  = snap.TotalManagedBytes / (1024f * 1024f);
        float amb = snap.AssetEstimatedBytes / (1024f * 1024f);

        string[] rows =
        {
            $"Managed Heap : {mb:F2} MB",
            $"GC Gen0      : {snap.Gen0}",
            $"GC Gen1      : {snap.Gen1}",
            $"GC Gen2      : {snap.Gen2}",
            $"Assets       : {snap.AssetCount}",
            $"Asset Est.   : {amb:F2} MB",
        };

        const int LineHeight   = 18;
        const int PanelPadding = 6;
        const int PanelWidth   = 260;
        const int SparklineH   = 30;
        const int SparklineW   = PanelWidth - PanelPadding * 2;

        int panelHeight = PanelPadding * 2
                        + rows.Length * LineHeight
                        + SparklineH + PanelPadding;

        int x = (int)position.X;
        int y = (int)position.Y;

        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);

        // Panel background
        sb.Draw(_pixel!, new Rectangle(x, y, PanelWidth, panelHeight), new Color(0, 0, 0, 180));

        int cy = y + PanelPadding;

        // Data rows
        foreach (var row in rows)
        {
            DrawText(sb, row, x + PanelPadding, cy, Color.White);
            cy += LineHeight;
        }

        // Memory sparkline
        cy += PanelPadding / 2;
        DrawSparkline(sb, x + PanelPadding, cy, SparklineW, SparklineH);

        sb.End();
    }

    // -------------------------------------------------------------------------
    // Sparkline renderer
    // -------------------------------------------------------------------------

    private static void DrawSparkline(SpriteBatch sb, int x, int y, int w, int h)
    {
        // Find max value in history for normalisation
        float maxVal = 1f;
        for (int i = 0; i < HistoryLength; i++)
            if (_memoryHistory[i] > maxVal) maxVal = _memoryHistory[i];

        // Background
        sb.Draw(_pixel!, new Rectangle(x, y, w, h), new Color(20, 20, 20, 200));

        float barW = (float)w / HistoryLength;
        for (int i = 0; i < HistoryLength; i++)
        {
            int idx = (_historyIndex + i) % HistoryLength;
            float val = _memoryHistory[idx];
            int barH = (int)(val / maxVal * h);
            if (barH < 1) barH = 1;

            // Colour: green-to-red based on fraction of max
            float frac = val / maxVal;
            Color c = new Color(
                (byte)(frac * 255),
                (byte)((1f - frac) * 200),
                30);

            sb.Draw(_pixel!,
                    new Rectangle(x + (int)(i * barW), y + h - barH, Math.Max(1, (int)barW - 1), barH),
                    c);
        }
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static int TryGetAssetCount()
    {
        try
        {
            var engine = SBEngine.Instance;
            if (engine?.Assets == null) return 0;

            // AssetManager.GetLoadedAssets() returns an IEnumerable of loaded paths
            int count = 0;
            foreach (var _ in engine.Assets.GetLoadedAssets())
                count++;
            return count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Minimal pixel-block text renderer (5×8 per glyph, monospaced solid blocks).
    /// </summary>
    private static void DrawText(SpriteBatch sb, string text, int x, int y, Color color)
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
