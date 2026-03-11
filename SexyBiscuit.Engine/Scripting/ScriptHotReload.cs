using System.Diagnostics;

namespace SexyBiscuit.Engine.Scripting;

// =============================================================================
// ScriptHotReload
//
// Only compiled in Debug and Development configurations.  In Release the class
// is a complete no-op stub so no FileSystemWatcher overhead ever reaches prod.
// =============================================================================

#if DEBUG || DEVELOPMENT

/// <summary>
/// Watches a scripts directory for <c>.js</c> file changes and hot-reloads any
/// registered <see cref="ScriptComponent"/> whose <see cref="ScriptComponent.ScriptPath"/>
/// matches the changed file.
///
/// Thread safety: <see cref="FileSystemWatcher"/> callbacks fire on a thread-pool
/// thread. Reload work is queued and drained on the main game thread via
/// <see cref="Update"/> to keep Jint access single-threaded.
///
/// Usage (typically in your engine bootstrap or scene):
/// <code>
///   var hotReload = new ScriptHotReload("Scripts");
///   hotReload.Register(myScriptComponent);
///   // Call hotReload.Update() once per frame from SBEngine.Update or similar.
/// </code>
/// </summary>
public sealed class ScriptHotReload : IDisposable
{
    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private readonly FileSystemWatcher _watcher;
    private readonly string _scriptsDirectory;

    // Registered components — accessed only from the main thread.
    private readonly List<ScriptComponent> _registered = new();

    // Paths queued for reload — written from the watcher thread, drained on the
    // main thread. Lock _pendingLock before accessing.
    private readonly HashSet<string> _pendingReloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingLock = new();

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates the watcher and begins monitoring <paramref name="scriptsDirectory"/>.
    /// Subdirectories are included.
    /// </summary>
    /// <param name="scriptsDirectory">
    /// Root directory to watch, e.g. <c>"Scripts"</c> or an absolute path.
    /// </param>
    public ScriptHotReload(string scriptsDirectory)
    {
        _scriptsDirectory = Path.GetFullPath(scriptsDirectory
            ?? throw new ArgumentNullException(nameof(scriptsDirectory)));

        if (!Directory.Exists(_scriptsDirectory))
        {
            System.Diagnostics.Debug.WriteLine($"[HotReload] Scripts directory does not exist: {_scriptsDirectory}. Watcher not started.");
            // Create a dummy watcher that does nothing rather than throwing.
            _watcher = new FileSystemWatcher();
            return;
        }

        _watcher = new FileSystemWatcher(_scriptsDirectory, "*.js")
        {
            IncludeSubdirectories = true,
            NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents   = true
        };

        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileRenamed;

        System.Diagnostics.Debug.WriteLine($"[HotReload] Watching: {_scriptsDirectory}");
    }

    // -------------------------------------------------------------------------
    // Registration
    // -------------------------------------------------------------------------

    /// <summary>
    /// Registers a <see cref="ScriptComponent"/> for hot-reload monitoring.
    /// Must be called from the main thread.
    /// </summary>
    public void Register(ScriptComponent script)
    {
        if (script == null) throw new ArgumentNullException(nameof(script));
        if (!_registered.Contains(script))
            _registered.Add(script);
    }

    /// <summary>
    /// Removes a previously registered <see cref="ScriptComponent"/>.
    /// Must be called from the main thread.
    /// </summary>
    public void Unregister(ScriptComponent script)
    {
        if (script == null) return;
        _registered.Remove(script);
    }

    // -------------------------------------------------------------------------
    // Main-thread drain
    // -------------------------------------------------------------------------

    /// <summary>
    /// Drains the reload queue and calls <see cref="ScriptComponent.Reload"/> on
    /// any component whose script file was modified since the last call.
    ///
    /// Call this once per frame from the engine update loop — ideally at the very
    /// start of the update tick, before any component logic runs.
    /// </summary>
    public void Update()
    {
        // Swap the pending set under the lock, then process outside the lock so
        // we hold it for the minimum possible time.
        HashSet<string>? toProcess = null;
        lock (_pendingLock)
        {
            if (_pendingReloads.Count > 0)
            {
                toProcess = new HashSet<string>(_pendingReloads, StringComparer.OrdinalIgnoreCase);
                _pendingReloads.Clear();
            }
        }

        if (toProcess == null) return;

        foreach (var changedPath in toProcess)
        {
            // Normalise to a full path so comparisons are robust.
            string fullChanged = Path.GetFullPath(changedPath);

            // Find all registered components whose script file matches.
            var affected = _registered
                .Where(sc => !string.IsNullOrEmpty(sc.ScriptPath)
                          && string.Equals(
                                 Path.GetFullPath(sc.ScriptPath),
                                 fullChanged,
                                 StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (affected.Length == 0) continue;

            foreach (var sc in affected)
            {
                try
                {
                    sc.Reload();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[HotReload] Exception during reload of '{changedPath}': {ex.Message}");
                }
            }

            // Use a relative path in the log if possible, for readability.
            string displayPath = TryMakeRelative(fullChanged);
            System.Diagnostics.Debug.WriteLine($"[HotReload] Reloaded: {displayPath} ({affected.Length} instance{(affected.Length == 1 ? "" : "s")})");
            Console.WriteLine($"[HotReload] Reloaded: {displayPath} ({affected.Length} instance{(affected.Length == 1 ? "" : "s")})");
        }
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileEvent;
        _watcher.Created -= OnFileEvent;
        _watcher.Renamed -= OnFileRenamed;
        _watcher.Dispose();
        _registered.Clear();

        lock (_pendingLock)
            _pendingReloads.Clear();
    }

    // -------------------------------------------------------------------------
    // Watcher callbacks (fire on thread-pool threads)
    // -------------------------------------------------------------------------

    private void OnFileEvent(object sender, FileSystemEventArgs e)
        => EnqueueReload(e.FullPath);

    private void OnFileRenamed(object sender, RenamedEventArgs e)
        => EnqueueReload(e.FullPath);

    private void EnqueueReload(string fullPath)
    {
        lock (_pendingLock)
            _pendingReloads.Add(fullPath);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private string TryMakeRelative(string fullPath)
    {
        try
        {
            if (fullPath.StartsWith(_scriptsDirectory, StringComparison.OrdinalIgnoreCase))
            {
                var rel = fullPath[_scriptsDirectory.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return rel;
            }
        }
        catch { /* swallow — display path is cosmetic */ }
        return fullPath;
    }
}

#else

/// <summary>
/// Release build no-op stub of <see cref="ScriptHotReload"/>.
/// All members compile cleanly but do nothing, incurring zero runtime cost.
/// </summary>
public sealed class ScriptHotReload : IDisposable
{
    public ScriptHotReload(string scriptsDirectory) { }
    public void Register(ScriptComponent script)    { }
    public void Unregister(ScriptComponent script)  { }
    public void Update()                            { }
    public void Dispose()                           { }
}

#endif
