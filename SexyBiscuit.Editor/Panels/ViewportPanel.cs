using System.Numerics;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using XnaButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using XnaMatrix = Microsoft.Xna.Framework.Matrix;
using XnaVector3 = Microsoft.Xna.Framework.Vector3;

namespace SexyBiscuit.Editor.Panels;

/// <summary>
/// Viewport panel — renders the engine's RenderTarget2D as an ImGui image,
/// draws gizmos, handles mouse picking, camera pan/zoom, and the play-mode banner.
/// </summary>
public sealed class ViewportPanel
{
    // -------------------------------------------------------------------------
    // Camera state (2D editor camera)
    // -------------------------------------------------------------------------
    private Vector2 _cameraOffset = Vector2.Zero;
    private float   _cameraZoom   = 1f;
    private const float ZoomMin   = 0.05f;
    private const float ZoomMax   = 20f;

    // Mouse tracking
    private Vector2 _prevMousePos;
    private bool    _panning;

    // Gizmo drag state
    private bool   _draggingGizmo;
    private GizmoMode _dragMode;
    private Vector2 _gizmoStartMouse;
    private Vector2 _gizmoStartValue;
    private float   _gizmoStartScalar;

    // Viewport bounds in screen space (updated each frame)
    private Vector2 _vpMin;
    private Vector2 _vpMax;

    // 3D handles and editor-camera navigation
    private readonly Gizmo3D _gizmo3D = new();
    private float _editorCamYaw;
    private float _editorCamPitch;
    private bool  _cameraAnglesSeeded;

    /// <summary>Editor camera movement speed in world units per second.</summary>
    public float FlySpeed { get; set; } = 8f;

    /// <summary>Mouse look sensitivity for the editor camera, in degrees per pixel.</summary>
    public float LookSensitivity { get; set; } = 0.2f;

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    public void Draw(RenderTarget2D? viewportTarget, ImGuiRenderer imGuiRenderer)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (!ImGui.Begin("Viewport", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.PopStyleVar();
            ImGui.End();
            return;
        }
        ImGui.PopStyleVar();

        var availSize = ImGui.GetContentRegionAvail();
        int displayW  = Math.Max(1, (int)availSize.X);
        int displayH  = Math.Max(1, (int)availSize.Y);

        // Request EditorApp to resize render target if needed
        if (viewportTarget != null &&
            (viewportTarget.Width != displayW || viewportTarget.Height != displayH))
        {
            EditorApp.Instance.ResizeViewport(displayW, displayH);
        }

        _vpMin = ImGui.GetCursorScreenPos();
        _vpMax = _vpMin + availSize;

        // Render the game texture
        if (viewportTarget != null)
        {
            var texId = imGuiRenderer.BindTexture(viewportTarget);
            ImGui.Image(texId, availSize);
        }
        else
        {
            ImGui.TextDisabled("(no viewport)");
        }

        // Play-mode banner overlay
        if (EditorState.IsPlaying)
            DrawPlayModeBanner();

        // Toolbar: gizmo mode buttons
        DrawGizmoToolbar();

        var selected = EditorState.SelectedActor;

        if (EditorState.Viewport3D)
        {
            // The 2D grid over a perspective view reads as noise, so it is 2D-only.
            var camera = EditorApp.Instance.ActiveViewportCamera;
            var io     = ImGui.GetIO();
            var mouse  = new Vector2(io.MousePos.X, io.MousePos.Y);

            bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);

            // The gizmo gets first refusal on the cursor: dragging a handle must not also
            // pick a different actor or fly the camera.
            bool gizmoBusy = _gizmo3D.Draw(selected, camera, _vpMin, availSize, mouse);

            if (hovered && !gizmoBusy)
            {
                Handle3DInput(camera, mouse);

                // Selection only on the press, and only when the gizmo did not take it —
                // otherwise finishing a handle drag over another object reselects it.
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    Pick3D(camera, availSize, mouse);
            }

            DrawViewportHud(camera);
        }
        else
        {
            DrawGrid();
            if (selected != null) DrawGizmoHandles(selected);
            HandleInput(selected);
        }

        ImGui.End();
    }

    // -------------------------------------------------------------------------
    // Play mode banner
    // -------------------------------------------------------------------------

    private void DrawPlayModeBanner()
    {
        var drawList = ImGui.GetWindowDrawList();
        var center   = (_vpMin + _vpMax) / 2f;

        string text = EditorState.IsPlayPaused ? "PAUSED" : "PLAY MODE";
        var textSize = ImGui.CalcTextSize(text);

        var rectMin = new Vector2(_vpMin.X, _vpMin.Y);
        var rectMax = new Vector2(_vpMax.X, _vpMin.Y + 28f);

        uint bgColor = EditorState.IsPlayPaused
            ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.5f, 0f, 0.7f))
            : ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f, 0.6f, 0.1f, 0.7f));

        drawList.AddRectFilled(rectMin, rectMax, bgColor);
        drawList.AddText(
            new Vector2(center.X - textSize.X / 2f, _vpMin.Y + 6f),
            0xFFFFFFFF, text);
    }

    // -------------------------------------------------------------------------
    // Gizmo toolbar
    // -------------------------------------------------------------------------

    private void DrawGizmoToolbar()
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos      = _vpMin + new Vector2(8f, 8f);
        float btnW   = 28f, btnH = 22f, gap = 4f;

        DrawGizmoBtn(drawList, pos, btnW, btnH, "T", GizmoMode.Translate, "Translate (G)");
        pos.X += btnW + gap;
        DrawGizmoBtn(drawList, pos, btnW, btnH, "R", GizmoMode.Rotate,    "Rotate (R)");
        pos.X += btnW + gap;
        DrawGizmoBtn(drawList, pos, btnW, btnH, "S", GizmoMode.Scale,     "Scale (S)");
    }

    private void DrawGizmoBtn(ImDrawListPtr dl, Vector2 pos, float w, float h,
        string label, GizmoMode mode, string tooltip)
    {
        bool active = EditorState.GizmoMode == mode;
        uint bg = active
            ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.6f, 1.0f, 0.9f))
            : ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.2f, 0.7f));

        dl.AddRectFilled(pos, pos + new Vector2(w, h), bg, 3f);
        dl.AddRect(pos, pos + new Vector2(w, h),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)));

        var ts   = ImGui.CalcTextSize(label);
        var tPos = pos + new Vector2((w - ts.X) / 2f, (h - ts.Y) / 2f);
        dl.AddText(tPos, 0xFFFFFFFF, label);

        // Click detection — use ImGui invisible button trick
        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton("##gbt" + label, new Vector2(w, h));
        if (ImGui.IsItemClicked()) EditorState.GizmoMode = mode;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
    }

    // -------------------------------------------------------------------------
    // Grid
    // -------------------------------------------------------------------------

    private void DrawGrid()
    {
        var drawList = ImGui.GetWindowDrawList();
        uint gridColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.3f, 0.5f));
        uint axisColorX = ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.3f, 0.3f, 0.7f));
        uint axisColorY = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.9f, 0.3f, 0.7f));

        float vpW = _vpMax.X - _vpMin.X;
        float vpH = _vpMax.Y - _vpMin.Y;

        // World-to-screen: screenPos = vpMin + (worldPos * zoom + cameraOffset)
        // Grid step in world units
        float gridStep = 64f; // 1 unit = 64 pixels base
        float screenStep = gridStep * _cameraZoom;

        // Avoid too-dense grids
        while (screenStep < 12f) screenStep *= 4f;
        while (screenStep > 200f) screenStep /= 4f;

        // Offset of grid origin on screen
        Vector2 origin = _vpMin + _cameraOffset;

        // Vertical lines
        float startX = origin.X % screenStep;
        for (float x = startX; x < _vpMax.X; x += screenStep)
        {
            bool isAxis = Math.Abs(x - origin.X) < 1f;
            drawList.AddLine(new Vector2(x, _vpMin.Y), new Vector2(x, _vpMax.Y),
                isAxis ? axisColorY : gridColor, isAxis ? 1.5f : 1f);
        }

        // Horizontal lines
        float startY = origin.Y % screenStep;
        for (float y = startY; y < _vpMax.Y; y += screenStep)
        {
            bool isAxis = Math.Abs(y - origin.Y) < 1f;
            drawList.AddLine(new Vector2(_vpMin.X, y), new Vector2(_vpMax.X, y),
                isAxis ? axisColorX : gridColor, isAxis ? 1.5f : 1f);
        }
    }

    // -------------------------------------------------------------------------
    // Gizmo handles
    // -------------------------------------------------------------------------

    private void DrawGizmoHandles(Actor actor)
    {
        var drawList = ImGui.GetWindowDrawList();
        var wp       = actor.Transform.Position;
        var screenPos = WorldToScreen(new Vector2(wp.X, wp.Y));

        const float arrowLen  = 60f;
        const float handleRad = 7f;

        switch (EditorState.GizmoMode)
        {
            case GizmoMode.Translate:
                // X axis (red)
                drawList.AddLine(screenPos, screenPos + new Vector2(arrowLen, 0f), 0xFF3333FF, 2.5f);
                drawList.AddCircleFilled(screenPos + new Vector2(arrowLen, 0f), handleRad, 0xFF3333FF);
                // Y axis (green, screen-down = world-up in 2D)
                drawList.AddLine(screenPos, screenPos + new Vector2(0f, -arrowLen), 0xFF33FF33, 2.5f);
                drawList.AddCircleFilled(screenPos + new Vector2(0f, -arrowLen), handleRad, 0xFF33FF33);
                // Center dot
                drawList.AddCircleFilled(screenPos, 5f, 0xFFFFFFFF);
                break;

            case GizmoMode.Rotate:
                drawList.AddCircle(screenPos, arrowLen, 0xFF33FFFF, 48, 2f);
                drawList.AddCircleFilled(screenPos, 5f, 0xFFFFFFFF);
                break;

            case GizmoMode.Scale:
                // X scale handle (yellow)
                drawList.AddLine(screenPos, screenPos + new Vector2(arrowLen, 0f), 0xFF33FFFF, 2.5f);
                drawList.AddRectFilled(screenPos + new Vector2(arrowLen - handleRad, -handleRad),
                    screenPos + new Vector2(arrowLen + handleRad, handleRad), 0xFF33FFFF);
                // Y scale handle
                drawList.AddLine(screenPos, screenPos + new Vector2(0f, -arrowLen), 0xFF33FFFF, 2.5f);
                drawList.AddRectFilled(screenPos + new Vector2(-handleRad, -arrowLen - handleRad),
                    screenPos + new Vector2(handleRad, -arrowLen + handleRad), 0xFF33FFFF);
                drawList.AddCircleFilled(screenPos, 5f, 0xFFFFFFFF);
                break;
        }
    }

    // -------------------------------------------------------------------------
    // 3D navigation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Unreal-style viewport navigation: hold the right button to look, WASD to fly while
    /// looking, Q/E for vertical, scroll to dolly, F to frame the selection.
    /// </summary>
    /// <remarks>
    /// Movement is gated behind the right button being held, which is what stops WASD
    /// stealing keystrokes from a text field elsewhere in the editor merely because the
    /// pointer happens to be over the viewport.
    /// </remarks>
    private void Handle3DInput(Camera3D? camera, Vector2 mouse)
    {
        var transform = EditorApp.Instance.EditorCameraTransform;
        if (transform == null || camera == null) return;

        // Seed the stored angles from the camera's actual orientation the first time, so
        // the view does not snap when navigation starts.
        if (!_cameraAnglesSeeded)
        {
            var euler = transform.EulerAngles;
            _editorCamPitch = euler.X;
            _editorCamYaw   = euler.Y;
            _cameraAnglesSeeded = true;
        }

        var io = ImGui.GetIO();
        float dt = MathF.Max(1f / 240f, io.DeltaTime);

        // Scroll dollies along the view direction whether or not a button is held.
        if (io.MouseWheel != 0f)
            transform.Position += transform.Forward * io.MouseWheel * FlySpeed * 0.25f;

        if (ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            var delta = ImGui.GetIO().MouseDelta;

            if (delta != System.Numerics.Vector2.Zero)
            {
                _editorCamYaw   = SBMath.WrapAngle(_editorCamYaw + delta.X * LookSensitivity);
                _editorCamPitch = Math.Clamp(_editorCamPitch + delta.Y * LookSensitivity, -89f, 89f);
                transform.EulerAngles = new XnaVector3(_editorCamPitch, _editorCamYaw, 0f);
            }

            float speed = FlySpeed * (ImGui.IsKeyDown(ImGuiKey.LeftShift) ? 3f : 1f);
            var move = XnaVector3.Zero;

            if (ImGui.IsKeyDown(ImGuiKey.W)) move += transform.Forward;
            if (ImGui.IsKeyDown(ImGuiKey.S)) move -= transform.Forward;
            if (ImGui.IsKeyDown(ImGuiKey.D)) move += transform.Right;
            if (ImGui.IsKeyDown(ImGuiKey.A)) move -= transform.Right;
            if (ImGui.IsKeyDown(ImGuiKey.E)) move += XnaVector3.Up;
            if (ImGui.IsKeyDown(ImGuiKey.Q)) move -= XnaVector3.Up;

            if (move != XnaVector3.Zero)
                transform.Position += SBMath.SafeNormalize(move) * speed * dt;
        }
        else
        {
            // Gizmo shortcuts only while not flying, so W does not fight Move mode.
            if (ImGui.IsKeyPressed(ImGuiKey.W)) EditorState.GizmoMode = GizmoMode.Translate;
            if (ImGui.IsKeyPressed(ImGuiKey.E)) EditorState.GizmoMode = GizmoMode.Rotate;
            if (ImGui.IsKeyPressed(ImGuiKey.R)) EditorState.GizmoMode = GizmoMode.Scale;
            if (ImGui.IsKeyPressed(ImGuiKey.F)) FrameSelection(transform);
        }
    }

    /// <summary>
    /// Selects the actor under the cursor by casting a ray through the viewport.
    /// </summary>
    /// <remarks>
    /// Tests against each renderer's world bounds rather than its triangles. Bounds are
    /// already maintained for frustum culling, they are cheap to intersect, and for
    /// clicking on objects in an editor the difference is rarely noticeable — the nearest
    /// hit along the ray wins, so overlapping bounds still resolve sensibly.
    ///
    /// The unprojection is done here rather than through Camera3D.ScreenToWorldRay
    /// because that one uses the device viewport, and the editor's viewport is a sub-rect
    /// of the window inside an ImGui panel.
    /// </remarks>
    private void Pick3D(Camera3D camera, Vector2 viewportSize, Vector2 mouse)
    {
        if (viewportSize.X < 1f || viewportSize.Y < 1f) return;

        var scene = EditorApp.Instance.Engine?.SceneManager.ActiveScene;
        if (scene == null) return;

        var view = camera.GetViewMatrix();
        var proj = camera.GetProjectionMatrix(viewportSize.X / viewportSize.Y);

        var invViewProj = XnaMatrix.Invert(view * proj);

        // Cursor to normalised device coordinates within the viewport image.
        float ndcX = (mouse.X - _vpMin.X) / viewportSize.X * 2f - 1f;
        float ndcY = 1f - (mouse.Y - _vpMin.Y) / viewportSize.Y * 2f;

        var near = UnprojectPoint(new XnaVector3(ndcX, ndcY, 0f), invViewProj);
        var far  = UnprojectPoint(new XnaVector3(ndcX, ndcY, 1f), invViewProj);

        var direction = far - near;
        if (direction.LengthSquared() < 1e-6f) return;

        var ray = new Microsoft.Xna.Framework.Ray(near, XnaVector3.Normalize(direction));

        Actor? best = null;
        float bestDistance = float.MaxValue;

        foreach (var renderer in MeshRenderer.All)
        {
            if (!renderer.Enabled || !renderer.Actor.IsActive) continue;

            float? hit = ray.Intersects(renderer.WorldBounds.ToBoundingBox());
            if (hit is not { } distance || distance >= bestDistance) continue;

            bestDistance = distance;
            best = renderer.Actor;
        }

        // Clicking empty space clears the selection, the same as every other editor.
        EditorState.SelectActor(best);
    }

    /// <summary>Undoes the perspective divide for one NDC point.</summary>
    private static XnaVector3 UnprojectPoint(XnaVector3 ndc, XnaMatrix invViewProj)
    {
        var transformed = XnaVector3.Transform(ndc, invViewProj);

        // Vector3.Transform drops W, so recompute it and divide through by hand.
        float w = ndc.X * invViewProj.M14 + ndc.Y * invViewProj.M24
                + ndc.Z * invViewProj.M34 + invViewProj.M44;

        return MathF.Abs(w) < 1e-6f ? transformed : transformed / w;
    }

    /// <summary>Moves the editor camera to look at the selected actor from a short distance.</summary>
    private static void FrameSelection(Transform3D cameraTransform)
    {
        var target = EditorState.SelectedActor?.GetComponent<Transform3D>();
        if (target == null) return;

        // Back off along the camera's current direction so framing does not also reorient.
        cameraTransform.Position = target.Position - cameraTransform.Forward * 6f;
        cameraTransform.LookAt(target.Position);
    }

    /// <summary>A corner readout of which camera is active and how to drive it.</summary>
    private void DrawViewportHud(Camera3D? camera)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = _vpMin + new Vector2(10f, 10f);

        string label = camera == null
            ? "No camera"
            : EditorState.UseGameCamera ? "Game camera" : "Editor camera";

        drawList.AddText(pos, 0xFFBBBBBB, label);

        if (camera != null && !EditorState.UseGameCamera)
        {
            drawList.AddText(pos + new Vector2(0f, 16f), 0xFF888888,
                "RMB look  ·  WASD/QE fly  ·  scroll dolly  ·  F frame");
        }
    }

    // -------------------------------------------------------------------------
    // Input handling
    // -------------------------------------------------------------------------

    private void HandleInput(Actor? selectedActor)
    {
        // Only process input when hovered over viewport
        if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)) return;

        var mouse    = Mouse.GetState();
        var keyboard = Keyboard.GetState();

        // Take the cursor from ImGui, not from Mouse.GetState(). _vpMin comes from
        // ImGui.GetCursorScreenPos, so every comparison and subtraction below has to be
        // in ImGui's space — mixing the two sources only happens to line up while the
        // window is at the origin and the display is 1:1.
        var io       = ImGui.GetIO();
        var mousePos = new Vector2(io.MousePos.X, io.MousePos.Y);

        // Gizmo mode shortcuts (Blender-style)
        if (ImGui.IsKeyPressed(ImGuiKey.G)) EditorState.GizmoMode = GizmoMode.Translate;
        if (ImGui.IsKeyPressed(ImGuiKey.R)) EditorState.GizmoMode = GizmoMode.Rotate;
        if (ImGui.IsKeyPressed(ImGuiKey.S)) EditorState.GizmoMode = GizmoMode.Scale;

        // Right-click drag = pan
        bool rightDown = mouse.RightButton == XnaButtonState.Pressed;
        if (rightDown && _panning)
        {
            _cameraOffset += mousePos - _prevMousePos;
        }
        _panning = rightDown;

        // Scroll = zoom
        float zoomDelta = io.MouseWheel;
        if (zoomDelta != 0f && !_panning)
        {
            float oldZoom = _cameraZoom;
            _cameraZoom = Math.Clamp(_cameraZoom * (1f + zoomDelta * 0.1f), ZoomMin, ZoomMax);

            // Zoom toward mouse cursor
            var cursorWorld = ScreenToWorld(mousePos);
            var newScreen   = WorldToScreen(cursorWorld);
            _cameraOffset  += mousePos - newScreen;
        }

        // Left-click picking (when not dragging a gizmo)
        if (mouse.LeftButton == XnaButtonState.Pressed && !_draggingGizmo)
        {
            if (IsInsideViewport(mousePos) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                PickActor(mousePos);
            }
        }

        _prevMousePos = mousePos;
    }

    private void PickActor(Vector2 screenPos)
    {
        var worldPos = ScreenToWorld(screenPos);
        var engine   = EditorApp.Instance.Engine;
        var scene    = engine?.SceneManager.ActiveScene;
        if (scene == null) return;

        const float pickRadius = 24f;
        Actor? best = null;
        float  bestDist = float.MaxValue;

        foreach (var layer in scene.Layers)
        {
            if (!layer.Visible) continue;
            foreach (var actor in layer.Actors)
            {
                if (!actor.IsActive) continue;
                var ap   = actor.Transform.Position;
                float dx = ap.X - worldPos.X;
                float dy = ap.Y - worldPos.Y;
                float d  = MathF.Sqrt(dx * dx + dy * dy);
                if (d < pickRadius && d < bestDist)
                {
                    bestDist = d;
                    best     = actor;
                }
            }
        }

        EditorState.SelectActor(best);
    }

    // -------------------------------------------------------------------------
    // Coordinate conversion
    // -------------------------------------------------------------------------

    private Vector2 WorldToScreen(Vector2 world)
        => _vpMin + _cameraOffset + world * _cameraZoom;

    private Vector2 ScreenToWorld(Vector2 screen)
        => (screen - _vpMin - _cameraOffset) / _cameraZoom;

    private bool IsInsideViewport(Vector2 screenPos)
        => screenPos.X >= _vpMin.X && screenPos.X <= _vpMax.X
        && screenPos.Y >= _vpMin.Y && screenPos.Y <= _vpMax.Y;
}
