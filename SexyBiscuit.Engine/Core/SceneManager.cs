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
    private readonly SBEngine      _engine;

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
    public SceneManager(SBEngine engine)
    {
        _engine = engine;
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

    // -------------------------------------------------------------------------
    // Lifecycle — called by SBEngine
    // -------------------------------------------------------------------------
    internal void Update(float dt)
    {
        ProcessPendingLoad();
        _activeScene?.Update(dt);
        foreach (var s in _additiveScenes.ToArray())
            s.Update(dt);
    }

    internal void FixedUpdate(float dt)
    {
        _activeScene?.FixedUpdate(dt);
        foreach (var s in _additiveScenes.ToArray())
            s.FixedUpdate(dt);
    }

    internal void LateUpdate(float dt)
    {
        _activeScene?.LateUpdate(dt);
        foreach (var s in _additiveScenes.ToArray())
            s.LateUpdate(dt);
    }

    internal void Draw(SpriteBatch sb)
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

        if (_pendingLoadAdditive)
        {
            var additive = new Scene(System.IO.Path.GetFileNameWithoutExtension(path));
            _additiveScenes.Add(additive);
            OnSceneLoaded?.Invoke(additive);
        }
        else
        {
            if (_activeScene != null)
            {
                OnSceneUnloaded?.Invoke(_activeScene);
                _activeScene.Destroy();
            }

            var scene = new Scene(System.IO.Path.GetFileNameWithoutExtension(path));
            foreach (var actor in _dontDestroyActors)
                scene.AddActor(actor);

            _activeScene = scene;
            OnSceneLoaded?.Invoke(scene);
        }
    }
}
