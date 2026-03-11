using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// Renders a <see cref="UI.Canvas"/> anchored to the owning Actor's world position.
/// Useful for in-world name tags, health bars, interaction prompts, etc.
///
/// Usage:
///   var wc = myActor.AddComponent&lt;WorldCanvas&gt;();
///   wc.Canvas.AddWidget&lt;Label&gt;().Text = "Hello";
///   wc.Offset = new Vector2(-50, -80);
/// </summary>
public class WorldCanvas : Component
{
    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------
    /// <summary>The canvas containing all UI widgets to render in world space.</summary>
    public Canvas Canvas { get; } = new Canvas();

    /// <summary>Pixel offset applied to the actor's world position before drawing.</summary>
    public Vector2 Offset { get; set; }

    /// <summary>
    /// When true, the canvas rotation is cancelled out so it always faces
    /// the camera (i.e. rotation locked to 0 regardless of actor rotation).
    /// </summary>
    public bool BillboardToCamera { get; set; } = true;

    /// <summary>Uniform scale factor applied to the canvas in world space.</summary>
    public float Scale { get; set; } = 1f;

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------
    public override void Awake()
    {
        // Wire the internal canvas to this component's actor so it has context
        Canvas.Actor = Actor;
    }

    public override void Update(float dt)
    {
        if (!Enabled) return;
        Canvas.Update(dt);
    }

    public override void Draw(SpriteBatch sb)
    {
        if (!Enabled) return;

        var gd = sb.GraphicsDevice;

        // Compute world-space origin
        Vector2 worldPos = Actor.Transform.Position + Offset;

        // Build the transform matrix:
        //   1. Scale by the canvas scale
        //   2. Apply billboard (cancel actor rotation if requested)
        //   3. Translate to world position
        float actorRot = BillboardToCamera ? 0f : Actor.Transform.Rotation;

        Matrix canvasTransform =
            Matrix.CreateScale(Scale, Scale, 1f) *
            Matrix.CreateRotationZ(actorRot) *
            Matrix.CreateTranslation(worldPos.X, worldPos.Y, 0f);

        // End the current SpriteBatch pass and begin a new one with our transform
        sb.End();

        sb.Begin(
            SpriteSortMode.Deferred,
            BlendState.AlphaBlend,
            SamplerState.LinearClamp,
            DepthStencilState.None,
            RasterizerState.CullNone,
            effect: null,
            transformMatrix: canvasTransform);

        // Draw all widgets relative to (0,0) — the offset is already in the matrix
        foreach (var widget in Canvas.Children)
            if (widget.Visible)
                widget.Draw(sb, Canvas.Font);

        sb.End();

        // Restore caller's SpriteBatch state (minimal; caller can re-Begin with their matrix)
        sb.Begin(
            SpriteSortMode.Deferred,
            BlendState.AlphaBlend,
            SamplerState.LinearClamp,
            DepthStencilState.None,
            RasterizerState.CullNone);
    }
}
