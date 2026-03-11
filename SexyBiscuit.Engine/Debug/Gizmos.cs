using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.Debug;

// ---------------------------------------------------------------------------
// GizmoType discriminates which draw primitive a GizmoCommand represents.
// ---------------------------------------------------------------------------
public enum GizmoType
{
    Line,
    Box,
    Circle,
    Sphere,
    Text,
    Line2D,
    Box2D,
}

// ---------------------------------------------------------------------------
// GizmoCommand — one entry in the command buffer.
// 'Duration' > 0 means the command persists across frames until it expires.
// 'Duration' == 0 means single-frame (drawn once, then discarded).
// ---------------------------------------------------------------------------
public struct GizmoCommand
{
    public GizmoType Type;
    public Color     Color;

    // 3-D geometry
    public Vector3 A;          // start / center
    public Vector3 B;          // end / size (box) / (radius,segments,0) (circle)

    // 2-D geometry
    public Vector2 A2;
    public Vector2 B2;

    // Text
    public string? Text;

    // Lifetime
    public float Duration;     // seconds remaining; 0 = single-frame
    public bool  IsPersistent; // true when originally issued with duration > 0
}

/// <summary>
/// Static command-buffer gizmo system.
/// Accumulate draw commands during Update; flush once per frame during Draw.
/// </summary>
public static class Gizmos
{
    // -------------------------------------------------------------------------
    // Config
    // -------------------------------------------------------------------------
    public static bool Enabled { get; set; } = true;

    // -------------------------------------------------------------------------
    // Internal command buffer
    // -------------------------------------------------------------------------
    private static readonly List<GizmoCommand> _commands = new();
    private static readonly object _lock = new();

    // Lazily created resources
    private static Texture2D? _pixel;
    private static BasicEffect? _basicEffect;

    // -------------------------------------------------------------------------
    // 3-D Draw API
    // -------------------------------------------------------------------------

    public static void DrawLine(Vector3 start, Vector3 end,
                                Color? color = null, float duration = 0f)
    {
        if (!Enabled) return;
        Enqueue(new GizmoCommand
        {
            Type = GizmoType.Line,
            Color = color ?? Color.White,
            A = start, B = end,
            Duration = duration,
            IsPersistent = duration > 0f,
        });
    }

    public static void DrawBox(Vector3 center, Vector3 size,
                               Color? color = null, float duration = 0f)
    {
        if (!Enabled) return;
        Enqueue(new GizmoCommand
        {
            Type = GizmoType.Box,
            Color = color ?? Color.Green,
            A = center, B = size,
            Duration = duration,
            IsPersistent = duration > 0f,
        });
    }

    public static void DrawCircle(Vector3 center, float radius, int segments = 32,
                                  Color? color = null, float duration = 0f)
    {
        if (!Enabled) return;
        Enqueue(new GizmoCommand
        {
            Type = GizmoType.Circle,
            Color = color ?? Color.Cyan,
            A = center,
            B = new Vector3(radius, segments, 0f),
            Duration = duration,
            IsPersistent = duration > 0f,
        });
    }

    public static void DrawSphere(Vector3 center, float radius,
                                  Color? color = null, float duration = 0f)
    {
        if (!Enabled) return;
        Color c = color ?? Color.Yellow;
        // Three circles: XY, XZ, YZ planes — stored as Sphere type
        Enqueue(new GizmoCommand
        {
            Type = GizmoType.Sphere,
            Color = c,
            A = center,
            B = new Vector3(radius, 0, 0),
            Duration = duration,
            IsPersistent = duration > 0f,
        });
    }

    public static void DrawText(Vector3 worldPos, string text,
                                Color? color = null, float duration = 0f)
    {
        if (!Enabled) return;
        Enqueue(new GizmoCommand
        {
            Type = GizmoType.Text,
            Color = color ?? Color.White,
            A = worldPos,
            Text = text,
            Duration = duration,
            IsPersistent = duration > 0f,
        });
    }

    // -------------------------------------------------------------------------
    // 2-D Draw API
    // -------------------------------------------------------------------------

    public static void DrawLine2D(Vector2 start, Vector2 end,
                                  Color? color = null, float duration = 0f)
    {
        if (!Enabled) return;
        Enqueue(new GizmoCommand
        {
            Type = GizmoType.Line2D,
            Color = color ?? Color.White,
            A2 = start, B2 = end,
            Duration = duration,
            IsPersistent = duration > 0f,
        });
    }

    public static void DrawBox2D(Vector2 center, Vector2 size,
                                 Color? color = null, float duration = 0f)
    {
        if (!Enabled) return;
        Enqueue(new GizmoCommand
        {
            Type = GizmoType.Box2D,
            Color = color ?? Color.LimeGreen,
            A2 = center, B2 = size,
            Duration = duration,
            IsPersistent = duration > 0f,
        });
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Call once per frame. Decrements duration on persistent commands and
    /// removes those that have expired.
    /// </summary>
    public static void Update(float dt)
    {
        if (!Enabled) return;

        lock (_lock)
        {
            for (int i = _commands.Count - 1; i >= 0; i--)
            {
                var cmd = _commands[i];
                if (!cmd.IsPersistent)
                {
                    // Single-frame commands are flushed during Draw; do not
                    // remove them here — they haven't been drawn yet.
                    continue;
                }

                cmd.Duration -= dt;
                if (cmd.Duration <= 0f)
                    _commands.RemoveAt(i);
                else
                    _commands[i] = cmd;
            }
        }
    }

    /// <summary>
    /// Flushes all queued gizmos to the screen.
    /// 2-D commands are drawn via SpriteBatch; 3-D commands via BasicEffect.
    /// Single-frame commands are removed after drawing.
    /// </summary>
    public static void Flush(SpriteBatch sb, GraphicsDevice gd)
    {
        if (!Enabled) return;

        EnsurePixel(gd);

        List<GizmoCommand> snapshot;
        lock (_lock)
        {
            snapshot = new List<GizmoCommand>(_commands);
        }

        if (snapshot.Count == 0) return;

        // ---- 2-D pass (SpriteBatch) -----------------------------------------
        bool any2D = snapshot.Any(c => c.Type == GizmoType.Line2D || c.Type == GizmoType.Box2D);
        if (any2D)
        {
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
            foreach (var cmd in snapshot)
            {
                switch (cmd.Type)
                {
                    case GizmoType.Line2D:
                        DrawLine2DInternal(sb, cmd.A2, cmd.B2, cmd.Color);
                        break;
                    case GizmoType.Box2D:
                        DrawBox2DInternal(sb, cmd.A2, cmd.B2, cmd.Color);
                        break;
                }
            }
            sb.End();
        }

        // ---- 3-D pass (BasicEffect + vertex primitives) ---------------------
        bool any3D = snapshot.Any(c =>
            c.Type == GizmoType.Line ||
            c.Type == GizmoType.Box  ||
            c.Type == GizmoType.Circle ||
            c.Type == GizmoType.Sphere);

        if (any3D)
        {
            EnsureBasicEffect(gd);

            _basicEffect!.VertexColorEnabled = true;
            _basicEffect.World = Matrix.Identity;

            // Derive view/projection from the engine's active camera if available
            // (For now use a default orthographic; game should set camera before Flush)
            var vp = gd.Viewport;
            _basicEffect.View = Matrix.CreateLookAt(
                new Vector3(0, 0, 10),
                Vector3.Zero,
                Vector3.Up);
            _basicEffect.Projection = Matrix.CreatePerspectiveFieldOfView(
                MathHelper.PiOver4, vp.AspectRatio, 0.1f, 10000f);

            gd.BlendState        = BlendState.AlphaBlend;
            gd.DepthStencilState = DepthStencilState.DepthRead;
            gd.RasterizerState   = RasterizerState.CullNone;

            foreach (var cmd in snapshot)
            {
                switch (cmd.Type)
                {
                    case GizmoType.Line:
                        DrawLine3DInternal(gd, cmd.A, cmd.B, cmd.Color);
                        break;
                    case GizmoType.Box:
                        DrawBox3DInternal(gd, cmd.A, cmd.B, cmd.Color);
                        break;
                    case GizmoType.Circle:
                        DrawCircle3DInternal(gd, cmd.A,
                            cmd.B.X, (int)cmd.B.Y, cmd.Color, CirclePlane.XZ);
                        break;
                    case GizmoType.Sphere:
                        DrawCircle3DInternal(gd, cmd.A, cmd.B.X, 32, cmd.Color, CirclePlane.XY);
                        DrawCircle3DInternal(gd, cmd.A, cmd.B.X, 32, cmd.Color, CirclePlane.XZ);
                        DrawCircle3DInternal(gd, cmd.A, cmd.B.X, 32, cmd.Color, CirclePlane.YZ);
                        break;
                }
            }
        }

        // ---- Purge single-frame commands from the live buffer ---------------
        lock (_lock)
        {
            _commands.RemoveAll(c => !c.IsPersistent);
        }
    }

    // -------------------------------------------------------------------------
    // Internal 2-D draw helpers
    // -------------------------------------------------------------------------

    private static void DrawLine2DInternal(SpriteBatch sb,
                                           Vector2 start, Vector2 end, Color color)
    {
        Vector2 delta = end - start;
        float length  = delta.Length();
        if (length < 0.5f) return;

        float angle = MathF.Atan2(delta.Y, delta.X);
        sb.Draw(_pixel!,
                position: start,
                sourceRectangle: null,
                color: color,
                rotation: angle,
                origin: new Vector2(0, 0.5f),
                scale: new Vector2(length, 1f),
                effects: SpriteEffects.None,
                layerDepth: 0f);
    }

    private static void DrawBox2DInternal(SpriteBatch sb,
                                          Vector2 center, Vector2 size, Color color)
    {
        Vector2 half = size * 0.5f;
        Vector2 tl = center - half;
        Vector2 tr = new Vector2(center.X + half.X, center.Y - half.Y);
        Vector2 br = center + half;
        Vector2 bl = new Vector2(center.X - half.X, center.Y + half.Y);

        DrawLine2DInternal(sb, tl, tr, color);
        DrawLine2DInternal(sb, tr, br, color);
        DrawLine2DInternal(sb, br, bl, color);
        DrawLine2DInternal(sb, bl, tl, color);
    }

    // -------------------------------------------------------------------------
    // Internal 3-D draw helpers using BasicEffect + immediate vertex arrays
    // -------------------------------------------------------------------------

    private enum CirclePlane { XY, XZ, YZ }

    private static void DrawLine3DInternal(GraphicsDevice gd,
                                           Vector3 start, Vector3 end, Color color)
    {
        var verts = new[]
        {
            new VertexPositionColor(start, color),
            new VertexPositionColor(end,   color),
        };

        foreach (var pass in _basicEffect!.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserPrimitives(PrimitiveType.LineList, verts, 0, 1);
        }
    }

    private static void DrawBox3DInternal(GraphicsDevice gd,
                                          Vector3 center, Vector3 size, Color color)
    {
        Vector3 h = size * 0.5f;

        Vector3 p0 = center + new Vector3(-h.X, -h.Y, -h.Z);
        Vector3 p1 = center + new Vector3( h.X, -h.Y, -h.Z);
        Vector3 p2 = center + new Vector3( h.X,  h.Y, -h.Z);
        Vector3 p3 = center + new Vector3(-h.X,  h.Y, -h.Z);
        Vector3 p4 = center + new Vector3(-h.X, -h.Y,  h.Z);
        Vector3 p5 = center + new Vector3( h.X, -h.Y,  h.Z);
        Vector3 p6 = center + new Vector3( h.X,  h.Y,  h.Z);
        Vector3 p7 = center + new Vector3(-h.X,  h.Y,  h.Z);

        var verts = new VertexPositionColor[]
        {
            // Bottom face
            new(p0, color), new(p1, color),
            new(p1, color), new(p2, color),
            new(p2, color), new(p3, color),
            new(p3, color), new(p0, color),
            // Top face
            new(p4, color), new(p5, color),
            new(p5, color), new(p6, color),
            new(p6, color), new(p7, color),
            new(p7, color), new(p4, color),
            // Verticals
            new(p0, color), new(p4, color),
            new(p1, color), new(p5, color),
            new(p2, color), new(p6, color),
            new(p3, color), new(p7, color),
        };

        foreach (var pass in _basicEffect!.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserPrimitives(PrimitiveType.LineList, verts, 0, verts.Length / 2);
        }
    }

    private static void DrawCircle3DInternal(GraphicsDevice gd,
                                             Vector3 center, float radius, int segments,
                                             Color color, CirclePlane plane)
    {
        segments = Math.Max(3, segments);
        var verts = new VertexPositionColor[segments * 2];

        for (int i = 0; i < segments; i++)
        {
            float a0 = MathHelper.TwoPi * i        / segments;
            float a1 = MathHelper.TwoPi * (i + 1f) / segments;

            Vector3 p0 = PlanePoint(center, a0, radius, plane);
            Vector3 p1 = PlanePoint(center, a1, radius, plane);

            verts[i * 2]     = new VertexPositionColor(p0, color);
            verts[i * 2 + 1] = new VertexPositionColor(p1, color);
        }

        foreach (var pass in _basicEffect!.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserPrimitives(PrimitiveType.LineList, verts, 0, segments);
        }
    }

    private static Vector3 PlanePoint(Vector3 center, float angle, float r, CirclePlane plane)
    {
        float cos = MathF.Cos(angle) * r;
        float sin = MathF.Sin(angle) * r;
        return plane switch
        {
            CirclePlane.XY => new Vector3(center.X + cos, center.Y + sin, center.Z),
            CirclePlane.XZ => new Vector3(center.X + cos, center.Y,       center.Z + sin),
            CirclePlane.YZ => new Vector3(center.X,       center.Y + cos, center.Z + sin),
            _              => center,
        };
    }

    // -------------------------------------------------------------------------
    // Resource helpers
    // -------------------------------------------------------------------------

    private static void EnsurePixel(GraphicsDevice gd)
    {
        if (_pixel != null) return;
        _pixel = new Texture2D(gd, 1, 1);
        _pixel.SetData(new[] { Color.White });
    }

    private static void EnsureBasicEffect(GraphicsDevice gd)
    {
        if (_basicEffect != null) return;
        _basicEffect = new BasicEffect(gd);
    }

    private static void Enqueue(GizmoCommand cmd)
    {
        lock (_lock)
        {
            _commands.Add(cmd);
        }
    }
}
