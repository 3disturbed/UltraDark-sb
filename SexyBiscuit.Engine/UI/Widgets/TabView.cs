using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.UI.Widgets;

/// <summary>One tab: a title and the widget shown when it is active.</summary>
public sealed class TabPage
{
    /// <summary>Text drawn on the tab button.</summary>
    public string Title { get; set; } = "Tab";

    /// <summary>The widget shown in the content area while this tab is selected.</summary>
    public Widget? Content { get; set; }

    /// <summary>Greys the tab out and blocks selection.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A tab bar with a content area below it. Only the selected page's content is drawn
/// or receives input.
/// </summary>
/// <remarks>
/// Pages are held by the <see cref="TabView"/> rather than being added as children,
/// because a child would still be drawn and hit-tested by the base <see cref="Widget"/>
/// traversal even when its tab is not selected. The view positions and sizes the active
/// page's content itself, so pages do not need to know they are in a tab view.
/// </remarks>
/// <example>
/// <code>
/// var settings = new TabView { Size = new Vector2(480, 320) };
/// settings.AddPage("Video", videoPanel);
/// settings.AddPage("Audio", audioPanel);
/// settings.AddPage("Controls", controlsPanel);
/// settings.SelectedChanged.Add(i =&gt; Console.WriteLine($"Tab {i}"));
/// </code>
/// </example>
public class TabView : Widget
{
    /// <summary>The pages, in tab-bar order.</summary>
    public List<TabPage> Pages { get; } = new();

    /// <summary>Index of the visible page, or -1 when there are none.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int clamped = Pages.Count == 0 ? -1 : Math.Clamp(value, 0, Pages.Count - 1);
            if (clamped == _selectedIndex) return;
            _selectedIndex = clamped;
            SelectedChanged.Broadcast(clamped);
        }
    }
    private int _selectedIndex = -1;

    /// <summary>The visible page, or null.</summary>
    public TabPage? SelectedPage
        => _selectedIndex >= 0 && _selectedIndex < Pages.Count ? Pages[_selectedIndex] : null;

    /// <summary>Raised when the selected tab changes, with the new index.</summary>
    public SBEvent<int> SelectedChanged { get; } = new();

    /// <summary>Height of the tab bar in pixels.</summary>
    public int TabBarHeight { get; set; } = 30;

    /// <summary>Minimum width of a tab button. Tabs grow to fit their title.</summary>
    public int MinTabWidth { get; set; } = 80;

    /// <summary>Horizontal padding inside each tab button.</summary>
    public int TabPadding { get; set; } = 14;

    public Color TabColor         { get; set; } = new(34, 36, 44);
    public Color ActiveTabColor   { get; set; } = new(52, 56, 68);
    public Color HoverTabColor    { get; set; } = new(44, 48, 58);
    public Color ContentColor     { get; set; } = new(52, 56, 68);
    public Color BorderColor      { get; set; } = new(90, 94, 108);
    public Color TextColor        { get; set; } = Color.White;
    public Color DisabledTextColor { get; set; } = new(110, 112, 120);
    public Color AccentColor      { get; set; } = new(120, 200, 255);

    private int _hoverIndex = -1;

    public TabView() => Size = new Vector2(480, 320);

    /// <summary>Adds a page and selects it when it is the first.</summary>
    public TabPage AddPage(string title, Widget? content = null)
    {
        var page = new TabPage { Title = title, Content = content };
        Pages.Add(page);
        if (_selectedIndex < 0) SelectedIndex = 0;
        return page;
    }

    /// <summary>Removes a page, keeping the selection in range.</summary>
    public void RemovePage(TabPage page)
    {
        int index = Pages.IndexOf(page);
        if (index < 0) return;

        Pages.RemoveAt(index);
        _selectedIndex = Pages.Count == 0 ? -1 : Math.Clamp(_selectedIndex, 0, Pages.Count - 1);
    }

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------

    /// <summary>The rectangle the active page's content occupies.</summary>
    public Rectangle ContentBounds
    {
        get
        {
            var b = Bounds;
            return new Rectangle(b.X, b.Y + TabBarHeight, b.Width, b.Height - TabBarHeight);
        }
    }

    private Rectangle GetTabBounds(int index, SpriteFont? font)
    {
        var b = Bounds;
        int x = b.X;

        for (int i = 0; i < index; i++)
            x += MeasureTabWidth(Pages[i], font);

        return new Rectangle(x, b.Y, MeasureTabWidth(Pages[index], font), TabBarHeight);
    }

    private int MeasureTabWidth(TabPage page, SpriteFont? font)
    {
        int textWidth = font != null ? (int)font.MeasureString(page.Title).X : page.Title.Length * 8;
        return Math.Max(MinTabWidth, textWidth + TabPadding * 2);
    }

    // -------------------------------------------------------------------------
    // Input
    // -------------------------------------------------------------------------

    public override void HandleInput(Vector2 mousePos, bool mouseDown, bool mouseJustPressed)
    {
        if (!Visible || !Interactable) return;

        var font = Canvas?.Font;
        _hoverIndex = -1;

        for (int i = 0; i < Pages.Count; i++)
        {
            var tab = GetTabBounds(i, font);
            if (!tab.Contains((int)mousePos.X, (int)mousePos.Y)) continue;

            _hoverIndex = i;
            if (mouseJustPressed && Pages[i].Enabled) SelectedIndex = i;
            return;   // a click on the bar never reaches the content below it
        }

        // Only the active page sees input; the others are not on screen.
        SelectedPage?.Content?.HandleInput(mousePos, mouseDown, mouseJustPressed);
    }

    public override void Update(float dt)
    {
        SelectedPage?.Content?.Update(dt);
        base.Update(dt);
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------

    public override void Draw(SpriteBatch sb, SpriteFont? font)
    {
        if (!Visible) return;

        var content = ContentBounds;
        FillRect(sb, content, ContentColor * Opacity);
        StrokeRect(sb, content, BorderColor * Opacity, 1);

        for (int i = 0; i < Pages.Count; i++)
        {
            var page = Pages[i];
            var tab  = GetTabBounds(i, font);

            var fill = i == _selectedIndex ? ActiveTabColor
                     : i == _hoverIndex    ? HoverTabColor
                     : TabColor;

            FillRect(sb, tab, fill * Opacity);

            // A bar along the top edge marks the active tab without moving anything,
            // so the bar does not jitter as the selection changes.
            if (i == _selectedIndex)
                FillRect(sb, new Rectangle(tab.X, tab.Y, tab.Width, 2), AccentColor * Opacity);

            var textColor = page.Enabled ? TextColor : DisabledTextColor;
            DrawTextInRect(sb, font, page.Title, tab, textColor * Opacity, TabPadding);
        }

        var active = SelectedPage?.Content;
        if (active != null)
        {
            // Place the page inside the content area, in the view's own coordinate space.
            active.Parent   = this;
            active.Position = new Vector2(0, TabBarHeight);
            active.Size     = new Vector2(content.Width, content.Height);
            active.Draw(sb, font);
        }
    }
}
