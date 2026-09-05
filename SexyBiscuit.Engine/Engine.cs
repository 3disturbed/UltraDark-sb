using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Assets;
using SexyBiscuit.Engine.Input;
using SexyBiscuit.Engine.Audio;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.Physics;
using SexyBiscuit.Engine.Animation;

namespace SexyBiscuit.Engine;

/// <summary>
/// Main engine host. Subclass this in your game to bootstrap the engine,
/// or use SBEngine.Run() directly for code-first entry points.
/// </summary>
public class SBEngine : Game
{
    // -------------------------------------------------------------------------
    // Singleton
    // -------------------------------------------------------------------------
    public static SBEngine Instance { get; private set; } = null!;

    // -------------------------------------------------------------------------
    // Core services (populated in Initialize)
    // -------------------------------------------------------------------------
    public GraphicsDeviceManager Graphics { get; }
    public SpriteBatch SpriteBatch { get; private set; } = null!;
    public SceneManager SceneManager { get; private set; } = null!;
    public AssetManager Assets { get; private set; } = null!;
    public InputManager Input { get; private set; } = null!;
    public AudioManager Audio { get; private set; } = null!;

    /// <summary>Session-wide state and subsystems that outlive every scene.</summary>
    public GameInstance GameInstance { get; private set; } = null!;

    /// <summary>Schedules delayed and repeating callbacks. Ticked between Update and LateUpdate.</summary>
    public TimerManager Timers { get; private set; } = null!;

    /// <summary>Drives <see cref="Coroutine"/> instances. Ticked between Update and LateUpdate.</summary>
    public CoroutineRunner Coroutines { get; private set; } = null!;

    /// <summary>
    /// The 3D forward renderer. Runs before the 2D <see cref="SpriteBatch"/> pass each frame
    /// so sprites and UI composite on top of the 3D scene.
    /// </summary>
    public RenderSystem3D Renderer3D { get; private set; } = null!;

    /// <summary>The 2D sprite renderer used for the scene's <c>Draw</c> pass.</summary>
    public RenderSystem2D Renderer2D { get; private set; } = null!;

    // -------------------------------------------------------------------------
    // Config
    // -------------------------------------------------------------------------
    public EngineConfig Config { get; }

    private float _fixedAccumulator;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public SBEngine(EngineConfig? config = null)
    {
        Instance = this;
        Config = config ?? new EngineConfig();

        Graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = Config.WindowWidth,
            PreferredBackBufferHeight = Config.WindowHeight,
            IsFullScreen              = Config.Fullscreen,
            SynchronizeWithVerticalRetrace = Config.VSync
        };

        Content.RootDirectory = "Assets";
        IsMouseVisible = Config.ShowCursor;
        Window.Title = Config.WindowTitle;
        Window.AllowUserResizing = Config.AllowResize;

        IsFixedTimeStep = false; // we do our own fixed-update accumulator
    }

    // -------------------------------------------------------------------------
    // Initialization
    // -------------------------------------------------------------------------
    protected override void Initialize()
    {
        base.Initialize();

        Time.Reset();
        Time.FixedDeltaTime = Config.FixedTimestep;

        Assets       = new AssetManager(GraphicsDevice, Content);
        Input        = new InputManager(Config);
        Audio        = new AudioManager();
        SceneManager = new SceneManager(this);

        Timers     = TimerManager.Instance     = new TimerManager();
        Coroutines = CoroutineRunner.Instance  = new CoroutineRunner();

        Renderer2D = new RenderSystem2D();
        Renderer2D.Initialize(GraphicsDevice);

        Renderer3D = new RenderSystem3D();
        Renderer3D.Initialize(GraphicsDevice);

        GameInstance = Config.GameInstanceFactory?.Invoke() ?? new GameInstance();
        GameInstance.InternalInit();
        GameInstance.InternalStart();

        OnEngineReady();
    }

    protected override void LoadContent()
    {
        SpriteBatch = new SpriteBatch(GraphicsDevice);
    }

    /// <summary>
    /// Called once the engine is fully initialized. Override to load your first scene.
    /// </summary>
    protected virtual void OnEngineReady() { }

    // -------------------------------------------------------------------------
    // Game loop
    // -------------------------------------------------------------------------
    protected override void Update(GameTime gameTime)
    {
        Time.Advance((float)gameTime.ElapsedGameTime.TotalSeconds);

        float dt         = Time.DeltaTime;
        float unscaledDt = Time.UnscaledDeltaTime;

        // Input runs on unscaled time so menus stay responsive while the game is paused.
        Input.Update(unscaledDt);

        GameInstance.InternalTick(dt);

        // Fixed timestep accumulator for physics and network. Capped so a long frame
        // catches up over several frames instead of stalling in a spiral of death.
        float step = Config.FixedTimestep;
        Time.FixedDeltaTime = step * Time.TimeScale;

        _fixedAccumulator += dt;
        int steps = 0;
        while (_fixedAccumulator >= step && steps < Config.MaxFixedStepsPerFrame)
        {
            // Physics steps before FixedUpdate so components see the results of the
            // step they are reacting to, not the previous one.
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

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Config.ClearColour);

        // 3D first, then the 2D/UI pass composites over it.
        if (Config.Enable3D && SceneManager.ActiveScene != null)
            Renderer3D.Render(SceneManager.ActiveScene);

        SceneManager.Draw(SpriteBatch);
        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        GameInstance?.InternalShutdown();
        Coroutines?.StopAll();
        Timers?.ClearAll();
        Renderer3D?.Dispose();
        Assets.UnloadAll();
        Audio.Dispose();
        base.UnloadContent();
    }

    // -------------------------------------------------------------------------
    // Static helpers
    // -------------------------------------------------------------------------
    /// <summary>
    /// Convenience entry point — creates and runs a default engine instance.
    /// </summary>
    public static void Run(EngineConfig? config = null)
    {
        using var engine = new SBEngine(config);
        ((Microsoft.Xna.Framework.Game)engine).Run();
    }
}

/// <summary>
/// Engine startup configuration. Can be loaded from ProjectSettings.json.
/// </summary>
public class EngineConfig
{
    public string WindowTitle   { get; set; } = "SexyBiscuit Engine";
    public int    WindowWidth   { get; set; } = 1920;
    public int    WindowHeight  { get; set; } = 1080;
    public bool   Fullscreen    { get; set; } = false;
    public bool   VSync         { get; set; } = true;
    public bool   ShowCursor    { get; set; } = true;
    public bool   AllowResize   { get; set; } = true;
    public float  FixedTimestep { get; set; } = 1f / 60f;
    public Color  ClearColour   { get; set; } = Color.CornflowerBlue;
    public bool   HotReload     { get; set; } = true;  // disabled in Release builds
    public string StartScene    { get; set; } = "";

    /// <summary>
    /// Runs the 3D render pass before the 2D pass each frame. Turn off for a pure 2D game
    /// to skip the culling and light-gathering work entirely.
    /// </summary>
    public bool Enable3D { get; set; } = true;

    /// <summary>
    /// Steps the Aether 2D simulation on each fixed update. Turn off in a 3D-only game so
    /// the 2D world is never created or stepped.
    /// </summary>
    public bool EnablePhysics2D { get; set; } = true;

    /// <summary>
    /// Steps the Bepu 3D simulation on each fixed update. Turn off in a 2D-only game so
    /// the 3D simulation is never created or stepped.
    /// </summary>
    public bool EnablePhysics3D { get; set; } = true;

    /// <summary>
    /// Maximum fixed-update steps executed in a single frame. Caps the catch-up work after a
    /// hitch so a slow frame cannot cascade into a permanently slower simulation.
    /// </summary>
    public int MaxFixedStepsPerFrame { get; set; } = 5;

    /// <summary>
    /// Creates the <see cref="Gameplay.GameInstance"/> for this session. Leave null to use the
    /// base class; supply a factory to install your own subclass.
    /// </summary>
    public Func<GameInstance>? GameInstanceFactory { get; set; }
}
