namespace SexyBiscuit.Engine.Core;

/// <summary>
/// A multicast delegate in the style of Unreal's <c>DECLARE_MULTICAST_DELEGATE</c>.
/// </summary>
/// <remarks>
/// Chosen over a plain C# <c>event</c> where the engine needs to broadcast safely while
/// listeners add or remove themselves mid-broadcast, and where a subsystem must be able
/// to clear every listener at once on teardown. Broadcasting iterates a snapshot, so a
/// handler that unsubscribes during the broadcast still receives the current call but
/// none after it.
/// </remarks>
public sealed class SBEvent
{
    private readonly List<Action> _handlers = new();
    private Action[]? _snapshot;

    /// <summary>Number of currently bound handlers.</summary>
    public int Count => _handlers.Count;

    /// <summary>Binds a handler. The same handler may be bound more than once.</summary>
    public void Add(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.Add(handler);
        _snapshot = null;
    }

    /// <summary>Unbinds the first occurrence of <paramref name="handler"/>. Safe to call during a broadcast.</summary>
    public bool Remove(Action handler)
    {
        bool removed = _handlers.Remove(handler);
        if (removed) _snapshot = null;
        return removed;
    }

    /// <summary>Unbinds every handler.</summary>
    public void Clear()
    {
        _handlers.Clear();
        _snapshot = null;
    }

    /// <summary>
    /// Invokes every bound handler in bind order. An exception thrown by one handler is
    /// logged and does not prevent the remaining handlers from running.
    /// </summary>
    public void Broadcast()
    {
        var handlers = _snapshot ??= _handlers.ToArray();
        foreach (var h in handlers)
        {
            try { h(); }
            catch (Exception ex) { Console.Error.WriteLine($"[SBEvent] Handler threw: {ex}"); }
        }
    }

    public static SBEvent operator +(SBEvent e, Action handler) { e.Add(handler);    return e; }
    public static SBEvent operator -(SBEvent e, Action handler) { e.Remove(handler); return e; }
}

/// <inheritdoc cref="SBEvent"/>
/// <typeparam name="T">Payload passed to each handler.</typeparam>
public sealed class SBEvent<T>
{
    private readonly List<Action<T>> _handlers = new();
    private Action<T>[]? _snapshot;

    /// <summary>Number of currently bound handlers.</summary>
    public int Count => _handlers.Count;

    /// <inheritdoc cref="SBEvent.Add"/>
    public void Add(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.Add(handler);
        _snapshot = null;
    }

    /// <inheritdoc cref="SBEvent.Remove"/>
    public bool Remove(Action<T> handler)
    {
        bool removed = _handlers.Remove(handler);
        if (removed) _snapshot = null;
        return removed;
    }

    /// <inheritdoc cref="SBEvent.Clear"/>
    public void Clear()
    {
        _handlers.Clear();
        _snapshot = null;
    }

    /// <inheritdoc cref="SBEvent.Broadcast"/>
    public void Broadcast(T payload)
    {
        var handlers = _snapshot ??= _handlers.ToArray();
        foreach (var h in handlers)
        {
            try { h(payload); }
            catch (Exception ex) { Console.Error.WriteLine($"[SBEvent] Handler threw: {ex}"); }
        }
    }

    public static SBEvent<T> operator +(SBEvent<T> e, Action<T> handler) { e.Add(handler);    return e; }
    public static SBEvent<T> operator -(SBEvent<T> e, Action<T> handler) { e.Remove(handler); return e; }
}
