using System.Collections;

namespace SexyBiscuit.Engine.Core;

/// <summary>
/// Yield instructions understood by <see cref="CoroutineRunner"/>.
/// Returning any other value from a coroutine simply waits one frame.
/// </summary>
public abstract class YieldInstruction
{
    /// <summary>True while the coroutine should stay suspended.</summary>
    internal abstract bool KeepWaiting(float scaledDt, float unscaledDt);
}

/// <summary>Suspends a coroutine for a number of scaled seconds.</summary>
public sealed class WaitForSeconds : YieldInstruction
{
    private float _remaining;

    /// <param name="seconds">Delay in scaled seconds — affected by <see cref="Time.TimeScale"/>.</param>
    public WaitForSeconds(float seconds) => _remaining = seconds;

    internal override bool KeepWaiting(float scaledDt, float unscaledDt)
    {
        _remaining -= scaledDt;
        return _remaining > 0f;
    }
}

/// <summary>Suspends a coroutine for a number of real seconds, ignoring <see cref="Time.TimeScale"/>.</summary>
public sealed class WaitForSecondsRealtime : YieldInstruction
{
    private float _remaining;

    public WaitForSecondsRealtime(float seconds) => _remaining = seconds;

    internal override bool KeepWaiting(float scaledDt, float unscaledDt)
    {
        _remaining -= unscaledDt;
        return _remaining > 0f;
    }
}

/// <summary>Suspends a coroutine until the supplied predicate returns true.</summary>
public sealed class WaitUntil : YieldInstruction
{
    private readonly Func<bool> _predicate;
    public WaitUntil(Func<bool> predicate) => _predicate = predicate;
    internal override bool KeepWaiting(float scaledDt, float unscaledDt) => !_predicate();
}

/// <summary>Suspends a coroutine while the supplied predicate returns true.</summary>
public sealed class WaitWhile : YieldInstruction
{
    private readonly Func<bool> _predicate;
    public WaitWhile(Func<bool> predicate) => _predicate = predicate;
    internal override bool KeepWaiting(float scaledDt, float unscaledDt) => _predicate();
}

/// <summary>A live coroutine. Pass it to <see cref="CoroutineRunner.Stop"/> to cancel.</summary>
public sealed class Coroutine
{
    internal IEnumerator       Enumerator = null!;
    internal YieldInstruction? Waiting;
    internal Coroutine?        WaitingOn;
    internal object?           Owner;

    /// <summary>False once the coroutine has run to completion or been stopped.</summary>
    public bool IsRunning { get; internal set; } = true;
}

/// <summary>
/// Drives <see cref="IEnumerator"/>-based coroutines — the engine's equivalent of Unreal's
/// latent actions. Ticked once per frame by <see cref="SBEngine"/> after Update.
/// </summary>
/// <remarks>
/// Coroutines started with an <c>owner</c> are stopped automatically when
/// <see cref="StopAllFor"/> is called for that owner, which <see cref="Actor"/> does on destroy.
/// This prevents a coroutine outliving the actor it manipulates.
/// </remarks>
/// <example>
/// <code>
/// IEnumerator FadeOut()
/// {
///     yield return new WaitForSeconds(1f);
///     for (float t = 0; t &lt; 1f; t += Time.DeltaTime)
///     {
///         SetAlpha(1f - t);
///         yield return null;          // wait one frame
///     }
/// }
///
/// CoroutineRunner.Instance.Start(FadeOut(), owner: this);
/// </code>
/// </example>
public sealed class CoroutineRunner
{
    /// <summary>The engine-wide coroutine runner. Created by <see cref="SBEngine"/> at startup.</summary>
    public static CoroutineRunner Instance { get; internal set; } = new();

    private readonly List<Coroutine> _running = new();
    private readonly List<Coroutine> _pending = new();

    /// <summary>Number of coroutines currently alive.</summary>
    public int ActiveCount => _running.Count(c => c.IsRunning) + _pending.Count;

    /// <summary>
    /// Starts <paramref name="routine"/>. The first step runs on the next tick, not immediately.
    /// </summary>
    /// <param name="routine">The enumerator returned by an iterator method.</param>
    /// <param name="owner">
    /// Optional owner used by <see cref="StopAllFor"/>. Pass the component or actor that owns
    /// the routine so it is cancelled if that object goes away.
    /// </param>
    public Coroutine Start(IEnumerator routine, object? owner = null)
    {
        ArgumentNullException.ThrowIfNull(routine);
        var c = new Coroutine { Enumerator = routine, Owner = owner };
        _pending.Add(c);
        return c;
    }

    /// <summary>Cancels a coroutine. Safe to call on one that has already finished.</summary>
    public void Stop(Coroutine? coroutine)
    {
        if (coroutine == null) return;
        coroutine.IsRunning = false;
    }

    /// <summary>Cancels every coroutine started with the given owner.</summary>
    public void StopAllFor(object owner)
    {
        foreach (var c in _running) if (ReferenceEquals(c.Owner, owner)) c.IsRunning = false;
        foreach (var c in _pending) if (ReferenceEquals(c.Owner, owner)) c.IsRunning = false;
    }

    /// <summary>Cancels every coroutine.</summary>
    public void StopAll()
    {
        foreach (var c in _running) c.IsRunning = false;
        _pending.Clear();
    }

    /// <summary>Advances every coroutine by one step where its wait condition allows. Called by the engine.</summary>
    internal void Tick(float scaledDt, float unscaledDt)
    {
        if (_pending.Count > 0)
        {
            _running.AddRange(_pending);
            _pending.Clear();
        }

        for (int i = 0; i < _running.Count; i++)
        {
            var c = _running[i];
            if (!c.IsRunning) continue;

            // Nested coroutine: block until the inner one finishes.
            if (c.WaitingOn != null)
            {
                if (c.WaitingOn.IsRunning) continue;
                c.WaitingOn = null;
            }

            if (c.Waiting != null)
            {
                if (c.Waiting.KeepWaiting(scaledDt, unscaledDt)) continue;
                c.Waiting = null;
            }

            bool moved;
            try
            {
                moved = c.Enumerator.MoveNext();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CoroutineRunner] Coroutine threw and was stopped: {ex}");
                c.IsRunning = false;
                continue;
            }

            if (!moved)
            {
                c.IsRunning = false;
                continue;
            }

            switch (c.Enumerator.Current)
            {
                case YieldInstruction y: c.Waiting   = y; break;
                case Coroutine inner:    c.WaitingOn = inner; break;
                // null or anything else: resume next frame
            }
        }

        _running.RemoveAll(c => !c.IsRunning);
    }
}
