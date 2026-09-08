using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// The two-pass layout engine: measure bottom-up for desired sizes, arrange top-down
/// to hand out the space there actually is.
/// </summary>
/// <remarks>
/// <para>
/// An unbounded axis is <see cref="float.PositiveInfinity"/>, never zero. Using zero to
/// mean "as much as you like" is the classic way two independent layout implementations
/// drift apart while both look right in isolation, so the sentinel is spelled the same
/// word on both engines and is a fixture case.
/// </para>
/// <para>
/// Sizes here are border boxes: padding is inside a node's own size, margin is outside it
/// and is added by whichever parent is arranging the node. That is what lets a parent sum
/// its children's desired sizes without knowing anything about their padding.
/// </para>
/// </remarks>
public static class UiLayout
{
    private const float Epsilon = 0.0001f;

    // =========================================================================
    // Measure
    // =========================================================================

    /// <summary>
    /// Computes and stores a node's desired border-box size, given the space its parent
    /// can offer. Recurses into children.
    /// </summary>
    public static Vector2 Measure(UiNode node, Vector2 available)
    {
        if (!node.Visible)
        {
            node.DesiredSize = Vector2.Zero;
            return Vector2.Zero;
        }

        float padX = node.Padding.X + node.Padding.Z;
        float padY = node.Padding.Y + node.Padding.W;

        // An axis whose size is already decided constrains its content: a wrapping label
        // inside a 200px panel must wrap at 200, not at whatever the window happens to be.
        float knownW = KnownSize(node.WidthMode,  node.Width,  available.X);
        float knownH = KnownSize(node.HeightMode, node.Height, available.Y);

        var inner = new Vector2(
            Shrink(float.IsNaN(knownW) ? available.X : knownW, padX),
            Shrink(float.IsNaN(knownH) ? available.Y : knownH, padY));

        // A scroll axis measures its children unbounded; that overflow is the thing to scroll.
        var childSpace = new Vector2(
            ScrollsHorizontally(node.Scroll) ? float.PositiveInfinity : inner.X,
            ScrollsVertically(node.Scroll)   ? float.PositiveInfinity : inner.Y);

        Vector2 content = MeasureContent(node, childSpace);
        node.ContentSize = content;

        float w = ResolveAxis(node.WidthMode,  node.Width,  content.X + padX, available.X);
        float h = ResolveAxis(node.HeightMode, node.Height, content.Y + padY, available.Y);

        node.DesiredSize = new Vector2(
            ClampAxis(w, node.MinWidth,  node.MaxWidth),
            ClampAxis(h, node.MinHeight, node.MaxHeight));

        return node.DesiredSize;
    }

    private static Vector2 MeasureContent(UiNode node, Vector2 inner)
    {
        Vector2 text = Vector2.Zero;
        if (DrawsText(node.Kind) && !string.IsNullOrEmpty(node.Text))
            text = UiTextMeasure.Measure(node.Text, node.TextScale, node.WrapText, inner.X, node.LineSpacing);

        Vector2 children = node.Layout switch
        {
            LayoutMode.Row or LayoutMode.Flow => MeasureLinear(node, inner, horizontal: true),
            LayoutMode.Column                 => MeasureLinear(node, inner, horizontal: false),
            LayoutMode.Grid                   => MeasureGrid(node, inner),
            _                                 => MeasureOverlay(node, inner),
        };

        return new Vector2(MathF.Max(text.X, children.X), MathF.Max(text.Y, children.Y));
    }

    /// <summary>Row and column, including the wrapped forms.</summary>
    private static Vector2 MeasureLinear(UiNode node, Vector2 inner, bool horizontal)
    {
        float gapMain  = horizontal ? node.Gap.X : node.Gap.Y;
        float gapCross = horizontal ? node.Gap.Y : node.Gap.X;
        float limit    = horizontal ? inner.X : inner.Y;
        bool  wraps    = node.Wrap || node.Layout == LayoutMode.Flow;

        float lineMain = 0f, lineCross = 0f;
        float totalMain = 0f, totalCross = 0f;
        int onThisLine = 0;

        foreach (UiNode child in node.Children)
        {
            if (!child.Visible || child.Positioning == PositionMode.Absolute) continue;

            Measure(child, inner);
            float main  = MainOf(child, horizontal)  + MarginMain(child, horizontal);
            float cross = CrossOf(child, horizontal) + MarginCross(child, horizontal);

            float withChild = onThisLine == 0 ? main : lineMain + gapMain + main;

            if (wraps && onThisLine > 0 && !float.IsPositiveInfinity(limit) && withChild > limit + Epsilon)
            {
                totalMain  = MathF.Max(totalMain, lineMain);
                totalCross += lineCross + gapCross;
                lineMain    = main;
                lineCross   = cross;
                onThisLine  = 1;
                continue;
            }

            lineMain  = withChild;
            lineCross = MathF.Max(lineCross, cross);
            onThisLine++;
        }

        totalMain   = MathF.Max(totalMain, lineMain);
        totalCross += lineCross;

        return horizontal ? new Vector2(totalMain, totalCross) : new Vector2(totalCross, totalMain);
    }

    private static Vector2 MeasureGrid(UiNode node, Vector2 inner)
    {
        int columns = Math.Max(1, node.Columns);
        float cellW = node.CellSize.X > 0f
            ? node.CellSize.X
            : float.IsPositiveInfinity(inner.X) ? 0f
            : MathF.Max(0f, (inner.X - node.Gap.X * (columns - 1)) / columns);

        var cellSpace = new Vector2(cellW > 0f ? cellW : float.PositiveInfinity,
                                    node.CellSize.Y > 0f ? node.CellSize.Y : float.PositiveInfinity);

        float widest = 0f, totalHeight = 0f, rowHeight = 0f;
        int index = 0;

        foreach (UiNode child in node.Children)
        {
            if (!child.Visible || child.Positioning == PositionMode.Absolute) continue;

            Measure(child, cellSpace);
            widest    = MathF.Max(widest, child.DesiredSize.X);
            rowHeight = MathF.Max(rowHeight, child.DesiredSize.Y);

            if (++index % columns == 0)
            {
                totalHeight += rowHeight + node.Gap.Y;
                rowHeight = 0f;
            }
        }

        if (rowHeight > 0f) totalHeight += rowHeight + node.Gap.Y;
        if (totalHeight > 0f) totalHeight -= node.Gap.Y;

        float rowW = node.CellSize.X > 0f ? node.CellSize.X : cellW > 0f ? cellW : widest;
        float width = rowW * columns + node.Gap.X * (columns - 1);

        return new Vector2(width, MathF.Max(0f, totalHeight));
    }

    /// <summary>No layout: children overlap, and the node is as big as the largest.</summary>
    private static Vector2 MeasureOverlay(UiNode node, Vector2 inner)
    {
        Vector2 largest = Vector2.Zero;

        foreach (UiNode child in node.Children)
        {
            if (!child.Visible) continue;
            Measure(child, inner);
            if (child.Positioning == PositionMode.Absolute) continue;

            largest.X = MathF.Max(largest.X, child.DesiredSize.X + MarginMain(child, true));
            largest.Y = MathF.Max(largest.Y, child.DesiredSize.Y + MarginMain(child, false));
        }

        return largest;
    }

    // =========================================================================
    // Arrange
    // =========================================================================

    /// <summary>
    /// Places a node in a final rectangle and distributes what is left to its children.
    /// </summary>
    public static void Arrange(UiNode node, RectangleF final, RectangleF parentClip)
    {
        node.Rect        = final;
        node.ContentRect = final.Deflate(node.Padding);
        node.ClipRect    = Clips(node) ? parentClip.Intersect(final) : parentClip;

        if (!node.Visible)
        {
            node.ClearDirtyTree();
            return;
        }

        RectangleF content = node.ContentRect;

        // Scrolling shifts the children, and clamps so a short list cannot be scrolled away.
        if (node.Scroll != ScrollMode.None)
        {
            float maxX = MathF.Max(0f, node.ContentSize.X - content.Width);
            float maxY = MathF.Max(0f, node.ContentSize.Y - content.Height);
            var clamped = new Vector2(
                ScrollsHorizontally(node.Scroll) ? Math.Clamp(node.ScrollOffset.X, 0f, maxX) : 0f,
                ScrollsVertically(node.Scroll)   ? Math.Clamp(node.ScrollOffset.Y, 0f, maxY) : 0f);

            node.ScrollOffset = clamped;
            content = new RectangleF(content.X - clamped.X, content.Y - clamped.Y,
                                     ScrollsHorizontally(node.Scroll) ? MathF.Max(content.Width,  node.ContentSize.X) : content.Width,
                                     ScrollsVertically(node.Scroll)   ? MathF.Max(content.Height, node.ContentSize.Y) : content.Height);
        }

        switch (node.Layout)
        {
            case LayoutMode.Row or LayoutMode.Flow: ArrangeLinear(node, content, horizontal: true);  break;
            case LayoutMode.Column:                 ArrangeLinear(node, content, horizontal: false); break;
            case LayoutMode.Grid:                   ArrangeGrid(node, content);                      break;
            default:                                ArrangeOverlay(node, content);                   break;
        }

        // Absolute children never join the flex distribution; they hang off the content box.
        foreach (UiNode child in node.Children)
            if (child.Visible && child.Positioning == PositionMode.Absolute)
                Arrange(child, AnchorRect(child, node.ContentRect), node.ClipRect);

        // A child that is not visible is skipped by both passes above — FlowChildren
        // filters it out and the absolute loop tests the same flag — so nothing has
        // cleared its subtree. Leaving it dirty breaks the invariant InvalidateMeasure
        // relies on, and the symptom is a whole panel that never appears.
        foreach (UiNode child in node.Children)
            if (!child.Visible) child.ClearDirtyTree();

        node.ClearDirty();
    }

    private static void ArrangeLinear(UiNode node, RectangleF content, bool horizontal)
    {
        List<UiNode> flow = FlowChildren(node);
        if (flow.Count == 0) return;

        float gapMain  = horizontal ? node.Gap.X : node.Gap.Y;
        float gapCross = horizontal ? node.Gap.Y : node.Gap.X;
        float mainSize = horizontal ? content.Width : content.Height;
        bool  wraps    = node.Wrap || node.Layout == LayoutMode.Flow;

        float crossCursor = horizontal ? content.Y : content.X;

        foreach (List<UiNode> line in SplitLines(flow, mainSize, gapMain, horizontal, wraps))
        {
            float lineCross = 0f;
            foreach (UiNode c in line)
                lineCross = MathF.Max(lineCross, CrossOf(c, horizontal) + MarginCross(c, horizontal));

            // A single line that is allowed to stretch takes the whole cross extent, which is
            // what makes `align: stretch` on a column give every button the same width.
            if (!wraps)
                lineCross = horizontal ? content.Height : content.Width;

            PlaceLine(node, line, content, crossCursor, lineCross, gapMain, mainSize, horizontal);
            crossCursor += lineCross + gapCross;
        }
    }

    private static void PlaceLine(UiNode node, List<UiNode> line, RectangleF content,
                                  float crossStart, float lineCross, float gapMain,
                                  float mainSize, bool horizontal)
    {
        int count = line.Count;
        var main = new float[count];
        float used = gapMain * (count - 1);
        float totalGrow = 0f, totalShrinkWeight = 0f;

        for (int i = 0; i < count; i++)
        {
            main[i] = MainOf(line[i], horizontal) + MarginMain(line[i], horizontal);
            used += main[i];
            totalGrow += MathF.Max(0f, line[i].Grow);
            totalShrinkWeight += MathF.Max(0f, line[i].Shrink) * main[i];
        }

        float free = mainSize - used;

        if (free > Epsilon && totalGrow > 0f)
        {
            for (int i = 0; i < count; i++)
                main[i] += free * MathF.Max(0f, line[i].Grow) / totalGrow;
            free = 0f;
        }
        else if (free < -Epsilon && totalShrinkWeight > 0f)
        {
            float overflow = -free;
            for (int i = 0; i < count; i++)
            {
                float share = overflow * (MathF.Max(0f, line[i].Shrink) * main[i]) / totalShrinkWeight;
                float floor = MinMainOf(line[i], horizontal) + MarginMain(line[i], horizontal);
                main[i] = MathF.Max(floor, main[i] - share);
            }
            free = 0f;
        }

        // Whatever is still spare is distributed by the main-axis alignment.
        float cursor = horizontal ? content.X : content.Y;
        float between = gapMain;

        switch (node.MainAlign)
        {
            case AlignMode.Center: cursor += free * 0.5f; break;
            case AlignMode.End:    cursor += free;        break;
            case AlignMode.SpaceBetween when count > 1: between += free / (count - 1); break;
            case AlignMode.SpaceAround when count > 0:
                between += free / count;
                cursor  += free / count * 0.5f;
                break;
            case AlignMode.SpaceEvenly when count > 0:
                between += free / (count + 1);
                cursor  += free / (count + 1);
                break;
        }

        for (int i = 0; i < count; i++)
        {
            UiNode child = line[i];
            Vector4 m = child.Margin;

            float mainStart = cursor + (horizontal ? m.X : m.Y);
            float mainExtent = MathF.Max(0f, main[i] - MarginMain(child, horizontal));

            float available = MathF.Max(0f, lineCross - MarginCross(child, horizontal));
            bool stretch = node.CrossAlign == AlignMode.Stretch
                        || (horizontal ? child.HeightMode : child.WidthMode) == SizeMode.Stretch;

            float crossExtent = stretch ? available : MathF.Min(CrossOf(child, horizontal), available);
            float crossOffset = node.CrossAlign switch
            {
                AlignMode.Center => (available - crossExtent) * 0.5f,
                AlignMode.End    =>  available - crossExtent,
                _                => 0f,
            };
            float crossPos = crossStart + (horizontal ? m.Y : m.X) + crossOffset;

            RectangleF rect = horizontal
                ? new RectangleF(mainStart, crossPos, mainExtent, crossExtent)
                : new RectangleF(crossPos, mainStart, crossExtent, mainExtent);

            // Re-measure a child whose width was decided by the line, so its own wrapped
            // text and auto height are computed against the width it actually got.
            if (NeedsRemeasure(child, rect))
                Measure(child, new Vector2(rect.Width, rect.Height));

            Arrange(child, rect, node.ClipRect);
            cursor += main[i] + between;
        }
    }

    private static void ArrangeGrid(UiNode node, RectangleF content)
    {
        List<UiNode> flow = FlowChildren(node);
        if (flow.Count == 0) return;

        int columns = Math.Max(1, node.Columns);
        float cellW = node.CellSize.X > 0f
            ? node.CellSize.X
            : MathF.Max(0f, (content.Width - node.Gap.X * (columns - 1)) / columns);

        float y = content.Y;
        for (int start = 0; start < flow.Count; start += columns)
        {
            int end = Math.Min(start + columns, flow.Count);

            float rowH = node.CellSize.Y;
            if (rowH <= 0f)
                for (int i = start; i < end; i++) rowH = MathF.Max(rowH, flow[i].DesiredSize.Y);

            for (int i = start; i < end; i++)
            {
                UiNode child = flow[i];
                float x = content.X + (i - start) * (cellW + node.Gap.X);
                var rect = new RectangleF(x + child.Margin.X, y + child.Margin.Y,
                                          MathF.Max(0f, cellW - child.Margin.X - child.Margin.Z),
                                          MathF.Max(0f, rowH  - child.Margin.Y - child.Margin.W));

                if (NeedsRemeasure(child, rect)) Measure(child, new Vector2(rect.Width, rect.Height));
                Arrange(child, rect, node.ClipRect);
            }

            y += rowH + node.Gap.Y;
        }
    }

    private static void ArrangeOverlay(UiNode node, RectangleF content)
    {
        foreach (UiNode child in FlowChildren(node))
        {
            float w = child.WidthMode  == SizeMode.Stretch ? content.Width  : child.DesiredSize.X;
            float h = child.HeightMode == SizeMode.Stretch ? content.Height : child.DesiredSize.Y;

            var rect = new RectangleF(content.X + child.Margin.X, content.Y + child.Margin.Y,
                                      MathF.Max(0f, w - child.Margin.X - child.Margin.Z),
                                      MathF.Max(0f, h - child.Margin.Y - child.Margin.W));

            if (NeedsRemeasure(child, rect)) Measure(child, new Vector2(rect.Width, rect.Height));
            Arrange(child, rect, node.ClipRect);
        }
    }

    // =========================================================================
    // Anchoring
    // =========================================================================

    /// <summary>
    /// An absolutely positioned node's rectangle within its parent's content box.
    /// </summary>
    /// <remarks>
    /// With a named anchor, min, max and pivot are all the same fraction, and this reduces
    /// exactly to what the flat script UI has always done: the anchor is both the point the
    /// node hangs from and the corner of its own box that hangs there, so a bottom-right
    /// anchor with a negative offset sits that far in from the corner at any window size.
    /// When min and max differ the node stretches between them instead, which is the
    /// behaviour the old widget tree declared and never implemented.
    /// </remarks>
    public static RectangleF AnchorRect(UiNode child, RectangleF parentContent)
    {
        ResolveAnchorAxis(child.AnchorMin.X, child.AnchorMax.X, child.Pivot.X,
                          child.Offset.X, child.OffsetMax.X,
                          parentContent.X, parentContent.Width, child.DesiredSize.X,
                          out float x, out float w);

        ResolveAnchorAxis(child.AnchorMin.Y, child.AnchorMax.Y, child.Pivot.Y,
                          child.Offset.Y, child.OffsetMax.Y,
                          parentContent.Y, parentContent.Height, child.DesiredSize.Y,
                          out float y, out float h);

        return new RectangleF(x, y, w, h);
    }

    private static void ResolveAnchorAxis(float anchorMin, float anchorMax, float pivot,
                                          float offset, float offsetMax,
                                          float parentPos, float parentSize, float desired,
                                          out float pos, out float size)
    {
        float lead = parentPos + parentSize * anchorMin + offset;

        if (MathF.Abs(anchorMax - anchorMin) < Epsilon)
        {
            size = desired;
            pos  = lead - size * pivot;
            return;
        }

        float trail = parentPos + parentSize * anchorMax + offsetMax;
        pos  = lead;
        size = MathF.Max(0f, trail - lead);
    }

    // =========================================================================
    // Hit testing
    // =========================================================================

    /// <summary>
    /// The top-most pickable node under a canvas-space point, or null.
    /// </summary>
    /// <remarks>
    /// Children are searched last-painted first, so the node a player can see on top is
    /// the node they hit. A node that is not interactive still blocks what is behind it —
    /// a disabled button is not a hole — but a panel with nothing drawn in it does not,
    /// because an invisible layout row that ate clicks would be impossible to debug.
    /// </remarks>
    public static UiNode? HitTest(UiNode node, Vector2 point)
    {
        if (!node.Visible || node.Opacity <= 0.01f) return null;
        if (Clips(node) && !node.ClipRect.Contains(point)) return null;

        List<UiNode> painted = PaintOrder(node);
        for (int i = painted.Count - 1; i >= 0; i--)
            if (HitTest(painted[i], point) is { } hit) return hit;

        return Pickable(node) && node.Rect.Contains(point) ? node : null;
    }

    private static bool Pickable(UiNode node)
        => node.Kind switch
        {
            UiKind.Button or UiKind.Slider or UiKind.Toggle
                or UiKind.TextField or UiKind.Dropdown or UiKind.TabStrip => true,
            UiKind.Spacer => false,
            // Anything that paints something can be hit; a bare layout row cannot.
            _ => node.Background.HasValue || !string.IsNullOrEmpty(node.TexturePath),
        };

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>Children in paint order: by Order, then by the order they were added.</summary>
    public static List<UiNode> PaintOrder(UiNode node)
    {
        var ordered = new List<UiNode>(node.Children);
        // A stable sort, so equal Order keeps tree order and paint order never flickers.
        for (int i = 1; i < ordered.Count; i++)
        {
            UiNode item = ordered[i];
            int j = i - 1;
            while (j >= 0 && ordered[j].Order > item.Order) { ordered[j + 1] = ordered[j]; j--; }
            ordered[j + 1] = item;
        }
        return ordered;
    }

    private static List<UiNode> FlowChildren(UiNode node)
    {
        var flow = new List<UiNode>();
        foreach (UiNode child in PaintOrder(node))
            if (child.Visible && child.Positioning != PositionMode.Absolute) flow.Add(child);
        return flow;
    }

    private static IEnumerable<List<UiNode>> SplitLines(List<UiNode> flow, float mainSize,
                                                        float gap, bool horizontal, bool wraps)
    {
        if (!wraps || float.IsPositiveInfinity(mainSize))
        {
            yield return flow;
            yield break;
        }

        var line = new List<UiNode>();
        float used = 0f;

        foreach (UiNode child in flow)
        {
            float main = MainOf(child, horizontal) + MarginMain(child, horizontal);
            float next = line.Count == 0 ? main : used + gap + main;

            if (line.Count > 0 && next > mainSize + Epsilon)
            {
                yield return line;
                line = new List<UiNode> { child };
                used = main;
                continue;
            }

            line.Add(child);
            used = next;
        }

        if (line.Count > 0) yield return line;
    }

    /// <summary>
    /// True when arrange gave a child a different width than measure assumed, so its
    /// wrapped text and content-driven height have to be worked out again.
    /// </summary>
    private static bool NeedsRemeasure(UiNode child, RectangleF rect)
        => child.Children.Count > 0
        || (child.WrapText && MathF.Abs(rect.Width - child.DesiredSize.X) > Epsilon);

    private static bool Clips(UiNode node) => node.Clip || node.Scroll != ScrollMode.None;

    private static bool DrawsText(UiKind kind)
        => kind is UiKind.Label or UiKind.Button or UiKind.Bar or UiKind.TextField or UiKind.Dropdown;

    private static bool ScrollsHorizontally(ScrollMode m) => m is ScrollMode.Horizontal or ScrollMode.Both;
    private static bool ScrollsVertically(ScrollMode m)   => m is ScrollMode.Vertical or ScrollMode.Both;

    private static float MainOf(UiNode n, bool horizontal)  => horizontal ? n.DesiredSize.X : n.DesiredSize.Y;
    private static float CrossOf(UiNode n, bool horizontal) => horizontal ? n.DesiredSize.Y : n.DesiredSize.X;

    private static float MinMainOf(UiNode n, bool horizontal) => horizontal ? n.MinWidth : n.MinHeight;

    private static float MarginMain(UiNode n, bool horizontal)
        => horizontal ? n.Margin.X + n.Margin.Z : n.Margin.Y + n.Margin.W;

    private static float MarginCross(UiNode n, bool horizontal)
        => horizontal ? n.Margin.Y + n.Margin.W : n.Margin.X + n.Margin.Z;

    /// <summary>The size an axis already knows, or NaN when it depends on the content.</summary>
    private static float KnownSize(SizeMode mode, float value, float available) => mode switch
    {
        SizeMode.Fixed                                            => value,
        SizeMode.Percent when !float.IsPositiveInfinity(available) => available * value,
        SizeMode.Stretch when !float.IsPositiveInfinity(available) => available,
        _                                                         => float.NaN,
    };

    /// <summary>
    /// A Percent or Stretch child of an Auto parent measures as Auto. Without that rule the
    /// two would define each other and the pass would never terminate.
    /// </summary>
    private static float ResolveAxis(SizeMode mode, float value, float content, float available) => mode switch
    {
        SizeMode.Fixed                                            => value,
        SizeMode.Percent when !float.IsPositiveInfinity(available) => available * value,
        _                                                         => content,
    };

    private static float ClampAxis(float v, float min, float max)
    {
        if (min > 0f && v < min) v = min;
        if (max > 0f && v > max) v = max;
        return MathF.Max(0f, v);
    }

    /// <summary>Subtracts an inset while leaving an unbounded axis unbounded.</summary>
    private static float Shrink(float available, float by)
        => float.IsPositiveInfinity(available) ? available : MathF.Max(0f, available - by);
}
