using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.Core;

/// <summary>
/// A Scene is the root container for all gameplay. It holds an ordered list of Layers.
/// </summary>
public class Scene
{
    // -------------------------------------------------------------------------
    // Identity
    // -------------------------------------------------------------------------
    public string Name     { get; set; }
    public bool   IsActive { get; set; } = true;

    // -------------------------------------------------------------------------
    // Layers
    // -------------------------------------------------------------------------
    private readonly List<Layer> _layers = new();
    private readonly HashSet<Actor> _markedForDestroy = new();

    public IReadOnlyList<Layer> Layers => _layers;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public Scene(string name = "Scene")
    {
        Name = name;
        // Default layers
        AddLayer("background", -100);
        AddLayer("default",     0);
        AddLayer("foreground",  100);
        AddLayer("ui",          200);
    }

    // -------------------------------------------------------------------------
    // Layer management
    // -------------------------------------------------------------------------
    public Layer AddLayer(string name, int order = 0)
    {
        var layer = new Layer(name, order) { Scene = this };
        _layers.Add(layer);
        _layers.Sort((a, b) => a.Order.CompareTo(b.Order));
        return layer;
    }

    public Layer? GetLayer(string name)
        => _layers.FirstOrDefault(l => l.Name == name);

    public Layer GetOrCreateLayer(string name, int order = 0)
        => GetLayer(name) ?? AddLayer(name, order);

    public void RemoveLayer(string name)
    {
        var layer = GetLayer(name);
        if (layer == null) return;
        layer.Destroy();
        _layers.Remove(layer);
    }

    // -------------------------------------------------------------------------
    // Actor shortcuts (operate on "default" layer)
    // -------------------------------------------------------------------------
    public Actor AddActor(Actor actor, string layerName = "default")
    {
        var layer = GetOrCreateLayer(layerName);
        return layer.AddActor(actor);
    }

    public T AddActor<T>(string layerName = "default") where T : Actor, new()
    {
        var layer = GetOrCreateLayer(layerName);
        return layer.AddActor<T>();
    }

    public Actor? FindByName(string name)
    {
        foreach (var layer in _layers)
        {
            var found = layer.FindByName(name);
            if (found != null) return found;
        }
        return null;
    }

    public IEnumerable<Actor> FindByTag(string tag)
        => _layers.SelectMany(l => l.FindByTag(tag));

    public IEnumerable<T> FindActorsOfType<T>() where T : Actor
        => _layers.SelectMany(l => l.FindActorsOfType<T>());

    public void MarkForDestroy(Actor actor) => _markedForDestroy.Add(actor);

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------
    public void Update(float dt)
    {
        if (!IsActive) return;

        foreach (var layer in _layers.ToArray())
            layer.Update(dt);

        FlushDestroyQueue();
    }

    internal void FixedUpdate(float dt)
    {
        if (!IsActive) return;
        foreach (var layer in _layers.ToArray())
            layer.FixedUpdate(dt);
    }

    internal void LateUpdate(float dt)
    {
        if (!IsActive) return;
        foreach (var layer in _layers.ToArray())
            layer.LateUpdate(dt);
    }

    public void Draw(SpriteBatch sb)
    {
        if (!IsActive) return;
        foreach (var layer in _layers)
            layer.Draw(sb);
    }

    /// <summary>
    /// Destroys every actor in the scene and drops its layers.
    /// </summary>
    /// <remarks>
    /// <see cref="SceneManager"/> calls this when a scene is replaced or unloaded. Call it
    /// yourself for a scene you created directly with <c>new Scene(...)</c> — in a test, a
    /// tool, or a headless server. It is not optional bookkeeping: components register
    /// themselves in static registries (<see cref="Rendering.MeshRenderer.All"/>,
    /// <see cref="Rendering.Light3D.All"/>, <see cref="Gameplay.PlayerStart.All"/> and the
    /// rest) and only leave them on destroy, so a dropped scene leaves its actors visible
    /// to the renderer and to spawn selection forever.
    /// </remarks>
    public void Destroy()
    {
        foreach (var layer in _layers)
            layer.Destroy();

        _layers.Clear();
        _markedForDestroy.Clear();
    }

    /// <summary>
    /// Applies every <see cref="MarkForDestroy"/> queued during this frame.
    /// </summary>
    /// <remarks>
    /// <see cref="Layer.RemoveActor"/> only queues, so the layer's pending list is
    /// flushed here too. Without that the actor survived until the next frame's
    /// <see cref="Layer.Update"/> — two frames to destroy something, which contradicts
    /// the documented "removed at the end of the current frame" and left destroyed
    /// actors updating one more time.
    /// </remarks>
    private void FlushDestroyQueue()
    {
        if (_markedForDestroy.Count == 0) return;

        var touched = new HashSet<Layer>();

        foreach (var actor in _markedForDestroy)
        {
            var layer = actor.Layer_;
            if (layer == null) continue;

            layer.RemoveActor(actor);
            touched.Add(layer);
        }

        _markedForDestroy.Clear();

        foreach (var layer in touched)
            layer.FlushPending();
    }
}
