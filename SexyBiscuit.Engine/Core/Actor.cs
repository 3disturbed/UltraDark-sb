using System.Collections;
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
    // Hierarchy
    // -------------------------------------------------------------------------

    private Actor?               _parent;
    private readonly List<Actor> _children = new();

    /// <summary>The actor this one is attached to, or <c>null</c> when it sits at the root.</summary>
    public Actor? Parent => _parent;

    /// <summary>The actors attached to this one, in attachment order.</summary>
    public IReadOnlyList<Actor> Children => _children;

    /// <summary>The topmost ancestor, or this actor when it is not attached to anything.</summary>
    public Actor Root
    {
        get
        {
            var a = this;
            while (a._parent != null) a = a._parent;
            return a;
        }
    }

    /// <summary>
    /// True only when this actor and every ancestor is active.
    /// </summary>
    /// <remarks>
    /// <see cref="IsActive"/> says whether this actor was switched off; it says nothing about
    /// a parent that was. Gameplay code asking "should this be running?" wants this one.
    /// </remarks>
    public bool IsActiveInHierarchy
    {
        get
        {
            for (var a = this; a != null; a = a._parent)
                if (!a.IsActive) return false;
            return true;
        }
    }

    /// <summary>The 3D transform, or <c>null</c> on a purely 2D actor.</summary>
    public Transform3D? Transform3D => GetComponent<Transform3D>();

    /// <summary>
    /// Attaches this actor to <paramref name="parent"/>, or detaches it when that is null.
    /// </summary>
    /// <param name="parent">The new parent, or <c>null</c> to return to the scene root.</param>
    /// <param name="keepWorldTransform">
    /// When true (the default) the actor does not move: its local transform is rebased into
    /// the parent's space. When false the local transform is kept as written and the actor
    /// jumps to the parent's frame — what a turret mounted at a socket offset wants.
    /// </param>
    /// <remarks>
    /// Both transforms follow the attachment: <see cref="Transform"/> always, and
    /// <see cref="Transform3D"/> whenever both actors have one. Keeping the two in step is
    /// the reason attachment lives on the actor rather than on a transform — a 3D actor
    /// parented through the 2D transform alone inherits nothing, because no 3D renderer
    /// ever reads it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The new parent is this actor or one of its own descendants, which would make a cycle
    /// that every hierarchy walk in the engine would hang on.
    /// </exception>
    public void AttachTo(Actor? parent, bool keepWorldTransform = true)
    {
        if (ReferenceEquals(parent, this))
            throw new InvalidOperationException($"{Name} cannot be attached to itself.");
        if (parent != null && parent.IsDescendantOf(this))
            throw new InvalidOperationException(
                $"{parent.Name} is a descendant of {Name}; attaching would make a cycle.");
        if (ReferenceEquals(_parent, parent)) return;

        _parent?._children.Remove(this);
        _parent = parent;
        _parent?._children.Add(this);

        Transform.SetParent(parent?.Transform, keepWorldTransform);

        // Only when both ends have one. Attaching a 3D child to a 2D parent leaves the 3D
        // transform at the root rather than silently inventing a Transform3D on the parent.
        var childSpatial  = Transform3D;
        var parentSpatial = parent?.Transform3D;
        if (childSpatial != null && (parent == null || parentSpatial != null))
            childSpatial.SetParent(parentSpatial, keepWorldTransform);
    }

    /// <summary>Detaches this actor from its parent, returning it to the scene root.</summary>
    public void Detach(bool keepWorldTransform = true) => AttachTo(null, keepWorldTransform);

    /// <summary>Detaches every child, leaving them at the scene root where they stand.</summary>
    public void DetachChildren(bool keepWorldTransform = true)
    {
        foreach (var child in _children.ToArray())
            child.AttachTo(null, keepWorldTransform);
    }

    /// <summary>True when <paramref name="other"/> is this actor's parent, or its parent's parent, and so on.</summary>
    public bool IsDescendantOf(Actor other)
    {
        for (var a = _parent; a != null; a = a._parent)
            if (ReferenceEquals(a, other)) return true;
        return false;
    }

    /// <summary>
    /// The first child with this name, searching the whole subtree when
    /// <paramref name="recursive"/> is set.
    /// </summary>
    public Actor? FindChild(string name, bool recursive = false)
    {
        foreach (var child in _children)
            if (child.Name == name) return child;

        if (!recursive) return null;

        foreach (var child in _children)
            if (child.FindChild(name, true) is { } found) return found;

        return null;
    }

    /// <summary>
    /// A child looked up by a slash-separated path, as the editor and scene files write it:
    /// <c>FindChildByPath("Turret/Barrel/Muzzle")</c>.
    /// </summary>
    public Actor? FindChildByPath(string path)
    {
        var actor = this;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            actor = actor.FindChild(segment);
            if (actor == null) return null;
        }
        return ReferenceEquals(actor, this) ? null : actor;
    }

    /// <summary>Every descendant, depth first, parents before their own children.</summary>
    public IEnumerable<Actor> Descendants()
    {
        foreach (var child in _children)
        {
            yield return child;
            foreach (var grandchild in child.Descendants())
                yield return grandchild;
        }
    }

    /// <summary>
    /// The path from the root, as <see cref="FindChildByPath"/> reads it. Used by the editor's
    /// outliner and by scene diffing, where two actors can share a name but never a path.
    /// </summary>
    public string HierarchyPath
    {
        get
        {
            if (_parent == null) return Name;
            var names = new List<string>();
            for (var a = this; a != null; a = a._parent) names.Add(a.Name);
            names.Reverse();
            return string.Join('/', names);
        }
    }

    // -------------------------------------------------------------------------
    // Unique ID
    // -------------------------------------------------------------------------
    public uint Id { get; } = _nextId++;
    private static uint _nextId = 1;

    // -------------------------------------------------------------------------
    // Lifespan
    // -------------------------------------------------------------------------

    /// <summary>
    /// Seconds until the actor destroys itself. Zero or negative means it lives until
    /// something calls <see cref="Destroy()"/>. Counts down on scaled time.
    /// </summary>
    /// <remarks>
    /// Set this on projectiles, decals and impact effects so cleanup does not need a
    /// bespoke timer component on every short-lived actor.
    /// </remarks>
    public float LifeSpan { get; set; }

    /// <summary>True once <see cref="Destroy()"/> has run and the actor is no longer usable.</summary>
    public bool IsDestroyed => _destroyed;

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

    /// <summary>
    /// Adds a component by type, honouring <see cref="RequireComponentAttribute"/> like the
    /// generic overload does. The type-name path tools take arrives here.
    /// </summary>
    /// <remarks>
    /// <see cref="AddComponentByType"/> deliberately skips the requirement walk because the
    /// scene loader restores components in file order and must not invent extra ones; a tool
    /// adding a single component by name wants the opposite, so it gets its own entry point.
    /// </remarks>
    public Component AddComponent(Type type, bool honourRequirements = true)
    {
        if (!typeof(Component).IsAssignableFrom(type))
            throw new ArgumentException($"{type.Name} is not a Component.", nameof(type));

        if (honourRequirements)
        {
            foreach (var attr in type.GetCustomAttributes(typeof(RequireComponentAttribute), true).Cast<RequireComponentAttribute>())
            {
                if (!HasComponent(attr.RequiredType))
                    AddComponent(attr.RequiredType);
            }
        }

        return AddComponentByType(type);
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

    /// <summary>
    /// Removes one specific component instance. The actor's own <see cref="Transform"/> is
    /// refused: every actor has exactly one and nothing else can stand in for it.
    /// </summary>
    /// <returns>False when the component was not on this actor or was the transform.</returns>
    public bool RemoveComponent(Component component)
    {
        if (ReferenceEquals(component, Transform)) return false;
        if (!_components.Contains(component)) return false;

        component.OnDestroy();
        _components.Remove(component);
        return true;
    }

    public IReadOnlyList<Component> GetAllComponents() => _components;

    // -------------------------------------------------------------------------
    // Lifecycle — called by Layer
    // -------------------------------------------------------------------------
    internal void InternalAwake()
    {
        // Awake is called per component during Attach; nothing extra needed here.
    }

    // Every callback below is guarded by ExceptionIsolation as an exception *filter*: with no
    // handler installed the filter is false and the exception propagates untouched, so a
    // shipped game keeps its fail-fast behaviour and pays nothing. The editor installs a
    // handler so one throwing game component cannot take the whole editor down.

    internal void InternalStart()
    {
        if (_started) return;
        _started = true;

        try { OnStart(); }
        catch (Exception ex) when (ExceptionIsolation.TryHandle(ex, this, "OnStart")) { }

        foreach (var c in _components.ToArray())
        {
            if (!c.Enabled) continue;
            try { c.Start(); }
            catch (Exception ex) when (ExceptionIsolation.TryHandle(ex, c, "Start")) { }
        }
    }

    internal void InternalUpdate(float dt)
    {
        if (!IsActive || _destroyed) return;

        if (LifeSpan > 0f)
        {
            LifeSpan -= dt;
            if (LifeSpan <= 0f) { Destroy(); return; }
        }

        try { Update(dt); }
        catch (Exception ex) when (ExceptionIsolation.TryHandle(ex, this, "Update")) { }

        foreach (var c in _components.ToArray())
        {
            if (!c.Enabled) continue;
            try { c.Update(dt); }
            catch (Exception ex) when (ExceptionIsolation.TryHandle(ex, c, "Update")) { }
        }
    }

    internal void InternalFixedUpdate(float dt)
    {
        if (!IsActive || _destroyed) return;

        try { FixedUpdate(dt); }
        catch (Exception ex) when (ExceptionIsolation.TryHandle(ex, this, "FixedUpdate")) { }

        foreach (var c in _components.ToArray())
        {
            if (!c.Enabled) continue;
            try { c.FixedUpdate(dt); }
            catch (Exception ex) when (ExceptionIsolation.TryHandle(ex, c, "FixedUpdate")) { }
        }
    }

    internal void InternalLateUpdate(float dt)
    {
        if (!IsActive || _destroyed) return;

        try { LateUpdate(dt); }
        catch (Exception ex) when (ExceptionIsolation.TryHandle(ex, this, "LateUpdate")) { }

        foreach (var c in _components.ToArray())
        {
            if (!c.Enabled) continue;
            try { c.LateUpdate(dt); }
            catch (Exception ex) when (ExceptionIsolation.TryHandle(ex, c, "LateUpdate")) { }
        }
    }

    internal void InternalDraw(SpriteBatch sb)
    {
        if (!IsActive || _destroyed) return;

        try { Draw(sb); }
        catch (Exception ex) when (ExceptionIsolation.TryHandle(ex, this, "Draw")) { }

        foreach (var c in _components.ToArray())
        {
            if (!c.Enabled) continue;
            try { c.Draw(sb); }
            catch (Exception ex) when (ExceptionIsolation.TryHandle(ex, c, "Draw")) { }
        }
    }

    internal void InternalDestroy()
    {
        if (_destroyed) return;
        _destroyed = true;

        // Leave the hierarchy before anything else, so a parent that outlives this actor is
        // not left holding a destroyed child in its Children list. Children that were queued
        // alongside this actor detach themselves the same way; any that were not (a child
        // attached after Destroy was called) are cut loose rather than left pointing at a
        // shell.
        AttachTo(null, keepWorldTransform: true);
        DetachChildren();

        // Cancel anything this actor started so a coroutine cannot outlive its target.
        CoroutineRunner.Instance.StopAllFor(this);

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
    // Coroutines
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts a coroutine owned by this actor. It is cancelled automatically when the
    /// actor is destroyed.
    /// </summary>
    /// <example>
    /// <code>
    /// StartCoroutine(Blink());
    ///
    /// IEnumerator Blink()
    /// {
    ///     while (true)
    ///     {
    ///         IsActive = !IsActive;
    ///         yield return new WaitForSeconds(0.2f);
    ///     }
    /// }
    /// </code>
    /// </example>
    public Coroutine StartCoroutine(IEnumerator routine)
        => CoroutineRunner.Instance.Start(routine, this);

    /// <summary>Cancels a coroutine started by this actor.</summary>
    public void StopCoroutine(Coroutine? coroutine) => CoroutineRunner.Instance.Stop(coroutine);

    /// <summary>Cancels every coroutine this actor started.</summary>
    public void StopAllCoroutines() => CoroutineRunner.Instance.StopAllFor(this);

    // -------------------------------------------------------------------------
    // Destroy helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Queues the actor, and everything attached to it, for destruction. They are removed at
    /// the end of the current frame, so iteration in progress over the scene's actors stays
    /// valid.
    /// </summary>
    /// <remarks>
    /// Destroying a parent destroys its children. The alternative — orphaning them where they
    /// stand — leaves a turret hanging in the air when its tank dies, and every caller would
    /// have to remember to walk the subtree first. Call <see cref="DetachChildren"/> before
    /// destroying when the children really are meant to survive.
    /// </remarks>
    public void Destroy()
    {
        if (_destroyed) return;
        Scene?.MarkForDestroy(this);

        // Snapshot: a child's own Destroy detaches it, which would mutate the list underneath.
        foreach (var child in _children.ToArray())
            child.Destroy();
    }

    /// <summary>Destroys the actor after <paramref name="delaySeconds"/> of scaled time.</summary>
    public void Destroy(float delaySeconds)
    {
        if (delaySeconds <= 0f) { Destroy(); return; }
        LifeSpan = delaySeconds;
    }

    public override string ToString() => $"Actor[{Id}:{Name}]";
}
