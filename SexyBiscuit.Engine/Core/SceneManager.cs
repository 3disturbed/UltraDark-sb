using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.Core;

/// <summary>
/// Manages scene loading, unloading, additive scenes, DontDestroyOnLoad, and the active scene stack.
/// </summary>
public class SceneManager
{
    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private Scene?       _activeScene;
    private readonly List<Scene>   _additiveScenes = new();
    private readonly List<Actor>   _dontDestroyActors = new();

    private string? _pendingLoad;
    private bool    _pendingLoadAdditive;

    public Scene? ActiveScene => _activeScene;
    public IReadOnlyList<Scene> AdditiveScenes => _additiveScenes;

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------
    public event Action<Scene>? OnSceneLoaded;
    public event Action<Scene>? OnSceneUnloaded;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public SceneManager()
    {
    }

    // -------------------------------------------------------------------------
    // Loading
    // -------------------------------------------------------------------------
    public void LoadScene(string scenePath)
    {
        _pendingLoad = scenePath;
        _pendingLoadAdditive = false;
    }

    public void LoadSceneAdditive(string scenePath)
    {
        _pendingLoad = scenePath;
        _pendingLoadAdditive = true;
    }

    public async Task LoadSceneAsync(string scenePath, Action<float>? onProgress = null,
                                     bool additive = false)
    {
        // Signal progress start
        onProgress?.Invoke(0f);
        await Task.Run(() =>
        {
            // Background: deserialise scene JSON (heavy IO)
            // Full implementation in Scene serialiser
        });
        onProgress?.Invoke(1f);
        if (additive)
            LoadSceneAdditive(scenePath);
        else
            LoadScene(scenePath);
    }

    public void UnloadScene(string sceneName)
    {
        var scene = _additiveScenes.FirstOrDefault(s => s.Name == sceneName);
        if (scene == null) return;
        OnSceneUnloaded?.Invoke(scene);
        scene.Destroy();
        _additiveScenes.Remove(scene);
    }

    // -------------------------------------------------------------------------
    // DontDestroyOnLoad
    // -------------------------------------------------------------------------
    public void DontDestroyOnLoad(Actor actor)
    {
        if (!_dontDestroyActors.Contains(actor))
            _dontDestroyActors.Add(actor);
    }

    // -------------------------------------------------------------------------
    // Direct scene creation (code-first / test usage)
    // -------------------------------------------------------------------------
    public Scene CreateScene(string name = "Scene")
    {
        if (_activeScene != null && !_pendingLoadAdditive)
        {
            OnSceneUnloaded?.Invoke(_activeScene);
            _activeScene.Destroy();
        }

        var scene = new Scene(name);

        // Re-add DontDestroyOnLoad actors
        foreach (var actor in _dontDestroyActors)
            scene.AddActor(actor);

        _activeScene = scene;
        OnSceneLoaded?.Invoke(scene);
        return scene;
    }

    /// <summary>
    /// Makes an already-built <see cref="Scene"/> the active one, destroying whatever was
    /// active before.
    /// </summary>
    /// <remarks>
    /// The path for a scene you constructed yourself — deserialised from a file, generated
    /// procedurally, or assembled by a tool. <see cref="CreateScene"/> only makes an empty
    /// one, so loading a file previously meant deserialising it and then throwing the
    /// result away.
    /// </remarks>
    public Scene AdoptScene(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (_activeScene != null && !ReferenceEquals(_activeScene, scene))
        {
            OnSceneUnloaded?.Invoke(_activeScene);
            _activeScene.Destroy();
        }

        foreach (var actor in _dontDestroyActors)
            scene.AddActor(actor);

        _activeScene = scene;
        OnSceneLoaded?.Invoke(scene);
        return scene;
    }

    // -------------------------------------------------------------------------
    // Lifecycle — driven by SBEngine, or by a host through SBEngine.TickHosted
    // -------------------------------------------------------------------------

    /// <summary>Ticks the active and additive scenes. Called once per frame.</summary>
    public void Update(float dt)
    {
        ProcessPendingLoad();
        _activeScene?.Update(dt);
        foreach (var s in _additiveScenes.ToArray())
            s.Update(dt);
    }

    /// <summary>Ticks the fixed step on every live scene.</summary>
    public void FixedUpdate(float dt)
    {
        _activeScene?.FixedUpdate(dt);
        foreach (var s in _additiveScenes.ToArray())
            s.FixedUpdate(dt);
    }

    /// <summary>Runs LateUpdate on every live scene, after all Updates.</summary>
    public void LateUpdate(float dt)
    {
        _activeScene?.LateUpdate(dt);
        foreach (var s in _additiveScenes.ToArray())
            s.LateUpdate(dt);
    }

    /// <summary>Draws the 2D pass for every live scene into an open sprite batch.</summary>
    public void Draw(SpriteBatch sb)
    {
        _activeScene?.Draw(sb);
        foreach (var s in _additiveScenes)
            s.Draw(sb);
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------
    private void ProcessPendingLoad()
    {
        if (_pendingLoad == null) return;

        var path = _pendingLoad;
        _pendingLoad = null;

        var scene = ReadScene(path);

        if (_pendingLoadAdditive)
        {
            _additiveScenes.Add(scene);
            OnSceneLoaded?.Invoke(scene);
        }
        else
        {
            if (_activeScene != null)
            {
                OnSceneUnloaded?.Invoke(_activeScene);
                _activeScene.Destroy();
            }

            foreach (var actor in _dontDestroyActors)
                scene.AddActor(actor);

            _activeScene = scene;
            OnSceneLoaded?.Invoke(scene);
        }
    }

    /// <summary>
    /// Finds the file a scene path refers to: as given, with a <c>.scene</c> or <c>.json</c>
    /// extension, or under <c>Assets/</c> — each relative to the project root. Null when
    /// nothing matches.
    /// </summary>
    public static string? ResolveScenePath(string path)
    {
        foreach (var candidate in new[] { path, path + ".scene", path + ".json" })
        {
            string direct = ProjectPaths.Resolve(candidate);
            if (File.Exists(direct)) return direct;

            string underAssets = ProjectPaths.Resolve(Path.Combine("Assets", candidate));
            if (File.Exists(underAssets)) return underAssets;
        }

        return null;
    }

    // LoadScene used to produce an empty scene named after the file — the queued load was
    // only ever a placeholder, and every game had to deserialise its own scenes. Now the file
    // is read when it exists; a missing file still yields the empty scene, with a warning.
    private static Scene ReadScene(string path)
    {
        string fallbackName = Path.GetFileNameWithoutExtension(path);
        string? file = ResolveScenePath(path);

        if (file == null)
        {
            Console.Error.WriteLine($"[SceneManager] No scene file found for '{path}' under '{ProjectPaths.EffectiveRoot}'. Created an empty scene.");
            return new Scene(fallbackName);
        }

        try
        {
            return global::SexyBiscuit.Engine.Scene.SceneSerializer.LoadFromFile(file);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SceneManager] Could not load '{file}': {ex.Message}. Created an empty scene.");
            return new Scene(fallbackName);
        }
    }
}
