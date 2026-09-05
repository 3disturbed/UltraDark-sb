using System.Text.Json;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scene;

namespace SexyBiscuit.Editor;

/// <summary>Which gizmo the viewport is currently manipulating with.</summary>
public enum GizmoMode
{
    Translate,
    Rotate,
    Scale,
}

/// <summary>A project the user has opened before, shown on the launcher.</summary>
public sealed class RecentProject
{
    /// <summary>Display name, taken from the project file.</summary>
    public string Name { get; set; } = "Untitled";

    /// <summary>Absolute path to the <c>.sbproject</c> file.</summary>
    public string Path { get; set; } = "";

    /// <summary>When it was last opened, used for ordering the list.</summary>
    public DateTime LastOpened { get; set; } = DateTime.Now;
}

/// <summary>
/// Shared state every editor panel reads and writes. Purely a data bus — no panel
/// logic lives here.
/// </summary>
public static class EditorState
{
    // -------------------------------------------------------------------------
    // Selection
    // -------------------------------------------------------------------------

    /// <summary>The actor shown in the inspector and manipulated by the gizmo.</summary>
    public static Actor? SelectedActor { get; private set; }

    /// <summary>The layer highlighted in the hierarchy.</summary>
    public static Layer? SelectedLayer { get; set; }

    /// <summary>The asset highlighted in the browser.</summary>
    public static string? SelectedAssetPath { get; set; }

    /// <summary>Raised whenever the selection changes, including to null.</summary>
    public static event Action<Actor?>? OnSelectionChanged;

    public static void SelectActor(Actor? actor)
    {
        SelectedActor = actor;
        OnSelectionChanged?.Invoke(actor);
    }

    // -------------------------------------------------------------------------
    // Play mode
    // -------------------------------------------------------------------------

    /// <summary>True while the scene is being simulated.</summary>
    public static bool IsPlaying { get; set; }

    /// <summary>True while play mode is suspended.</summary>
    public static bool IsPlayPaused { get; set; }

    // -------------------------------------------------------------------------
    // Viewport
    // -------------------------------------------------------------------------

    /// <summary>Which transform handle the viewport shows.</summary>
    public static GizmoMode GizmoMode { get; set; } = GizmoMode.Translate;

    /// <summary>
    /// Renders the scene through the 3D pipeline instead of the 2D sprite pass.
    /// </summary>
    /// <remarks>
    /// On by default: this is a 3D engine, and the transform gizmos only exist on the 3D
    /// path. A purely 2D project turns it off from the toolbar.
    /// </remarks>
    public static bool Viewport3D { get; set; } = true;

    /// <summary>True while the pointer is over the viewport, so it can capture navigation keys.</summary>
    public static bool ViewportFocused { get; set; }

    /// <summary>
    /// Renders the 3D viewport through the scene's MainCamera3D rather than the editor's
    /// own camera.
    /// </summary>
    /// <remarks>
    /// Off by default, so flying around the level does not move the camera the game ships
    /// with. Turn it on to check what the player will actually see.
    /// </remarks>
    public static bool UseGameCamera { get; set; }

    // -------------------------------------------------------------------------
    // Gizmo snapping
    // -------------------------------------------------------------------------

    /// <summary>Quantises gizmo drags to the snap increments below.</summary>
    public static bool SnapEnabled { get; set; }

    /// <summary>Translation snap, in world units.</summary>
    public static float TranslateSnap { get; set; } = 0.25f;

    /// <summary>Rotation snap, in degrees.</summary>
    public static float RotateSnap { get; set; } = 15f;

    /// <summary>Scale snap, as a multiplier increment.</summary>
    public static float ScaleSnap { get; set; } = 0.1f;

    // -------------------------------------------------------------------------
    // Panel visibility
    // -------------------------------------------------------------------------

    /// <summary>Shows the reflected engine API browser.</summary>
    public static bool ShowApiReference { get; set; }

    /// <summary>Shows the script editor.</summary>
    public static bool ShowCodeEditor { get; set; }

    /// <summary>Shows the Git panel.</summary>
    public static bool ShowGitPanel { get; set; }

    /// <summary>Shows the project launcher. Open on startup until a project is chosen.</summary>
    public static bool ShowProjectManager { get; set; } = true;

    /// <summary>Shows the renderer statistics overlay.</summary>
    public static bool ShowRenderStats { get; set; }

    // -------------------------------------------------------------------------
    // Project
    // -------------------------------------------------------------------------

    /// <summary>Root directory of the open project.</summary>
    public static string ProjectPath { get; set; } = Directory.GetCurrentDirectory();

    /// <summary>The open project file, or null when none is loaded.</summary>
    public static ProjectFile? CurrentProject { get; private set; }

    /// <summary>Absolute path of the open <c>.sbproject</c> file, or null.</summary>
    public static string? CurrentProjectFile { get; private set; }

    // -------------------------------------------------------------------------
    // Scene file state
    // -------------------------------------------------------------------------

    /// <summary>
    /// Where the active scene was loaded from or last saved, relative to the project root.
    /// Null for a scene that has never been saved.
    /// </summary>
    public static string? CurrentScenePath { get; set; }

    /// <summary>True when the scene has changes not yet on disk.</summary>
    public static bool SceneDirty { get; set; }

    /// <summary>Raised after a project is opened, with its root directory.</summary>
    public static event Action<string>? OnProjectOpened;

    /// <summary>Projects the user has opened before, most recent first.</summary>
    public static List<RecentProject> RecentProjects { get; } = new();

    /// <summary>
    /// Opens a project from its <c>.sbproject</c> file and records it in the recent list.
    /// </summary>
    public static void OpenProject(string projectFilePath)
    {
        try
        {
            var project = ProjectFile.Load(projectFilePath);
            var root    = Path.GetDirectoryName(Path.GetFullPath(projectFilePath))
                          ?? Directory.GetCurrentDirectory();

            CurrentProject     = project;
            CurrentProjectFile = Path.GetFullPath(projectFilePath);
            ProjectPath        = root;
            ProjectPaths.Root  = root;

            AddRecentProject(project.ProjectName, projectFilePath);
            SaveRecentProjects();

            ShowProjectManager = false;
            ConsoleLog.Add($"Opened project '{project.ProjectName}' at {root}", LogLevel.Info);

            LoadDefaultScene(project, root);
            OnProjectOpened?.Invoke(root);
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Could not open '{projectFilePath}': {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    /// Opening a project used to leave whatever scene was already showing. Load the project's
    /// default scene when the file exists; a project without one keeps the current scene.
    /// </summary>
    private static void LoadDefaultScene(ProjectFile project, string root)
    {
        if (string.IsNullOrWhiteSpace(project.DefaultScene)) return;

        var engine = EditorApp.Instance?.Engine;
        if (engine == null) return;

        string? file = SceneManager.ResolveScenePath(project.DefaultScene);
        if (file == null)
        {
            ConsoleLog.Add($"The project's default scene '{project.DefaultScene}' was not found under {root}.", LogLevel.Warning);
            return;
        }

        try
        {
            var scene = SceneSerializer.LoadFromFile(file);
            engine.SceneManager.AdoptScene(scene);
            scene.FlushPendingActors();
            SelectActor(null);

            CurrentScenePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            SceneDirty       = false;
            ConsoleLog.Add($"Opened scene {CurrentScenePath}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Could not open the default scene '{file}': {ex.Message}", LogLevel.Warning);
        }
    }

    private static void AddRecentProject(string name, string path)
    {
        string full = Path.GetFullPath(path);

        // Re-opening a project moves it to the top rather than duplicating it.
        RecentProjects.RemoveAll(p =>
            string.Equals(Path.GetFullPath(p.Path), full, StringComparison.OrdinalIgnoreCase));

        RecentProjects.Insert(0, new RecentProject
        {
            Name       = name,
            Path       = full,
            LastOpened = DateTime.Now,
        });

        const int keep = 12;
        if (RecentProjects.Count > keep)
            RecentProjects.RemoveRange(keep, RecentProjects.Count - keep);
    }

    // -------------------------------------------------------------------------
    // Recent-project persistence
    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    /// <summary>
    /// Where the recent list lives: the platform's per-user application data directory,
    /// not next to the executable, so it survives a rebuild and works from a read-only
    /// install.
    /// </summary>
    private static string RecentProjectsFile
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SexyBiscuit");

            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "recent-projects.json");
        }
    }

    /// <summary>Reads the recent list from disk. Safe to call when the file is absent.</summary>
    public static void LoadRecentProjects()
    {
        RecentProjects.Clear();

        try
        {
            if (!File.Exists(RecentProjectsFile)) return;

            var loaded = JsonSerializer.Deserialize<List<RecentProject>>(
                File.ReadAllText(RecentProjectsFile), _json);

            if (loaded == null) return;

            // Drop entries whose project has since been moved or deleted, so the
            // launcher never offers a dead link.
            RecentProjects.AddRange(loaded
                .Where(p => File.Exists(p.Path))
                .OrderByDescending(p => p.LastOpened));
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Could not read the recent-project list: {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>Writes the recent list to disk.</summary>
    public static void SaveRecentProjects()
    {
        try
        {
            File.WriteAllText(RecentProjectsFile, JsonSerializer.Serialize(RecentProjects, _json));
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Could not save the recent-project list: {ex.Message}", LogLevel.Warning);
        }
    }
}
