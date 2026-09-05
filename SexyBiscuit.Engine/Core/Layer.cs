using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.Core;

/// <summary>
/// An ordered collection of Actors within a Scene.
/// Layers control draw order (lower index = drawn first/behind).
/// </summary>
public class Layer
{
    // -------------------------------------------------------------------------
    // Identity
    // -------------------------------------------------------------------------
    public string Name    { get; set; }
    public int    Order   { get; set; }
    public bool   Visible { get; set; } = true;
    public bool   Active  { get; set; } = true;

    public Scene Scene { get; internal set; } = null!;

    // -------------------------------------------------------------------------
    // Actors
    // -------------------------------------------------------------------------
    private readonly List<Actor>  _actors       = new();
    private readonly List<Actor>  _pendingAdd    = new();
    private readonly List<Actor>  _pendingRemove = new();

    // Reused scratch so a flush allocates nothing.
    private readonly List<Actor>  _flushBuffer   = new();

    public IReadOnlyList<Actor> Actors => _actors;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public Layer(string name, int order = 0)
    {
        Name  = name;
        Order = order;
    }

    // -------------------------------------------------------------------------
    // Actor management
    // -------------------------------------------------------------------------
    public Actor AddActor(Actor actor)
    {
        actor.Scene  = Scene;
        actor.Layer_ = this;
        _pendingAdd.Add(actor);
        return actor;
    }

    public T AddActor<T>() where T : Actor, new()
    {
        var a = new T();
        AddActor(a);
        return a;
    }

    public void RemoveActor(Actor actor)
    {
        _pendingRemove.Add(actor);
    }

    public Actor? FindByName(string name)
        => _actors.FirstOrDefault(a => a.Name == name);

    public IEnumerable<Actor> FindByTag(string tag)
        => _actors.Where(a => a.Tag == tag);

    public IEnumerable<T> FindActorsOfType<T>() where T : Actor
        => _actors.OfType<T>();

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------
    /// <summary>
    /// Applies queued adds and removes. Called at the start of <see cref="Update"/>.
    /// </summary>
    /// <remarks>
    /// Both queues are drained into a local buffer before being walked, because
    /// <c>Start</c> and <c>OnDestroy</c> routinely spawn or destroy actors — a game mode
    /// spawning its controller and pawn is the obvious case — and that would otherwise
    /// mutate the list mid-enumeration. Anything queued during the flush lands on the
    /// next frame, which matches the documented "added at the start of the next Update".
    /// </remarks>
    internal void FlushPending()
    {
        if (_pendingAdd.Count > 0)
        {
            _flushBuffer.Clear();
            _flushBuffer.AddRange(_pendingAdd);
            _pendingAdd.Clear();

            foreach (var a in _flushBuffer)
            {
                _actors.Add(a);
                a.InternalStart();
            }
        }

        if (_pendingRemove.Count > 0)
        {
            _flushBuffer.Clear();
            _flushBuffer.AddRange(_pendingRemove);
            _pendingRemove.Clear();

            foreach (var a in _flushBuffer)
            {
                a.InternalDestroy();
                _actors.Remove(a);
            }
        }

        _flushBuffer.Clear();
    }

    internal void Update(float dt)
    {
        if (!Active) return;
        FlushPending();
        foreach (var a in _actors.ToArray())
            a.InternalUpdate(dt);
    }

    internal void FixedUpdate(float dt)
    {
        if (!Active) return;
        foreach (var a in _actors.ToArray())
            a.InternalFixedUpdate(dt);
    }

    internal void LateUpdate(float dt)
    {
        if (!Active) return;
        foreach (var a in _actors.ToArray())
            a.InternalLateUpdate(dt);
    }

    internal void Draw(SpriteBatch sb)
    {
        if (!Visible) return;
        // Sort by transform Z depth each frame (float sort key via Transform.LocalPosition.Y or a ZOrder property)
        var sorted = _actors
            .Where(a => a.IsActive)
            .OrderBy(a => a.Transform.LocalPosition.Y); // default painter's algorithm; override with ZOrder component if needed

        foreach (var a in sorted)
            a.InternalDraw(sb);
    }

    /// <summary>Destroys every actor in the layer and clears its queues.</summary>
    public void Destroy()
    {
        // Snapshot: InternalDestroy runs OnDestroy, which can touch the actor list.
        foreach (var a in _actors.ToArray())
            a.InternalDestroy();
        _actors.Clear();
        _pendingAdd.Clear();
        _pendingRemove.Clear();
    }
}
