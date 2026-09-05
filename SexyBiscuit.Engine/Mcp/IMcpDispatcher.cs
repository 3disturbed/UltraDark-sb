using System.Collections.Concurrent;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>
/// Marshals work onto the thread that owns the scene. The scene graph has no locks, so every
/// tool that touches it runs through one of these.
/// </summary>
public interface IMcpDispatcher
{
    bool IsOnMainThread { get; }

    Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellation = default);

    Task InvokeAsync(Action work, CancellationToken cancellation = default);
}

/// <summary>Runs work immediately on the calling thread. For tests and headless hosts.</summary>
public sealed class InlineMcpDispatcher : IMcpDispatcher
{
    public static InlineMcpDispatcher Instance { get; } = new();

    public bool IsOnMainThread => true;

    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellation = default)
    {
        if (cancellation.IsCancellationRequested) return Task.FromCanceled<T>(cancellation);

        try
        {
            return Task.FromResult(work());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    public Task InvokeAsync(Action work, CancellationToken cancellation = default)
        => InvokeAsync<object?>(() => { work(); return null; }, cancellation);
}

/// <summary>
/// Queue-then-drain, the same shape <c>ScriptHotReload</c> uses for its watcher callbacks: any
/// thread enqueues, the game thread calls <see cref="Drain"/> once per frame.
/// </summary>
/// <remarks>
/// Continuations run asynchronously (<see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>),
/// so the HTTP thread awaiting a result never resumes inside the game loop. Work invoked from
/// the main thread itself runs inline, so a tool calling another tool cannot deadlock.
/// </remarks>
public sealed class QueuedMcpDispatcher : IMcpDispatcher
{
    private sealed record WorkItem(Action Run, Action Cancel);

    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private int  _mainThreadId = -1;
    private bool _shutdown;

    /// <summary>Records the calling thread as the main thread. Call once from the game loop.</summary>
    public void BindMainThread() => _mainThreadId = Environment.CurrentManagedThreadId;

    public bool IsOnMainThread => _mainThreadId == Environment.CurrentManagedThreadId;

    public int PendingCount => _queue.Count;

    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellation = default)
    {
        if (_shutdown) return Task.FromCanceled<T>(new CancellationToken(canceled: true));
        if (cancellation.IsCancellationRequested) return Task.FromCanceled<T>(cancellation);

        if (IsOnMainThread)
        {
            try
            {
                return Task.FromResult(work());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        _queue.Enqueue(new WorkItem(
            Run: () =>
            {
                // Cancelled while queued: skip the work rather than mutate a scene nobody is
                // waiting on any more.
                if (cancellation.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellation);
                    return;
                }

                try
                {
                    tcs.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            Cancel: () => tcs.TrySetCanceled()));

        return tcs.Task;
    }

    public Task InvokeAsync(Action work, CancellationToken cancellation = default)
        => InvokeAsync<object?>(() => { work(); return null; }, cancellation);

    /// <summary>
    /// Runs everything queued at the moment of the call. Items queued during the drain wait for
    /// the next one, matching <c>Layer.FlushPending</c>'s rule for spawns during a flush.
    /// </summary>
    public int Drain()
    {
        int budget = _queue.Count;
        int ran    = 0;

        while (ran < budget && _queue.TryDequeue(out var item))
        {
            item.Run();
            ran++;
        }

        return ran;
    }

    /// <summary>Fails every pending item and rejects new work. Called on editor exit.</summary>
    public void Shutdown()
    {
        _shutdown = true;
        while (_queue.TryDequeue(out var item))
            item.Cancel();
    }
}
