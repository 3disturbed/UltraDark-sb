using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

/// <summary>
/// Renders a 2D sprite to the screen. Supports standard sprite drawing, spritesheet frame
/// selection, and 9-patch (sliced) rendering for UI-style scalable sprites.
/// </summary>
public class SpriteRenderer : Component
{
    // -------------------------------------------------------------------------
    // Core sprite properties
    // -------------------------------------------------------------------------

    /// <summary>The texture to render. If null, nothing is drawn.</summary>
    public Texture2D? Texture { get; set; }

    /// <summary>
    /// Asset path of the texture, relative to the project root. This is what a scene file
    /// stores: a <see cref="Texture2D"/> cannot be serialised, so the path is kept and loaded
    /// through the running host's asset manager — immediately when one exists, otherwise on
    /// <see cref="Start"/>.
    /// </summary>
    public string? TexturePath
    {
        get => _texturePath;
        set
        {
            _texturePath = value;
            TryResolveTexture();
        }
    }
    private string? _texturePath;

    public override void Start()
    {
        if (Texture == null) TryResolveTexture();
    }

    private void TryResolveTexture()
    {
        if (string.IsNullOrWhiteSpace(_texturePath)) return;

        var assets = Assets.AssetManager.Current;
        if (assets == null) return;

        try
        {
            Texture = assets.Load<Texture2D>(_texturePath);
            if (FrameWidth > 0 && FrameHeight > 0) RecalculateFrameRect();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SpriteRenderer] Could not load '{_texturePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Source rectangle within the texture. When null the full texture is used.
    /// Setting FrameIndex will override this automatically.
    /// </summary>
    public Rectangle? SourceRect { get; set; }

    /// <summary>Tint colour multiplied with the texture. Default is White (no tint).</summary>
    public Color Tint { get; set; } = Color.White;

    /// <summary>Horizontal/vertical flip flags.</summary>
    public SpriteEffects Effects { get; set; } = SpriteEffects.None;

    /// <summary>
    /// Depth used when SpriteSortMode.BackToFront or FrontToBack is active.
    /// 0 = front, 1 = back.
    /// </summary>
    public float LayerDepth { get; set; } = 0f;

    /// <summary>
    /// Normalised pivot point (origin) relative to the sprite bounds.
    /// (0,0) = top-left, (0.5,0.5) = centre, (1,1) = bottom-right.
    /// </summary>
    public Vector2 Pivot { get; set; } = new Vector2(0.5f, 0.5f);

    // -------------------------------------------------------------------------
    // Spritesheet animation support
    // -------------------------------------------------------------------------

    /// <summary>
    /// Width of a single frame in pixels. Set both FrameWidth and FrameHeight
    /// to enable spritesheet mode; SourceRect will be derived from FrameIndex.
    /// </summary>
    public int FrameWidth { get; set; }

    /// <summary>Height of a single frame in pixels.</summary>
    public int FrameHeight { get; set; }

    /// <summary>
    /// Zero-based index of the frame to display. The sheet is read left-to-right,
    /// top-to-bottom. Setting this recalculates SourceRect automatically.
    /// </summary>
    private int _frameIndex;
    public int FrameIndex
    {
        get => _frameIndex;
        set
        {
            _frameIndex = value;
            RecalculateFrameRect();
        }
    }

    // -------------------------------------------------------------------------
    // 9-patch / sliced sprite
    // -------------------------------------------------------------------------

    /// <summary>
    /// When true the sprite is drawn using a 9-patch technique.
    /// The texture is split into 9 regions by <see cref="SliceBorder"/> pixels on each side.
    /// Corners are drawn at native size; edges and centre are stretched to fill the target area.
    /// </summary>
    public bool IsSliced { get; set; }

    /// <summary>
    /// Uniform border size (in pixels) for 9-patch slicing.
    /// All four sides use the same border width.
    /// </summary>
    public int SliceBorder { get; set; } = 8;

    /// <summary>
    /// When IsSliced is true, this defines the target draw size in pixels before Transform.Scale
    /// is applied. If left at zero the texture's native size is used.
    /// </summary>
    public Vector2 SliceSize { get; set; } = Vector2.Zero;

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private bool IsSpritesheetMode => FrameWidth > 0 && FrameHeight > 0;

    private void RecalculateFrameRect()
    {
        if (Texture == null || !IsSpritesheetMode) return;

        int columns = Texture.Width / FrameWidth;
        if (columns < 1) columns = 1;

        int col = _frameIndex % columns;
        int row = _frameIndex / columns;

        SourceRect = new Rectangle(col * FrameWidth, row * FrameHeight, FrameWidth, FrameHeight);
    }

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    public override void Draw(SpriteBatch sb)
    {
        if (Texture == null) return;

        // Refresh spritesheet rect in case Texture changed after FrameIndex was set
        if (IsSpritesheetMode && SourceRect == null)
            RecalculateFrameRect();

        var transform = Actor.Transform;
        Vector2 position = transform.Position;
        float rotation   = transform.Rotation;
        Vector2 scale    = transform.Scale;

        if (IsSliced)
            DrawSliced(sb, position, rotation, scale);
        else
            DrawNormal(sb, position, rotation, scale);
    }

    private void DrawNormal(SpriteBatch sb, Vector2 position, float rotation, Vector2 scale)
    {
        Rectangle src = SourceRect ?? Texture!.Bounds;
        Vector2 origin = new Vector2(src.Width * Pivot.X, src.Height * Pivot.Y);

        sb.Draw(
            Texture,
            position,
            src,
            Tint,
            rotation,
            origin,
            scale,
            Effects,
            LayerDepth);
    }

    private void DrawSliced(SpriteBatch sb, Vector2 position, float rotation, Vector2 scale)
    {
        if (Texture == null) return;

        int b    = SliceBorder;
        int texW = Texture.Width;
        int texH = Texture.Height;

        // Destination size in world pixels (before actor scale is applied)
        float dstW = SliceSize.X > 0f ? SliceSize.X : texW;
        float dstH = SliceSize.Y > 0f ? SliceSize.Y : texH;

        // Full draw area in screen pixels
        float drawW   = dstW * scale.X;
        float drawH   = dstH * scale.Y;
        float borderX = b * scale.X;
        float borderY = b * scale.Y;

        // Pivot offset from the draw area top-left (screen pixels)
        float originX = drawW * Pivot.X;
        float originY = drawH * Pivot.Y;

        // 9-patch column widths and row heights (screen pixels)
        float[] colW = { borderX, drawW - borderX * 2f, borderX };
        float[] rowH = { borderY, drawH - borderY * 2f, borderY };

        // 9-patch source rects (texture pixels)
        int midTexW = texW - b * 2;
        int midTexH = texH - b * 2;
        int[] srcX  = { 0, b, texW - b };
        int[] srcY  = { 0, b, texH - b };
        int[] srcW  = { b, midTexW,   b };
        int[] srcH  = { b, midTexH,   b };

        // Precompute rotation trig — cells are placed by rotating their
        // unrotated offset from the draw-area top-left around the pivot point.
        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);

        // World-space position of the draw area's top-left corner (unrotated).
        // We rotate each cell's (offsetX, offsetY) relative to this origin
        // and then shift by the rotated pivot so the actor Transform.Position
        // corresponds to the pivot.
        // rotatedPivot is the pivot vector after rotation — used to translate
        // back so Transform.Position stays at the pivot.
        float rotPivX = originX * cos - originY * sin;
        float rotPivY = originX * sin + originY * cos;

        float accumY = 0f;
        for (int row = 0; row < 3; row++)
        {
            float rowHeight = rowH[row];
            float accumX = 0f;
            for (int col = 0; col < 3; col++)
            {
                float cw = colW[col];
                float rh = rowHeight;

                if (cw > 0f && rh > 0f && srcW[col] > 0 && srcH[row] > 0)
                {
                    Rectangle src = new Rectangle(srcX[col], srcY[row], srcW[col], srcH[row]);

                    // Rotate this cell's top-left offset around (0,0), then
                    // offset by the world position minus the rotated pivot.
                    float rotOffX = accumX * cos - accumY * sin;
                    float rotOffY = accumX * sin + accumY * cos;

                    Vector2 cellWorld = new Vector2(
                        position.X - rotPivX + rotOffX,
                        position.Y - rotPivY + rotOffY);

                    // Per-cell scale so the patch fills its destination area exactly
                    Vector2 cellScale = new Vector2(cw / srcW[col], rh / srcH[row]);

                    sb.Draw(
                        Texture,
                        cellWorld,
                        src,
                        Tint,
                        rotation,
                        Vector2.Zero,
                        cellScale,
                        Effects,
                        LayerDepth);
                }

                accumX += cw;
            }
            accumY += rowHeight;
        }
    }
}
