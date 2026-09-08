using System.Numerics;
using ImGuiNET;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using XnaMatrix = Microsoft.Xna.Framework.Matrix;
using XnaQuaternion = Microsoft.Xna.Framework.Quaternion;
using XnaVector3 = Microsoft.Xna.Framework.Vector3;

namespace SexyBiscuit.Editor;

/// <summary>
/// The translate / rotate / scale handles drawn over the 3D viewport, and the drag
/// maths behind them.
/// </summary>
/// <remarks>
/// <para>
/// Handles are projected from world space through the active camera but sized in
/// <em>pixels</em>, so the gizmo stays the same size on screen no matter how far away the
/// object is. A gizmo that shrinks with distance is unusable on a large level.
/// </para>
/// <para>
/// Dragging works entirely in screen space: the axis is projected to a 2D direction, the
/// mouse delta is projected onto it, and the resulting pixel distance is converted back to
/// world units using the axis's own on-screen length. That handles perspective correctly
/// without unprojecting, and it degenerates gracefully — an axis pointing almost straight
/// at the camera has a near-zero screen length, which is exactly when it should stop
/// responding rather than fly off.
/// </para>
/// </remarks>
public sealed class Gizmo3D
{
    /// <summary>Length of each axis handle, in pixels.</summary>
    public float AxisLengthPixels { get; set; } = 90f;

    /// <summary>How close the cursor must be to an axis line to grab it, in pixels.</summary>
    public float GrabThreshold { get; set; } = 8f;

    /// <summary>True while an axis is being dragged.</summary>
    public bool IsDragging => _activeAxis >= 0;

    // -1 none, 0 X, 1 Y, 2 Z, 3 screen-space (centre handle)
    private int _activeAxis = -1;
    private int _hoverAxis  = -1;

    private Vector2      _dragStartMouse;
    private XnaVector3   _dragStartPosition;
    private XnaVector3   _dragStartScale;
    private XnaQuaternion _dragStartRotation;

    private static readonly uint[] AxisColours =
    {
        0xFF3A3AE8, // X — red   (ImGui packs as ABGR)
        0xFF3AE83A, // Y — green
        0xFFE8883A, // Z — blue
    };

    private const uint HighlightColour = 0xFF40E0FF; // amber-yellow when hovered or active
    private const uint CentreColour    = 0xFFFFFFFF;

    /// <summary>
    /// Draws the gizmo and applies any drag. Returns true when it consumed the mouse, so
    /// the caller knows not to also treat the click as a selection.
    /// </summary>
    /// <param name="actor">The selected actor. Must have a Transform3D to be manipulated.</param>
    /// <param name="camera">Camera the viewport is rendering through.</param>
    /// <param name="viewportMin">Top-left of the viewport image, in ImGui screen space.</param>
    /// <param name="viewportSize">Size of the viewport image in pixels.</param>
    /// <param name="mouse">Cursor position in ImGui screen space.</param>
    public bool Draw(Actor? actor, Camera3D? camera, Vector2 viewportMin, Vector2 viewportSize, Vector2 mouse)
    {
        if (actor == null || camera == null || viewportSize.X < 1f || viewportSize.Y < 1f)
        {
            _activeAxis = -1;
            return false;
        }

        var transform = actor.GetComponent<Transform3D>();
        if (transform == null)
        {
            _activeAxis = -1;
            return false;
        }

        float aspect = viewportSize.X / viewportSize.Y;
        var view = camera.GetViewMatrix();
        var proj = camera.GetProjectionMatrix(aspect);
        var viewProj = view * proj;

        var origin = transform.Position;
        if (!TryProject(origin, viewProj, viewportMin, viewportSize, out var originScreen))
        {
            // Behind the camera: nothing sensible to draw or drag.
            _activeAxis = -1;
            return false;
        }

        // Local is the legacy behaviour. World deliberately uses the scene's fixed
        // cardinal axes, including for the rotation ring, so moving a rotated actor
        // can be constrained against the level rather than its own orientation.
        var axes = EditorState.GizmoTransformSpace == GizmoTransformSpace.World
            ? new[] { XnaVector3.Right, XnaVector3.Up, XnaVector3.Forward }
            : new[]
            {
                XnaVector3.Transform(XnaVector3.Right,   transform.Rotation),
                XnaVector3.Transform(XnaVector3.Up,      transform.Rotation),
                XnaVector3.Transform(XnaVector3.Forward, transform.Rotation),
            };

        // Work out each axis's screen direction and how many world units one pixel is
        // along it, by projecting a probe point one world unit out.
        var axisScreenDir   = new Vector2[3];
        var worldUnitsPerPx = new float[3];
        var axisEnd         = new Vector2[3];

        for (int i = 0; i < 3; i++)
        {
            if (!TryProject(origin + axes[i], viewProj, viewportMin, viewportSize, out var probe))
            {
                axisScreenDir[i]   = Vector2.Zero;
                worldUnitsPerPx[i] = 0f;
                axisEnd[i]         = originScreen;
                continue;
            }

            var delta = probe - originScreen;
            float len = delta.Length();

            if (len < 0.5f)
            {
                // The axis points nearly at the camera; dragging it would be wild.
                axisScreenDir[i]   = Vector2.Zero;
                worldUnitsPerPx[i] = 0f;
                axisEnd[i]         = originScreen;
                continue;
            }

            axisScreenDir[i]   = delta / len;
            worldUnitsPerPx[i] = 1f / len;              // one world unit spanned `len` pixels
            axisEnd[i]         = originScreen + axisScreenDir[i] * AxisLengthPixels;
        }

        // The centre handle moves in the view plane. Project the camera's right/up
        // basis at the selected actor so its screen drag remains correct at any
        // depth and under perspective, rather than guessing a world-unit scale.
        var cameraTransform = camera.GetTransform3D();
        var viewPlaneAxes = new[] { cameraTransform.Right, cameraTransform.Up };
        var viewPlaneProjection = new Vector2[2];
        for (int i = 0; i < viewPlaneAxes.Length; i++)
        {
            viewPlaneProjection[i] = TryProject(origin + viewPlaneAxes[i], viewProj, viewportMin, viewportSize, out var probe)
                ? probe - originScreen
                : Vector2.Zero;
        }

        UpdateDrag(transform, originScreen, axes, axisScreenDir, worldUnitsPerPx,
            viewPlaneAxes, viewPlaneProjection, cameraTransform.Forward, mouse);
        DrawHandles(originScreen, axisEnd);

        return IsDragging;
    }

    // -------------------------------------------------------------------------
    // Interaction
    // -------------------------------------------------------------------------

    private void UpdateDrag(Transform3D transform, Vector2 originScreen, XnaVector3[] axes,
                            Vector2[] axisScreenDir, float[] worldUnitsPerPx,
                            XnaVector3[] viewPlaneAxes, Vector2[] viewPlaneProjection,
                            XnaVector3 cameraForward, Vector2 mouse)
    {
        bool down = ImGui.IsMouseDown(ImGuiMouseButton.Left);

        if (!down)
        {
            _activeAxis = -1;
            _hoverAxis  = PickAxis(originScreen, axisScreenDir, mouse);
            return;
        }

        if (_activeAxis < 0)
        {
            // Only start a drag on the press, not on a button already held from elsewhere.
            if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;

            int picked = PickAxis(originScreen, axisScreenDir, mouse);
            if (picked < 0) return;

            _activeAxis        = picked;
            _dragStartMouse    = mouse;
            _dragStartPosition = transform.Position;
            _dragStartScale    = transform.LocalScale;
            _dragStartRotation = transform.Rotation;
            return;
        }

        var totalDelta = mouse - _dragStartMouse;

        switch (EditorState.GizmoMode)
        {
            case GizmoMode.Translate:
                ApplyTranslate(transform, axes, axisScreenDir, worldUnitsPerPx,
                    viewPlaneAxes, viewPlaneProjection, totalDelta);
                break;
            case GizmoMode.Scale:  ApplyScale(transform, axisScreenDir, totalDelta); break;
            case GizmoMode.Rotate: ApplyRotate(transform, axes, cameraForward, originScreen, mouse); break;
        }
    }

    private void ApplyTranslate(Transform3D transform, XnaVector3[] axes, Vector2[] axisScreenDir,
                                float[] worldUnitsPerPx, XnaVector3[] viewPlaneAxes,
                                Vector2[] viewPlaneProjection, Vector2 totalDelta)
    {
        if (_activeAxis == 3)
        {
            // Invert the camera right/up projection so an arbitrary screen delta
            // becomes movement in the plane facing the editor camera.
            var right = viewPlaneProjection[0];
            var up    = viewPlaneProjection[1];
            float determinant = right.X * up.Y - right.Y * up.X;
            if (MathF.Abs(determinant) < 1e-4f) return;

            float alongRight = (totalDelta.X * up.Y - totalDelta.Y * up.X) / determinant;
            float alongUp    = (right.X * totalDelta.Y - right.Y * totalDelta.X) / determinant;
            var position = _dragStartPosition
                         + viewPlaneAxes[0] * alongRight
                         + viewPlaneAxes[1] * alongUp;

            if (EditorState.SnapEnabled && EditorState.TranslateSnap > 0f)
            {
                float snap = EditorState.TranslateSnap;
                position = new XnaVector3(
                    MathF.Round(position.X / snap) * snap,
                    MathF.Round(position.Y / snap) * snap,
                    MathF.Round(position.Z / snap) * snap);
            }
            transform.Position = position;
            return;
        }

        float pixelsAlongAxis = Vector2.Dot(totalDelta, axisScreenDir[_activeAxis]);
        float worldDistance   = pixelsAlongAxis * worldUnitsPerPx[_activeAxis];

        if (EditorState.SnapEnabled && EditorState.TranslateSnap > 0f)
            worldDistance = MathF.Round(worldDistance / EditorState.TranslateSnap) * EditorState.TranslateSnap;

        transform.Position = _dragStartPosition + axes[_activeAxis] * worldDistance;
    }

    private void ApplyScale(Transform3D transform, Vector2[] axisScreenDir, Vector2 totalDelta)
    {
        // Scale is unitless, so pixels map to a factor directly rather than through the
        // axis's world length — dragging 100px always doubles, near or far.
        float pixels = _activeAxis == 3
            ? totalDelta.X - totalDelta.Y
            : Vector2.Dot(totalDelta, axisScreenDir[_activeAxis]);
        float factor = MathF.Max(0.01f, 1f + pixels / 100f);

        if (EditorState.SnapEnabled && EditorState.ScaleSnap > 0f)
            factor = MathF.Max(EditorState.ScaleSnap, MathF.Round(factor / EditorState.ScaleSnap) * EditorState.ScaleSnap);

        var scale = _dragStartScale;
        if (_activeAxis == 3)
        {
            scale *= factor;
            transform.LocalScale = scale;
            return;
        }
        switch (_activeAxis)
        {
            case 0: scale.X = _dragStartScale.X * factor; break;
            case 1: scale.Y = _dragStartScale.Y * factor; break;
            case 2: scale.Z = _dragStartScale.Z * factor; break;
        }

        transform.LocalScale = scale;
    }

    private void ApplyRotate(Transform3D transform, XnaVector3[] axes, XnaVector3 cameraForward,
                             Vector2 originScreen, Vector2 mouse)
    {
        // Angle swept around the gizmo centre, measured from where the drag started.
        float startAngle = MathF.Atan2(_dragStartMouse.Y - originScreen.Y, _dragStartMouse.X - originScreen.X);
        float nowAngle   = MathF.Atan2(mouse.Y - originScreen.Y, mouse.X - originScreen.X);
        float degrees    = (nowAngle - startAngle) * 180f / MathF.PI;

        if (EditorState.SnapEnabled && EditorState.RotateSnap > 0f)
            degrees = MathF.Round(degrees / EditorState.RotateSnap) * EditorState.RotateSnap;

        var axis = _activeAxis == 3 ? cameraForward : axes[_activeAxis];
        var delta = XnaQuaternion.CreateFromAxisAngle(
            XnaVector3.Normalize(axis), degrees * MathF.PI / 180f);

        transform.Rotation = delta * _dragStartRotation;
    }

    /// <summary>Returns the axis under the cursor, or -1.</summary>
    private int PickAxis(Vector2 originScreen, Vector2[] axisScreenDir, Vector2 mouse)
    {
        // The centre handle wins when the cursor is on it — it overlaps all three axes.
        if (Vector2.Distance(mouse, originScreen) <= GrabThreshold) return 3;

        int best = -1;
        float bestDistance = GrabThreshold;

        for (int i = 0; i < 3; i++)
        {
            if (axisScreenDir[i] == Vector2.Zero) continue;

            var end = originScreen + axisScreenDir[i] * AxisLengthPixels;
            float d = DistanceToSegment(mouse, originScreen, end);

            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        return best;
    }

    private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float lengthSq = ab.LengthSquared();
        if (lengthSq < 1e-4f) return Vector2.Distance(point, a);

        float t = Math.Clamp(Vector2.Dot(point - a, ab) / lengthSq, 0f, 1f);
        return Vector2.Distance(point, a + ab * t);
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------

    private void DrawHandles(Vector2 originScreen, Vector2[] axisEnd)
    {
        var drawList = ImGui.GetWindowDrawList();
        var mode = EditorState.GizmoMode;

        for (int i = 0; i < 3; i++)
        {
            if (axisEnd[i] == originScreen) continue;   // degenerate axis, nothing to grab

            bool lit = _activeAxis == i || (_activeAxis < 0 && _hoverAxis == i);
            uint colour = lit ? HighlightColour : AxisColours[i];
            float thickness = lit ? 3.5f : 2.5f;

            drawList.AddLine(originScreen, axisEnd[i], colour, thickness);

            switch (mode)
            {
                case GizmoMode.Translate:
                    DrawArrowHead(drawList, originScreen, axisEnd[i], colour);
                    break;

                case GizmoMode.Scale:
                    // A cube on the end reads as "scale" the way a cone reads as "move".
                    drawList.AddRectFilled(axisEnd[i] - new Vector2(5f), axisEnd[i] + new Vector2(5f), colour);
                    break;

                case GizmoMode.Rotate:
                    drawList.AddCircle(originScreen, AxisLengthPixels * (0.7f + i * 0.12f), colour, 48, thickness);
                    break;
            }
        }

        bool centreLit = _activeAxis == 3 || (_activeAxis < 0 && _hoverAxis == 3);
        drawList.AddCircleFilled(originScreen, centreLit ? 6f : 4f,
            centreLit ? HighlightColour : CentreColour);
    }

    private static void DrawArrowHead(ImDrawListPtr drawList, Vector2 from, Vector2 to, uint colour)
    {
        var dir = to - from;
        float len = dir.Length();
        if (len < 1e-3f) return;

        dir /= len;
        var perp = new Vector2(-dir.Y, dir.X);

        const float headLength = 12f;
        const float headWidth  = 5f;

        var baseP = to - dir * headLength;
        drawList.AddTriangleFilled(to, baseP + perp * headWidth, baseP - perp * headWidth, colour);
    }

    // -------------------------------------------------------------------------
    // Projection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Projects a world point into viewport screen space. Returns false when the point is
    /// behind the camera, where the perspective divide flips the result.
    /// </summary>
    private static bool TryProject(XnaVector3 world, XnaMatrix viewProj,
                                   Vector2 viewportMin, Vector2 viewportSize, out Vector2 screen)
    {
        var clip = XnaVector3.Transform(world, viewProj);

        // Transform drops W, so recover it to test for points behind the near plane.
        float w = world.X * viewProj.M14 + world.Y * viewProj.M24
                + world.Z * viewProj.M34 + viewProj.M44;

        if (w <= 1e-4f)
        {
            screen = Vector2.Zero;
            return false;
        }

        var ndc = new Vector2(clip.X / w, clip.Y / w);

        screen = new Vector2(
            viewportMin.X + (ndc.X * 0.5f + 0.5f) * viewportSize.X,
            viewportMin.Y + (0.5f - ndc.Y * 0.5f) * viewportSize.Y);

        return true;
    }
}
