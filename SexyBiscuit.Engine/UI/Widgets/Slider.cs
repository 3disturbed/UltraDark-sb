using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.UI.Widgets;

/// <summary>
/// Draggable slider. Supports horizontal and vertical orientation,
/// optional step snapping, and custom track/thumb textures.
/// </summary>
public class Slider : Widget
{
    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------
    private float _value = 0f;

    /// <summary>Current value, always clamped to [MinValue, MaxValue].</summary>
    public float Value
    {
        get => _value;
        set
        {
            float clamped = Math.Clamp(value, MinValue, MaxValue);
            clamped = Snap(clamped);
            if (MathF.Abs(clamped - _value) > float.Epsilon)
            {
                _value = clamped;
                OnValueChanged?.Invoke(_value);
            }
        }
    }

    public float MinValue  { get; set; } = 0f;
    public float MaxValue  { get; set; } = 1f;
    /// <summary>Snap increment. 0 = continuous.</summary>
    public float Step      { get; set; } = 0f;
    public bool  Horizontal { get; set; } = true;

    public Texture2D? TrackTexture { get; set; }
    public Texture2D? ThumbTexture { get; set; }

    /// <summary>Visual size of the thumb handle in pixels.</summary>
    public Vector2 ThumbSize { get; set; } = new(16, 16);

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------
    public event Action<float>? OnValueChanged;

    // -------------------------------------------------------------------------
    // Internal drag state
    // -------------------------------------------------------------------------
    private bool _dragging;

    // -------------------------------------------------------------------------
    // Input
    // -------------------------------------------------------------------------
    public override void HandleInput(Vector2 mousePos, bool mouseDown, bool mouseJustPressed)
    {
        if (!Visible || !Interactable) return;

        var bounds = Bounds;

        if (mouseJustPressed && bounds.Contains((int)mousePos.X, (int)mousePos.Y))
            _dragging = true;

        if (!mouseDown)
            _dragging = false;

        if (_dragging)
        {
            float normalised;
            if (Horizontal)
            {
                float trackStart = bounds.X + ThumbSize.X * 0.5f;
                float trackEnd   = bounds.Right - ThumbSize.X * 0.5f;
                float trackLen   = trackEnd - trackStart;
                normalised = trackLen > 0
                    ? Math.Clamp((mousePos.X - trackStart) / trackLen, 0f, 1f)
                    : 0f;
            }
            else
            {
                float trackStart = bounds.Y + ThumbSize.Y * 0.5f;
                float trackEnd   = bounds.Bottom - ThumbSize.Y * 0.5f;
                float trackLen   = trackEnd - trackStart;
                normalised = trackLen > 0
                    ? Math.Clamp((mousePos.Y - trackStart) / trackLen, 0f, 1f)
                    : 0f;
            }

            Value = MinValue + normalised * (MaxValue - MinValue);
        }

        for (int i = Children.Count - 1; i >= 0; i--)
            Children[i].HandleInput(mousePos, mouseDown, mouseJustPressed);
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------
    public override void Draw(SpriteBatch sb, SpriteFont? font)
    {
        if (!Visible) return;

        var  bounds = Bounds;
        var  color  = EffectiveColor;
        float t     = (MaxValue > MinValue) ? (_value - MinValue) / (MaxValue - MinValue) : 0f;

        // Track
        if (TrackTexture != null)
            sb.Draw(TrackTexture, bounds, color);

        // Thumb
        Rectangle thumbRect;
        if (Horizontal)
        {
            float trackStart = bounds.X + ThumbSize.X * 0.5f;
            float trackEnd   = bounds.Right - ThumbSize.X * 0.5f;
            float thumbX     = trackStart + t * (trackEnd - trackStart) - ThumbSize.X * 0.5f;
            float thumbY     = bounds.Y + (bounds.Height - ThumbSize.Y) * 0.5f;
            thumbRect = new Rectangle((int)thumbX, (int)thumbY, (int)ThumbSize.X, (int)ThumbSize.Y);
        }
        else
        {
            float trackStart = bounds.Y + ThumbSize.Y * 0.5f;
            float trackEnd   = bounds.Bottom - ThumbSize.Y * 0.5f;
            float thumbY     = trackStart + t * (trackEnd - trackStart) - ThumbSize.Y * 0.5f;
            float thumbX     = bounds.X + (bounds.Width - ThumbSize.X) * 0.5f;
            thumbRect = new Rectangle((int)thumbX, (int)thumbY, (int)ThumbSize.X, (int)ThumbSize.Y);
        }

        if (ThumbTexture != null)
            sb.Draw(ThumbTexture, thumbRect, color);

        foreach (var child in Children)
            if (child.Visible)
                child.Draw(sb, font);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private float Snap(float v)
    {
        if (Step <= 0f) return v;
        return MinValue + MathF.Round((v - MinValue) / Step) * Step;
    }
}
