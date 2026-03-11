using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SexyBiscuit.Editor.Panels;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scene;
using Color = Microsoft.Xna.Framework.Color;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace SexyBiscuit.Editor;

/// <summary>
/// Main editor application window. Hosts the engine in a viewport render target
/// and drives all ImGui panels each frame.
/// </summary>
public sealed class EditorApp : Microsoft.Xna.Framework.Game
{
    // -------------------------------------------------------------------------
    // Singleton
    // -------------------------------------------------------------------------
    public static EditorApp Instance { get; private set; } = null!;

    // -------------------------------------------------------------------------
    // Graphics
    // -------------------------------------------------------------------------
    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch = null!;

    // Viewport render target — engine draws into this
    private RenderTarget2D? _viewportTarget;
    private int _viewportWidth  = 1280;
    private int _viewportHeight = 720;

    // -------------------------------------------------------------------------
    // ImGui
    // -------------------------------------------------------------------------
    private ImGuiRenderer _imGui = null!;

    // -------------------------------------------------------------------------
    // Panels
    // -------------------------------------------------------------------------
    private HierarchyPanel     _hierarchy     = null!;
    private InspectorPanel     _inspector     = null!;
    private AssetBrowserPanel  _assetBrowser  = null!;
    private ConsolePanel       _console       = null!;
    private ViewportPanel      _viewport      = null!;
    private BuildSettingsPanel _buildSettings = null!;

    // -------------------------------------------------------------------------
    // Engine
    // -------------------------------------------------------------------------
    private SBEngine? _engine;
    private bool      _engineInitialized;

    // Scene snapshot for play-mode restoration
    private string? _sceneSnapshot;

    // -------------------------------------------------------------------------
    // Input tracking
    // -------------------------------------------------------------------------
    private KeyboardState _prevKeys;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public EditorApp()
    {
        Instance = this;

        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = 1920,
            PreferredBackBufferHeight = 1080,
            IsFullScreen              = false,
            SynchronizeWithVerticalRetrace = true,
        };

        Content.RootDirectory = "Assets";
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "SexyBiscuit Engine — Editor";
        IsFixedTimeStep = false;
    }

    // -------------------------------------------------------------------------
    // Initialize
    // -------------------------------------------------------------------------
    protected override void Initialize()
    {
        base.Initialize();

        // ImGui
        _imGui = new ImGuiRenderer();
        _imGui.Initialize(GraphicsDevice, Window);

        // Viewport render target
        RecreateViewportTarget();

        // Engine instance (headless — renders to our render target)
        _engine = new SBEngine(new EngineConfig
        {
            WindowTitle  = "SexyBiscuit [Editor Preview]",
            WindowWidth  = _viewportWidth,
            WindowHeight = _viewportHeight,
        });

        // Create a default empty scene
        _engine.SceneManager.CreateScene("Untitled");
        _engineInitialized = true;

        // Panels
        _hierarchy    = new HierarchyPanel();
        _inspector    = new InspectorPanel();
        _assetBrowser = new AssetBrowserPanel();
        _console      = new ConsolePanel();
        _viewport     = new ViewportPanel();
        _buildSettings = new BuildSettingsPanel();

        // Route Debug output to editor console
        System.Diagnostics.Trace.Listeners.Add(new EditorTraceListener());
        ConsoleLog.Add("SexyBiscuit Editor initialized.", LogLevel.Info);
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
    }

    // -------------------------------------------------------------------------
    // Update
    // -------------------------------------------------------------------------
    protected override void Update(GameTime gameTime)
    {
        var keys = Keyboard.GetState();

        // Play / Pause / Stop hotkeys
        if (KeyJustPressed(keys, Keys.F5))  EnterPlayMode();
        if (KeyJustPressed(keys, Keys.F6))  TogglePause();
        if (KeyJustPressed(keys, Keys.F7))  ExitPlayMode();

        _prevKeys = keys;

        // Pump engine update only in play mode and not paused
        if (EditorState.IsPlaying && !EditorState.IsPlayPaused && _engineInitialized)
        {
            float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
            _engine?.SceneManager.ActiveScene?.Update(dt);
        }

        base.Update(gameTime);
    }

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------
    protected override void Draw(GameTime gameTime)
    {
        // Render game scene to viewport render target
        if (_viewportTarget != null && _engineInitialized)
        {
            GraphicsDevice.SetRenderTarget(_viewportTarget);
            GraphicsDevice.Clear(Color.Black);

            _spriteBatch.Begin();
            _engine?.SceneManager.ActiveScene?.Draw(_spriteBatch);
            _spriteBatch.End();

            GraphicsDevice.SetRenderTarget(null);
        }

        // Main backbuffer — ImGui
        GraphicsDevice.Clear(new Color(30, 30, 30));

        _imGui.NewFrame(gameTime);

        DrawMainMenuBar();
        DrawDockspace();
        DrawPanels();

        _imGui.Render();

        base.Draw(gameTime);
    }

    // -------------------------------------------------------------------------
    // Menu bar
    // -------------------------------------------------------------------------
    private void DrawMainMenuBar()
    {
        if (!ImGui.BeginMainMenuBar()) return;

        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("New Scene"))
            {
                _engine?.SceneManager.CreateScene("Untitled");
                EditorState.SelectActor(null);
                ConsoleLog.Add("New scene created.", LogLevel.Info);
            }
            if (ImGui.MenuItem("Open Scene..."))
            {
                OpenSceneDialog();
            }
            if (ImGui.MenuItem("Save Scene", "Ctrl+S"))
            {
                SaveCurrentScene();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Exit"))
                Exit();

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem("Undo", "Ctrl+Z")) { /* future */ }
            if (ImGui.MenuItem("Redo", "Ctrl+Y")) { /* future */ }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Play"))
        {
            if (ImGui.MenuItem("Play",  "F5", EditorState.IsPlaying && !EditorState.IsPlayPaused))
                EnterPlayMode();
            if (ImGui.MenuItem("Pause", "F6", EditorState.IsPlayPaused))
                TogglePause();
            if (ImGui.MenuItem("Stop",  "F7", !EditorState.IsPlaying))
                ExitPlayMode();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("Hierarchy"))      { /* already visible */ }
            if (ImGui.MenuItem("Inspector"))      { /* already visible */ }
            if (ImGui.MenuItem("Asset Browser"))  { /* already visible */ }
            if (ImGui.MenuItem("Console"))        { /* already visible */ }
            if (ImGui.MenuItem("Build Settings")) { /* already visible */ }
            ImGui.EndMenu();
        }

        ImGui.EndMainMenuBar();
    }

    // -------------------------------------------------------------------------
    // Dockspace
    // -------------------------------------------------------------------------
    private unsafe void DrawDockspace()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, System.Numerics.Vector2.Zero);

        var flags = ImGuiWindowFlags.NoDocking
                  | ImGuiWindowFlags.NoTitleBar
                  | ImGuiWindowFlags.NoCollapse
                  | ImGuiWindowFlags.NoResize
                  | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoBringToFrontOnFocus
                  | ImGuiWindowFlags.NoNavFocus
                  | ImGuiWindowFlags.NoBackground
                  | ImGuiWindowFlags.MenuBar;

        ImGui.Begin("##DockspaceHost", flags);
        ImGui.PopStyleVar(3);
        ImGui.DockSpace(ImGui.GetID("MainDockspace"), System.Numerics.Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);
        ImGui.End();
    }

    // -------------------------------------------------------------------------
    // Panels
    // -------------------------------------------------------------------------
    private void DrawPanels()
    {
        if (_engine?.SceneManager.ActiveScene != null)
        {
            _hierarchy.Draw(_engine.SceneManager.ActiveScene);
            _inspector.Draw();
            _assetBrowser.Draw();
            _console.Draw(_engine.SceneManager.ActiveScene);
            _viewport.Draw(_viewportTarget, _imGui);
            _buildSettings.Draw();
        }
    }

    // -------------------------------------------------------------------------
    // Play mode
    // -------------------------------------------------------------------------
    private void EnterPlayMode()
    {
        if (EditorState.IsPlaying) return;

        // Snapshot current scene
        if (_engine?.SceneManager.ActiveScene != null)
        {
            try
            {
                _sceneSnapshot = SceneSerializer.Serialize(_engine.SceneManager.ActiveScene);
            }
            catch (Exception ex)
            {
                ConsoleLog.Add($"Scene snapshot failed: {ex.Message}", LogLevel.Warning);
                _sceneSnapshot = null;
            }
        }

        EditorState.IsPlaying    = true;
        EditorState.IsPlayPaused = false;
        ConsoleLog.Add("Play mode started (F7 to stop).", LogLevel.Info);
    }

    private void TogglePause()
    {
        if (!EditorState.IsPlaying) return;
        EditorState.IsPlayPaused = !EditorState.IsPlayPaused;
        ConsoleLog.Add(EditorState.IsPlayPaused ? "Paused." : "Resumed.", LogLevel.Info);
    }

    private void ExitPlayMode()
    {
        if (!EditorState.IsPlaying) return;

        EditorState.IsPlaying    = false;
        EditorState.IsPlayPaused = false;

        // Restore scene snapshot
        if (_sceneSnapshot != null && _engine != null)
        {
            try
            {
                var restored = SceneSerializer.Deserialize(_sceneSnapshot);
                // Replace active scene via CreateScene path (unloads old, sets new)
                _engine.SceneManager.CreateScene(restored.Name);
                ConsoleLog.Add("Scene restored from snapshot.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                ConsoleLog.Add($"Scene restore failed: {ex.Message}", LogLevel.Error);
            }
            _sceneSnapshot = null;
        }

        EditorState.SelectActor(null);
        ConsoleLog.Add("Play mode stopped.", LogLevel.Info);
    }

    // -------------------------------------------------------------------------
    // Scene I/O helpers
    // -------------------------------------------------------------------------
    private void OpenSceneDialog()
    {
        var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title  = "Open Scene",
            Filter = "Scene files (*.scene)|*.scene|All files (*.*)|*.*",
            InitialDirectory = EditorState.ProjectPath,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            try
            {
                var scene = SceneSerializer.LoadFromFile(dialog.FileName);
                _engine?.SceneManager.CreateScene(scene.Name);
                EditorState.SelectActor(null);
                ConsoleLog.Add($"Opened: {dialog.FileName}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                ConsoleLog.Add($"Failed to open scene: {ex.Message}", LogLevel.Error);
            }
        }
    }

    private void SaveCurrentScene()
    {
        var scene = _engine?.SceneManager.ActiveScene;
        if (scene == null)
        {
            ConsoleLog.Add("No active scene to save.", LogLevel.Warning);
            return;
        }

        var dialog = new System.Windows.Forms.SaveFileDialog
        {
            Title  = "Save Scene",
            Filter = "Scene files (*.scene)|*.scene|All files (*.*)|*.*",
            FileName = scene.Name + ".scene",
            InitialDirectory = EditorState.ProjectPath,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            try
            {
                SceneSerializer.SaveToFile(scene, dialog.FileName);
                ConsoleLog.Add($"Scene saved: {dialog.FileName}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                ConsoleLog.Add($"Failed to save scene: {ex.Message}", LogLevel.Error);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Viewport target
    // -------------------------------------------------------------------------
    private void RecreateViewportTarget()
    {
        _viewportTarget?.Dispose();
        _viewportTarget = new RenderTarget2D(
            GraphicsDevice,
            _viewportWidth,
            _viewportHeight,
            false,
            SurfaceFormat.Color,
            DepthFormat.None);
    }

    public void ResizeViewport(int width, int height)
    {
        if (width == _viewportWidth && height == _viewportHeight) return;
        _viewportWidth  = Math.Max(1, width);
        _viewportHeight = Math.Max(1, height);
        RecreateViewportTarget();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private bool KeyJustPressed(KeyboardState current, Keys key)
        => current.IsKeyDown(key) && !_prevKeys.IsKeyDown(key);

    public SBEngine? Engine => _engine;
}
