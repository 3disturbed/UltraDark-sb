using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using SexyBiscuit.Editor.Panels;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Mcp;
using Scene = SexyBiscuit.Engine.Core.Scene;

namespace SexyBiscuit.Editor.Assistant;

/// <summary>
/// Tools that only make sense inside the editor process: the project, selection, the editor
/// camera, screenshots, play mode and the Output Log. Everything about the scene itself lives
/// in the engine's tool classes.
/// </summary>
public sealed class EditorTools
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);

    private readonly McpHost _host;

    public EditorTools(McpHost host) => _host = host;

    private static EditorApp App => EditorApp.Instance;

    private static Scene RequireScene()
        => App.Engine?.SceneManager.ActiveScene ?? throw new McpToolException("No scene is open.", "Call new_scene or load_scene first.");

    private static ProjectFile RequireProject()
        => EditorState.CurrentProject ?? throw new McpToolException("No project is open.", "Call open_project or create_project first; get_project_info lists templates and recent projects.");

    // -------------------------------------------------------------------------
    // Project
    // -------------------------------------------------------------------------

    [McpTool("get_project_info",
        "Describe the open project and the editor: root folder, asset/script/scene folders, the open scene and whether it " +
        "has unsaved changes, play mode, and the MCP URL. Call this first. With no project open it lists the templates " +
        "create_project accepts and the recent projects open_project can take.",
        Label = "Read project info")]
    public McpToolResult GetProjectInfo() => McpToolResult.Json(ProjectInfo());

    internal JsonObject ProjectInfo()
    {
        var templates = new JsonArray();
        foreach (var t in TemplateLocator.List())
            templates.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["category"] = t.Category });

        var info = new JsonObject
        {
            ["open"]          = EditorState.CurrentProject != null,
            ["mcpUrl"]        = _host.Url?.ToString(),
            ["engineVersion"] = McpHost.EngineVersion,
            ["templates"]     = templates,
        };

        var project = EditorState.CurrentProject;
        if (project == null)
        {
            info["message"] = "No project is open. Call open_project with a .sbproject path (or a folder containing one), or create_project.";
            var recent = new JsonArray();
            foreach (var r in EditorState.RecentProjects)
                recent.Add(new JsonObject { ["name"] = r.Name, ["path"] = r.Path });
            info["recentProjects"] = recent;
            return info;
        }

        var scene = App.Engine?.SceneManager.ActiveScene;

        info["name"]              = project.ProjectName;
        info["root"]              = EditorState.ProjectPath;
        info["projectFile"]       = EditorState.CurrentProjectFile;
        info["defaultScene"]      = project.DefaultScene;
        info["assetDirectories"]  = new JsonArray(project.AssetDirectories.Select(d => (JsonNode)d).ToArray());
        info["scriptDirectories"] = new JsonArray(project.ScriptDirectories.Select(d => (JsonNode)d).ToArray());
        info["sceneDirectory"]    = "Scenes";
        info["scene"] = scene == null ? null : new JsonObject
        {
            ["name"]       = scene.Name,
            ["path"]       = EditorState.CurrentScenePath,
            ["dirty"]      = EditorState.SceneDirty,
            ["actorCount"] = scene.Layers.Sum(l => l.Actors.Count),
        };
        info["isPlaying"] = EditorState.IsPlaying;
        info["isPaused"]  = EditorState.IsPlayPaused;

        foreach (var contributor in _host.ProjectInfoContributors)
        {
            try
            {
                contributor(info);
            }
            catch (Exception ex)
            {
                info["contributorError"] = ex.Message;
            }
        }

        return info;
    }

    [McpTool("open_project", "Open a project from its .sbproject file, or from a folder that contains one. Its default scene is loaded when the file exists.",
             Label = "Open project {path}")]
    public McpToolResult OpenProject([McpParam("Path to a .sbproject file or a project folder")] string path)
    {
        string full = Path.GetFullPath(path);
        string? file = null;

        if (Directory.Exists(full))
        {
            var candidates = Directory.GetFiles(full, "*.sbproject");
            if (candidates.Length == 0) throw new McpToolException($"No .sbproject file in '{full}'.");
            if (candidates.Length > 1) throw new McpToolException($"Several .sbproject files in '{full}': {string.Join(", ", candidates.Select(Path.GetFileName))}. Pass one of them.");
            file = candidates[0];
        }
        else if (File.Exists(full))
        {
            file = full;
        }

        if (file == null) throw new McpToolException($"'{path}' does not exist.");

        EditorState.OpenProject(file);

        if (!string.Equals(EditorState.CurrentProjectFile, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
            throw new McpToolException($"Could not open '{file}'.", "See the Output Log (read_console) for the reason.");

        return McpToolResult.Json(ProjectInfo(), $"Opened project '{EditorState.CurrentProject!.ProjectName}'.");
    }

    [McpTool("create_project",
        "Create a new project folder <directory>/<name> from a template (names from get_project_info; omit for an empty " +
        "project) and open it.",
        Label = "Create project {name}")]
    public McpToolResult CreateProject(
        [McpParam("Project name; also the folder name")] string name,
        [McpParam("Parent directory the project folder is created in")] string directory,
        [McpParam("Template name, e.g. '3D Scene'")] string? template = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new McpToolException("name must be a valid folder name.");

        string? templatePath = null;
        if (!string.IsNullOrWhiteSpace(template))
        {
            templatePath = TemplateLocator.Find(template)?.Path
                ?? throw new McpToolException($"No template named '{template}'.",
                    "Available: " + string.Join(", ", TemplateLocator.List().Select(t => t.Name)) + ".");
        }

        string parent = Path.GetFullPath(directory);
        string projectDir = Path.Combine(parent, name);
        if (Directory.Exists(projectDir) && Directory.EnumerateFileSystemEntries(projectDir).Any())
            throw new McpToolException($"'{projectDir}' already exists and is not empty.", "Use open_project for an existing project.");

        string projectFile;
        try
        {
            projectFile = ProjectFile.CreateNew(name, projectDir, templatePath);
        }
        catch (Exception ex)
        {
            throw new McpToolException($"Could not create the project: {ex.Message}");
        }

        EditorState.OpenProject(projectFile);
        return McpToolResult.Json(ProjectInfo(), $"Created and opened '{name}' at {projectDir}.");
    }

    [McpTool("list_scenes", "The .scene files in the project, as project-relative paths, marking the open one.")]
    public McpToolResult ListScenes()
    {
        RequireProject();
        string root = EditorState.ProjectPath;
        var list = new JsonArray();

        foreach (var dir in new[] { Path.Combine(root, "Scenes"), Path.Combine(root, "Assets", "Scenes") })
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                                          .Where(f => f.EndsWith(".scene", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                          .OrderBy(f => f))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                list.Add(new JsonObject
                {
                    ["path"]      = relative,
                    ["name"]      = Path.GetFileNameWithoutExtension(file),
                    ["sizeBytes"] = new FileInfo(file).Length,
                    ["isCurrent"] = string.Equals(relative, EditorState.CurrentScenePath, StringComparison.OrdinalIgnoreCase),
                });
            }
        }

        return McpToolResult.Json(list, $"{list.Count} scene file(s).");
    }

    [McpTool("list_assets", "Files under the project's asset directories with a type: texture, audio, model, script, scene, font or other.")]
    public McpToolResult ListAssets(
        [McpParam("Only this sub-folder of the asset directory")] string? subdirectory = null,
        [McpParam("Only these extensions, e.g. ['.png', '.wav']")] string[]? extensions = null,
        [McpParam("Maximum entries")] int limit = 500)
    {
        var project = RequireProject();
        string root = EditorState.ProjectPath;
        var list = new JsonArray();
        bool truncated = false;

        var wanted = extensions?.Select(e => e.StartsWith('.') ? e : "." + e).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var assetDir in project.AssetDirectories.DefaultIfEmpty("Assets"))
        {
            string dir = Path.Combine(root, assetDir, subdirectory ?? "");
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f))
            {
                string ext = Path.GetExtension(file);
                if (wanted != null && !wanted.Contains(ext)) continue;
                if (list.Count >= limit) { truncated = true; break; }

                list.Add(new JsonObject
                {
                    ["path"]      = Path.GetRelativePath(root, file).Replace('\\', '/'),
                    ["type"]      = ClassifyAsset(file),
                    ["sizeBytes"] = new FileInfo(file).Length,
                });
            }
        }

        var result = McpToolResult.Json(new JsonObject { ["assets"] = list, ["truncated"] = truncated }, $"{list.Count} asset(s).");
        return result;
    }

    private static string ClassifyAsset(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".dds" or ".gif" => "texture",
            ".wav" or ".ogg" or ".mp3" or ".flac"                                => "audio",
            ".obj" or ".fbx" or ".gltf" or ".glb" or ".dae" or ".3ds"            => "model",
            ".js" or ".cs" or ".ts"                                              => "script",
            ".scene"                                                             => "scene",
            ".json" when file.Contains($"{Path.DirectorySeparatorChar}Scenes{Path.DirectorySeparatorChar}") => "scene",
            ".ttf" or ".otf" or ".spritefont"                                    => "font",
            _                                                                    => "other",
        };
    }

    // -------------------------------------------------------------------------
    // Selection and the editor camera
    // -------------------------------------------------------------------------

    [McpTool("select_actor", "Select an actor in the editor so it shows in the Details panel and wears the gizmo. Omit actor to clear the selection.",
             Label = "Select {actor}")]
    public McpToolResult SelectActor([McpParam("Actor id or name; omit to deselect")] string? actor = null)
    {
        var scene = RequireScene();
        if (actor == null)
        {
            EditorState.SelectActor(null);
            return McpToolResult.Json(new JsonObject { ["selected"] = null });
        }

        var target = ActorRef.Resolve(scene, actor);
        EditorState.SelectActor(target);
        EditorState.SelectedLayer = target.Layer_;
        return McpToolResult.Json(SceneViews.ActorRow(target));
    }

    [McpTool("get_selection", "The actor and layer currently selected in the editor, if any.")]
    public McpToolResult GetSelection()
    {
        var selected = EditorState.SelectedActor;
        return McpToolResult.Json(new JsonObject
        {
            ["actor"] = selected != null && !selected.IsDestroyed ? SceneViews.ActorRow(selected) : null,
            ["layer"] = EditorState.SelectedLayer?.Name,
        });
    }

    [McpTool("focus_actor", "Select an actor and move the editor camera to frame it.", Label = "Focus {actor}")]
    public McpToolResult FocusActor([McpParam("Actor id or name")] string actor)
    {
        var scene  = RequireScene();
        var target = ActorRef.Resolve(scene, actor);
        var camera = App.EditorCameraTransform ?? throw new McpToolException("The editor camera is not ready.");

        EditorState.SelectActor(target);
        EditorState.SelectedLayer = target.Layer_;

        if (target.GetComponent<Transform3D>() == null)
            throw new McpToolException($"'{target.Name}' is a 2D actor; the 3D editor camera cannot frame it.");

        ViewportPanel.FrameActor(camera, target);
        return McpToolResult.Json(new JsonObject
        {
            ["actor"]  = SceneViews.ActorRow(target),
            ["camera"] = CameraPose(camera),
        });
    }

    [McpTool("get_editor_camera", "The editor camera's pose and which view mode the viewport is in.")]
    public McpToolResult GetEditorCamera()
    {
        var camera = App.EditorCameraTransform ?? throw new McpToolException("The editor camera is not ready.");
        return McpToolResult.Json(CameraPose(camera));
    }

    [McpTool("set_editor_camera", "Move the editor camera (not any scene camera). Give lookAt or rotation [pitch, yaw, roll] degrees.",
             Label = "Move the editor camera")]
    public McpToolResult SetEditorCamera(
        [McpParam("[x, y, z]")] float[]? position = null,
        [McpParam("World point to look at [x, y, z]")] float[]? lookAt = null,
        [McpParam("[pitch, yaw, roll] degrees")] float[]? rotation = null)
    {
        var camera = App.EditorCameraTransform ?? throw new McpToolException("The editor camera is not ready.");

        if (position != null) camera.Position    = ToVector3(position, "position");
        if (rotation != null) camera.EulerAngles = ToVector3(rotation, "rotation");
        if (lookAt != null)   camera.LookAt(ToVector3(lookAt, "lookAt"));

        return McpToolResult.Json(CameraPose(camera));
    }

    [McpTool("set_viewport", "Switch the viewport between 3D and 2D rendering, between the editor camera and the scene's MainCamera3D, " +
                             "or (while playing) between the docked viewport and the game over the whole window. Play always starts on the game camera.",
             Label = "Change the viewport mode")]
    public McpToolResult SetViewport(
        [McpParam("Render the 3D pipeline (true) or the 2D sprite pass (false)")] bool? view3d = null,
        [McpParam("Look through the scene's MainCamera3D instead of the editor camera")] bool? useGameCamera = null,
        [McpParam("Show the game over the whole window; only while playing")] bool? fullscreen = null)
    {
        if (view3d.HasValue)        EditorState.Viewport3D         = view3d.Value;
        if (useGameCamera.HasValue) EditorState.UseGameCamera      = useGameCamera.Value;
        if (fullscreen.HasValue)    EditorState.ViewportFullscreen = fullscreen.Value && EditorState.IsPlaying;
        return McpToolResult.Json(new JsonObject
        {
            ["view3d"]        = EditorState.Viewport3D,
            ["useGameCamera"] = EditorState.UseGameCamera,
            ["fullscreen"]    = EditorState.ViewportFullscreen,
        });
    }

    private static JsonObject CameraPose(Transform3D camera) => new()
    {
        ["position"]      = ValueConverter.ToJson(camera.Position),
        ["rotation"]      = ValueConverter.ToJson(camera.EulerAngles),
        ["forward"]       = ValueConverter.ToJson(camera.Forward),
        ["view3d"]        = EditorState.Viewport3D,
        ["useGameCamera"] = EditorState.UseGameCamera,
    };

    // -------------------------------------------------------------------------
    // Screenshots
    // -------------------------------------------------------------------------

    [McpTool("capture_viewport",
        "A PNG screenshot of the editor viewport as it is rendered right now, downscaled to maxWidth. Look at it to check " +
        "your work. includeUi captures the whole editor window with its panels instead.",
        MainThread = false, Label = "Capture the viewport")]
    public async Task<McpToolResult> CaptureViewport(
        [McpParam("Longest edge in pixels")] int maxWidth = 1024,
        [McpParam("Capture the whole editor window including panels")] bool includeUi = false,
        CancellationToken cancellation = default)
    {
        var png = await AwaitFrame(_host.Capture.CaptureViewportAsync(maxWidth, includeUi, cancellation), cancellation);
        string caption = await _host.Dispatcher.InvokeAsync(DescribeView, cancellation);
        return McpToolResult.Image(png, caption);
    }

    [McpTool("capture_scene_from",
        "Render the scene from a camera pose of your choosing, as a PNG, without moving any camera. Give lookAt or " +
        "rotation [pitch, yaw, roll] degrees.",
        MainThread = false, Label = "Render from a pose")]
    public async Task<McpToolResult> CaptureSceneFrom(
        [McpParam("[x, y, z]")] float[] position,
        [McpParam("World point to look at [x, y, z]")] float[]? lookAt = null,
        [McpParam("[pitch, yaw, roll] degrees")] float[]? rotation = null,
        [McpParam("Vertical field of view in degrees")] float fov = 60f,
        [McpParam("Image width")] int width = 1024,
        [McpParam("Image height")] int height = 576,
        CancellationToken cancellation = default)
    {
        var pose = new ViewportCapture.CameraPose(
            ToVector3(position, "position"),
            lookAt != null ? ToVector3(lookAt, "lookAt") : null,
            rotation != null ? ToVector3(rotation, "rotation") : null,
            fov);

        var png = await AwaitFrame(_host.Capture.CaptureFromAsync(pose, width, height, cancellation), cancellation);
        return McpToolResult.Image(png, $"{width}x{height} from {ValueConverter.ToJson(pose.Position)?.ToJsonString()}, fov {fov}.");
    }

    private static async Task<byte[]> AwaitFrame(Task<byte[]> capture, CancellationToken cancellation)
    {
        var finished = await Task.WhenAny(capture, Task.Delay(CaptureTimeout, cancellation));
        if (finished != capture)
        {
            cancellation.ThrowIfCancellationRequested();
            throw new McpToolException("The editor did not draw a frame within 10 seconds.", "Is the editor window minimised or hidden behind another window?");
        }

        try
        {
            return await capture;
        }
        catch (InvalidOperationException ex)
        {
            throw new McpToolException(ex.Message);
        }
    }

    private static string DescribeView()
    {
        var camera   = App.EditorCameraTransform;
        var selected = EditorState.SelectedActor;
        string pose  = camera != null
            ? $"editor camera at {ValueConverter.ToJson(camera.Position)?.ToJsonString()} facing {ValueConverter.ToJson(camera.Forward)?.ToJsonString()}"
            : "no editor camera";
        return $"{pose}; view: {(EditorState.UseGameCamera ? "game camera" : "editor camera")}, {(EditorState.Viewport3D ? "3D" : "2D")}; " +
               $"selected: {(selected != null ? selected.Name : "nothing")}; playing: {EditorState.IsPlaying}.";
    }

    // -------------------------------------------------------------------------
    // Play mode
    // -------------------------------------------------------------------------

    [McpTool("play", "Start play mode (F5). The scene is snapshotted; changes made while playing are discarded on stop.", Label = "Play")]
    public McpToolResult Play()
    {
        RequireScene();
        App.EnterPlayMode();
        return McpToolResult.Json(PlayState());
    }

    [McpTool("pause", "Pause or resume play mode. Omit paused to toggle.", Label = "Pause")]
    public McpToolResult Pause([McpParam("true to pause, false to resume; omit to toggle")] bool? paused = null)
    {
        if (!EditorState.IsPlaying) throw new McpToolException("Not in play mode.", "Call play first.");
        if (paused.HasValue) App.SetPaused(paused.Value); else App.TogglePause();
        return McpToolResult.Json(PlayState());
    }

    [McpTool("stop", "Stop play mode (F7) and restore the scene as it was when play started.", Label = "Stop")]
    public McpToolResult Stop()
    {
        App.ExitPlayMode();
        return McpToolResult.Json(PlayState());
    }

    [McpTool("step_frame", "While paused, advance the simulation by N frames of 1/60 s.", Label = "Step {frames} frame(s)")]
    public McpToolResult StepFrame([McpParam("Frames to advance")] int frames = 1)
    {
        if (!EditorState.IsPlaying) throw new McpToolException("Not in play mode.", "Call play, then pause, then step_frame.");
        if (!EditorState.IsPlayPaused) throw new McpToolException("Play mode is running, not paused.", "Call pause first.");
        if (frames < 1 || frames > 600) throw new McpToolException("frames must be between 1 and 600.");

        App.StepFrames(frames);
        return McpToolResult.Json(PlayState());
    }

    [McpTool("get_play_state", "Playing and paused flags, fps, frame count, time scale and the scene name.")]
    public McpToolResult GetPlayState() => McpToolResult.Json(PlayState());

    private static JsonObject PlayState() => new()
    {
        ["playing"]    = EditorState.IsPlaying,
        ["paused"]     = EditorState.IsPlayPaused,
        ["fps"]        = MathF.Round(Time.Fps, 1),
        ["frameCount"] = Time.FrameCount,
        ["timeScale"]  = Time.TimeScale,
        ["sceneName"]  = App.Engine?.SceneManager.ActiveScene?.Name,
    };

    // -------------------------------------------------------------------------
    // Output Log
    // -------------------------------------------------------------------------

    [McpTool("read_console",
        "Read the editor's Output Log. Pass the latestSequence from the previous result as sinceSequence to get only new " +
        "entries. level filters to that severity and above: info, warning, error.",
        MainThread = false)]
    public McpToolResult ReadConsole(
        [McpParam("Only entries after this sequence number")] long sinceSequence = 0,
        [McpParam("Minimum level: info, warning or error")] string? level = null,
        [McpParam("Case-insensitive substring filter")] string? contains = null,
        [McpParam("Maximum entries returned (the newest)")] int limit = 200)
    {
        LogLevel minimum = LogLevel.Info;
        if (level != null && !Enum.TryParse(level, ignoreCase: true, out minimum))
            throw new McpToolException($"Unknown level '{level}'.", "Use info, warning or error.");

        var all = ConsoleLog.EntriesSince(sinceSequence)
            .Where(e => e.Level >= minimum)
            .Where(e => contains == null || e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var entries = new JsonArray();
        foreach (var e in all.Skip(Math.Max(0, all.Count - limit)))
        {
            entries.Add(new JsonObject
            {
                ["seq"]     = e.Sequence,
                ["time"]    = e.Timestamp.ToString("HH:mm:ss"),
                ["level"]   = e.Level.ToString().ToLowerInvariant(),
                ["message"] = e.Message,
            });
        }

        return McpToolResult.Json(new JsonObject
        {
            ["entries"]        = entries,
            ["total"]          = all.Count,
            ["latestSequence"] = ConsoleLog.NextSequence - 1,
        });
    }

    [McpTool("clear_console", "Clear the Output Log.", MainThread = false)]
    public McpToolResult ClearConsole()
    {
        ConsoleLog.Clear();
        return McpToolResult.Json(new JsonObject { ["cleared"] = true });
    }

    [McpTool("log_message", "Write a line to the Output Log, prefixed [Claude].", MainThread = false)]
    public McpToolResult LogMessage(
        [McpParam("Text to log")] string message,
        [McpParam("info, warning or error")] string level = "info")
    {
        if (!Enum.TryParse(level, ignoreCase: true, out LogLevel parsed))
            throw new McpToolException($"Unknown level '{level}'.", "Use info, warning or error.");

        ConsoleLog.Add("[Claude] " + message, parsed);
        return McpToolResult.Json(new JsonObject { ["seq"] = ConsoleLog.NextSequence - 1 });
    }

    // -------------------------------------------------------------------------

    private static Vector3 ToVector3(float[] values, string name)
    {
        if (values.Length != 3) throw new McpToolException($"'{name}' must have 3 elements [x, y, z]; got {values.Length}.");
        return new Vector3(values[0], values[1], values[2]);
    }
}
