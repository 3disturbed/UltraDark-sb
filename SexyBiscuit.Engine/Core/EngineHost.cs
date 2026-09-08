using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Assets;
using SexyBiscuit.Engine.Audio;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Input;
using SexyBiscuit.Engine.Physics;
using SexyBiscuit.Engine.Rendering;

namespace SexyBiscuit.Engine.Core;

/// <summary>
/// Every engine service, and the frame loop that drives them, with no window of its own.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SBEngine"/> is a MonoGame <c>Game</c>, which is right for a game: it owns
/// the window, the device and the message loop. It is wrong for anything that already has
/// those — an editor, a tool, a headless server — because constructing a <c>Game</c>
/// creates a window whether you show it or not.
/// </para>
/// <para>
/// That is not merely wasteful. MonoGame's <c>Mouse</c> is static and binds to whichever
/// window was created last, with a no-op setter on the SDL backend, so a second
/// <c>Game</c> silently steals cursor coordinates and there is no supported way to give
/// them back. Hosting through this class instead of constructing an <c>SBEngine</c> is
/// the only way to avoid it.
/// </para>
/// <para>
/// <see cref="SBEngine"/> now owns one of these and forwards to it, so the two paths
/// share exactly one implementation of startup and the frame loop.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Inside a tool that already owns a GraphicsDevice:
/// var host = new EngineHost(GraphicsDevice, Content, new EngineConfig { Enable3D = false });
/// host.SceneManager.CreateScene("Untitled");
///
/// // Each frame:
/// host.Tick(dt);
/// host.Renderer3D.Render(host.SceneManager.ActiveScene!);
/// </code>
/// </example>
public sealed class EngineHost : IDisposable
{
    /// <summary>
    /// The host running right now, or null before one exists. Engine code that needs a service
    /// (input, audio, the scene manager) reaches it here, so it works the same under the
    /// standalone game and under the editor; <c>SBEngine.Instance</c> only exists in the former.
    /// </summary>
    public static EngineHost? Current { get; private set; }

    /// <summary>Startup configuration this host was built with.</summary>
    public EngineConfig Config { get; }

    /// <summary>The device everything renders and uploads with.</summary>
    public GraphicsDevice GraphicsDevice { get; }

    /// <summary>Sprite batch for the 2D pass.</summary>
    public SpriteBatch SpriteBatch { get; }

    /// <summary>Texture, audio, font and model loading, with reference counting.</summary>
    public AssetManager Assets { get; }

    /// <summary>Keyboard, mouse, gamepad and touch, behind named actions.</summary>
    public InputManager Input { get; }

    /// <summary>Buses, voices and streaming.</summary>
    public AudioManager Audio { get; }

    /// <summary>Scene loading and the active scene.</summary>
    public SceneManager SceneManager { get; }

    /// <summary>Delayed and repeating callbacks.</summary>
    public TimerManager Timers { get; }

    /// <summary>Frame-spanning sequences.</summary>
    public CoroutineRunner Coroutines { get; }

    /// <summary>The 2D sprite renderer.</summary>
    public RenderSystem2D Renderer2D { get; }

    /// <summary>The 3D forward renderer.</summary>
    public RenderSystem3D Renderer3D { get; }

    /// <summary>Session-wide state and subsystems.</summary>
    public GameInstance GameInstance { get; }

    private float _fixedAccumulator;

    /// <param name="graphicsDevice">Device used for asset upload and rendering.</param>
    /// <param name="content">Content manager for anything loaded through the pipeline.</param>
    /// <param name="config">Startup configuration. A default is used when null.</param>
    public EngineHost(GraphicsDevice graphicsDevice, ContentManager content, EngineConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(graphicsDevice);
        ArgumentNullException.ThrowIfNull(content);

        Config         = config ?? new EngineConfig();
        GraphicsDevice = graphicsDevice;
        Current        = this;

        Time.Reset();
        Time.FixedDeltaTime = Config.FixedTimestep;

        SpriteBatch  = new SpriteBatch(graphicsDevice);
        Assets       = AssetManager.Current = new AssetManager(graphicsDevice, content);
        Input        = new InputManager(Config);
        Audio        = new AudioManager();
        SceneManager = new SceneManager();

        Timers     = TimerManager.Instance    = new TimerManager();
        Coroutines = CoroutineRunner.Instance = new CoroutineRunner();

        Renderer2D = new RenderSystem2D();
        Renderer2D.Initialize(graphicsDevice);

        Renderer3D = new RenderSystem3D();
        Renderer3D.Initialize(graphicsDevice);

        InitialiseGraphics();

        GameInstance = Config.GameInstanceFactory?.Invoke() ?? new GameInstance();
        GameInstance.InternalInit();
        GameInstance.InternalStart();
    }

    /// <summary>
    /// Advances one frame: time, the game instance, fixed steps, update, tweens, timers,
    /// coroutines, late update and audio.
    /// </summary>
    /// <param name="rawDeltaSeconds">Real seconds since the previous frame.</param>
    /// <param name="pumpInput">
    /// Whether to poll input. A host with its own UI usually wants false most of the time
    /// and true only while the game has focus, so editor shortcuts and game bindings never
    /// fight over the same keys.
    /// </param>
    public void Tick(float rawDeltaSeconds, bool pumpInput = true)
    {
        Time.Advance(rawDeltaSeconds);

        float dt         = Time.DeltaTime;
        float unscaledDt = Time.UnscaledDeltaTime;

        // Input runs on unscaled time so menus stay responsive while the game is paused.
        if (pumpInput)
        {
            Input.Update(unscaledDt);

            // The touch joysticks claim a half of the screen each, and without this they use a
            // hardcoded 1920: on a narrower window the right stick's half starts too far right
            // and steals fingers meant for the left.
            var backBuffer = GraphicsDevice.PresentationParameters;
            Input.Touch.SetScreenSize(backBuffer.BackBufferWidth, backBuffer.BackBufferHeight);
        }

        // Before the game ticks, so a script reading UiNode.Clicked in Update sees this
        // frame's click rather than the previous one's.
        if (pumpInput) UpdateUiCanvases(unscaledDt);

        if (pumpInput && GraphicsMenuKey != Keys.None && Input.IsKeyPressed(GraphicsMenuKey))
            ToggleGraphicsMenu();

        // Unscaled, so a game paused behind the menu still drives it.
        _graphicsMenu?.Tick();
        TickBenchmark(unscaledDt);

        GameInstance.InternalTick(dt);

        // Fixed steps, capped so a long frame catches up over several frames rather than
        // owing more work every frame — the spiral of death.
        float step = Config.FixedTimestep;
        Time.FixedDeltaTime = step * Time.TimeScale;

        _fixedAccumulator += dt;
        int steps = 0;
        while (_fixedAccumulator >= step && steps < Config.MaxFixedStepsPerFrame)
        {
            // Physics steps before FixedUpdate so components see the result of the step
            // they are reacting to, not the previous one.
            if (Config.EnablePhysics2D) PhysicsSystem2D.Instance.FixedStep(step);
            if (Config.EnablePhysics3D) PhysicsSystem3D.Instance.FixedStep(step);

            SceneManager.FixedUpdate(step);
            _fixedAccumulator -= step;
            steps++;
        }

        if (_fixedAccumulator > step * Config.MaxFixedStepsPerFrame)
            _fixedAccumulator = 0f;

        SceneManager.Update(dt);

        Tween.UpdateAll(dt);
        Timers.Tick(dt, unscaledDt);
        Coroutines.Tick(dt, unscaledDt);

        SceneManager.LateUpdate(dt);
        Audio.Update(dt);
    }

    /// <summary>
    /// Draws the scene: the 3D pass when <see cref="EngineConfig.Enable3D"/> is set, then
    /// the 2D sprite pass over it.
    /// </summary>
    public void Render()
    {
        var scene = SceneManager.ActiveScene;
        if (scene == null) return;

        if (Config.Enable3D) Renderer3D.Render(scene);

        // Through RenderSystem2D, not a bare SpriteBatch.Begin(). The bare call takes
        // SpriteSortMode.Deferred and no transform matrix, which silently drops two things
        // at once: layerDepth stops ordering anything, so a scene draws in creation order
        // and a procedurally built ground lands on top of the world it is under; and the
        // camera's view matrix is never applied, so the view never moves and camera-follow
        // scripts look broken. Both are invisible to every test in this repository — the
        // renderer this line skips is the one that already sorts back-to-front and applies
        // Camera2D.main.
        Renderer2D.RenderScene(SpriteBatch, scene, Rendering.Camera2D.Main);

        DrawScriptUi();
        DrawUiCanvases();
    }

    /// <summary>
    /// The screen-space UI a script built, drawn last and with no camera transform.
    /// </summary>
    /// <remarks>
    /// Outside the camera matrix on purpose: the HUD is in screen space, so a camera
    /// that has panned, zoomed or shaken must not take it along. Its own batch for the
    /// same reason — the scene's batch carries the view transform.
    /// </remarks>
    private void DrawScriptUi()
    {
        var ui = UI.ScriptUi.Instance;
        ui.SetViewport(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);

        var mouse = Input?.MousePosition ?? Microsoft.Xna.Framework.Vector2.Zero;
        ui.SetPointer(mouse.X, mouse.Y, Input?.IsMouseButtonDown(SexyBiscuit.Engine.Input.MouseButton.Left) ?? false);
        ui.Update();

        if (ui.Elements.Count == 0) return;

        // NonPremultiplied for the same reason the scene batch uses it: a panel
        // written "#161920e6" is a straight colour with an alpha, not a
        // premultiplied one.
        SpriteBatch.Begin(blendState: BlendState.NonPremultiplied, samplerState: SamplerState.PointClamp);
        ui.Draw(SpriteBatch);
        SpriteBatch.End();
    }

    // -------------------------------------------------------------------------
    // Graphics settings
    // -------------------------------------------------------------------------

    /// <summary>The quality settings currently in force.</summary>
    public Rendering.GraphicsSettings Graphics { get; private set; } = new();

    /// <summary>
    /// Applies the back-buffer half of a settings change: resolution, fullscreen, vsync
    /// and multisampling. Set by whatever owns the window.
    /// </summary>
    /// <remarks>
    /// A delegate because the <c>GraphicsDeviceManager</c> belongs to the MonoGame
    /// <c>Game</c>, which the host deliberately does not have — an editor and a headless
    /// server run this same loop. Nothing in this repository had ever called
    /// <c>ApplyChanges</c>, so changing a resolution at runtime was not merely
    /// unimplemented but impossible.
    /// </remarks>
    public Action<Rendering.GraphicsSettings>? ApplyDisplaySettings { get; set; }

    /// <summary>What this machine can actually do, probed once at startup.</summary>
    public Rendering.GraphicsCapabilities Capabilities { get; private set; } = new();

    /// <summary>Adopts a settings object and pushes every value to its real owner.</summary>
    public void ApplyGraphics(Rendering.GraphicsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Graphics = settings;
        settings.Apply(this);
    }

    /// <summary>
    /// Picks the settings this session starts on, and applies them.
    /// </summary>
    /// <remarks>
    /// A saved choice always wins. Failing that the project may pin a preset — a
    /// pixel-art 2D game runs at Ultra on a netbook, and probing it down would turn its
    /// own post-processing off for no reason — and only when neither exists is the
    /// machine measured. Probing on every launch would silently undo the choice of
    /// anybody who had deliberately turned something down.
    /// </remarks>
    private void InitialiseGraphics()
    {
        Capabilities = Rendering.GraphicsCapabilities.Probe(GraphicsDevice, Renderer3D);

        var settings = Rendering.GraphicsSettings.Load();
        if (settings is null)
        {
            settings = new Rendering.GraphicsSettings();
            settings.ApplyPreset(string.IsNullOrWhiteSpace(Config.GraphicsPreset)
                ? Capabilities.SuggestPreset()
                : Config.GraphicsPreset);
        }

        ApplyGraphics(settings);
    }

    // -------------------------------------------------------------------------
    // The graphics menu
    // -------------------------------------------------------------------------

    private UI.GraphicsMenu?         _graphicsMenu;
    private Debug.GraphicsBenchmark? _benchmark;

    /// <summary>
    /// The engine's graphics settings screen, built on first use.
    /// </summary>
    /// <remarks>
    /// Every game gets this without asking for it, which is the point: a settings screen
    /// each game writes for itself is a settings screen most games never write.
    /// </remarks>
    public UI.GraphicsMenu GraphicsMenu => _graphicsMenu ??= BuildGraphicsMenu();

    /// <summary>Whether the settings screen is currently up.</summary>
    public bool IsGraphicsMenuOpen => _graphicsMenu?.IsOpen ?? false;

    /// <summary>The key that opens the settings screen, or <c>Keys.None</c> to disable it.</summary>
    public Keys GraphicsMenuKey { get; set; } = Keys.F10;

    /// <summary>Opens the settings screen.</summary>
    public void OpenGraphicsMenu() => GraphicsMenu.Open();

    /// <summary>Opens the settings screen, or closes it if it is already up.</summary>
    public void ToggleGraphicsMenu()
    {
        if (IsGraphicsMenuOpen) GraphicsMenu.Close();
        else GraphicsMenu.Open();
    }

    private UI.GraphicsMenu BuildGraphicsMenu()
    {
        _benchmark = new Debug.GraphicsBenchmark();

        var menu = new UI.GraphicsMenu(Graphics, Capabilities)
        {
            Changed = settings =>
            {
                ApplyGraphics(settings);
                settings.Save();
            },
            BenchmarkRequested = StartBenchmark,
        };

        return menu;
    }

    /// <summary>
    /// Switches to the benchmark preset and starts timing.
    /// </summary>
    /// <remarks>
    /// The player's own settings are kept and put back when the run ends. A benchmark
    /// that left the machine on Ultra afterwards would be a benchmark that made every
    /// laptop unplayable to run once.
    /// </remarks>
    private void StartBenchmark()
    {
        if (_benchmark is null || _graphicsMenu is null) return;

        _benchmark.Restore = Graphics.Clone();

        var running = Graphics.Clone();
        running.ApplyPreset("benchmark");
        ApplyGraphics(running);

        _graphicsMenu.Adopt(running, Capabilities);
        _graphicsMenu.Note = "Running…";
        _graphicsMenu.Refresh();
        _benchmark.Start();
    }

    private void TickBenchmark(float unscaledDt)
    {
        if (_benchmark is not { IsRunning: true }) return;

        if (_graphicsMenu != null)
        {
            _graphicsMenu.Note = $"Running… {_benchmark.Progress * 100f:0}%";
            _graphicsMenu.Refresh();
        }

        Debug.BenchmarkResult? result = _benchmark.Tick(unscaledDt, Renderer3D.Stats);
        if (result is not { } done) return;

        if (_benchmark.Restore is { } previous) ApplyGraphics(previous);

        if (_graphicsMenu != null)
        {
            _graphicsMenu.Adopt(Graphics, Capabilities);
            _graphicsMenu.Note = done.ToString();
            _graphicsMenu.Refresh();
        }
    }

    // -------------------------------------------------------------------------
    // The retained UI
    // -------------------------------------------------------------------------

    /// <summary>
    /// Lays out every <see cref="UI.UiCanvas"/> and feeds it this frame's input.
    /// </summary>
    /// <remarks>
    /// Layout happens here rather than in the paint pass because hit-testing needs
    /// this frame's rectangles, and the paint pass runs after the game has had its turn.
    /// A clean tree costs the dirty-flag check and nothing more.
    /// </remarks>
    private void UpdateUiCanvases(float unscaledDt)
    {
        if (UI.UiCanvas.All.Count == 0) return;

        var frame = BuildUiInputFrame(unscaledDt);
        var viewport = GraphicsDevice.Viewport;

        // A copy, because a script reacting to a click is allowed to destroy a canvas.
        foreach (UI.UiCanvas canvas in new List<UI.UiCanvas>(UI.UiCanvas.All))
        {
            canvas.SetViewport(viewport.Width, viewport.Height);
            canvas.Layout();
            canvas.Input.Update(frame);
        }
    }

    private UI.UiInputFrame BuildUiInputFrame(float unscaledDt)
    {
        var pad = Input.IsGamepadConnected(0) ? Input.GetGamepad(0) : null;
        Vector2 stick = pad?.LeftStick ?? Vector2.Zero;

        // The stick's Y is up-positive and the UI's is down-positive, so the axis is
        // flipped once here rather than in every direction test below it.
        return new UI.UiInputFrame
        {
            DeltaTime      = unscaledDt,
            Pointer        = Input.MousePosition,
            PointerDelta   = Input.MouseDelta,
            PointerDown    = Input.IsMouseButtonDown(SexyBiscuit.Engine.Input.MouseButton.Left),
            PointerIsTouch = Input.Touch.Touches.Count > 0,
            Wheel          = Input.ScrollDelta,
            NavAxis        = new Vector2(stick.X, -stick.Y),
            NavUp          = Input.IsKeyPressed(Keys.Up)    || (pad?.IsButtonPressed(Buttons.DPadUp)    ?? false),
            NavDown        = Input.IsKeyPressed(Keys.Down)  || (pad?.IsButtonPressed(Buttons.DPadDown)  ?? false),
            NavLeft        = Input.IsKeyPressed(Keys.Left)  || (pad?.IsButtonPressed(Buttons.DPadLeft)  ?? false),
            NavRight       = Input.IsKeyPressed(Keys.Right) || (pad?.IsButtonPressed(Buttons.DPadRight) ?? false),
            Confirm        = Input.IsKeyPressed(Keys.Enter) || Input.IsKeyPressed(Keys.Space)
                                                           || (pad?.IsButtonPressed(Buttons.A) ?? false),
            Cancel         = Input.IsKeyPressed(Keys.Escape) || (pad?.IsButtonPressed(Buttons.B) ?? false),
            Typed          = Input.TypedText.Replace("\b", ""),
            Backspace      = Input.TypedText.Contains('\b') || Input.IsKeyPressed(Keys.Back),
        };
    }

    /// <summary>
    /// Paints every canvas over the world, outside the camera transform.
    /// </summary>
    /// <remarks>
    /// The UI this replaces drew through <c>Component.Draw</c>, inside the camera-transformed
    /// world batch, so a HUD panned, zoomed and shook with the camera. Painting here is the
    /// whole reason <see cref="UI.UiCanvas"/> is not a drawing component.
    /// </remarks>
    private void DrawUiCanvases()
    {
        if (UI.UiCanvas.All.Count == 0) return;

        var viewport = GraphicsDevice.Viewport;
        bool ring = UI.UiCanvas.All.Count > 0 && UI.UiCanvas.All[0].Input.Focus.Modes.ShowFocusRing;

        UI.UiPainter.PaintAll(SpriteBatch, viewport.Width, viewport.Height, ring);
    }

    public void Dispose()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        if (ReferenceEquals(AssetManager.Current, Assets)) AssetManager.Current = null;
        GameInstance.InternalShutdown();
        Coroutines.StopAll();
        Timers.ClearAll();
        Renderer3D.Dispose();
        Assets.UnloadAll();
        Audio.Dispose();
        SpriteBatch.Dispose();
    }
}
