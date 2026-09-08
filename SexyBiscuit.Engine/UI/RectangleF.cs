using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// A rectangle with float edges. XNA's Rectangle is integral and UI is not: a
/// three-way split of a 100px row is 33.333px per column, and rounding each one as
/// it is produced loses a pixel off the end of every such row.
/// </summary>
public readonly record struct RectangleF(float X, float Y, float Width, float Height)
{
    public static readonly RectangleF Empty = new(0f, 0f, 0f, 0f);

    public float Left   => X;
    public float Top    => Y;
    public float Right  => X + Width;
    public float Bottom => Y + Height;

    public Vector2 Position => new(X, Y);
    public Vector2 Size     => new(Width, Height);
    public Vector2 Centre   => new(X + Width * 0.5f, Y + Height * 0.5f);

    /// <summary>True when the rectangle encloses no area, so nothing can be drawn in or hit inside it.</summary>
    public bool IsEmpty => Width <= 0f || Height <= 0f;

    /// <summary>
    /// Half-open containment: the left and top edges are inside, the right and bottom
    /// are not. Two rectangles that share an edge therefore never both claim a point,
    /// which is what stops a pointer on a seam hitting two adjacent buttons at once.
    /// </summary>
    public bool Contains(Vector2 p)
        => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public bool Contains(float px, float py)
        => px >= X && px < Right && py >= Y && py < Bottom;

    /// <summary>The overlap of two rectangles, or an empty rectangle when they do not meet.</summary>
    public RectangleF Intersect(RectangleF other)
    {
        float x0 = MathF.Max(X, other.X);
        float y0 = MathF.Max(Y, other.Y);
        float x1 = MathF.Min(Right, other.Right);
        float y1 = MathF.Min(Bottom, other.Bottom);
        return x1 <= x0 || y1 <= y0 ? Empty : new RectangleF(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Shrinks by an inset per edge (left, top, right, bottom), never past zero.</summary>
    public RectangleF Deflate(Vector4 inset)
    {
        float w = MathF.Max(0f, Width  - inset.X - inset.Z);
        float h = MathF.Max(0f, Height - inset.Y - inset.W);
        return new RectangleF(X + inset.X, Y + inset.Y, w, h);
    }

    public RectangleF Offset(Vector2 by) => new(X + by.X, Y + by.Y, Width, Height);

    /// <summary>
    /// The integral rectangle covering this one, for a scissor test. Rounded outwards
    /// so a clip never shaves a pixel off the content it is meant to contain.
    /// </summary>
    public Rectangle ToScissor()
    {
        int x0 = (int)MathF.Floor(X);
        int y0 = (int)MathF.Floor(Y);
        int x1 = (int)MathF.Ceiling(Right);
        int y1 = (int)MathF.Ceiling(Bottom);
        return new Rectangle(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    public override string ToString() => $"({X}, {Y}, {Width}x{Height})";
}
