namespace SexyBiscuit.Editor;

public record ConsoleEntry(string Message, LogLevel Level, DateTime Timestamp);

/// <summary>
/// Thread-safe static console log shared across all editor panels.
/// </summary>
public static class ConsoleLog
{
    private static readonly object _lock = new();
    private static readonly List<ConsoleEntry> _entries = new();

    public static IReadOnlyList<ConsoleEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public static event Action? OnEntryAdded;

    public static void Add(string message, LogLevel level = LogLevel.Info)
    {
        var entry = new ConsoleEntry(message, level, DateTime.Now);
        lock (_lock)
        {
            _entries.Add(entry);
        }
        OnEntryAdded?.Invoke();
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
        OnEntryAdded?.Invoke();
    }
}

/// <summary>
/// TraceListener that routes Debug.WriteLine output into the editor console.
/// </summary>
public sealed class EditorTraceListener : System.Diagnostics.TraceListener
{
    public override void Write(string? message)
    {
        if (!string.IsNullOrEmpty(message))
            ConsoleLog.Add(message, LogLevel.Info);
    }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            var level = LogLevel.Info;
            if (message.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("[Script Error]", StringComparison.OrdinalIgnoreCase))
                level = LogLevel.Error;
            else if (message.StartsWith("[Warning]", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("Warning:", StringComparison.OrdinalIgnoreCase))
                level = LogLevel.Warning;

            ConsoleLog.Add(message, level);
        }
    }
}
