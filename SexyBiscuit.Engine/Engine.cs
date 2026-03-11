using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Assets;
using SexyBiscuit.Engine.Input;
using SexyBiscuit.Engine.Audio;

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

        Assets       = new AssetManager(GraphicsDevice, Content);
        Input        = new InputManager(Config);
        Audio        = new AudioManager();
        SceneManager = new SceneManager(this);

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
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        Input.Update(dt);

        // Fixed timestep accumulator for physics and network
        _fixedAccumulator += dt;
        while (_fixedAccumulator >= Config.FixedTimestep)
        {
            SceneManager.FixedUpdate(Config.FixedTimestep);
            _fixedAccumulator -= Config.FixedTimestep;
        }

        SceneManager.Update(dt);
        SceneManager.LateUpdate(dt);
        Audio.Update(dt);

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Config.ClearColour);
        SceneManager.Draw(SpriteBatch);
        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
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
}
