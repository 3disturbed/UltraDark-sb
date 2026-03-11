using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Networking;

namespace SexyBiscuit.Engine.Debug;

/// <summary>
/// Displays live networking statistics when a NetworkManager is active.
/// Tracks a 1-second sliding window for bandwidth estimates.
/// </summary>
public static class NetworkDiagnostics
{
    // -------------------------------------------------------------------------
    // Visibility
    // -------------------------------------------------------------------------
    public static bool Visible { get; set; } = false;

    // -------------------------------------------------------------------------
    // Polled stats snapshot
    // -------------------------------------------------------------------------
    private static bool   _isConnected  = false;
    private static bool   _isServer     = false;
    private static bool   _isClient     = false;
    private static int    _peerCount    = 0;
    private static int    _pingMs       = 0;
    private static float  _packetLoss   = 0f; // 0-100%

    // -------------------------------------------------------------------------
    // Bandwidth — 1-second sliding window
    // -------------------------------------------------------------------------
    // Each entry: (timestamp in seconds, bytes)
    private static readonly List<(double Time, long Bytes)> _inBandwidthWindow  = new();
    private static readonly List<(double Time, long Bytes)> _outBandwidthWindow = new();

    private static long  _inBytesThisSecond  = 0L;
    private static long  _outBytesThisSecond = 0L;
    private static float _inKBps             = 0f;
    private static float _outKBps            = 0f;

    private static double _elapsedTotal = 0.0;

    // Lazily created resources
    private static Texture2D? _pixel;

    // -------------------------------------------------------------------------
    // Public API — record bytes for bandwidth tracking
    // -------------------------------------------------------------------------

    /// <summary>Record bytes received this frame for bandwidth estimation.</summary>
    public static void RecordBytesIn(long bytes)
    {
        lock (_inBandwidthWindow)
        {
            _inBandwidthWindow.Add((_elapsedTotal, bytes));
            _inBytesThisSecond += bytes;
        }
    }

    /// <summary>Record bytes sent this frame for bandwidth estimation.</summary>
    public static void RecordBytesOut(long bytes)
    {
        lock (_outBandwidthWindow)
        {
            _outBandwidthWindow.Add((_elapsedTotal, bytes));
            _outBytesThisSecond += bytes;
        }
    }

    // -------------------------------------------------------------------------
    // Update — call once per frame
    // -------------------------------------------------------------------------

    /// <summary>
    /// Polls NetworkManager stats and updates the sliding bandwidth window.
    /// Call once per engine Update frame.
    /// </summary>
    public static void Update(float dt)
    {
        _elapsedTotal += dt;

        // Read stats directly from NetworkManager singleton
        var nm = NetworkManager.Instance;
        if (nm == null || !nm.IsRunning)
        {
            _isConnected = false;
            _isServer    = false;
            _isClient    = false;
            _peerCount   = 0;
            _pingMs      = 0;
            _packetLoss  = 0f;
            _inKBps      = 0f;
            _outKBps     = 0f;
            return;
        }

        _isConnected = nm.IsRunning;
        _isServer    = nm.IsServer;
        _isClient    = nm.IsClient;
        _pingMs      = nm.Ping;

        // Peer count: on server this is ConnectedClientIds; on client it's 1 if connected
        if (nm.IsServer)
        {
            int c = 0;
            foreach (var _ in nm.ConnectedClientIds) c++;
            _peerCount = c;
        }
        else
        {
            _peerCount = nm.IsClient ? 1 : 0;
        }

        // NetworkManager does not expose PacketLoss directly; use 0 as default.
        // If the underlying LiteNetLib peer exposes it, it would be wired here.
        _packetLoss = 0f;

        // Flush bandwidth sliding window (keep only last 1 second)
        double cutoff = _elapsedTotal - 1.0;

        lock (_inBandwidthWindow)
        {
            PruneWindow(_inBandwidthWindow, cutoff);
            _inKBps = SumWindow(_inBandwidthWindow) / 1024f;
        }

        lock (_outBandwidthWindow)
        {
            PruneWindow(_outBandwidthWindow, cutoff);
            _outKBps = SumWindow(_outBandwidthWindow) / 1024f;
        }
    }

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    /// <summary>
    /// Renders the network diagnostics table at the given screen position.
    /// Nothing is drawn when not connected.
    /// </summary>
    public static void Draw(SpriteBatch sb, Vector2 position)
    {
        if (!Visible) return;

        EnsurePixel(sb.GraphicsDevice);

        string role = _isServer ? "Server" : (_isClient ? "Client" : "None");

        string[] rows =
        {
            $"Connected  : {(_isConnected ? "Yes" : "No")}",
            $"Role       : {role}",
            $"Peers      : {_peerCount}",
            $"Ping       : {_pingMs} ms",
            $"Pkt Loss   : {_packetLoss:F1} %",
            $"BW In      : {_inKBps:F1} KB/s",
            $"BW Out     : {_outKBps:F1} KB/s",
        };

        const int LineHeight   = 18;
        const int PanelPadding = 6;
        const int PanelWidth   = 250;

        int panelHeight = PanelPadding * 2 + rows.Length * LineHeight;

        int x = (int)position.X;
        int y = (int)position.Y;

        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);

        // Panel background
        sb.Draw(_pixel!, new Rectangle(x, y, PanelWidth, panelHeight), new Color(0, 0, 0, 180));

        int cy = y + PanelPadding;
        foreach (var row in rows)
        {
            Color rowColor = RowColor(row);
            DrawText(sb, row, x + PanelPadding, cy, rowColor);
            cy += LineHeight;
        }

        sb.End();
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static Color RowColor(string row)
    {
        if (row.StartsWith("Connected") && row.Contains("No"))  return Color.Red;
        if (row.StartsWith("Ping"))
        {
            if (_pingMs >= 150) return Color.Red;
            if (_pingMs >= 80)  return Color.Yellow;
            return Color.LimeGreen;
        }
        if (row.StartsWith("Pkt Loss"))
        {
            if (_packetLoss >= 10f) return Color.Red;
            if (_packetLoss >= 3f)  return Color.Yellow;
        }
        return Color.White;
    }

    private static void PruneWindow(List<(double Time, long Bytes)> window, double cutoff)
    {
        window.RemoveAll(e => e.Time < cutoff);
    }

    private static long SumWindow(List<(double Time, long Bytes)> window)
    {
        long sum = 0;
        foreach (var (_, bytes) in window) sum += bytes;
        return sum;
    }

    /// <summary>Minimal pixel-block text renderer (5×8 per glyph).</summary>
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
