namespace SexyBiscuit.Engine.Mcp;

public enum ActivityState
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>One tool call as the Assistant panel shows it.</summary>
public sealed record ActivityEntry(
    long          Seq,
    DateTime      StartedUtc,
    string        Tool,
    string        Label,
    string        Arguments,
    bool          Mutating,
    ActivityState State,
    TimeSpan      Duration,
    string        Summary,
    string?       Client);

/// <summary>
/// A thread-safe ring buffer of tool calls. Tools begin and complete entries from whatever
/// thread they run on; the panel polls <see cref="Snapshot"/> when <see cref="Version"/> moves.
/// </summary>
public sealed class ActivityLog
{
    private readonly object              _lock    = new();
    private readonly List<ActivityEntry> _entries = new();
    private readonly int                 _capacity;
    private long _seq;
    private int  _version;

    public ActivityLog(int capacity = 500) => _capacity = Math.Max(1, capacity);

    /// <summary>Bumps on every change, so a reader can skip re-snapshotting an unchanged log.</summary>
    public int Version => Volatile.Read(ref _version);

    public int RunningCount
    {
        get { lock (_lock) return _entries.Count(e => e.State == ActivityState.Running); }
    }

    /// <summary>Fires on the caller's thread — never touch UI from a handler.</summary>
    public event Action<ActivityEntry>? EntryChanged;

    public long Begin(string tool, string label, string argumentsSummary, bool mutating, string? client)
    {
        ActivityEntry entry;
        lock (_lock)
        {
            entry = new ActivityEntry(++_seq, DateTime.UtcNow, tool, label, argumentsSummary, mutating,
                                      ActivityState.Running, TimeSpan.Zero, "", client);
            _entries.Add(entry);
            if (_entries.Count > _capacity) _entries.RemoveAt(0);
            Interlocked.Increment(ref _version);
        }

        EntryChanged?.Invoke(entry);
        return entry.Seq;
    }

    public void Complete(long seq, ActivityState state, string summary)
    {
        ActivityEntry updated;
        lock (_lock)
        {
            int index = _entries.FindIndex(e => e.Seq == seq);
            if (index < 0) return;

            var old = _entries[index];
            updated = old with
            {
                State    = state,
                Duration = DateTime.UtcNow - old.StartedUtc,
                Summary  = summary,
            };
            _entries[index] = updated;
            Interlocked.Increment(ref _version);
        }

        EntryChanged?.Invoke(updated);
    }

    public IReadOnlyList<ActivityEntry> Snapshot()
    {
        lock (_lock) return _entries.ToArray();
    }

    public IReadOnlyList<ActivityEntry> Since(long seq)
    {
        lock (_lock) return _entries.Where(e => e.Seq > seq).ToArray();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            Interlocked.Increment(ref _version);
        }
    }
}
