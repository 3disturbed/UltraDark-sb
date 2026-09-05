using System.Runtime.InteropServices;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;
using XnaButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;

namespace SexyBiscuit.Editor;

/// <summary>
/// Minimal MonoGame-compatible ImGui renderer.
/// Manages the ImGui context, IO wiring, vertex/index buffer upload,
/// and texture binding for ImGui image calls.
/// </summary>
public sealed class ImGuiRenderer : IDisposable
{
    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------
    private GraphicsDevice _gd = null!;
    private GameWindow     _window = null!;

    // Vertex / index buffers (grown on demand)
    private DynamicVertexBuffer? _vertexBuffer;
    private DynamicIndexBuffer?  _indexBuffer;
    private int _vertexBufferSize;
    private int _indexBufferSize;

    // Shader / rasterizer
    private BasicEffect    _effect = null!;
    private RasterizerState _rasterizerState = null!;

    // Font texture
    private Texture2D? _fontTexture;
    private IntPtr     _fontTextureId;

    // Texture registry
    private readonly Dictionary<IntPtr, Texture2D> _textures = new();
    private int _textureIdCounter = 1;

    // Input
    private int _scrollWheelValue;
    private int _hScrollWheelValue;

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public void Initialize(GraphicsDevice gd, GameWindow window)
    {
        _gd     = gd;
        _window = window;

        ImGui.CreateContext();

        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

        // NOT ViewportsEnable. That flag makes Dear ImGui treat io.MousePos as absolute
        // desktop coordinates and expects the backend to create real OS windows on demand
        // (Platform_CreateWindow, Platform_GetWindowPos, and the rest). MonoGame has one
        // GameWindow and no way to spawn more, so none of that exists here — the result
        // was every click landing offset by the window's position on screen.
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;

        // Key mapping is handled automatically in ImGui.NET 1.90+

        // Hook char input
        window.TextInput += OnTextInput;

        BuildFontTexture();
        BuildEffect();
        BuildRasterizerState();
    }

    public void NewFrame(GameTime gt)
    {
        var io = ImGui.GetIO();

        // DisplaySize is in the same units as io.MousePos — window points. On a display
        // where the framebuffer is larger than the window (a Retina panel with high-DPI
        // enabled) the ratio goes in DisplayFramebufferScale, and the renderer multiplies
        // by it to get pixels. They are equal on a 1:1 display, but deriving the scale
        // rather than assuming One means the UI does not halve itself on a Retina Mac.
        var clientBounds = _window.ClientBounds;
        float pointsW = MathF.Max(1f, clientBounds.Width);
        float pointsH = MathF.Max(1f, clientBounds.Height);

        io.DisplaySize = new System.Numerics.Vector2(pointsW, pointsH);
        io.DisplayFramebufferScale = new System.Numerics.Vector2(
            _gd.PresentationParameters.BackBufferWidth  / pointsW,
            _gd.PresentationParameters.BackBufferHeight / pointsH);
        io.DeltaTime = (float)gt.ElapsedGameTime.TotalSeconds;
        if (io.DeltaTime <= 0f) io.DeltaTime = 1f / 60f;

        UpdateMouse(io);
        UpdateKeyboard(io);

        ImGui.NewFrame();
    }

    public void Render()
    {
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData());
    }

    /// <summary>
    /// Binds a MonoGame Texture2D and returns the IntPtr ID suitable for ImGui.Image().
    /// Calling this with the same texture multiple times returns the same ID.
    /// </summary>
    public IntPtr BindTexture(Texture2D texture)
    {
        foreach (var (ptr, tex) in _textures)
            if (tex == texture) return ptr;

        var id = new IntPtr(_textureIdCounter++);
        _textures[id] = texture;
        return id;
    }

    public void UnbindTexture(IntPtr id) => _textures.Remove(id);

    // -------------------------------------------------------------------------
    // Private: input
    // -------------------------------------------------------------------------

    private void UpdateMouse(ImGuiIOPtr io)
    {
        var mouse    = Mouse.GetState();
        var keyboard = Keyboard.GetState();

        // Window-relative, which is the space ImGui wants with multi-viewport off.
        io.MousePos = new System.Numerics.Vector2(mouse.X, mouse.Y);

        io.MouseDown[0] = mouse.LeftButton   == XnaButtonState.Pressed;
        io.MouseDown[1] = mouse.RightButton  == XnaButtonState.Pressed;
        io.MouseDown[2] = mouse.MiddleButton == XnaButtonState.Pressed;

        // One wheel notch is 120 units. Passing the real fraction rather than snapping to
        // +/-1 is what makes trackpad scrolling feel continuous instead of jumping a line
        // at a time.
        int scrollDelta = mouse.ScrollWheelValue - _scrollWheelValue;
        io.MouseWheel   = scrollDelta / 120f;
        _scrollWheelValue = mouse.ScrollWheelValue;

        int hScrollDelta = mouse.HorizontalScrollWheelValue - _hScrollWheelValue;
        io.MouseWheelH   = hScrollDelta / 120f;
        _hScrollWheelValue = mouse.HorizontalScrollWheelValue;

        io.KeyCtrl  = keyboard.IsKeyDown(XnaKeys.LeftControl)  || keyboard.IsKeyDown(XnaKeys.RightControl);
        io.KeyShift = keyboard.IsKeyDown(XnaKeys.LeftShift)    || keyboard.IsKeyDown(XnaKeys.RightShift);
        io.KeyAlt   = keyboard.IsKeyDown(XnaKeys.LeftAlt)      || keyboard.IsKeyDown(XnaKeys.RightAlt);
        io.KeySuper = keyboard.IsKeyDown(XnaKeys.LeftWindows)  || keyboard.IsKeyDown(XnaKeys.RightWindows);

        UpdateMouseCursor(io);
    }

    private MouseCursor? _appliedCursor;

    /// <summary>
    /// Applies the cursor shape ImGui asks for — a resize arrow on a window edge, a beam
    /// over a text field, a hand over a link.
    /// </summary>
    /// <remarks>
    /// The backend advertises <see cref="ImGuiBackendFlags.HasMouseCursors"/>, which is a
    /// promise to do this. It was being advertised without being honoured, so the pointer
    /// stayed an arrow everywhere and panel edges gave no hint they were draggable.
    /// The shape is only pushed when it changes; MonoGame's SetCursor is a platform call.
    /// </remarks>
    private void UpdateMouseCursor(ImGuiIOPtr io)
    {
        if ((io.ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) != 0) return;

        var desired = ImGui.GetMouseCursor();

        var cursor = desired switch
        {
            ImGuiMouseCursor.TextInput  => MouseCursor.IBeam,
            ImGuiMouseCursor.ResizeAll  => MouseCursor.SizeAll,
            ImGuiMouseCursor.ResizeNS   => MouseCursor.SizeNS,
            ImGuiMouseCursor.ResizeEW   => MouseCursor.SizeWE,
            ImGuiMouseCursor.ResizeNESW => MouseCursor.SizeNESW,
            ImGuiMouseCursor.ResizeNWSE => MouseCursor.SizeNWSE,
            ImGuiMouseCursor.Hand       => MouseCursor.Hand,
            ImGuiMouseCursor.NotAllowed => MouseCursor.No,
            _                            => MouseCursor.Arrow,
        };

        if (ReferenceEquals(cursor, _appliedCursor)) return;
        _appliedCursor = cursor;
        Mouse.SetCursor(cursor);
    }

    private void UpdateKeyboard(ImGuiIOPtr io)
    {
        var keyboard = Keyboard.GetState();

        MapKey(io, keyboard, XnaKeys.Tab, ImGuiKey.Tab);
        MapKey(io, keyboard, XnaKeys.Left, ImGuiKey.LeftArrow);
        MapKey(io, keyboard, XnaKeys.Right, ImGuiKey.RightArrow);
        MapKey(io, keyboard, XnaKeys.Up, ImGuiKey.UpArrow);
        MapKey(io, keyboard, XnaKeys.Down, ImGuiKey.DownArrow);
        MapKey(io, keyboard, XnaKeys.PageUp, ImGuiKey.PageUp);
        MapKey(io, keyboard, XnaKeys.PageDown, ImGuiKey.PageDown);
        MapKey(io, keyboard, XnaKeys.Home, ImGuiKey.Home);
        MapKey(io, keyboard, XnaKeys.End, ImGuiKey.End);
        MapKey(io, keyboard, XnaKeys.Delete, ImGuiKey.Delete);
        MapKey(io, keyboard, XnaKeys.Back, ImGuiKey.Backspace);
        MapKey(io, keyboard, XnaKeys.Enter, ImGuiKey.Enter);
        MapKey(io, keyboard, XnaKeys.Escape, ImGuiKey.Escape);
        MapKey(io, keyboard, XnaKeys.A, ImGuiKey.A);
        MapKey(io, keyboard, XnaKeys.C, ImGuiKey.C);
        MapKey(io, keyboard, XnaKeys.V, ImGuiKey.V);
        MapKey(io, keyboard, XnaKeys.X, ImGuiKey.X);
        MapKey(io, keyboard, XnaKeys.Y, ImGuiKey.Y);
        MapKey(io, keyboard, XnaKeys.Z, ImGuiKey.Z);
    }

    private static void MapKey(ImGuiIOPtr io, KeyboardState keyboard, XnaKeys xnaKey, ImGuiKey imGuiKey)
    {
        io.AddKeyEvent(imGuiKey, keyboard.IsKeyDown(xnaKey));
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Character != '\t')
            ImGui.GetIO().AddInputCharacter(e.Character);
    }

    // -------------------------------------------------------------------------
    // Private: font texture
    // -------------------------------------------------------------------------

    private void BuildFontTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.AddFontDefault();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);

        _fontTexture = new Texture2D(_gd, width, height, false, SurfaceFormat.Color);
        var data = new byte[width * height * 4];
        Marshal.Copy(pixels, data, 0, data.Length);
        _fontTexture.SetData(data);

        _fontTextureId = BindTexture(_fontTexture);
        io.Fonts.SetTexID(_fontTextureId);
        io.Fonts.ClearTexData();
    }

    // -------------------------------------------------------------------------
    // Private: effect / rasterizer
    // -------------------------------------------------------------------------

    private void BuildEffect()
    {
        _effect = new BasicEffect(_gd)
        {
            World           = Matrix.Identity,
            TextureEnabled  = true,
            VertexColorEnabled = true,
        };
    }

    private void BuildRasterizerState()
    {
        _rasterizerState = new RasterizerState
        {
            CullMode            = CullMode.None,
            DepthBias           = 0f,
            FillMode            = FillMode.Solid,
            MultiSampleAntiAlias = false,
            ScissorTestEnable   = true,
            SlopeScaleDepthBias = 0f,
        };
    }

    // -------------------------------------------------------------------------
    // RenderDrawData
    // -------------------------------------------------------------------------

    private void RenderDrawData(ImDrawDataPtr drawData)
    {
        if (drawData.CmdListsCount == 0) return;

        int fbWidth  = (int)(drawData.DisplaySize.X * drawData.FramebufferScale.X);
        int fbHeight = (int)(drawData.DisplaySize.Y * drawData.FramebufferScale.Y);
        if (fbWidth <= 0 || fbHeight <= 0) return;

        // Ensure vertex and index buffers are large enough
        EnsureBuffers(drawData);

        // Save state
        var prevSamplerState   = _gd.SamplerStates[0];
        var prevBlendState     = _gd.BlendState;
        var prevDepthState     = _gd.DepthStencilState;
        var prevRastState      = _gd.RasterizerState;
        var prevScissor        = _gd.ScissorRectangle;
        var prevViewport       = _gd.Viewport;

        _gd.BlendState          = BlendState.NonPremultiplied;
        _gd.SamplerStates[0]    = SamplerState.LinearClamp;
        _gd.DepthStencilState   = DepthStencilState.DepthRead;
        _gd.RasterizerState     = _rasterizerState;

        // Projection
        _effect.Projection = Matrix.CreateOrthographicOffCenter(
            drawData.DisplayPos.X,
            drawData.DisplayPos.X + drawData.DisplaySize.X,
            drawData.DisplayPos.Y + drawData.DisplaySize.Y,
            drawData.DisplayPos.Y,
            -1f, 1f);

        // Upload and draw each command list
        var clipOff   = drawData.DisplayPos;
        var clipScale = drawData.FramebufferScale;

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];

            // Upload vertices
            var vtxArray = new VertexPositionColorTexture[cmdList.VtxBuffer.Size];
            for (int i = 0; i < cmdList.VtxBuffer.Size; i++)
            {
                var v = cmdList.VtxBuffer[i];
                vtxArray[i] = new VertexPositionColorTexture(
                    new Vector3(v.pos.X, v.pos.Y, 0f),
                    new XnaColor(
                        (int)(v.col & 0xFF),
                        (int)((v.col >> 8)  & 0xFF),
                        (int)((v.col >> 16) & 0xFF),
                        (int)((v.col >> 24) & 0xFF)),
                    new Vector2(v.uv.X, v.uv.Y));
            }
            _vertexBuffer!.SetData(vtxArray, 0, vtxArray.Length, SetDataOptions.Discard);

            // Upload indices
            var idxArray = new short[cmdList.IdxBuffer.Size];
            for (int i = 0; i < cmdList.IdxBuffer.Size; i++)
                idxArray[i] = (short)cmdList.IdxBuffer[i];
            _indexBuffer!.SetData(idxArray, 0, idxArray.Length, SetDataOptions.Discard);

            _gd.SetVertexBuffer(_vertexBuffer);
            _gd.Indices = _indexBuffer;

            int idxOffset = 0;
            for (int cmdIdx = 0; cmdIdx < cmdList.CmdBuffer.Size; cmdIdx++)
            {
                var cmd = cmdList.CmdBuffer[cmdIdx];

                if (cmd.UserCallback != IntPtr.Zero)
                {
                    idxOffset += (int)cmd.ElemCount;
                    continue;
                }

                // Scissor rect
                var clipMin = new System.Numerics.Vector2(
                    (cmd.ClipRect.X - clipOff.X) * clipScale.X,
                    (cmd.ClipRect.Y - clipOff.Y) * clipScale.Y);
                var clipMax = new System.Numerics.Vector2(
                    (cmd.ClipRect.Z - clipOff.X) * clipScale.X,
                    (cmd.ClipRect.W - clipOff.Y) * clipScale.Y);

                if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y)
                {
                    idxOffset += (int)cmd.ElemCount;
                    continue;
                }

                _gd.ScissorRectangle = new XnaRectangle(
                    (int)clipMin.X, (int)clipMin.Y,
                    (int)(clipMax.X - clipMin.X),
                    (int)(clipMax.Y - clipMin.Y));

                // Texture
                if (_textures.TryGetValue(cmd.GetTexID(), out var texture))
                    _effect.Texture = texture;

                foreach (var pass in _effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _gd.DrawIndexedPrimitives(
                        PrimitiveType.TriangleList,
                        (int)cmd.VtxOffset,
                        idxOffset,
                        (int)cmd.ElemCount / 3);
                }

                idxOffset += (int)cmd.ElemCount;
            }
        }

        // Restore state
        _gd.BlendState        = prevBlendState;
        _gd.SamplerStates[0]  = prevSamplerState;
        _gd.DepthStencilState = prevDepthState;
        _gd.RasterizerState   = prevRastState;
        _gd.ScissorRectangle  = prevScissor;
        _gd.Viewport          = prevViewport;
    }

    private void EnsureBuffers(ImDrawDataPtr drawData)
    {
        int totalVtx = 0, totalIdx = 0;
        for (int i = 0; i < drawData.CmdListsCount; i++)
        {
            totalVtx += drawData.CmdLists[i].VtxBuffer.Size;
            totalIdx += drawData.CmdLists[i].IdxBuffer.Size;
        }

        if (_vertexBuffer == null || _vertexBufferSize < totalVtx)
        {
            _vertexBuffer?.Dispose();
            _vertexBufferSize = Math.Max(totalVtx, 4096);
            _vertexBuffer = new DynamicVertexBuffer(
                _gd, VertexPositionColorTexture.VertexDeclaration,
                _vertexBufferSize, BufferUsage.WriteOnly);
        }

        if (_indexBuffer == null || _indexBufferSize < totalIdx)
        {
            _indexBuffer?.Dispose();
            _indexBufferSize = Math.Max(totalIdx, 8192);
            _indexBuffer = new DynamicIndexBuffer(
                _gd, IndexElementSize.SixteenBits,
                _indexBufferSize, BufferUsage.WriteOnly);
        }
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        _window.TextInput -= OnTextInput;
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _fontTexture?.Dispose();
        _effect?.Dispose();
        _rasterizerState?.Dispose();
    }
}
