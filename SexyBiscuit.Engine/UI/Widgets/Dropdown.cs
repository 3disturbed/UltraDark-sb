using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.UI.Widgets;

/// <summary>
/// A closed list that expands to show its options on click.
/// </summary>
/// <remarks>
/// The expanded list is drawn and hit-tested by the dropdown itself rather than being a
/// child widget, because it has to paint over whatever sits below it in the layout. A
/// child would be clipped or drawn under its siblings depending on tree order.
/// </remarks>
/// <example>
/// <code>
/// var quality = new Dropdown();
/// quality.SetOptions("Low", "Medium", "High", "Ultra");
/// quality.SelectedIndex = 2;
/// quality.SelectionChanged.Add(i =&gt; settings.Quality = i);
/// </code>
/// </example>
public class Dropdown : Widget
{
    /// <summary>The available options, in display order.</summary>
    public List<string> Options { get; } = new();

    /// <summary>Index of the chosen option, or -1 when nothing is chosen.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int clamped = Options.Count == 0 ? -1 : Math.Clamp(value, 0, Options.Count - 1);
            if (clamped == _selectedIndex) return;
            _selectedIndex = clamped;
            SelectionChanged.Broadcast(clamped);
        }
    }
    private int _selectedIndex = -1;

    /// <summary>The chosen option's text, or <see cref="Placeholder"/> when nothing is chosen.</summary>
    public string SelectedText
        => _selectedIndex >= 0 && _selectedIndex < Options.Count ? Options[_selectedIndex] : Placeholder;

    /// <summary>Shown when no option is chosen.</summary>
    public string Placeholder { get; set; } = "Select…";

    /// <summary>True while the option list is showing.</summary>
    public bool IsExpanded { get; private set; }

    /// <summary>Height of each row in the expanded list.</summary>
    public int RowHeight { get; set; } = 26;

    /// <summary>Rows shown before the list starts scrolling.</summary>
    public int MaxVisibleRows { get; set; } = 8;

    /// <summary>Raised when the selection changes, with the new index.</summary>
    public SBEvent<int> SelectionChanged { get; } = new();

    public Color BackgroundColor { get; set; } = new(40, 42, 50);
    public Color BorderColor     { get; set; } = new(120, 124, 138);
    public Color TextColor       { get; set; } = Color.White;
    public Color HighlightColor  { get; set; } = new(70, 110, 160);
    public Color ListColor       { get; set; } = new(28, 30, 36);

    private int _scrollOffset;
    private int _hoverIndex = -1;

    public Dropdown() => Size = new Vector2(200, 28);

    /// <summary>Replaces the option list and resets the selection to the first entry.</summary>
    public void SetOptions(params string[] options)
    {
        Options.Clear();
        Options.AddRange(options);
        _selectedIndex = Options.Count > 0 ? 0 : -1;
        _scrollOffset  = 0;
    }

    /// <summary>Closes the list without changing the selection.</summary>
    public void Collapse() => IsExpanded = false;

    // -------------------------------------------------------------------------
    // Input
    // -------------------------------------------------------------------------

    public override void HandleInput(Vector2 mousePos, bool mouseDown, bool mouseJustPressed)
    {
        if (!Visible || !Interactable) return;

        var bounds = Bounds;

        if (IsExpanded)
        {
            var list = GetListBounds(bounds);
            _hoverIndex = -1;

            if (list.Contains((int)mousePos.X, (int)mousePos.Y))
            {
                int row = ((int)mousePos.Y - list.Y) / Math.Max(1, RowHeight);
                _hoverIndex = Math.Clamp(row + _scrollOffset, 0, Math.Max(0, Options.Count - 1));

                if (mouseJustPressed)
                {
                    SelectedIndex = _hoverIndex;
                    IsExpanded = false;
                }
                return;
            }

            // A click anywhere else dismisses the list rather than falling through
            // to whatever is underneath it.
            if (mouseJustPressed)
            {
                IsExpanded = false;
                if (bounds.Contains((int)mousePos.X, (int)mousePos.Y)) return;
            }
            return;
        }

        if (mouseJustPressed && bounds.Contains((int)mousePos.X, (int)mousePos.Y))
        {
            IsExpanded = true;
            ScrollToSelected();
        }
    }

    private void ScrollToSelected()
    {
        if (_selectedIndex < 0) { _scrollOffset = 0; return; }

        int visible = Math.Min(MaxVisibleRows, Options.Count);
        _scrollOffset = Math.Clamp(_selectedIndex - visible / 2, 0, Math.Max(0, Options.Count - visible));
    }

    private Rectangle GetListBounds(Rectangle bounds)
    {
        int visible = Math.Min(MaxVisibleRows, Options.Count);
        return new Rectangle(bounds.X, bounds.Bottom, bounds.Width, visible * RowHeight);
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------

    public override void Draw(SpriteBatch sb, SpriteFont? font)
    {
        if (!Visible) return;

        var bounds = Bounds;

        FillRect(sb, bounds, BackgroundColor * Opacity);
        StrokeRect(sb, bounds, BorderColor * Opacity, 2);

        var textRect = new Rectangle(bounds.X, bounds.Y, bounds.Width - 24, bounds.Height);
        DrawTextInRect(sb, font, SelectedText, textRect, TextColor * Opacity, padding: 8f);

        DrawChevron(sb, bounds);

        if (IsExpanded) DrawList(sb, font, bounds);
    }

    /// <summary>Draws the open/closed indicator as a small stack of narrowing bars.</summary>
    private void DrawChevron(SpriteBatch sb, Rectangle bounds)
    {
        int cx = bounds.Right - 14;
        int cy = bounds.Y + bounds.Height / 2;
        var color = TextColor * Opacity;

        for (int i = 0; i < 4; i++)
        {
            int width = 8 - i * 2;
            int y = IsExpanded ? cy + 2 - i : cy - 2 + i;
            FillRect(sb, new Rectangle(cx - width / 2, y, width, 1), color);
        }
    }

    private void DrawList(SpriteBatch sb, SpriteFont? font, Rectangle bounds)
    {
        var list = GetListBounds(bounds);

        FillRect(sb, list, ListColor * Opacity);
        StrokeRect(sb, list, BorderColor * Opacity, 2);

        int visible = Math.Min(MaxVisibleRows, Options.Count);
        for (int i = 0; i < visible; i++)
        {
            int index = i + _scrollOffset;
            if (index >= Options.Count) break;

            var row = new Rectangle(list.X, list.Y + i * RowHeight, list.Width, RowHeight);

            if (index == _hoverIndex)         FillRect(sb, row, HighlightColor * Opacity);
            else if (index == _selectedIndex) FillRect(sb, row, (HighlightColor * 0.4f) * Opacity);

            DrawTextInRect(sb, font, Options[index], row, TextColor * Opacity, padding: 8f);
        }
    }
}
