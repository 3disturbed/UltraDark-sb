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

    internal void Destroy()
    {
        foreach (var layer in _layers)
            layer.Destroy();
        _layers.Clear();
    }

    private void FlushDestroyQueue()
    {
        foreach (var actor in _markedForDestroy)
        {
            actor.Layer_?.RemoveActor(actor);
        }
        _markedForDestroy.Clear();
    }
}
