using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.UI.Layout;

// =============================================================================
// StackLayout
// =============================================================================

/// <summary>Axis along which <see cref="StackLayout"/> stacks children.</summary>
public enum StackOrientation { Horizontal, Vertical }

/// <summary>
/// Positions a widget's direct children sequentially along one axis,
/// separated by a configurable gap. Children's own Position values are
/// overwritten by the layout.
/// </summary>
public class StackLayout
{
    public StackOrientation Orientation { get; set; } = StackOrientation.Vertical;
    public float Gap                    { get; set; } = 4f;
    /// <summary>Pixel inset from the top-left of the parent bounds.</summary>
    public float Padding                { get; set; } = 0f;

    /// <summary>
    /// Repositions all direct children of <paramref name="parent"/> according
    /// to the current orientation and gap.
    /// </summary>
    public void Apply(Widget parent)
    {
        float cursor = Padding;

        foreach (var child in parent.Children)
        {
            if (!child.Visible) continue;

            if (Orientation == StackOrientation.Vertical)
            {
                child.Position = new Vector2(Padding, cursor);
                cursor += child.Size.Y + Gap;
            }
            else
            {
                child.Position = new Vector2(cursor, Padding);
                cursor += child.Size.X + Gap;
            }
        }
    }
}

// =============================================================================
// GridLayout
// =============================================================================

/// <summary>
/// Arranges a widget's direct children in a fixed-column grid.
/// Children are placed left-to-right, top-to-bottom.
/// </summary>
public class GridLayout
{
    public int   Columns   { get; set; } = 2;
    public float CellWidth  { get; set; } = 100f;
    public float CellHeight { get; set; } = 30f;
    public float GapX       { get; set; } = 4f;
    public float GapY       { get; set; } = 4f;
    /// <summary>Pixel inset from the top-left of the parent bounds.</summary>
    public float Padding    { get; set; } = 0f;

    /// <summary>
    /// Repositions all direct children of <paramref name="parent"/> in a grid.
    /// </summary>
    public void Apply(Widget parent)
    {
        int cols = Math.Max(1, Columns);
        int idx  = 0;

        foreach (var child in parent.Children)
        {
            int col = idx % cols;
            int row = idx / cols;

            child.Position = new Vector2(
                Padding + col * (CellWidth  + GapX),
                Padding + row * (CellHeight + GapY));

            // Optionally resize child to cell dimensions
            child.Size = new Vector2(CellWidth, CellHeight);

            idx++;
        }
    }
}

// =============================================================================
// AnchorLayout
// =============================================================================

/// <summary>
/// Resolves a single child widget's position and size from its anchor
/// (AnchorMin / AnchorMax / AnchorOffset) relative to a parent widget.
/// </summary>
public static class AnchorLayout
{
    /// <summary>
    /// Positions <paramref name="child"/> using its AnchorMin, AnchorMax, and
    /// AnchorOffset properties relative to <paramref name="parent"/>'s bounds.
    /// When AnchorMin != AnchorMax the child is *stretched* to fill the anchor
    /// region; otherwise it uses its own Size.
    /// </summary>
    public static void Apply(Widget child, Widget parent)
    {
        var pb = parent.Bounds;

        float anchorLeft   = pb.X + pb.Width  * child.AnchorMin.X;
        float anchorTop    = pb.Y + pb.Height * child.AnchorMin.Y;
        float anchorRight  = pb.X + pb.Width  * child.AnchorMax.X;
        float anchorBottom = pb.Y + pb.Height * child.AnchorMax.Y;

        bool stretchX = !MathF.Abs(child.AnchorMax.X - child.AnchorMin.X).Equals(0f)
                      && child.AnchorMax.X > child.AnchorMin.X;
        bool stretchY = !MathF.Abs(child.AnchorMax.Y - child.AnchorMin.Y).Equals(0f)
                      && child.AnchorMax.Y > child.AnchorMin.Y;

        float newX = anchorLeft  + child.AnchorOffset.X;
        float newY = anchorTop   + child.AnchorOffset.Y;
        float newW = stretchX ? (anchorRight  - anchorLeft) : child.Size.X;
        float newH = stretchY ? (anchorBottom - anchorTop)  : child.Size.Y;

        // We set Position relative to the parent's top-left, not screen space
        child.Position = new Vector2(newX - pb.X, newY - pb.Y);
        child.Size     = new Vector2(newW, newH);
    }

    /// <summary>
    /// Convenience: applies anchor layout to every child in <paramref name="parent"/>.
    /// </summary>
    public static void ApplyAll(Widget parent)
    {
        foreach (var child in parent.Children)
            Apply(child, parent);
    }
}
