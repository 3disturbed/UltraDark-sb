using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.Core;

/// <summary>
/// Base class for every object in the world.
/// Actors hold a list of Components and drive their lifecycle.
/// </summary>
public class Actor
{
    // -------------------------------------------------------------------------
    // Identity
    // -------------------------------------------------------------------------
    public string Name     { get; set; } = "Actor";
    public string Tag      { get; set; } = "Untagged";
    public int    Layer    { get; set; } = 0;
    public bool   IsActive { get; set; } = true;

    // -------------------------------------------------------------------------
    // Transform (always present — first component added)
    // -------------------------------------------------------------------------
    public Transform Transform { get; }

    // -------------------------------------------------------------------------
    // Scene graph
    // -------------------------------------------------------------------------
    public Scene?  Scene  { get; internal set; }
    public Layer?  Layer_ { get; internal set; }

    // -------------------------------------------------------------------------
    // Unique ID
    // -------------------------------------------------------------------------
    public uint Id { get; } = _nextId++;
    private static uint _nextId = 1;

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------
    private readonly List<Component> _components = new();
    private bool _started;
    private bool _destroyed;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public Actor()
    {
        Transform = new Transform();
        AttachComponent(Transform);
    }

    public Actor(string name) : this()
    {
        Name = name;
    }

    // -------------------------------------------------------------------------
    // Component management
    // -------------------------------------------------------------------------
    public T AddComponent<T>() where T : Component, new()
    {
        // Enforce [RequireComponent] dependencies first
        var attrs = typeof(T).GetCustomAttributes(typeof(RequireComponentAttribute), true);
        foreach (RequireComponentAttribute attr in attrs)
        {
            if (!HasComponent(attr.RequiredType))
                AddComponentByType(attr.RequiredType);
        }

        var c = new T();
        AttachComponent(c);
        return c;
    }

    public Component AddComponentByType(Type t)
    {
        var c = (Component)Activator.CreateInstance(t)!;
        AttachComponent(c);
        return c;
    }

    private void AttachComponent(Component c)
    {
        c.Actor = this;
        _components.Add(c);
        c.Awake();
        if (_started) c.Start();
    }

    public T? GetComponent<T>() where T : Component
    {
        foreach (var c in _components)
            if (c is T typed) return typed;
        return null;
    }

    public bool TryGetComponent<T>(out T component) where T : Component
    {
        component = GetComponent<T>()!;
        return component != null;
    }

    public IEnumerable<T> GetComponents<T>() where T : Component
        => _components.OfType<T>();

    public bool HasComponent<T>() where T : Component
        => _components.Any(c => c is T);

    public bool HasComponent(Type t)
        => _components.Any(c => t.IsInstanceOfType(c));

    public void RemoveComponent<T>() where T : Component
    {
        var c = GetComponent<T>();
        if (c == null) return;
        c.OnDestroy();
        _components.Remove(c);
    }

    public IReadOnlyList<Component> GetAllComponents() => _components;

    // -------------------------------------------------------------------------
    // Lifecycle — called by Layer
    // -------------------------------------------------------------------------
    internal void InternalAwake()
    {
        // Awake is called per component during Attach; nothing extra needed here.
    }

    internal void InternalStart()
    {
        if (_started) return;
        _started = true;
        OnStart();
        foreach (var c in _components.ToArray())
            if (c.Enabled) c.Start();
    }

    internal void InternalUpdate(float dt)
    {
        if (!IsActive || _destroyed) return;
        Update(dt);
        foreach (var c in _components.ToArray())
            if (c.Enabled) c.Update(dt);
    }

    internal void InternalFixedUpdate(float dt)
    {
        if (!IsActive || _destroyed) return;
        FixedUpdate(dt);
        foreach (var c in _components.ToArray())
            if (c.Enabled) c.FixedUpdate(dt);
    }

    internal void InternalLateUpdate(float dt)
    {
        if (!IsActive || _destroyed) return;
        LateUpdate(dt);
        foreach (var c in _components.ToArray())
            if (c.Enabled) c.LateUpdate(dt);
    }

    internal void InternalDraw(SpriteBatch sb)
    {
        if (!IsActive || _destroyed) return;
        Draw(sb);
        foreach (var c in _components.ToArray())
            if (c.Enabled) c.Draw(sb);
    }

    internal void InternalDestroy()
    {
        if (_destroyed) return;
        _destroyed = true;
        OnDestroy();
        foreach (var c in _components.ToArray())
            c.OnDestroy();
        _components.Clear();
    }

    // -------------------------------------------------------------------------
    // Virtual overrides for subclassing
    // -------------------------------------------------------------------------
    protected virtual void OnStart()         { }
    protected virtual void Update(float dt)        { }
    protected virtual void FixedUpdate(float dt)   { }
    protected virtual void LateUpdate(float dt)    { }
    protected virtual void Draw(SpriteBatch sb)    { }
    protected virtual void OnDestroy()       { }

    // -------------------------------------------------------------------------
    // Collision / trigger — forwarded from physics system
    // -------------------------------------------------------------------------
    public virtual void OnCollisionEnter(CollisionData data)
    {
        foreach (var c in _components.ToArray())
            if (c.Enabled) c.OnCollisionEnter(data);
    }

    public virtual void OnCollisionStay(CollisionData data)
    {
        foreach (var c in _components.ToArray())
            if (c.Enabled) c.OnCollisionStay(data);
    }

    public virtual void OnCollisionExit(CollisionData data)
    {
        foreach (var c in _components.ToArray())
            if (c.Enabled) c.OnCollisionExit(data);
    }

    public virtual void OnTriggerEnter(Actor other)
    {
        foreach (var c in _components.ToArray())
            if (c.Enabled) c.OnTriggerEnter(other);
    }

    public virtual void OnTriggerStay(Actor other)
    {
        foreach (var c in _components.ToArray())
            if (c.Enabled) c.OnTriggerStay(other);
    }

    public virtual void OnTriggerExit(Actor other)
    {
        foreach (var c in _components.ToArray())
            if (c.Enabled) c.OnTriggerExit(other);
    }

    // -------------------------------------------------------------------------
    // Destroy helper
    // -------------------------------------------------------------------------
    public void Destroy()
    {
        Scene?.MarkForDestroy(this);
    }

    public override string ToString() => $"Actor[{Id}:{Name}]";
}
