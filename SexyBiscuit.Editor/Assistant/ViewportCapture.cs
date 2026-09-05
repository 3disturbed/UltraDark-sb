using System.Collections.Concurrent;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using Scene = SexyBiscuit.Engine.Core.Scene;

namespace SexyBiscuit.Editor.Assistant;

/// <summary>
/// Turns "what does the viewport look like?" into a PNG. Requests come from any thread and are
/// serviced on the draw thread, where the render target and the device are in hand.
/// </summary>
public sealed class ViewportCapture
{
    /// <summary>A camera to render the scene from without touching any real camera.</summary>
    public sealed record CameraPose(Vector3 Position, Vector3? LookAt, Vector3? EulerDegrees, float FieldOfView);

    private sealed class Request
    {
        public int  Width;
        public int  Height;
        public int  MaxWidth;
        public bool IncludeUi;
        public CameraPose? Pose;
        public CancellationToken Cancellation;
        public readonly TaskCompletionSource<byte[]> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentQueue<Request> _scene = new();
    private readonly ConcurrentQueue<Request> _ui    = new();

    /// <summary>The viewport as the editor draws it, downscaled to <paramref name="maxWidth"/>.</summary>
    public Task<byte[]> CaptureViewportAsync(int maxWidth, bool includeUi, CancellationToken cancellation)
    {
        var request = new Request { MaxWidth = Math.Clamp(maxWidth, 64, 4096), IncludeUi = includeUi, Cancellation = cancellation };
        (includeUi ? _ui : _scene).Enqueue(request);
        return request.Tcs.Task;
    }

    /// <summary>The scene from an arbitrary pose, at an exact size.</summary>
    public Task<byte[]> CaptureFromAsync(CameraPose pose, int width, int height, CancellationToken cancellation)
    {
        var request = new Request
        {
            Pose         = pose,
            Width        = Math.Clamp(width, 64, 4096),
            Height       = Math.Clamp(height, 64, 4096),
            Cancellation = cancellation,
        };
        _scene.Enqueue(request);
        return request.Tcs.Task;
    }

    /// <summary>Call right after the viewport render target has been drawn and unset.</summary>
    public void ServiceScene(GraphicsDevice gd, RenderTarget2D? viewport, SpriteBatch spriteBatch, EngineHost? engine, Scene? scene)
    {
        while (_scene.TryDequeue(out var request))
        {
            if (request.Cancellation.IsCancellationRequested)
            {
                request.Tcs.TrySetCanceled(request.Cancellation);
                continue;
            }

            try
            {
                byte[] png = request.Pose != null
                    ? RenderFromPose(gd, engine, scene, request)
                    : DownscaleViewport(gd, viewport, spriteBatch, request);
                request.Tcs.TrySetResult(png);
            }
            catch (Exception ex)
            {
                request.Tcs.TrySetException(ex);
            }
        }
    }

    /// <summary>Call after ImGui has rendered, for captures that want the panels too.</summary>
    public void ServiceBackBuffer(GraphicsDevice gd)
    {
        while (_ui.TryDequeue(out var request))
        {
            if (request.Cancellation.IsCancellationRequested)
            {
                request.Tcs.TrySetCanceled(request.Cancellation);
                continue;
            }

            try
            {
                int w = gd.PresentationParameters.BackBufferWidth;
                int h = gd.PresentationParameters.BackBufferHeight;
                var pixels = new Color[w * h];
                gd.GetBackBufferData(pixels);

                using var texture = new Texture2D(gd, w, h);
                texture.SetData(pixels);

                var (outW, outH) = Fit(w, h, request.MaxWidth);
                using var stream = new MemoryStream();
                texture.SaveAsPng(stream, outW, outH);
                request.Tcs.TrySetResult(stream.ToArray());
            }
            catch (Exception ex)
            {
                request.Tcs.TrySetException(ex);
            }
        }
    }

    private static byte[] DownscaleViewport(GraphicsDevice gd, RenderTarget2D? viewport, SpriteBatch spriteBatch, Request request)
    {
        if (viewport == null) throw new InvalidOperationException("The viewport has not been rendered yet.");

        var (w, h) = Fit(viewport.Width, viewport.Height, request.MaxWidth);

        // Resample on the GPU: SaveAsPng's own resize is a nearest-neighbour shrink.
        using var target = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(target);
        gd.Clear(Color.Black);
        spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
        spriteBatch.Draw(viewport, new Rectangle(0, 0, w, h), Color.White);
        spriteBatch.End();
        gd.SetRenderTarget(null);

        using var stream = new MemoryStream();
        target.SaveAsPng(stream, w, h);
        return stream.ToArray();
    }

    private static byte[] RenderFromPose(GraphicsDevice gd, EngineHost? engine, Scene? scene, Request request)
    {
        if (engine == null || scene == null) throw new InvalidOperationException("No scene is loaded.");
        var pose = request.Pose!;

        // A detached camera, never added to the scene, exactly like the editor's own camera.
        var rig       = new Actor("(Capture Camera)");
        var transform = rig.AddComponent<Transform3D>();
        var camera    = rig.AddComponent<Camera3D>();
        camera.FieldOfView = Math.Clamp(pose.FieldOfView, 10f, 170f);
        transform.Position = pose.Position;
        if (pose.LookAt.HasValue)            transform.LookAt(pose.LookAt.Value);
        else if (pose.EulerDegrees.HasValue) transform.EulerAngles = pose.EulerDegrees.Value;

        var previous = engine.Renderer3D.OverrideCamera;
        try
        {
            using var target = new RenderTarget2D(gd, request.Width, request.Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
            gd.SetRenderTarget(target);
            gd.Clear(new Color(18, 20, 26));

            engine.Renderer3D.OverrideCamera = camera;
            engine.Renderer3D.Render(scene);

            gd.SetRenderTarget(null);

            using var stream = new MemoryStream();
            target.SaveAsPng(stream, request.Width, request.Height);
            return stream.ToArray();
        }
        finally
        {
            engine.Renderer3D.OverrideCamera = previous;

            // The camera registered itself on Awake; deregister so Camera3D.Main never picks it.
            camera.OnDestroy();
            transform.OnDestroy();
        }
    }

    private static (int w, int h) Fit(int width, int height, int maxWidth)
    {
        if (width <= maxWidth) return (Math.Max(1, width), Math.Max(1, height));
        float scale = maxWidth / (float)width;
        return (maxWidth, Math.Max(1, (int)MathF.Round(height * scale)));
    }
}
