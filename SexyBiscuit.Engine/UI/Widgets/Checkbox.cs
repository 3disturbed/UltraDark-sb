using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.UI.Widgets;

/// <summary>
/// A boolean toggle with a label. Clicking anywhere on the widget flips it.
/// </summary>
/// <remarks>
/// Draws with solid fills when no textures are assigned, so it is visible the moment you
/// construct one. Assign <see cref="BoxTexture"/> and <see cref="CheckTexture"/> for a
/// themed look.
/// </remarks>
/// <example>
/// <code>
/// var fullscreen = new Checkbox { Text = "Fullscreen", IsChecked = config.Fullscreen };
/// fullscreen.CheckedChanged.Add(v =&gt; config.Fullscreen = v);
/// panel.AddChild(fullscreen);
/// </code>
/// </example>
public class Checkbox : Widget
{
    /// <summary>Label drawn to the right of the box.</summary>
    public string Text { get; set; } = "";

    /// <summary>Current state.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            CheckedChanged.Broadcast(value);
        }
    }
    private bool _isChecked;

    /// <summary>Raised whenever <see cref="IsChecked"/> changes, by click or by code.</summary>
    public SBEvent<bool> CheckedChanged { get; } = new();

    /// <summary>Side length of the square box in pixels.</summary>
    public int BoxSize { get; set; } = 20;

    /// <summary>Gap between the box and the label.</summary>
    public float LabelGap { get; set; } = 8f;

    public Color BoxColor      { get; set; } = new(40, 42, 50);
    public Color BorderColor   { get; set; } = new(120, 124, 138);
    public Color CheckColor    { get; set; } = new(120, 200, 255);
    public Color TextColor     { get; set; } = Color.White;
    public Color DisabledColor { get; set; } = new(90, 92, 100);

    /// <summary>Optional box background texture.</summary>
    public Texture2D? BoxTexture { get; set; }

    /// <summary>Optional tick mark texture, drawn inside the box when checked.</summary>
    public Texture2D? CheckTexture { get; set; }

    private bool _pressedInside;

    public Checkbox() => Size = new Vector2(180, 24);

    public override void HandleInput(Vector2 mousePos, bool mouseDown, bool mouseJustPressed)
    {
        if (!Visible) return;

        if (Interactable)
        {
            bool over = ContainsPoint(mousePos);

            if (mouseJustPressed && over) _pressedInside = true;

            // Commit on release inside, so dragging off cancels the click.
            if (!mouseDown)
            {
                if (_pressedInside && over) IsChecked = !IsChecked;
                _pressedInside = false;
            }
        }

        for (int i = Children.Count - 1; i >= 0; i--)
            Children[i].HandleInput(mousePos, mouseDown, mouseJustPressed);
    }

    public override void Draw(SpriteBatch sb, SpriteFont? font)
    {
        if (!Visible) return;

        var bounds = Bounds;
        var box = new Rectangle(
            bounds.X,
            bounds.Y + (bounds.Height - BoxSize) / 2,
            BoxSize, BoxSize);

        var border = (Interactable ? BorderColor : DisabledColor) * Opacity;

        if (BoxTexture != null) sb.Draw(BoxTexture, box, BoxColor * Opacity);
        else                    FillRect(sb, box, BoxColor * Opacity);

        StrokeRect(sb, box, border, 2);

        if (IsChecked)
        {
            var tick = new Rectangle(box.X + 4, box.Y + 4, box.Width - 8, box.Height - 8);
            if (CheckTexture != null) sb.Draw(CheckTexture, tick, CheckColor * Opacity);
            else                      FillRect(sb, tick, CheckColor * Opacity);
        }

        var labelRect = new Rectangle(
            (int)(box.Right + LabelGap), bounds.Y,
            (int)(bounds.Width - BoxSize - LabelGap), bounds.Height);

        DrawTextInRect(sb, font, Text, labelRect,
            (Interactable ? TextColor : DisabledColor) * Opacity, padding: 0f);

        foreach (var child in Children)
            if (child.Visible) child.Draw(sb, font);
    }
}

/// <summary>
/// A checkbox that belongs to a <see cref="RadioGroup"/>: checking one unchecks its
/// siblings, and it cannot be unchecked by clicking it again.
/// </summary>
public class Toggle : Checkbox
{
    /// <summary>The group this toggle belongs to. Set by <see cref="RadioGroup.Add"/>.</summary>
    public RadioGroup? Group { get; internal set; }

    /// <summary>Value this toggle represents when selected.</summary>
    public object? Value { get; set; }

    public override void HandleInput(Vector2 mousePos, bool mouseDown, bool mouseJustPressed)
    {
        if (Group == null)
        {
            base.HandleInput(mousePos, mouseDown, mouseJustPressed);
            return;
        }

        // An exclusive option cannot be turned off by clicking it — only by
        // another option in the group being chosen.
        if (Visible && Interactable && mouseJustPressed && ContainsPoint(mousePos))
            Group.Select(this);

        for (int i = Children.Count - 1; i >= 0; i--)
            Children[i].HandleInput(mousePos, mouseDown, mouseJustPressed);
    }
}

/// <summary>
/// Makes a set of <see cref="Toggle"/> widgets mutually exclusive — exactly one is
/// selected at a time.
/// </summary>
/// <remarks>
/// The group is not a widget and does not draw anything; it owns the selection rule while
/// the toggles stay wherever you put them in the widget tree. That keeps layout and
/// grouping independent — a radio group can span two columns.
/// </remarks>
/// <example>
/// <code>
/// var difficulty = new RadioGroup();
/// difficulty.Add(new Toggle { Text = "Easy",   Value = Difficulty.Easy   });
/// difficulty.Add(new Toggle { Text = "Normal", Value = Difficulty.Normal });
/// difficulty.Add(new Toggle { Text = "Hard",   Value = Difficulty.Hard   });
///
/// difficulty.SelectionChanged.Add(t =&gt; settings.Difficulty = (Difficulty)t.Value!);
/// foreach (var t in difficulty.Options) panel.AddChild(t);
/// </code>
/// </example>
public class RadioGroup
{
    private readonly List<Toggle> _options = new();

    /// <summary>The toggles in this group, in the order they were added.</summary>
    public IReadOnlyList<Toggle> Options => _options;

    /// <summary>The currently selected toggle, or null when nothing is selected.</summary>
    public Toggle? Selected { get; private set; }

    /// <summary>The selected toggle's <see cref="Toggle.Value"/>, or null.</summary>
    public object? SelectedValue => Selected?.Value;

    /// <summary>Raised when the selection changes, with the newly selected toggle.</summary>
    public SBEvent<Toggle> SelectionChanged { get; } = new();

    /// <summary>Adds a toggle to the group. The first one added becomes the selection.</summary>
    public Toggle Add(Toggle toggle)
    {
        toggle.Group = this;
        _options.Add(toggle);

        if (Selected == null) Select(toggle);
        return toggle;
    }

    /// <summary>Removes a toggle. Clears the selection if it was the selected one.</summary>
    public void Remove(Toggle toggle)
    {
        if (!_options.Remove(toggle)) return;
        toggle.Group = null;
        if (ReferenceEquals(Selected, toggle)) Selected = null;
    }

    /// <summary>Selects a toggle, unchecking every other option in the group.</summary>
    public void Select(Toggle toggle)
    {
        if (!_options.Contains(toggle)) return;
        if (ReferenceEquals(Selected, toggle)) return;

        foreach (var option in _options)
            option.IsChecked = ReferenceEquals(option, toggle);

        Selected = toggle;
        SelectionChanged.Broadcast(toggle);
    }

    /// <summary>Selects the first option whose <see cref="Toggle.Value"/> equals <paramref name="value"/>.</summary>
    public bool SelectByValue(object? value)
    {
        var match = _options.FirstOrDefault(o => Equals(o.Value, value));
        if (match == null) return false;
        Select(match);
        return true;
    }
}
