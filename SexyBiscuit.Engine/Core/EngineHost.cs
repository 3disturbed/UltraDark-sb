using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
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
        if (pumpInput) Input.Update(unscaledDt);

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

        SpriteBatch.Begin();
        SceneManager.Draw(SpriteBatch);
        SpriteBatch.End();
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
