namespace SexyBiscuit.Engine.Core;

/// <summary>
/// An opaque handle to a scheduled timer. Keep it to query or cancel the timer later.
/// </summary>
public readonly struct TimerHandle : IEquatable<TimerHandle>
{
    internal readonly ulong Id;
    internal TimerHandle(ulong id) => Id = id;

    /// <summary>False for a default-constructed handle that was never scheduled.</summary>
    public bool IsValid => Id != 0;

    public bool Equals(TimerHandle other) => Id == other.Id;
    public override bool Equals(object? obj) => obj is TimerHandle h && Equals(h);
    public override int GetHashCode() => Id.GetHashCode();
    public static bool operator ==(TimerHandle a, TimerHandle b) => a.Equals(b);
    public static bool operator !=(TimerHandle a, TimerHandle b) => !a.Equals(b);
}

/// <summary>
/// Schedules callbacks to run after a delay or on a repeating interval, in the style of
/// Unreal's <c>FTimerManager</c>. Ticked once per frame by <see cref="SBEngine"/>.
/// </summary>
/// <remarks>
/// Timers run on the game thread between <c>Update</c> and <c>LateUpdate</c>, so a callback
/// can safely touch actors and components. Scheduling or clearing a timer from inside a
/// callback is safe — changes take effect on the following tick.
/// </remarks>
/// <example>
/// <code>
/// // Fire once after 3 seconds
/// var h = TimerManager.Instance.SetTimer(3f, () => Spawn());
///
/// // Fire every 0.5s after a 2s initial delay
/// TimerManager.Instance.SetTimer(0.5f, Fire, looping: true, firstDelay: 2f);
///
/// TimerManager.Instance.Clear(h);
/// </code>
/// </example>
public sealed class TimerManager
{
    /// <summary>The engine-wide timer manager. Created by <see cref="SBEngine"/> at startup.</summary>
    public static TimerManager Instance { get; internal set; } = new();

    private sealed class Entry
    {
        public ulong    Id;
        public float    Remaining;
        public float    Interval;
        public bool     Looping;
        public bool     UseUnscaledTime;
        public bool     Paused;
        public Action   Callback = null!;
        public bool     Cancelled;
    }

    private readonly List<Entry> _timers  = new();
    private readonly List<Entry> _pending = new();
    private ulong _nextId = 1;

    /// <summary>Number of live timers, including paused ones.</summary>
    public int ActiveCount => _timers.Count(t => !t.Cancelled) + _pending.Count;

    // -------------------------------------------------------------------------
    // Scheduling
    // -------------------------------------------------------------------------

    /// <summary>
    /// Schedules <paramref name="callback"/> to run after <paramref name="intervalSeconds"/>.
    /// </summary>
    /// <param name="intervalSeconds">Delay between fires. Values below zero are clamped to zero.</param>
    /// <param name="callback">Runs on the game thread.</param>
    /// <param name="looping">When true the timer reschedules itself after each fire.</param>
    /// <param name="firstDelay">Delay before the first fire. Negative means "use <paramref name="intervalSeconds"/>".</param>
    /// <param name="useUnscaledTime">When true the timer ignores <see cref="Time.TimeScale"/>, so it keeps running while the game is paused.</param>
    public TimerHandle SetTimer(float intervalSeconds, Action callback, bool looping = false,
                                float firstDelay = -1f, bool useUnscaledTime = false)
    {
        ArgumentNullException.ThrowIfNull(callback);

        float interval = MathF.Max(0f, intervalSeconds);
        var entry = new Entry
        {
            Id              = _nextId++,
            Interval        = interval,
            Remaining       = firstDelay >= 0f ? firstDelay : interval,
            Looping         = looping,
            UseUnscaledTime = useUnscaledTime,
            Callback        = callback,
        };

        _pending.Add(entry);
        return new TimerHandle(entry.Id);
    }

    /// <summary>Runs <paramref name="callback"/> at the start of the next frame's timer tick.</summary>
    public TimerHandle SetTimerForNextTick(Action callback) => SetTimer(0f, callback);

    // -------------------------------------------------------------------------
    // Control
    // -------------------------------------------------------------------------

    /// <summary>Cancels a timer. Returns false when the handle is unknown or already fired.</summary>
    public bool Clear(TimerHandle handle)
    {
        var e = Find(handle);
        if (e == null) return false;
        e.Cancelled = true;
        return true;
    }

    /// <summary>Cancels every scheduled timer.</summary>
    public void ClearAll()
    {
        foreach (var t in _timers) t.Cancelled = true;
        _pending.Clear();
    }

    /// <summary>Suspends a timer without losing its remaining time.</summary>
    public bool Pause(TimerHandle handle)
    {
        var e = Find(handle);
        if (e == null) return false;
        e.Paused = true;
        return true;
    }

    /// <summary>Resumes a timer suspended by <see cref="Pause"/>.</summary>
    public bool Resume(TimerHandle handle)
    {
        var e = Find(handle);
        if (e == null) return false;
        e.Paused = false;
        return true;
    }

    /// <summary>True while the handle refers to a timer that has not fired or been cancelled.</summary>
    public bool IsActive(TimerHandle handle) => Find(handle) != null;

    /// <summary>Seconds until the next fire, or -1 when the handle is unknown.</summary>
    public float GetRemaining(TimerHandle handle) => Find(handle)?.Remaining ?? -1f;

    private Entry? Find(TimerHandle handle)
    {
        if (!handle.IsValid) return null;
        foreach (var t in _timers)  if (t.Id == handle.Id && !t.Cancelled) return t;
        foreach (var t in _pending) if (t.Id == handle.Id && !t.Cancelled) return t;
        return null;
    }

    // -------------------------------------------------------------------------
    // Tick
    // -------------------------------------------------------------------------

    /// <summary>Advances every timer. Called once per frame by the engine.</summary>
    internal void Tick(float scaledDt, float unscaledDt)
    {
        if (_pending.Count > 0)
        {
            _timers.AddRange(_pending);
            _pending.Clear();
        }

        for (int i = 0; i < _timers.Count; i++)
        {
            var t = _timers[i];
            if (t.Cancelled || t.Paused) continue;

            t.Remaining -= t.UseUnscaledTime ? unscaledDt : scaledDt;
            if (t.Remaining > 0f) continue;

            try { t.Callback(); }
            catch (Exception ex) { Console.Error.WriteLine($"[TimerManager] Timer callback threw: {ex}"); }

            if (t.Looping && !t.Cancelled)
            {
                // Carry the overshoot forward so a fast-repeating timer does not drift.
                t.Remaining += t.Interval > 0f ? t.Interval : float.Epsilon;
                if (t.Remaining <= 0f) t.Remaining = t.Interval;
            }
            else
            {
                t.Cancelled = true;
            }
        }

        _timers.RemoveAll(t => t.Cancelled);
    }
}
