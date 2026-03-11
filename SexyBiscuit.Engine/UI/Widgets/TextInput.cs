using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace SexyBiscuit.Engine.UI.Widgets;

/// <summary>
/// Single-line text input field. Tracks keyboard state each frame to accept
/// character input, supports a blinking cursor, placeholder text, and raises
/// events on text change and submit (Enter key).
/// </summary>
public class TextInput : Widget
{
    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------
    private string _text = "";

    public string Text
    {
        get => _text;
        set
        {
            string next = value ?? "";
            if (MaxLength > 0) next = next[..Math.Min(next.Length, MaxLength)];
            if (next != _text)
            {
                _text = next;
                _caretIndex = Math.Clamp(_caretIndex, 0, _text.Length);
                OnTextChanged?.Invoke(_text);
            }
        }
    }

    public string Placeholder { get; set; } = "";
    public int    MaxLength   { get; set; } = 256;
    public bool   IsFocused   { get; private set; }

    public Color BackgroundColor { get; set; } = new Color(30, 30, 30);
    public Color TextColor       { get; set; } = Color.White;
    public Color PlaceholderColor { get; set; } = Color.Gray;
    public Color BorderColor     { get; set; } = Color.Gray;
    public Color FocusBorderColor { get; set; } = Color.CornflowerBlue;

    public Texture2D? BackgroundTexture { get; set; }

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------
    public event Action<string>? OnTextChanged;
    public event Action<string>? OnSubmit;

    // -------------------------------------------------------------------------
    // Internal state
    // -------------------------------------------------------------------------
    private int        _caretIndex;
    private float      _cursorBlinkTimer;
    private bool       _cursorVisible = true;
    private const float CursorBlinkRate = 0.53f; // seconds per blink half-cycle

    private KeyboardState _prevKeyboard;

    // -------------------------------------------------------------------------
    // Input handling
    // -------------------------------------------------------------------------
    public override void HandleInput(Vector2 mousePos, bool mouseDown, bool mouseJustPressed)
    {
        if (!Visible || !Interactable) return;

        bool over = ContainsPoint(mousePos);

        if (mouseJustPressed)
        {
            bool wasFocused = IsFocused;
            IsFocused = over;

            if (over && !wasFocused)
            {
                // Place caret at end on fresh focus
                _caretIndex = _text.Length;
                _cursorVisible = true;
                _cursorBlinkTimer = 0f;
            }
        }

        for (int i = Children.Count - 1; i >= 0; i--)
            Children[i].HandleInput(mousePos, mouseDown, mouseJustPressed);
    }

    public override void Update(float dt)
    {
        base.Update(dt);

        if (!IsFocused) return;

        // Blink cursor
        _cursorBlinkTimer += dt;
        if (_cursorBlinkTimer >= CursorBlinkRate)
        {
            _cursorBlinkTimer -= CursorBlinkRate;
            _cursorVisible = !_cursorVisible;
        }

        // Keyboard input
        var keyboard = Keyboard.GetState();
        ProcessKeyboard(keyboard);
        _prevKeyboard = keyboard;
    }

    private void ProcessKeyboard(KeyboardState keyboard)
    {
        var pressedKeys = keyboard.GetPressedKeys();

        foreach (var key in pressedKeys)
        {
            // Only act on newly pressed keys this frame
            if (_prevKeyboard.IsKeyDown(key)) continue;

            // Reset blink on any key press
            _cursorVisible    = true;
            _cursorBlinkTimer = 0f;

            switch (key)
            {
                case Keys.Enter:
                    OnSubmit?.Invoke(_text);
                    IsFocused = false;
                    return;

                case Keys.Escape:
                    IsFocused = false;
                    return;

                case Keys.Back:
                    if (_caretIndex > 0)
                    {
                        _text = _text.Remove(_caretIndex - 1, 1);
                        _caretIndex--;
                        OnTextChanged?.Invoke(_text);
                    }
                    break;

                case Keys.Delete:
                    if (_caretIndex < _text.Length)
                    {
                        _text = _text.Remove(_caretIndex, 1);
                        OnTextChanged?.Invoke(_text);
                    }
                    break;

                case Keys.Left:
                    if (_caretIndex > 0) _caretIndex--;
                    break;

                case Keys.Right:
                    if (_caretIndex < _text.Length) _caretIndex++;
                    break;

                case Keys.Home:
                    _caretIndex = 0;
                    break;

                case Keys.End:
                    _caretIndex = _text.Length;
                    break;

                default:
                    char? ch = KeyToChar(key, keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift));
                    if (ch.HasValue && (_text.Length < MaxLength || MaxLength <= 0))
                    {
                        _text = _text.Insert(_caretIndex, ch.Value.ToString());
                        _caretIndex++;
                        OnTextChanged?.Invoke(_text);
                    }
                    break;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------
    public override void Draw(SpriteBatch sb, SpriteFont? font)
    {
        if (!Visible) return;

        var bounds = Bounds;
        Color bg = BackgroundColor * Opacity;

        if (BackgroundTexture != null)
            sb.Draw(BackgroundTexture, bounds, bg);

        if (font == null) return;

        string display = string.IsNullOrEmpty(_text) && !IsFocused ? Placeholder : _text;
        Color  textCol = string.IsNullOrEmpty(_text) && !IsFocused
            ? PlaceholderColor * Opacity
            : TextColor * Opacity;

        const int padding = 4;
        Vector2 textOrigin = new(bounds.X + padding, bounds.Y + (bounds.Height - font.LineSpacing) * 0.5f);

        sb.DrawString(font, display, textOrigin, textCol);

        // Blink cursor
        if (IsFocused && _cursorVisible)
        {
            string beforeCaret = _text[.._caretIndex];
            float  caretX      = textOrigin.X + font.MeasureString(beforeCaret).X;
            Vector2 cursorTop  = new(caretX, bounds.Y + padding);
            Vector2 cursorBot  = new(caretX, bounds.Bottom - padding);

            // DrawLine using two degenerate draws — use a 1px wide rectangle
            sb.Draw(
                GetPixelTexture(sb),
                new Rectangle((int)caretX, (int)cursorTop.Y, 1, (int)(cursorBot.Y - cursorTop.Y)),
                TextColor * Opacity);
        }

        foreach (var child in Children)
            if (child.Visible)
                child.Draw(sb, font);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a cached 1x1 white pixel texture stored as a tag on the GraphicsDevice.
    /// This avoids requiring an injected texture reference.
    /// </summary>
    private static Texture2D? _cachedPixel;

    private static Texture2D GetPixelTexture(SpriteBatch sb)
    {
        if (_cachedPixel == null || _cachedPixel.IsDisposed)
        {
            _cachedPixel = new Texture2D(sb.GraphicsDevice, 1, 1);
            _cachedPixel.SetData(new[] { Color.White });
        }
        return _cachedPixel;
    }

    private static char? KeyToChar(Keys key, bool shift)
    {
        // Letters
        if (key >= Keys.A && key <= Keys.Z)
        {
            char c = (char)('a' + (key - Keys.A));
            return shift ? char.ToUpper(c) : c;
        }

        // Digits (top row)
        if (key >= Keys.D0 && key <= Keys.D9)
        {
            char[] shifted = { ')', '!', '@', '#', '$', '%', '^', '&', '*', '(' };
            char[] normal  = { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };
            int idx = key - Keys.D0;
            return shift ? shifted[idx] : normal[idx];
        }

        // Numpad digits
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            return (char)('0' + (key - Keys.NumPad0));

        return key switch
        {
            Keys.Space        => ' ',
            Keys.OemPeriod    => shift ? '>' : '.',
            Keys.OemComma     => shift ? '<' : ',',
            Keys.OemMinus     => shift ? '_' : '-',
            Keys.OemPlus      => shift ? '+' : '=',
            Keys.OemSemicolon => shift ? ':' : ';',
            Keys.OemQuotes    => shift ? '"' : '\'',
            Keys.OemOpenBrackets => shift ? '{' : '[',
            Keys.OemCloseBrackets => shift ? '}' : ']',
            Keys.OemBackslash => shift ? '|' : '\\',
            Keys.OemQuestion  => shift ? '?' : '/',
            Keys.OemTilde     => shift ? '~' : '`',
            Keys.Multiply     => '*',
            Keys.Add          => '+',
            Keys.Subtract     => '-',
            Keys.Divide       => '/',
            Keys.Decimal      => '.',
            _ => null
        };
    }
}
