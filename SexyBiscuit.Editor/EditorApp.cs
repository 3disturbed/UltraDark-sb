using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SexyBiscuit.Editor.Panels;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.AI;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.Scene;
using SexyBiscuit.Engine.UI;
using Color = Microsoft.Xna.Framework.Color;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Scene = SexyBiscuit.Engine.Core.Scene;
using XnaVector3 = Microsoft.Xna.Framework.Vector3;

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
    private ApiReferencePanel  _apiReference  = null!;
    private CodeEditorPanel    _codeEditor    = null!;
    private GitPanel           _git           = null!;
    private ProjectManagerPanel _projectManager = null!;
    private PlaceActorsPanel   _placeActors   = null!;

    // The editor's own 3D camera. Lives outside the scene so it is not saved with it
    // and is not destroyed when the scene is replaced.
    private Actor?           _editorCamera;
    private Camera3D?        _editorCamera3D;
    private Transform3D?     _editorCameraTransform;

    // -------------------------------------------------------------------------
    // Engine
    // -------------------------------------------------------------------------
    private EngineHost? _engine;
    private bool        _engineInitialized;

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

        // EngineHost, not SBEngine. SBEngine is a MonoGame Game, and constructing one
        // creates a second SDL window — which MonoGame's static Mouse then binds to,
        // stealing cursor coordinates with no supported way to give them back.
        _engine = new EngineHost(GraphicsDevice, Content, new EngineConfig
        {
            WindowTitle  = "SexyBiscuit [Editor Preview]",
            WindowWidth  = _viewportWidth,
            WindowHeight = _viewportHeight,
            // The editor draws the scene itself, into its viewport target.
            Enable3D     = false,
        });

        _engine.SceneManager.CreateScene("Untitled");
        _engineInitialized = true;

        // Panels
        _hierarchy    = new HierarchyPanel();
        _inspector    = new InspectorPanel();
        _assetBrowser = new AssetBrowserPanel();
        _console      = new ConsolePanel();
        _viewport      = new ViewportPanel();
        _buildSettings = new BuildSettingsPanel();
        _apiReference  = new ApiReferencePanel();
        _codeEditor    = new CodeEditorPanel();
        _git           = new GitPanel();
        _projectManager = new ProjectManagerPanel();
        _placeActors    = new PlaceActorsPanel();

        EditorState.LoadRecentProjects();
        CreateEditorCamera();

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

        // Pump the engine only in play mode. Outside it the scene is static and the
        // editor still draws it, which is what lets you build a level without it running.
        if (EditorState.IsPlaying && !EditorState.IsPlayPaused && _engineInitialized)
        {
            float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

            // Input goes to the game only while playing, so editor shortcuts and the
            // game's own bindings never fight over the same keys.
            _engine?.Tick(dt, pumpInput: true);
        }

        base.Update(gameTime);
    }

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------
    protected override void Draw(GameTime gameTime)
    {
        // Render the scene into the viewport target, in the same order SBEngine does:
        // the 3D pass first, then the 2D sprite pass composited over it.
        if (_viewportTarget != null && _engineInitialized)
        {
            GraphicsDevice.SetRenderTarget(_viewportTarget);
            GraphicsDevice.Clear(new Color(18, 20, 26));

            var scene = _engine?.SceneManager.ActiveScene;

            if (scene != null && EditorState.Viewport3D && _engine != null)
            {
                _engine.Renderer3D.OverrideCamera = ActiveViewportCamera;
                _engine.Renderer3D.Render(scene);
            }

            // A render target leaves the device in a 3D state; SpriteBatch does not
            // restore depth or rasteriser state itself, so sprites would z-fight or be
            // culled without this.
            GraphicsDevice.DepthStencilState = DepthStencilState.None;
            GraphicsDevice.RasterizerState   = RasterizerState.CullCounterClockwise;
            GraphicsDevice.BlendState        = BlendState.AlphaBlend;

            _spriteBatch.Begin();
            scene?.Draw(_spriteBatch);
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
            bool viewport3D = EditorState.Viewport3D;
            if (ImGui.MenuItem("3D Viewport", "", viewport3D))
                EditorState.Viewport3D = !viewport3D;

            bool stats = EditorState.ShowRenderStats;
            if (ImGui.MenuItem("Render Stats", "", stats))
                EditorState.ShowRenderStats = !stats;

            ImGui.Separator();

            bool api = EditorState.ShowApiReference;
            if (ImGui.MenuItem("API Reference", "", api))
                EditorState.ShowApiReference = !api;

            bool code = EditorState.ShowCodeEditor;
            if (ImGui.MenuItem("Code Editor", "", code))
                EditorState.ShowCodeEditor = !code;

            bool git = EditorState.ShowGitPanel;
            if (ImGui.MenuItem("Git", "", git))
                EditorState.ShowGitPanel = !git;

            ImGui.Separator();

            if (ImGui.MenuItem("Project Manager"))
                EditorState.ShowProjectManager = true;

            ImGui.Separator();

            if (ImGui.MenuItem("Reset Layout"))
                _resetLayoutRequested = true;

            ImGui.EndMenu();
        }

        DrawCreateMenu();

        ImGui.EndMainMenuBar();
    }

    // -------------------------------------------------------------------------
    // Create menu
    // -------------------------------------------------------------------------

    /// <summary>
    /// Presets for the actors a scene almost always needs.
    /// </summary>
    /// <remarks>
    /// Building a 3D scene by hand means adding a bare Actor, then a Transform3D, then a
    /// Camera3D, then remembering the "MainCamera3D" tag — four steps to get anything on
    /// screen. Each preset here is one click, and the result is selected so the inspector
    /// opens on it.
    /// </remarks>
    private void DrawCreateMenu()
    {
        var scene = _engine?.SceneManager.ActiveScene;
        if (scene == null) return;

        if (!ImGui.BeginMenu("Create")) return;

        if (ImGui.MenuItem("Empty Actor"))
            Spawn(scene, new Actor("Actor"));

        if (ImGui.MenuItem("Empty Actor (3D)"))
        {
            var a = new Actor("Actor 3D");
            a.AddComponent<Transform3D>();
            Spawn(scene, a);
        }

        ImGui.Separator();

        if (ImGui.BeginMenu("3D Object"))
        {
            if (ImGui.MenuItem("Mesh"))
            {
                var a = new Actor("Mesh");
                a.AddComponent<Transform3D>();
                a.AddComponent<MeshRenderer>();      // draws a unit cube until a model loads
                Spawn(scene, a);
            }

            if (ImGui.MenuItem("Skinned Mesh"))
            {
                var a = new Actor("Skinned Mesh");
                a.AddComponent<Transform3D>();
                a.AddComponent<SkeletalAnimator>();
                a.AddComponent<SkinnedMeshRenderer>();
                Spawn(scene, a);
            }

            if (ImGui.MenuItem("Particle System"))
            {
                var a = new Actor("Particles");
                a.AddComponent<Transform3D>();
                a.AddComponent<ParticleSystem3D>();
                Spawn(scene, a);
            }

            if (ImGui.MenuItem("Skybox"))
            {
                var a = new Actor("Skybox");
                a.AddComponent<Transform3D>();
                a.AddComponent<Skybox>();
                Spawn(scene, a);
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Light"))
        {
            if (ImGui.MenuItem("Directional")) SpawnLight(scene, LightType.Directional, "Sun");
            if (ImGui.MenuItem("Point"))       SpawnLight(scene, LightType.Point, "Point Light");
            if (ImGui.MenuItem("Spot"))        SpawnLight(scene, LightType.Spot, "Spot Light");

            ImGui.Separator();

            if (ImGui.MenuItem("2D Light"))
            {
                var a = new Actor("Light 2D");
                a.AddComponent<Light2D>();
                Spawn(scene, a);
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Camera"))
        {
            if (ImGui.MenuItem("Camera 3D"))
            {
                var a = new Actor("Camera") { Tag = "MainCamera3D" };
                a.AddComponent<Transform3D>().Position = new XnaVector3(0f, 2f, 8f);
                a.AddComponent<Camera3D>();
                Spawn(scene, a);
            }

            if (ImGui.MenuItem("Camera 3D + Fly Controls"))
            {
                var a = new Actor("Fly Camera") { Tag = "MainCamera3D" };
                a.AddComponent<Transform3D>().Position = new XnaVector3(0f, 2f, 8f);
                a.AddComponent<Camera3D>();
                a.AddComponent<FlyCamController>();
                Spawn(scene, a);
            }

            if (ImGui.MenuItem("Camera 2D"))
            {
                var a = new Actor("Camera 2D");
                a.AddComponent<Camera2D>();
                Spawn(scene, a);
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Gameplay"))
        {
            if (ImGui.MenuItem("Game Mode"))     Spawn(scene, new GameMode());
            if (ImGui.MenuItem("Character"))
            {
                var c = new Character("Character");
                c.AddComponent<Transform3D>();
                Spawn(scene, c);
            }

            if (ImGui.MenuItem("Player Start"))
            {
                var a = new Actor("Player Start");
                a.AddComponent<Transform3D>();
                a.AddComponent<PlayerStart>();
                Spawn(scene, a);
            }

            if (ImGui.MenuItem("AI Character"))
            {
                var c = new Character("AI Character");
                c.AddComponent<Transform3D>();
                c.AddComponent<NavMeshAgent>().DriveCharacter = true;
                Spawn(scene, c);
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("UI"))
        {
            if (ImGui.MenuItem("Canvas"))
            {
                var a = new Actor("Canvas");
                a.AddComponent<Canvas>();
                Spawn(scene, a);
            }

            if (ImGui.MenuItem("World Canvas"))
            {
                var a = new Actor("World Canvas");
                a.AddComponent<Transform3D>();
                a.AddComponent<WorldCanvas>();
                Spawn(scene, a);
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("2D Object"))
        {
            if (ImGui.MenuItem("Sprite"))
            {
                var a = new Actor("Sprite");
                a.AddComponent<SpriteRenderer>();
                Spawn(scene, a);
            }

            if (ImGui.MenuItem("Tilemap"))
            {
                var a = new Actor("Tilemap");
                a.AddComponent<TilemapRenderer>();
                Spawn(scene, a);
            }

            if (ImGui.MenuItem("Particle Emitter"))
            {
                var a = new Actor("Emitter");
                a.AddComponent<ParticleEmitter>();
                Spawn(scene, a);
            }

            ImGui.EndMenu();
        }

        ImGui.EndMenu();
    }

    private void SpawnLight(Scene scene, LightType type, string name)
    {
        var a = new Actor(name);
        var t = a.AddComponent<Transform3D>();
        var light = a.AddComponent<Light3D>();
        light.Type = type;

        if (type == LightType.Directional)
        {
            t.EulerAngles = new XnaVector3(-50f, 30f, 0f);
        }
        else
        {
            t.Position = new XnaVector3(0f, 3f, 0f);
            light.Range = 10f;
        }

        Spawn(scene, a);
    }

    private void Spawn(Scene scene, Actor actor)
    {
        // Honour the hierarchy's selection so a preset lands where the user is looking.
        string layer = EditorState.SelectedLayer?.Name ?? "default";
        scene.AddActor(actor, layer);
        EditorState.SelectActor(actor);
        ConsoleLog.Add($"Created {actor.Name}", LogLevel.Info);
    }

    // -------------------------------------------------------------------------
    // Editor camera
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the camera the 3D viewport renders through when the scene has none.
    /// </summary>
    /// <remarks>
    /// Deliberately not added to the scene: it must not be saved with the level, must not
    /// appear in the hierarchy, and must survive the scene being replaced. It is only used
    /// as a fallback — a scene with its own MainCamera3D renders through that instead, so
    /// what you see matches what the game will show.
    /// </remarks>
    /// <summary>
    /// The camera the 3D viewport renders through.
    /// </summary>
    /// <remarks>
    /// The editor's own camera by default, so flying around does not move the camera the
    /// game will ship with. <see cref="EditorState.UseGameCamera"/> switches to the
    /// scene's MainCamera3D when you want to check the actual framing.
    /// </remarks>
    public Camera3D? ActiveViewportCamera
        => EditorState.UseGameCamera ? Camera3D.Main ?? _editorCamera3D : _editorCamera3D;

    /// <summary>The editor camera's transform, for viewport navigation.</summary>
    public Transform3D? EditorCameraTransform => _editorCameraTransform;

    private void CreateEditorCamera()
    {
        _editorCamera          = new Actor("(Editor Camera)");
        _editorCameraTransform = _editorCamera.AddComponent<Transform3D>();
        _editorCamera3D        = _editorCamera.AddComponent<Camera3D>();

        _editorCameraTransform.Position = new XnaVector3(6f, 5f, 10f);
        _editorCameraTransform.LookAt(XnaVector3.Zero);
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

        DrawToolbar();

        uint dockspaceId = ImGui.GetID("MainDockspace");
        ImGui.DockSpace(dockspaceId, System.Numerics.Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);

        // Build the default arrangement once. After that ImGui restores whatever the user
        // last had from imgui.ini, so rearranging panels sticks.
        if (!_layoutBuilt)
        {
            _layoutBuilt = true;
            if (_resetLayoutRequested || !File.Exists("imgui.ini"))
                BuildDefaultLayout(dockspaceId, viewport.WorkSize);
        }

        if (_resetLayoutRequested)
        {
            _resetLayoutRequested = false;
            BuildDefaultLayout(dockspaceId, viewport.WorkSize);
        }

        ImGui.End();
    }

    private bool _layoutBuilt;
    private bool _resetLayoutRequested;

    /// <summary>
    /// Arranges the panels the way Unreal arranges its editor: a palette on the left, the
    /// viewport filling the centre, browsers along the bottom, and outliner over details
    /// on the right.
    /// </summary>
    /// <remarks>
    /// Splitting is order-dependent. Each split returns two node ids and the parent id
    /// stops being valid for docking, so every subsequent split works on one of the
    /// returned halves — reusing the parent silently docks windows into the wrong pane.
    /// </remarks>
    private void BuildDefaultLayout(uint dockspaceId, System.Numerics.Vector2 size)
    {
        ImGuiDock.RemoveNode(dockspaceId);
        ImGuiDock.AddNode(dockspaceId, ImGuiDockNodeFlags.PassthruCentralNode);
        ImGuiDock.SetNodeSize(dockspaceId, size);

        // Left column: the actor palette.
        uint left = ImGuiDock.Split(dockspaceId, ImGuiDir.Left, 0.15f, out uint afterLeft);

        // Right column: outliner above details.
        uint right = ImGuiDock.Split(afterLeft, ImGuiDir.Right, 0.24f, out uint centre);
        uint rightBottom = ImGuiDock.Split(right, ImGuiDir.Down, 0.62f, out uint rightTop);

        // Centre column: viewport above the browsers.
        uint bottom = ImGuiDock.Split(centre, ImGuiDir.Down, 0.30f, out uint centreTop);
        uint bottomRight = ImGuiDock.Split(bottom, ImGuiDir.Right, 0.42f, out uint bottomLeft);

        ImGuiDock.DockWindow("Place Actors",   left);

        ImGuiDock.DockWindow("Viewport",       centreTop);
        ImGuiDock.DockWindow("Code Editor",    centreTop);
        ImGuiDock.DockWindow("API Reference",  centreTop);

        ImGuiDock.DockWindow("Content Browser", bottomLeft);
        ImGuiDock.DockWindow("Output Log",      bottomRight);

        ImGuiDock.DockWindow("World Outliner", rightTop);

        ImGuiDock.DockWindow("Details",        rightBottom);
        ImGuiDock.DockWindow("Build Settings", rightBottom);
        ImGuiDock.DockWindow("Git",            rightBottom);
        ImGuiDock.DockWindow("Render Stats",   rightBottom);

        ImGuiDock.Finish(dockspaceId);
        ConsoleLog.Add("Editor layout reset to default.", LogLevel.Info);
    }

    // -------------------------------------------------------------------------
    // Toolbar
    // -------------------------------------------------------------------------

    /// <summary>
    /// The strip under the menu bar: transport controls, gizmo mode and viewport options,
    /// the things you reach for constantly and should not have to open a menu for.
    /// </summary>
    private void DrawToolbar()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(10f, 5f));

        bool playing = EditorState.IsPlaying;
        bool paused  = EditorState.IsPlayPaused;

        // Play turns green while running so the editor's state is obvious at a glance —
        // in a docked layout the viewport border is easy to miss.
        if (playing) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.20f, 0.55f, 0.25f, 1f));
        if (ImGui.Button(playing ? "Stop" : "Play"))
        {
            if (playing) ExitPlayMode();
            else         EnterPlayMode();
        }
        if (playing) ImGui.PopStyleColor();
        Tooltip(playing ? "Stop (F7)" : "Play (F5)");

        ImGui.SameLine();
        ImGui.BeginDisabled(!playing);
        if (ImGui.Button(paused ? "Resume" : "Pause")) TogglePause();
        ImGui.EndDisabled();
        Tooltip("Pause (F6)");

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();

        GizmoButton("Move",   GizmoMode.Translate, "Translate (G)");
        ImGui.SameLine();
        GizmoButton("Rotate", GizmoMode.Rotate,    "Rotate (R)");
        ImGui.SameLine();
        GizmoButton("Scale",  GizmoMode.Scale,     "Scale (S)");

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();

        bool is3D = EditorState.Viewport3D;
        if (is3D) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.22f, 0.38f, 0.58f, 1f));
        if (ImGui.Button(is3D ? "3D" : "2D")) EditorState.Viewport3D = !is3D;
        if (is3D) ImGui.PopStyleColor();
        Tooltip(is3D ? "Rendering through RenderSystem3D. Click for the 2D sprite pass."
                     : "Rendering the 2D sprite pass. Click for RenderSystem3D.");

        ImGui.SameLine();
        ImGui.BeginDisabled(!EditorState.Viewport3D);
        bool gameCam = EditorState.UseGameCamera;
        if (gameCam) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.22f, 0.38f, 0.58f, 1f));
        if (ImGui.Button(gameCam ? "Game Cam" : "Editor Cam")) EditorState.UseGameCamera = !gameCam;
        if (gameCam) ImGui.PopStyleColor();
        ImGui.EndDisabled();
        Tooltip(gameCam
            ? "Rendering through the scene's MainCamera3D. Click to fly the editor camera instead."
            : "Rendering through the editor camera. Right-drag to look, WASD to move, scroll to dolly.");

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();

        bool snap = EditorState.SnapEnabled;
        if (snap) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.30f, 0.45f, 0.65f, 1f));
        if (ImGui.Button("Snap")) EditorState.SnapEnabled = !snap;
        if (snap) ImGui.PopStyleColor();
        Tooltip("Quantise gizmo drags to the increments beside this button.");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(70f);
        float translateSnap = EditorState.TranslateSnap;
        if (ImGui.DragFloat("##snapT", ref translateSnap, 0.05f, 0.01f, 100f, "%.2f m"))
            EditorState.TranslateSnap = translateSnap;
        Tooltip("Translation snap, in world units.");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(64f);
        float rotateSnap = EditorState.RotateSnap;
        if (ImGui.DragFloat("##snapR", ref rotateSnap, 1f, 1f, 180f, "%.0f deg"))
            EditorState.RotateSnap = rotateSnap;
        Tooltip("Rotation snap, in degrees.");

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();

        bool stats = EditorState.ShowRenderStats;
        if (ImGui.Button("Stats")) EditorState.ShowRenderStats = !stats;
        Tooltip("Toggle the render statistics panel.");

        // Frame timing on the right, where Unreal puts its performance readout.
        var readout = $"{Time.Fps:F0} fps";
        float textWidth = ImGui.CalcTextSize(readout).X;
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - textWidth);
        ImGui.TextDisabled(readout);

        ImGui.PopStyleVar();
        ImGui.Separator();
    }

    private static void GizmoButton(string label, GizmoMode mode, string tooltip)
    {
        bool active = EditorState.GizmoMode == mode;
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.30f, 0.45f, 0.65f, 1f));
        if (ImGui.Button(label)) EditorState.GizmoMode = mode;
        if (active) ImGui.PopStyleColor();
        Tooltip(tooltip);
    }

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }

    // -------------------------------------------------------------------------
    // Panels
    // -------------------------------------------------------------------------
    private void DrawPanels()
    {
        // The launcher is modal in spirit: while it is up, the workspace behind it is
        // meaningless because no project is loaded.
        if (EditorState.ShowProjectManager)
        {
            _projectManager.Draw();
            FileDialog.Draw();
            return;
        }

        if (_engine?.SceneManager.ActiveScene != null)
        {
            _placeActors.Draw(_engine.SceneManager.ActiveScene);
            _hierarchy.Draw(_engine.SceneManager.ActiveScene);
            _inspector.Draw();
            _assetBrowser.Draw();
            _console.Draw(_engine.SceneManager.ActiveScene);
            _viewport.Draw(_viewportTarget, _imGui);
            _buildSettings.Draw();

            _apiReference.Draw();
            _codeEditor.Draw();
            _git.Draw();

            if (EditorState.ShowRenderStats) DrawRenderStats();
        }

        // Last, so its modal sits above every panel.
        FileDialog.Draw();
    }

    /// <summary>A small overlay of what the 3D renderer submitted last frame.</summary>
    private void DrawRenderStats()
    {
        if (_engine == null) return;

        var stats = _engine.Renderer3D.Stats;
        bool open = EditorState.ShowRenderStats;

        if (ImGui.Begin("Render Stats", ref open))
        {
            ImGui.Text($"FPS            {Time.Fps,8:F1}");
            ImGui.Separator();
            ImGui.Text($"Renderers      {stats.RenderersDrawn,8} drawn");
            ImGui.Text($"               {stats.RenderersCulled,8} culled");
            ImGui.Text($"               {stats.RenderersTotal,8} total");
            ImGui.Separator();
            ImGui.Text($"Draw calls     {stats.DrawCalls,8}");
            ImGui.Text($"Triangles      {stats.Triangles,8:N0}");
            ImGui.Separator();
            ImGui.Text($"Lights         {stats.LightsActive,8}");
            ImGui.Text($"Shadow casters {stats.ShadowCasters,8}");
        }

        ImGui.End();
        EditorState.ShowRenderStats = open;
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
        FileDialog.OpenFile("Open Scene", EditorState.ProjectPath, new[] { ".scene", ".json" }, path =>
        {
            try
            {
                var loaded = SceneSerializer.LoadFromFile(path);
                _engine?.SceneManager.AdoptScene(loaded);
                EditorState.SelectActor(null);
                ConsoleLog.Add($"Opened: {path}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                ConsoleLog.Add($"Failed to open scene: {ex.Message}", LogLevel.Error);
            }
        });
    }

    private void SaveCurrentScene()
    {
        var scene = _engine?.SceneManager.ActiveScene;
        if (scene == null)
        {
            ConsoleLog.Add("No active scene to save.", LogLevel.Warning);
            return;
        }

        FileDialog.SaveFile("Save Scene", EditorState.ProjectPath, new[] { ".scene", ".json" },
            scene.Name + ".scene", path =>
        {
            try
            {
                SceneSerializer.SaveToFile(scene, path);
                ConsoleLog.Add($"Scene saved: {path}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                ConsoleLog.Add($"Failed to save scene: {ex.Message}", LogLevel.Error);
            }
        });
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

    public EngineHost? Engine => _engine;
}
