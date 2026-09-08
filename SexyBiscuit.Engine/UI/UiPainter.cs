using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Assets;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// Paints a <see cref="UiCanvas"/>. The mirror of <c>html5/src/ui/UiPainter.js</c>.
/// </summary>
/// <remarks>
/// <para>
/// Only five of the twelve <see cref="UiKind"/>s have a painter of their own, which is
/// the bargain <see cref="UiKind"/> describes: everything after <see cref="UiKind.Image"/>
/// is drawn as a composition of a box, a bar and a run of text. Twelve painters written
/// twice is twenty-four; five written twice is ten, and the seven compositions are the
/// same arithmetic on both engines.
/// </para>
/// <para>
/// This owns its <c>SpriteBatch.Begin</c>/<c>End</c> pairs rather than painting inside
/// somebody else's batch, because a clip rectangle cannot be changed mid-batch: MonoGame
/// reads <c>GraphicsDevice.ScissorRectangle</c> when the batch flushes, so a scissor set
/// between two <c>Draw</c> calls applies to both of them. Every clip change therefore
/// closes the batch and opens a new one, and a tree with no clipping is still one batch.
/// </para>
/// </remarks>
public static class UiPainter
{
    /// <summary>How much lighter a hovered button is. Matches the browser's `lighten`.</summary>
    private const float HoverGain = 1.25f;

    /// <summary>How much darker a pressed control is, so a press reads as a press.</summary>
    private const float PressGain = 0.82f;

    /// <summary>Seconds for a full caret blink cycle.</summary>
    private const float CaretPeriod = 1.0f;

    /// <summary>The thickness of the ring drawn round the focused node under a gamepad.</summary>
    private const float FocusRingWidth = 2f;

    // -------------------------------------------------------------------------
    // Entry points
    // -------------------------------------------------------------------------

    /// <summary>
    /// Paints every live canvas in <see cref="UiCanvas.Order"/>, lowest first.
    /// </summary>
    /// <remarks>
    /// Laying out here rather than expecting the caller to have done it keeps the
    /// contract to one call: a canvas whose tree changed this frame is measured before
    /// it is painted, and a clean one costs the dirty-flag check and nothing else.
    /// </remarks>
    public static void PaintAll(SpriteBatch sb, float viewportWidth, float viewportHeight,
                                bool showFocusRing = false)
    {
        List<UiCanvas> canvases = new(UiCanvas.All);
        canvases.Sort(static (a, b) => a.Order.CompareTo(b.Order));

        foreach (UiCanvas canvas in canvases)
        {
            canvas.SetViewport(viewportWidth, viewportHeight);
            canvas.Layout();
            Paint(sb, canvas, showFocusRing);
        }
    }

    /// <summary>Paints one canvas. The tree must already have been laid out.</summary>
    public static void Paint(SpriteBatch sb, UiCanvas canvas, bool showFocusRing = false)
    {
        if (canvas.Root.Children.Count == 0 && canvas.Root.Background is null) return;

        var state = new PaintState(sb, canvas, showFocusRing);
        state.PaintNode(canvas.Root, 1f);
        state.Finish();
    }

    // -------------------------------------------------------------------------
    // The walk
    // -------------------------------------------------------------------------

    /// <summary>One canvas's paint pass, holding the batch and the live clip rectangle.</summary>
    private sealed class PaintState
    {
        private readonly SpriteBatch _sb;
        private readonly UiCanvas    _canvas;
        private readonly bool        _focusRing;

        private RectangleF _clip;
        private bool       _open;

        public PaintState(SpriteBatch sb, UiCanvas canvas, bool focusRing)
        {
            _sb        = sb;
            _canvas    = canvas;
            _focusRing = focusRing;
            _clip      = new RectangleF(0f, 0f, canvas.CanvasSize.X, canvas.CanvasSize.Y);
        }

        public void PaintNode(UiNode node, float inheritedOpacity)
        {
            if (!node.Visible) return;

            float opacity = inheritedOpacity * Math.Clamp(node.Opacity, 0f, 1f);
            if (opacity <= 0.004f) return;

            // An empty clip rectangle means every descendant is scrolled or masked out
            // of view. Returning here is not just an optimisation: a zero-area scissor
            // is rejected by the device, so painting on would ignore the clip entirely.
            if (node.ClipRect.IsEmpty) return;

            SetClip(node.ClipRect);
            PaintSelf(node, opacity);

            foreach (UiNode child in UiLayout.PaintOrder(node)) PaintNode(child, opacity);
        }

        public void Finish() => Close();

        // ---------------------------------------------------------------------
        // Batch and clipping
        // ---------------------------------------------------------------------

        private void SetClip(RectangleF clip)
        {
            if (_open && clip == _clip) return;
            Close();
            _clip = clip;
        }

        private void Open()
        {
            if (_open) return;

            Rectangle scissor = _canvas.CanvasToScreen(_clip).ToScissor();
            _sb.Begin(blendState: BlendState.NonPremultiplied,
                      samplerState: SamplerState.PointClamp,
                      rasterizerState: ScissorRasteriser,
                      transformMatrix: _canvas.GetTransform());
            _sb.GraphicsDevice.ScissorRectangle = scissor;
            _open = true;
        }

        private void Close()
        {
            if (!_open) return;
            _sb.End();
            _open = false;
        }

        // ---------------------------------------------------------------------
        // The five painters, and the seven compositions
        // ---------------------------------------------------------------------

        private void PaintSelf(UiNode node, float opacity)
        {
            switch (node.Kind)
            {
                case UiKind.Panel:      PaintPanel(node, opacity);      break;
                case UiKind.Label:      PaintLabel(node, opacity, null); break;
                case UiKind.Bar:        PaintBar(node, opacity);        break;
                case UiKind.Button:     PaintButton(node, opacity);     break;
                case UiKind.Image:      PaintImage(node, opacity);      break;

                case UiKind.Slider:     PaintSlider(node, opacity);     break;
                case UiKind.Toggle:     PaintToggle(node, opacity);     break;
                case UiKind.TextField:  PaintTextField(node, opacity);  break;
                case UiKind.Dropdown:   PaintDropdown(node, opacity);   break;
                case UiKind.ScrollView: PaintScrollView(node, opacity); break;
                case UiKind.TabStrip:   PaintTabStrip(node, opacity);   break;
                case UiKind.Spacer:                                     break;
            }

            if (_focusRing && node.Focused) DrawRing(node.Rect, node.Tint, opacity, FocusRingWidth);
        }

        private void PaintPanel(UiNode node, float opacity)
        {
            Fill(node.Rect, node.Background, opacity);
            DrawBorder(node, opacity);
        }

        private void PaintLabel(UiNode node, float opacity, AlignMode? forceAlign)
        {
            Fill(node.Rect, node.Background, opacity);
            DrawBorder(node, opacity);
            DrawText(node, node.Text, node.ContentRect, forceAlign, opacity);
        }

        private void PaintBar(UiNode node, float opacity)
        {
            Fill(node.Rect, node.Background, opacity);

            RectangleF r = node.Rect;
            float fraction = Fraction(node);
            if (fraction > 0f) Fill(new RectangleF(r.X, r.Y, r.Width * fraction, r.Height), node.Tint, opacity);

            DrawBorder(node, opacity);
            if (!string.IsNullOrEmpty(node.Text)) DrawText(node, node.Text, node.Rect, AlignMode.Center, opacity);
        }

        private void PaintButton(UiNode node, float opacity)
        {
            Fill(node.Rect, Shade(node.Background, node), opacity);
            DrawBorder(node, opacity);
            DrawText(node, node.Text, node.Rect, AlignMode.Center, opacity);
        }

        private void PaintImage(UiNode node, float opacity)
        {
            Texture2D? texture = Texture(node.TexturePath);
            if (texture is null)
            {
                // No art: a tinted box, the same answer SpriteRenderer gives.
                Fill(node.Rect, node.Background, opacity);
                DrawBorder(node, opacity);
                return;
            }

            Open();
            Rectangle? source = node.SourceRect is { Z: > 0f, W: > 0f } s
                ? new Rectangle((int)s.X, (int)s.Y, (int)s.Z, (int)s.W)
                : null;

            if (node.NinePatch != Vector4.Zero) DrawNinePatch(node, texture, source, opacity);
            else DrawStretched(node.Rect, texture, source, Alpha(node.Tint, opacity));

            DrawBorder(node, opacity);
        }

        /// <summary>A track, the filled part of it, and a handle sitting at the value.</summary>
        private void PaintSlider(UiNode node, float opacity)
        {
            RectangleF r = node.Rect;
            float fraction = Fraction(node);

            // The track is inset vertically so the handle is the tall thing, which is
            // what makes a slider read as a slider rather than as a progress bar.
            float trackHeight = MathF.Max(4f, r.Height * 0.3f);
            var track = new RectangleF(r.X, r.Y + (r.Height - trackHeight) * 0.5f, r.Width, trackHeight);

            Fill(track, node.Background, opacity);
            if (fraction > 0f) Fill(new RectangleF(track.X, track.Y, track.Width * fraction, track.Height), node.Tint, opacity);

            float handleWidth = MathF.Max(8f, r.Height * 0.5f);
            float handleX = r.X + (r.Width - handleWidth) * fraction;
            var handle = new RectangleF(handleX, r.Y, handleWidth, r.Height);

            Fill(handle, Shade(node.Tint, node), opacity);
            DrawBorder(node, opacity);
        }

        /// <summary>A box, a tick when checked, and the label to the right of both.</summary>
        private void PaintToggle(UiNode node, float opacity)
        {
            RectangleF r = node.Rect;
            float side = MathF.Min(r.Height, MathF.Max(12f, r.Height));
            var box = new RectangleF(r.X, r.Y + (r.Height - side) * 0.5f, side, side);

            Fill(box, Shade(node.Background, node), opacity);
            DrawRing(box, node.Tint, opacity * 0.7f, 1f);

            if (node.Checked)
            {
                float inset = MathF.Max(2f, side * 0.25f);
                Fill(box.Deflate(new Vector4(inset, inset, inset, inset)), node.Tint, opacity);
            }

            if (string.IsNullOrEmpty(node.Text)) return;

            float gap = MathF.Max(4f, side * 0.4f);
            var textArea = new RectangleF(box.Right + gap, r.Y, MathF.Max(0f, r.Right - box.Right - gap), r.Height);
            DrawText(node, node.Text, textArea, AlignMode.Start, opacity);
        }

        /// <summary>A box, the text, and a caret that blinks only while the field has focus.</summary>
        private void PaintTextField(UiNode node, float opacity)
        {
            Fill(node.Rect, node.Background, opacity);
            DrawBorder(node, opacity);
            DrawText(node, node.Text, node.ContentRect, AlignMode.Start, opacity);

            if (!node.Focused) return;
            if (Core.Time.RealtimeSinceStartup % CaretPeriod >= CaretPeriod * 0.5f) return;

            float scale = MathF.Max(1f, MathF.Round(node.TextScale));
            float caretX = node.ContentRect.X + UiTextMeasure.Width(node.Text, scale);
            float caretHeight = UiTextMeasure.LineHeight(scale);
            float caretY = node.ContentRect.Y + (node.ContentRect.Height - caretHeight) * 0.5f;

            Fill(new RectangleF(caretX + 1f, caretY, MathF.Max(1f, scale), caretHeight), node.Tint, opacity);
        }

        /// <summary>The closed control, plus the list when the router has opened it.</summary>
        private void PaintDropdown(UiNode node, float opacity)
        {
            Fill(node.Rect, Shade(node.Background, node), opacity);
            DrawBorder(node, opacity);
            DrawText(node, SelectedLabel(node), node.ContentRect, AlignMode.Start, opacity);

            // A chevron, drawn as a stack of narrowing rows: three fills instead of a
            // glyph the shared 5x7 table does not have.
            RectangleF r = node.Rect;
            float size = MathF.Max(3f, MathF.Min(8f, r.Height * 0.22f));
            float cx = r.Right - size * 2f;
            float cy = r.Y + (r.Height - size) * 0.5f;
            for (int i = 0; i < (int)size; i++)
                Fill(new RectangleF(cx + i, cy + i, size * 2f - i * 2f, 1f), node.Tint, opacity);

            if (!node.Expanded || node.Options.Count == 0) return;
            PaintDropdownList(node, opacity);
        }

        private void PaintDropdownList(UiNode node, float opacity)
        {
            RectangleF list = DropdownListRect(node);

            // The list escapes the control's own clip: it is drawn over whatever is
            // below it, which is the whole point of a popup.
            SetClip(new RectangleF(0f, 0f, _canvas.CanvasSize.X, _canvas.CanvasSize.Y));

            Fill(list, node.Background ?? Color.Black, opacity);
            DrawRing(list, node.Tint, opacity * 0.6f, 1f);

            float rowHeight = node.Rect.Height;
            for (int i = 0; i < node.Options.Count; i++)
            {
                var row = new RectangleF(list.X, list.Y + i * rowHeight, list.Width, rowHeight);
                if (i == node.SelectedIndex) Fill(row, Alpha(node.Tint, 0.25f), opacity);

                DrawText(node, node.Options[i],
                         row.Deflate(new Vector4(node.Padding.X, 0f, node.Padding.Z, 0f)),
                         AlignMode.Start, opacity);
            }
        }

        /// <summary>The viewport, plus a thumb on each axis that actually overflows.</summary>
        private void PaintScrollView(UiNode node, float opacity)
        {
            Fill(node.Rect, node.Background, opacity);
            DrawBorder(node, opacity);

            RectangleF view = node.ContentRect;
            Color thumb = Alpha(node.Tint, 0.5f);
            float bar = ScrollBarThickness;

            if (node.Scroll is ScrollMode.Vertical or ScrollMode.Both && node.ContentSize.Y > view.Height)
            {
                float travel = node.ContentSize.Y - view.Height;
                float height = MathF.Max(bar * 2f, view.Height * (view.Height / node.ContentSize.Y));
                float y = view.Y + (view.Height - height) * Math.Clamp(node.ScrollOffset.Y / travel, 0f, 1f);
                Fill(new RectangleF(node.Rect.Right - bar, y, bar, height), thumb, opacity);
            }

            if (node.Scroll is ScrollMode.Horizontal or ScrollMode.Both && node.ContentSize.X > view.Width)
            {
                float travel = node.ContentSize.X - view.Width;
                float width = MathF.Max(bar * 2f, view.Width * (view.Width / node.ContentSize.X));
                float x = view.X + (view.Width - width) * Math.Clamp(node.ScrollOffset.X / travel, 0f, 1f);
                Fill(new RectangleF(x, node.Rect.Bottom - bar, width, bar), thumb, opacity);
            }
        }

        /// <summary>Equal segments across the box, the selected one lit.</summary>
        private void PaintTabStrip(UiNode node, float opacity)
        {
            Fill(node.Rect, node.Background, opacity);
            DrawBorder(node, opacity);

            int count = node.Options.Count;
            if (count == 0) return;

            for (int i = 0; i < count; i++)
            {
                RectangleF tab = TabRect(node, i);
                bool selected = i == node.SelectedIndex;

                if (selected)
                {
                    Fill(tab, Alpha(node.Tint, 0.22f), opacity);
                    // An underline rather than a filled tab: it survives any background.
                    Fill(new RectangleF(tab.X, tab.Bottom - 2f, tab.Width, 2f), node.Tint, opacity);
                }

                DrawText(node, node.Options[i], tab, AlignMode.Center,
                         opacity * (selected ? 1f : 0.65f));
            }
        }

        // ---------------------------------------------------------------------
        // Primitives
        // ---------------------------------------------------------------------

        private void Fill(RectangleF r, Color? colour, float opacity)
        {
            if (colour is not Color c || r.Width <= 0f || r.Height <= 0f) return;

            Open();
            _sb.Draw(WhitePixel(_sb.GraphicsDevice), new Vector2(r.X, r.Y), null, Alpha(c, opacity),
                     0f, Vector2.Zero, new Vector2(r.Width, r.Height), SpriteEffects.None, 0f);
        }

        private void DrawBorder(UiNode node, float opacity)
        {
            if (node.BorderColour is null || node.BorderWidth <= 0f) return;
            DrawRing(node.Rect, node.BorderColour.Value, opacity, node.BorderWidth);
        }

        /// <summary>Four fills, not a stroked rectangle: the corners must not double up.</summary>
        private void DrawRing(RectangleF r, Color colour, float opacity, float width)
        {
            if (width <= 0f || r.Width <= 0f || r.Height <= 0f) return;

            float w = MathF.Min(width, MathF.Min(r.Width, r.Height) * 0.5f);
            Fill(new RectangleF(r.X, r.Y, r.Width, w), colour, opacity);
            Fill(new RectangleF(r.X, r.Bottom - w, r.Width, w), colour, opacity);
            Fill(new RectangleF(r.X, r.Y + w, w, r.Height - w * 2f), colour, opacity);
            Fill(new RectangleF(r.Right - w, r.Y + w, w, r.Height - w * 2f), colour, opacity);
        }

        private void DrawText(UiNode node, string? text, RectangleF area, AlignMode? forceAlign, float opacity)
        {
            if (string.IsNullOrEmpty(text)) return;

            float scale = MathF.Max(1f, MathF.Round(node.TextScale));
            string body = node.WrapText ? UiTextMeasure.Wrap(text, scale, area.Width) : text;

            float textWidth  = UiTextMeasure.Width(body, scale);
            float textHeight = UiTextMeasure.Height(body, scale, node.LineSpacing);

            float x = (forceAlign ?? node.TextAlign) switch
            {
                AlignMode.Center => area.X + (area.Width - textWidth) * 0.5f,
                AlignMode.End    => area.Right - textWidth,
                _                => area.X,
            };

            float y = node.VerticalAlign switch
            {
                AlignMode.Start => area.Y,
                AlignMode.End   => area.Bottom - textHeight,
                _               => area.Y + (area.Height - textHeight) * 0.5f,
            };

            Open();
            BitmapFont.Draw(_sb, body, new Vector2(MathF.Round(x), MathF.Round(y)),
                            Alpha(node.Tint, opacity), scale);
        }

        private void DrawStretched(RectangleF r, Texture2D texture, Rectangle? source, Color tint)
        {
            int sw = source?.Width  ?? texture.Width;
            int sh = source?.Height ?? texture.Height;
            if (sw <= 0 || sh <= 0 || r.Width <= 0f || r.Height <= 0f) return;

            _sb.Draw(texture, new Vector2(r.X, r.Y), source, tint, 0f, Vector2.Zero,
                     new Vector2(r.Width / sw, r.Height / sh), SpriteEffects.None, 0f);
        }

        /// <summary>
        /// Nine stretched quads: corners fixed, edges stretched on one axis, centre on both.
        /// </summary>
        private void DrawNinePatch(UiNode node, Texture2D texture, Rectangle? source, float opacity)
        {
            Rectangle src = source ?? texture.Bounds;
            Vector4 n = node.NinePatch;
            RectangleF r = node.Rect;
            Color tint = Alpha(node.Tint, opacity);

            // Columns and rows, in source pixels then in destination pixels.
            float[] sx = { src.Left, src.Left + n.X, src.Right - n.Z, src.Right };
            float[] sy = { src.Top,  src.Top  + n.Y, src.Bottom - n.W, src.Bottom };
            float[] dx = { r.Left,   r.Left   + n.X, r.Right - n.Z,    r.Right };
            float[] dy = { r.Top,    r.Top    + n.Y, r.Bottom - n.W,   r.Bottom };

            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    var s = new Rectangle((int)sx[col], (int)sy[row],
                                          (int)(sx[col + 1] - sx[col]), (int)(sy[row + 1] - sy[row]));
                    var d = new RectangleF(dx[col], dy[row], dx[col + 1] - dx[col], dy[row + 1] - dy[row]);
                    if (s.Width <= 0 || s.Height <= 0 || d.Width <= 0f || d.Height <= 0f) continue;
                    DrawStretched(d, texture, s, tint);
                }
            }
        }

        /// <summary>Hover lightens, press darkens; a press wins because it is the later state.</summary>
        private static Color? Shade(Color? colour, UiNode node)
        {
            if (colour is not Color c) return null;
            if (!node.Interactive) return c;
            if (node.Pressed) return Scale(c, PressGain);
            if (node.Hovered) return Lighten(c);
            return c;
        }
    }

    // -------------------------------------------------------------------------
    // Geometry the router needs too
    // -------------------------------------------------------------------------

    /// <summary>How wide a scroll thumb is, in canvas units.</summary>
    public const float ScrollBarThickness = 6f;

    /// <summary>The rectangle of one tab, so painting and hit-testing agree.</summary>
    public static RectangleF TabRect(UiNode node, int index)
    {
        int count = Math.Max(1, node.Options.Count);
        float width = node.Rect.Width / count;
        return new RectangleF(node.Rect.X + width * index, node.Rect.Y, width, node.Rect.Height);
    }

    /// <summary>The open list's rectangle, which the router hit-tests against.</summary>
    public static RectangleF DropdownListRect(UiNode node)
    {
        float rowHeight = node.Rect.Height;
        float height = rowHeight * node.Options.Count;
        float y = node.Rect.Bottom;

        // Flip above the control when the list would run off the bottom of the canvas.
        if (node.Canvas is { } canvas && y + height > canvas.CanvasSize.Y && node.Rect.Y - height >= 0f)
            y = node.Rect.Y - height;

        return new RectangleF(node.Rect.X, y, node.Rect.Width, height);
    }

    /// <summary>Where a node's value sits between its bounds, as 0..1.</summary>
    public static float Fraction(UiNode node)
    {
        float span = node.MaxValue - node.MinValue;
        return span <= 0f ? 0f : Math.Clamp((node.Value - node.MinValue) / span, 0f, 1f);
    }

    /// <summary>The label a dropdown shows when it is closed.</summary>
    public static string SelectedLabel(UiNode node)
        => node.SelectedIndex >= 0 && node.SelectedIndex < node.Options.Count
            ? node.Options[node.SelectedIndex]
            : node.Text;

    // -------------------------------------------------------------------------
    // Shared bits
    // -------------------------------------------------------------------------

    /// <summary>Scissor testing on, everything else at its default.</summary>
    private static readonly RasterizerState ScissorRasteriser = new()
    {
        CullMode = CullMode.CullCounterClockwiseFace,
        ScissorTestEnable = true,
    };

    private static Color Alpha(Color c, float opacity)
        => opacity >= 0.999f ? c : new Color(c.R, c.G, c.B, (byte)Math.Clamp(c.A * opacity, 0f, 255f));

    private static Color Scale(Color c, float gain)
        => new((byte)Math.Clamp(c.R * gain, 0f, 255f),
               (byte)Math.Clamp(c.G * gain, 0f, 255f),
               (byte)Math.Clamp(c.B * gain, 0f, 255f), c.A);

    /// <summary>The hover shade, matching <see cref="ScriptUi"/> and the browser's `lighten`.</summary>
    private static Color Lighten(Color c)
    {
        static byte Up(byte v) => (byte)Math.Min(255, (int)Math.Round(v * HoverGain) + 12);
        return new Color(Up(c.R), Up(c.G), Up(c.B), c.A);
    }

    private static Texture2D? Texture(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return AssetManager.Current?.Load<Texture2D>(path); }
        catch { return null; }   // A missing texture is a tinted box, never a crash mid-frame.
    }

    private static Texture2D? _whitePixel;

    private static Texture2D WhitePixel(GraphicsDevice gd)
    {
        if (_whitePixel is null || _whitePixel.IsDisposed || _whitePixel.GraphicsDevice != gd)
        {
            _whitePixel = new Texture2D(gd, 1, 1);
            _whitePixel.SetData(new[] { Color.White });
        }
        return _whitePixel;
    }
}
