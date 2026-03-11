using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.UI.Widgets;

/// <summary>Visual state of a <see cref="Button"/>.</summary>
public enum ButtonState { Normal, Hover, Pressed, Disabled }

/// <summary>
/// Clickable button with four texture states (normal / hover / pressed / disabled)
/// and a centred text label. Raises <see cref="Widget.OnClick"/> on release inside bounds.
/// </summary>
public class Button : Widget
{
    // -------------------------------------------------------------------------
    // Textures
    // -------------------------------------------------------------------------
    public Texture2D? NormalTexture   { get; set; }
    public Texture2D? HoverTexture    { get; set; }
    public Texture2D? PressedTexture  { get; set; }
    public Texture2D? DisabledTexture { get; set; }

    // -------------------------------------------------------------------------
    // Text
    // -------------------------------------------------------------------------
    public string Text      { get; set; } = "";
    public Color  TextColor { get; set; } = Color.White;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    public ButtonState State { get; private set; } = ButtonState.Normal;

    private bool _pressedInside; // tracks whether the press started inside this button

    // -------------------------------------------------------------------------
    // Input
    // -------------------------------------------------------------------------
    public override void HandleInput(Vector2 mousePos, bool mouseDown, bool mouseJustPressed)
    {
        if (!Visible) return;

        if (!Interactable || State == ButtonState.Disabled)
        {
            State = ButtonState.Disabled;
            // Still propagate to children
            for (int i = Children.Count - 1; i >= 0; i--)
                Children[i].HandleInput(mousePos, mouseDown, mouseJustPressed);
            return;
        }

        bool over = ContainsPoint(mousePos);

        if (mouseJustPressed && over)
            _pressedInside = true;

        if (!mouseDown)
        {
            // Mouse released
            if (_pressedInside && over)
                RaiseClick();
            _pressedInside = false;
        }

        if (_pressedInside && mouseDown)
            State = ButtonState.Pressed;
        else if (over)
            State = ButtonState.Hover;
        else
            State = ButtonState.Normal;

        // Hover events from base (using the IsHovered backing field isn't accessible, replicate)
        for (int i = Children.Count - 1; i >= 0; i--)
            Children[i].HandleInput(mousePos, mouseDown, mouseJustPressed);
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------
    public override void Draw(SpriteBatch sb, SpriteFont? font)
    {
        if (!Visible) return;

        var bounds = Bounds;

        Texture2D? tex = State switch
        {
            ButtonState.Hover    => HoverTexture    ?? NormalTexture,
            ButtonState.Pressed  => PressedTexture  ?? HoverTexture ?? NormalTexture,
            ButtonState.Disabled => DisabledTexture ?? NormalTexture,
            _                    => NormalTexture
        };

        if (tex != null)
        {
            sb.Draw(tex, bounds, EffectiveColor);
        }
        else
        {
            // Fallback: draw a coloured rectangle using a 1x1 white pixel if available
            // (users should supply textures; we draw nothing if none provided)
        }

        // Draw centred text
        if (font != null && !string.IsNullOrEmpty(Text))
        {
            Vector2 textSize = font.MeasureString(Text);
            Vector2 textPos  = new Vector2(
                bounds.X + (bounds.Width  - textSize.X) * 0.5f,
                bounds.Y + (bounds.Height - textSize.Y) * 0.5f);

            Color effectiveTextColor = State == ButtonState.Disabled
                ? TextColor * 0.5f
                : TextColor * Opacity;

            sb.DrawString(font, Text, textPos, effectiveTextColor);
        }

        // Draw children
        foreach (var child in Children)
            if (child.Visible)
                child.Draw(sb, font);
    }
}
