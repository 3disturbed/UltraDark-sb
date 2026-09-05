using System.Numerics;
using ImGuiNET;

namespace SexyBiscuit.Editor.Assistant;

/// <summary>
/// An animated spinner drawn straight into the ImGui draw list.
/// </summary>
/// <remarks>
/// <para>
/// ImGui has no built-in spinner. A text one cycling through <c>|/-\</c> is the usual
/// stand-in and reads as flicker rather than motion — the eye tracks a rotating arc as
/// continuous, and four discrete glyphs as four things.
/// </para>
/// <para>
/// The arc's own length breathes as well as rotating. A constant-length arc spinning at a
/// constant rate can look frozen when the frame rate drops, because every frame shows the
/// same shape at a slightly different angle; varying the sweep means there is always a
/// visible difference between consecutive frames.
/// </para>
/// </remarks>
public static class Throbber
{
    /// <summary>
    /// The colour transient status is drawn in — amber, matching the busy dot in the
    /// assistant header, so "something is happening" looks the same everywhere.
    /// </summary>
    public static readonly Vector4 Working = new(0.95f, 0.70f, 0.20f, 1f);

    /// <summary>
    /// Draws a spinner as an inline item, advancing the cursor like any other widget.
    /// </summary>
    /// <param name="radius">Radius in pixels.</param>
    /// <param name="thickness">Stroke width in pixels.</param>
    /// <param name="colour">Arc colour. Defaults to the current text colour.</param>
    /// <param name="speed">Revolutions per second.</param>
    public static void Draw(float radius = 7f, float thickness = 2.5f, Vector4? colour = null, float speed = 1.1f)
    {
        var drawList = ImGui.GetWindowDrawList();

        float lineHeight = ImGui.GetTextLineHeight();
        float diameter   = radius * 2f + thickness;

        var origin = ImGui.GetCursorScreenPos();
        var centre = new Vector2(origin.X + diameter / 2f, origin.Y + lineHeight / 2f);

        // Reserve the space first: the arc is drawn directly, so without this the next
        // item would overlap it.
        ImGui.Dummy(new Vector2(diameter, lineHeight));

        float time = (float)ImGui.GetTime();
        uint  packed = ImGui.ColorConvertFloat4ToU32(colour ?? ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);

        float start = time * MathF.Tau * speed;

        // Sweep between a third and most of the circle, so the shape changes frame to
        // frame even when rotation alone would be hard to see.
        float sweep = MathF.PI * (0.7f + 0.5f * MathF.Sin(time * 2.2f));

        const int segments = 24;
        drawList.PathArcTo(centre, radius, start, start + sweep, segments);
        drawList.PathStroke(packed, ImDrawFlags.None, thickness);
    }

    /// <summary>
    /// Draws a spinner followed by a label and, when given, an elapsed time.
    /// </summary>
    /// <param name="label">What is happening, for example "Running Bash".</param>
    /// <param name="elapsed">Time the operation has been running. Hidden below a second.</param>
    /// <param name="colour">Colour for both the arc and the text.</param>
    public static void DrawWithLabel(string label, TimeSpan? elapsed = null, Vector4? colour = null)
    {
        Draw(colour: colour);
        ImGui.SameLine();

        var textColour = colour ?? ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        ImGui.TextColored(textColour, label);

        // Sub-second times flicker through every value and tell nobody anything.
        if (elapsed is not { TotalSeconds: >= 1 } time) return;

        ImGui.SameLine();
        ImGui.TextDisabled(time.TotalMinutes >= 1
            ? $"· {(int)time.TotalMinutes}:{time.Seconds:00}"
            : $"· {time.Seconds}s");
    }
}
