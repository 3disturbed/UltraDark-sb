using Microsoft.Xna.Framework.Graphics;

namespace SexyBiscuit.Engine.Core;

/// <summary>
/// Base class for all components. Attach to an Actor to add behaviour.
/// </summary>
public abstract class Component
{
    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    public Actor Actor { get; internal set; } = null!;
    public bool Enabled { get; set; } = true;

    // -------------------------------------------------------------------------
    // Lifecycle — called by the Actor, forwarded from the engine
    // -------------------------------------------------------------------------
    public virtual void Awake()       { }
    public virtual void Start()       { }
    public virtual void Update(float dt)       { }
    public virtual void FixedUpdate(float dt)  { }
    public virtual void LateUpdate(float dt)   { }
    public virtual void Draw(SpriteBatch sb)   { }
    public virtual void OnDestroy()   { }

    // -------------------------------------------------------------------------
    // Collision / trigger callbacks (populated by Physics system)
    // -------------------------------------------------------------------------
    public virtual void OnCollisionEnter(CollisionData data) { }
    public virtual void OnCollisionStay(CollisionData data)  { }
    public virtual void OnCollisionExit(CollisionData data)  { }
    public virtual void OnTriggerEnter(Actor other)          { }
    public virtual void OnTriggerStay(Actor other)           { }
    public virtual void OnTriggerExit(Actor other)           { }

    // -------------------------------------------------------------------------
    // Convenience accessors
    // -------------------------------------------------------------------------
    protected Transform Transform => Actor.Transform;

    public T? GetComponent<T>() where T : Component => Actor.GetComponent<T>();
    public T  AddComponent<T>() where T : Component, new() => Actor.AddComponent<T>();
}

/// <summary>
/// Minimal collision contact data passed to OnCollisionEnter/Stay/Exit.
/// </summary>
public readonly struct CollisionData
{
    public Actor Other         { get; init; }
    public Microsoft.Xna.Framework.Vector2 ContactPoint  { get; init; }
    public Microsoft.Xna.Framework.Vector2 Normal        { get; init; }
    public float RelativeVelocity { get; init; }
}

/// <summary>
/// Decorate a Component class to declare that it requires another component type.
/// The engine will auto-add the dependency if missing.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RequireComponentAttribute : Attribute
{
    public Type RequiredType { get; }
    public RequireComponentAttribute(Type t) => RequiredType = t;
}
