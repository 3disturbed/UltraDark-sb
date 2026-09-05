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

    /// <summary>
    /// Every engine service. Created in <c>Initialize</c>; the properties below forward
    /// to it so game code can keep saying <c>SBEngine.Instance.Assets</c>.
    /// </summary>
    public EngineHost Host { get; private set; } = null!;

    public SpriteBatch  SpriteBatch  => Host.SpriteBatch;
    public SceneManager SceneManager => Host.SceneManager;
    public AssetManager Assets       => Host.Assets;
    public InputManager Input        => Host.Input;
    public AudioManager Audio        => Host.Audio;

    /// <summary>Session-wide state and subsystems that outlive every scene.</summary>
    public GameInstance GameInstance => Host.GameInstance;

    /// <summary>Schedules delayed and repeating callbacks.</summary>
    public TimerManager Timers => Host.Timers;

    /// <summary>Drives <see cref="Coroutine"/> instances.</summary>
    public CoroutineRunner Coroutines => Host.Coroutines;

    /// <summary>The 3D forward renderer, run before the 2D pass each frame.</summary>
    public RenderSystem3D Renderer3D => Host.Renderer3D;

    /// <summary>The 2D sprite renderer.</summary>
    public RenderSystem2D Renderer2D => Host.Renderer2D;

    // -------------------------------------------------------------------------
    // Config
    // -------------------------------------------------------------------------
    public EngineConfig Config { get; }


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

        Host = new EngineHost(GraphicsDevice, Content, Config);
        Host.Input.CursorVisibilityChanged = visible => IsMouseVisible = visible;

        OnEngineReady();
    }

    protected override void LoadContent()
    {
        // The sprite batch belongs to the host, which is created in Initialize.
    }

    /// <summary>
    /// Called once the engine is fully initialized. The default queues
    /// <see cref="EngineConfig.StartScene"/> when one is configured; override to load your
    /// first scene yourself.
    /// </summary>
    protected virtual void OnEngineReady()
    {
        if (!string.IsNullOrEmpty(Config.StartScene))
            SceneManager.LoadScene(Config.StartScene);
    }

    // -------------------------------------------------------------------------
    // Game loop
    // -------------------------------------------------------------------------
    protected override void Update(GameTime gameTime)
    {
        Host.Tick((float)gameTime.ElapsedGameTime.TotalSeconds);
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Config.ClearColour);
        Host.Render();
        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        Host?.Dispose();
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

    /// <summary>
    /// Reads a project's <c>ProjectSettings.json</c> — the same file the editor reads. Keys are
    /// matched ignoring case, so the templates' PascalCase and the demo's camelCase both work;
    /// <c>appName</c> is accepted for <see cref="WindowTitle"/>. A missing file gives defaults
    /// with a note on stderr, and <c>Assets/ProjectSettings.json</c> is tried as well.
    /// </summary>
    public static EngineConfig FromProjectSettings(string path)
    {
        var config = new EngineConfig();

        string? file = null;
        foreach (var candidate in new[] { path, Path.Combine("Assets", path) })
        {
            string full = Core.ProjectPaths.Resolve(candidate);
            if (File.Exists(full)) { file = full; break; }
        }

        if (file == null)
        {
            Console.Error.WriteLine($"[EngineConfig] '{path}' not found; using defaults.");
            return config;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var value = property.Value;
                switch (property.Name.ToLowerInvariant())
                {
                    case "windowtitle":
                    case "appname":          if (value.ValueKind == System.Text.Json.JsonValueKind.String) config.WindowTitle = value.GetString() ?? config.WindowTitle; break;
                    case "windowwidth":      if (value.TryGetInt32(out int w)) config.WindowWidth = w; break;
                    case "windowheight":     if (value.TryGetInt32(out int h)) config.WindowHeight = h; break;
                    case "fullscreen":       config.Fullscreen = ReadBool(value, config.Fullscreen); break;
                    case "vsync":            config.VSync = ReadBool(value, config.VSync); break;
                    case "showcursor":       config.ShowCursor = ReadBool(value, config.ShowCursor); break;
                    case "allowresize":      config.AllowResize = ReadBool(value, config.AllowResize); break;
                    case "hotreload":        config.HotReload = ReadBool(value, config.HotReload); break;
                    case "enable3d":         config.Enable3D = ReadBool(value, config.Enable3D); break;
                    case "enablephysics2d":  config.EnablePhysics2D = ReadBool(value, config.EnablePhysics2D); break;
                    case "enablephysics3d":  config.EnablePhysics3D = ReadBool(value, config.EnablePhysics3D); break;
                    case "startscene":       if (value.ValueKind == System.Text.Json.JsonValueKind.String) config.StartScene = value.GetString() ?? ""; break;
                    case "fixedtimestep":    if (value.TryGetSingle(out float step) && step > 0f) config.FixedTimestep = step; break;
                    case "maxfixedstepsperframe": if (value.TryGetInt32(out int steps) && steps > 0) config.MaxFixedStepsPerFrame = steps; break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EngineConfig] Could not read '{file}': {ex.Message}; using defaults.");
        }

        return config;

        static bool ReadBool(System.Text.Json.JsonElement element, bool fallback) => element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True  => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => fallback,
        };
    }
}
