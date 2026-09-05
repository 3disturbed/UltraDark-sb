namespace SexyBiscuit.Engine.Core;

/// <summary>
/// A reusable pool of objects, for the bullets/particles/enemies case where allocating
/// per spawn would churn the GC.
/// </summary>
/// <typeparam name="T">Pooled type. Must be a reference type.</typeparam>
/// <remarks>
/// Not thread-safe by design — the engine spawns from the game thread, and locking would
/// cost more than it saves. Pool from the game thread only.
/// </remarks>
/// <example>
/// <code>
/// var pool = new ObjectPool&lt;Bullet&gt;(
///     factory:   () =&gt; new Bullet(),
///     onRent:    b =&gt; b.Enabled = true,
///     onReturn:  b =&gt; b.Enabled = false,
///     prewarm:   64);
///
/// var bullet = pool.Rent();
/// // ...later
/// pool.Return(bullet);
/// </code>
/// </example>
public sealed class ObjectPool<T> where T : class
{
    private readonly Stack<T>   _available = new();
    private readonly Func<T>    _factory;
    private readonly Action<T>? _onRent;
    private readonly Action<T>? _onReturn;
    private readonly int        _maxRetained;

    /// <summary>Objects sitting in the pool ready to be rented.</summary>
    public int CountInactive => _available.Count;

    /// <summary>Objects currently rented out and not yet returned.</summary>
    public int CountActive { get; private set; }

    /// <summary>Total objects the pool has ever created.</summary>
    public int CountCreated { get; private set; }

    /// <param name="factory">Creates a new instance when the pool is empty.</param>
    /// <param name="onRent">Runs on an object as it leaves the pool — reset it to a usable state here.</param>
    /// <param name="onReturn">Runs on an object as it comes back — release references here so they can be collected.</param>
    /// <param name="prewarm">Number of instances to create up front, avoiding a hitch on first use.</param>
    /// <param name="maxRetained">
    /// Upper bound on pooled instances. Objects returned beyond this are dropped for the GC
    /// rather than retained, so a burst does not permanently inflate memory.
    /// </param>
    public ObjectPool(Func<T> factory, Action<T>? onRent = null, Action<T>? onReturn = null,
                      int prewarm = 0, int maxRetained = 1024)
    {
        _factory     = factory ?? throw new ArgumentNullException(nameof(factory));
        _onRent      = onRent;
        _onReturn    = onReturn;
        _maxRetained = Math.Max(1, maxRetained);

        for (int i = 0; i < prewarm; i++)
        {
            _available.Push(_factory());
            CountCreated++;
        }
    }

    /// <summary>Takes an object from the pool, creating one if none are available.</summary>
    public T Rent()
    {
        T item;
        if (_available.Count > 0)
        {
            item = _available.Pop();
        }
        else
        {
            item = _factory();
            CountCreated++;
        }

        CountActive++;
        _onRent?.Invoke(item);
        return item;
    }

    /// <summary>
    /// Returns an object to the pool. Returning the same object twice is a bug that
    /// would hand the same instance to two callers, so it throws in debug builds.
    /// </summary>
    public void Return(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

#if DEBUG
        if (_available.Contains(item))
            throw new InvalidOperationException(
                $"Object of type {typeof(T).Name} was returned to the pool twice.");
#endif

        _onReturn?.Invoke(item);
        CountActive = Math.Max(0, CountActive - 1);

        if (_available.Count < _maxRetained)
            _available.Push(item);
    }

    /// <summary>Drops every pooled instance. Rented objects are unaffected.</summary>
    public void Clear() => _available.Clear();
}

/// <summary>
/// An <see cref="Actor"/> pool that spawns from a prefab-style factory and parks
/// returned actors by deactivating them rather than destroying them.
/// </summary>
/// <remarks>
/// Pooled actors stay in their layer; <see cref="Actor.IsActive"/> is toggled instead of
/// removing them from the scene, so no per-spawn list mutation occurs.
/// </remarks>
public sealed class ActorPool
{
    private readonly ObjectPool<Actor> _pool;
    private readonly Scene             _scene;
    private readonly string            _layerName;

    /// <inheritdoc cref="ObjectPool{T}.CountInactive"/>
    public int CountInactive => _pool.CountInactive;

    /// <inheritdoc cref="ObjectPool{T}.CountActive"/>
    public int CountActive => _pool.CountActive;

    /// <param name="scene">Scene that pooled actors are added to.</param>
    /// <param name="factory">Creates a fresh actor — typically <c>() =&gt; prefab.Instantiate()</c>.</param>
    /// <param name="layerName">Layer new actors are added to.</param>
    /// <param name="prewarm">Actors to create and park up front.</param>
    public ActorPool(Scene scene, Func<Actor> factory, string layerName = "default", int prewarm = 0)
    {
        _scene     = scene ?? throw new ArgumentNullException(nameof(scene));
        _layerName = layerName;

        _pool = new ObjectPool<Actor>(
            factory: () =>
            {
                var a = factory();
                _scene.AddActor(a, _layerName);
                return a;
            },
            onRent:   a => a.IsActive = true,
            onReturn: a => a.IsActive = false);

        // Prewarm through Rent/Return so parked actors go in deactivated.
        if (prewarm > 0)
        {
            var warm = new Actor[prewarm];
            for (int i = 0; i < prewarm; i++) warm[i] = _pool.Rent();
            for (int i = 0; i < prewarm; i++) _pool.Return(warm[i]);
        }
    }

    /// <summary>Activates and returns a pooled actor.</summary>
    public Actor Spawn() => _pool.Rent();

    /// <summary>Deactivates an actor and returns it to the pool for reuse.</summary>
    public void Despawn(Actor actor) => _pool.Return(actor);
}
