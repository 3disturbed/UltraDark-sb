using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// Abstract base for all UI widgets. Handles layout anchoring, hit-testing,
/// input propagation, and a child hierarchy.
/// </summary>
public abstract class Widget
{
    // -------------------------------------------------------------------------
    // Identity / Visibility
    // -------------------------------------------------------------------------
    public string Name         { get; set; } = "";
    public bool   Visible      { get; set; } = true;
    public bool   Interactable { get; set; } = true;

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------
    /// <summary>Position relative to parent widget origin (or screen origin if root).</summary>
    public Vector2 Position     { get; set; }
    public Vector2 Size         { get; set; } = new(100, 30);
    public Color   Tint         { get; set; } = Color.White;
    public float   Opacity      { get; set; } = 1f;

    /// <summary>Normalised anchor corner within parent rect (0–1).</summary>
    public Vector2 AnchorMin    { get; set; } = new(0, 0);
    public Vector2 AnchorMax    { get; set; } = new(0, 0);
    /// <summary>Additional pixel offset applied after the anchor point is resolved.</summary>
    public Vector2 AnchorOffset { get; set; }

    // -------------------------------------------------------------------------
    // Hierarchy
    // -------------------------------------------------------------------------
    public Canvas? Canvas   { get; internal set; }
    public Widget? Parent   { get; internal set; }
    public List<Widget> Children { get; } = new();

    // -------------------------------------------------------------------------
    // Computed bounds
    // -------------------------------------------------------------------------
    /// <summary>
    /// Screen-space rectangle resolved from Position, Size, anchor relative to parent.
    /// When there is no parent the position is treated as screen-space directly.
    /// </summary>
    public Rectangle Bounds
    {
        get
        {
            Vector2 origin;
            if (Parent != null)
            {
                var pb = Parent.Bounds;
                // Resolve the anchor pivot inside the parent rectangle
                float anchorX = pb.X + pb.Width  * AnchorMin.X;
                float anchorY = pb.Y + pb.Height * AnchorMin.Y;
                origin = new Vector2(anchorX, anchorY) + AnchorOffset;
            }
            else
            {
                origin = AnchorOffset;
            }

            return new Rectangle(
                (int)(origin.X + Position.X),
                (int)(origin.Y + Position.Y),
                (int)Size.X,
                (int)Size.Y);
        }
    }

    // -------------------------------------------------------------------------
    // Input events
    // -------------------------------------------------------------------------
    public event Action? OnClick;
    public event Action? OnHoverEnter;
    public event Action? OnHoverExit;

    protected void RaiseClick() => OnClick?.Invoke();

    protected bool IsHovered { get; private set; }

    // -------------------------------------------------------------------------
    // Abstract / virtual lifecycle
    // -------------------------------------------------------------------------
    public abstract void Draw(SpriteBatch sb, SpriteFont? font);

    public virtual void Update(float dt)
    {
        foreach (var child in Children)
            child.Update(dt);
    }

    // -------------------------------------------------------------------------
    // Child management
    // -------------------------------------------------------------------------
    public void AddChild(Widget child)
    {
        if (child.Parent != null)
            child.Parent.RemoveChild(child);

        child.Parent = this;
        child.Canvas = Canvas;
        Children.Add(child);
    }

    public void RemoveChild(Widget child)
    {
        if (Children.Remove(child))
        {
            child.Parent = null;
            child.Canvas = null;
        }
    }

    // -------------------------------------------------------------------------
    // Hit testing
    // -------------------------------------------------------------------------
    public bool ContainsPoint(Vector2 screenPoint)
        => Bounds.Contains((int)screenPoint.X, (int)screenPoint.Y);

    // -------------------------------------------------------------------------
    // Input handling
    // -------------------------------------------------------------------------
    /// <summary>
    /// Default input handling: hover detection and click raising.
    /// Subclasses override to add drag, press-state, etc.
    /// </summary>
    public virtual void HandleInput(Vector2 mousePos, bool mouseDown, bool mouseJustPressed)
    {
        if (!Visible || !Interactable) return;

        bool over = ContainsPoint(mousePos);

        if (over && !IsHovered)
        {
            IsHovered = true;
            OnHoverEnter?.Invoke();
        }
        else if (!over && IsHovered)
        {
            IsHovered = false;
            OnHoverExit?.Invoke();
        }

        if (over && mouseJustPressed)
            RaiseClick();

        // Propagate to children (deepest first so top-most child gets priority)
        for (int i = Children.Count - 1; i >= 0; i--)
            Children[i].HandleInput(mousePos, mouseDown, mouseJustPressed);
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------
    /// <summary>Effective tint multiplied by opacity alpha.</summary>
    protected Color EffectiveColor => Tint * Opacity;

    public override string ToString() => $"{GetType().Name}[{Name}]";
}
