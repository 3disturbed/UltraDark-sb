using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;
using System.Collections.Concurrent;

namespace SexyBiscuit.Engine.Assets;

/// <summary>
/// Central asset registry. Loads raw assets by file-system path, reference-counts
/// entries, and (in DEBUG/DEVELOPMENT builds) hot-reloads changed files via
/// <see cref="FileSystemWatcher"/>.
/// </summary>
public class AssetManager : IDisposable
{
    // -------------------------------------------------------------------------
    // Cache entry
    // -------------------------------------------------------------------------
    private sealed class CacheEntry
    {
        public object    Value       { get; set; } = null!;
        public Type      Type        { get; set; } = null!;
        public int       RefCount    { get; set; }
        public long      ByteSize    { get; set; }
        public DateTime  LastLoaded  { get; set; }
    }

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private readonly GraphicsDevice                         _gd;
    private readonly ContentManager                         _content;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Hot-reload watcher — only compiled when DEBUG or DEVELOPMENT is defined.
#if DEBUG || DEVELOPMENT
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _reloadLock = new();
#endif

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------
    /// <summary>
    /// Raised when an asset file changes on disk and has been hot-reloaded. The payload
    /// is the normalised asset path.
    /// </summary>
    /// <remarks>
    /// Only ever raised in Debug and Development builds, where the file watchers exist —
    /// the member itself is always present so game code does not need its own
    /// <c>#if DEBUG</c> around the subscription. Raised from the watcher's background
    /// thread, so a handler that touches the scene should marshal onto the game thread
    /// via <see cref="Core.TimerManager.SetTimerForNextTick"/>.
    /// </remarks>
    public Core.SBEvent<string> OnAssetReloaded { get; } = new();

    // -------------------------------------------------------------------------
    // Construction / disposal
    // -------------------------------------------------------------------------
    public AssetManager(GraphicsDevice gd, ContentManager content)
    {
        _gd      = gd      ?? throw new ArgumentNullException(nameof(gd));
        _content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public void Dispose()
    {
        UnloadAll();
#if DEBUG || DEVELOPMENT
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
#endif
    }

    // -------------------------------------------------------------------------
    // Public API — synchronous load
    // -------------------------------------------------------------------------
    /// <summary>
    /// Loads an asset from <paramref name="path"/> and caches it.
    /// Subsequent calls with the same path return the cached instance and
    /// increment its reference count.
    /// </summary>
    public T Load<T>(string path) where T : class
    {
        var key = NormalisePath(path);

        if (_cache.TryGetValue(key, out var existing))
        {
            existing.RefCount++;
            return (T)existing.Value;
        }

        // Check bundles before the raw file system.
        var (stream, byteSize) = ResolveStream(key);

        T asset;
        try
        {
            asset = LoadFromStream<T>(key, stream);
        }
        finally
        {
            stream?.Dispose();
        }

        var entry = new CacheEntry
        {
            Value      = asset,
            Type       = typeof(T),
            RefCount   = 1,
            ByteSize   = byteSize,
            LastLoaded = DateTime.UtcNow
        };
        _cache[key] = entry;

#if DEBUG || DEVELOPMENT
        RegisterWatcher(key);
#endif

        return asset;
    }

    // -------------------------------------------------------------------------
    // Public API — async load
    // -------------------------------------------------------------------------
    /// <summary>
    /// Asynchronously loads an asset. <paramref name="onProgress"/> is called
    /// with values 0.0→1.0 during the load (best-effort; byte-level progress
    /// is only available for raw byte reads).
    /// </summary>
    public async Task<T> LoadAsync<T>(string path, Action<float>? onProgress = null) where T : class
    {
        var key = NormalisePath(path);

        if (_cache.TryGetValue(key, out var existing))
        {
            existing.RefCount++;
            onProgress?.Invoke(1f);
            return (T)existing.Value;
        }

        return await Task.Run(() =>
        {
            onProgress?.Invoke(0f);
            var result = Load<T>(path);
            onProgress?.Invoke(1f);
            return result;
        }).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Public API — unload
    // -------------------------------------------------------------------------
    /// <summary>
    /// Decrements the reference count for the asset at <paramref name="path"/>.
    /// When the count reaches zero the asset is disposed and removed from cache.
    /// </summary>
    public void Unload(string path)
    {
        var key = NormalisePath(path);
        if (!_cache.TryGetValue(key, out var entry)) return;

        entry.RefCount--;
        if (entry.RefCount > 0) return;

        DisposeEntry(entry);
        _cache.TryRemove(key, out _);

#if DEBUG || DEVELOPMENT
        if (_watchers.TryRemove(key, out var watcher))
            watcher.Dispose();
#endif
    }

    /// <summary>Forcibly unloads every cached asset, disposing disposable instances.</summary>
    public void UnloadAll()
    {
        foreach (var entry in _cache.Values)
            DisposeEntry(entry);
        _cache.Clear();

#if DEBUG || DEVELOPMENT
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
#endif
    }

    // -------------------------------------------------------------------------
    // Public API — queries
    // -------------------------------------------------------------------------
    public bool IsLoaded(string path) => _cache.ContainsKey(NormalisePath(path));

    /// <summary>Returns a snapshot of every loaded asset and its memory footprint.</summary>
    public IEnumerable<(string path, Type type, long bytes)> GetLoadedAssets()
        => _cache.Select(kvp => (kvp.Key, kvp.Value.Type, kvp.Value.ByteSize));

    // -------------------------------------------------------------------------
    // Internal helpers — stream resolution (bundles → file system)
    // -------------------------------------------------------------------------
    private static (Stream stream, long byteSize) ResolveStream(string normalisedPath)
    {
        // 1. Try mounted bundles first.
        foreach (var bundle in AssetBundle.MountedBundles)
        {
            var stream = bundle.OpenEntry(normalisedPath);
            if (stream != null)
            {
                // Bundle streams are typically not seekable; buffer them so
                // loaders that require seeking (e.g. Texture2D.FromStream) work.
                var ms = new MemoryStream();
                stream.CopyTo(ms);
                stream.Dispose();
                ms.Position = 0;
                return (ms, ms.Length);
            }
        }

        // 2. Fall back to raw file system.
        if (!File.Exists(normalisedPath))
            throw new FileNotFoundException($"AssetManager: asset not found: '{normalisedPath}'");

        var info = new FileInfo(normalisedPath);
        return (File.OpenRead(normalisedPath), info.Length);
    }

    // -------------------------------------------------------------------------
    // Internal helpers — typed loading
    // -------------------------------------------------------------------------
    private T LoadFromStream<T>(string normalisedPath, Stream stream) where T : class
    {
        var t = typeof(T);

        if (t == typeof(Texture2D))
        {
            // Texture2D.FromStream requires a seekable stream.
            var seekable = EnsureSeekable(stream);
            return (T)(object)Texture2D.FromStream(_gd, seekable);
        }

        if (t == typeof(SoundEffect))
        {
            var seekable = EnsureSeekable(stream);
            return (T)(object)SoundEffect.FromStream(seekable);
        }

        if (t == typeof(string))
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            return (T)(object)reader.ReadToEnd();
        }

        if (t == typeof(byte[]))
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return (T)(object)ms.ToArray();
        }

        throw new NotSupportedException(
            $"AssetManager: no raw loader registered for type '{t.FullName}'. " +
            $"Supported types: Texture2D, SoundEffect, string, byte[].");
    }

    private static Stream EnsureSeekable(Stream s)
    {
        if (s.CanSeek) return s;
        var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

    // -------------------------------------------------------------------------
    // Internal helpers — hot-reload (DEBUG / DEVELOPMENT only)
    // -------------------------------------------------------------------------
#if DEBUG || DEVELOPMENT
    private void RegisterWatcher(string normalisedPath)
    {
        // Only watch real files — bundle entries are not file-system objects.
        if (!File.Exists(normalisedPath)) return;
        if (_watchers.ContainsKey(normalisedPath)) return;

        var dir      = Path.GetDirectoryName(normalisedPath)!;
        var fileName = Path.GetFileName(normalisedPath);

        var watcher = new FileSystemWatcher(dir, fileName)
        {
            NotifyFilter           = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents    = true,
            IncludeSubdirectories  = false
        };

        watcher.Changed += OnFileChanged;
        _watchers[normalisedPath] = watcher;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        var key = NormalisePath(e.FullPath);
        if (!_cache.TryGetValue(key, out var entry)) return;

        // Small delay — editors often write in multiple flushes.
        Thread.Sleep(80);

        lock (_reloadLock)
        {
            try
            {
                var (stream, byteSize) = ResolveStream(key);
                object newAsset;
                try
                {
                    newAsset = ReloadEntry(entry, key, stream);
                }
                finally
                {
                    stream.Dispose();
                }

                // Dispose old disposable value only if it differs.
                if (!ReferenceEquals(entry.Value, newAsset))
                    DisposeEntry(entry);

                entry.Value      = newAsset;
                entry.ByteSize   = byteSize;
                entry.LastLoaded = DateTime.UtcNow;

                OnAssetReloaded.Broadcast(key);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AssetManager] Hot-reload failed for '{key}': {ex.Message}");
            }
        }
    }

    private object ReloadEntry(CacheEntry entry, string key, Stream stream)
    {
        var t = entry.Type;

        if (t == typeof(Texture2D))  return LoadFromStream<Texture2D>(key, stream);
        if (t == typeof(SoundEffect)) return LoadFromStream<SoundEffect>(key, stream);
        if (t == typeof(string))     return LoadFromStream<string>(key, stream);
        if (t == typeof(byte[]))     return LoadFromStream<byte[]>(key, stream);

        throw new NotSupportedException($"AssetManager: cannot hot-reload type '{t.FullName}'.");
    }
#endif

    // -------------------------------------------------------------------------
    // Internal helpers — disposal
    // -------------------------------------------------------------------------
    private static void DisposeEntry(CacheEntry entry)
    {
        if (entry.Value is IDisposable d)
            d.Dispose();
    }

    // -------------------------------------------------------------------------
    // Internal helpers — path normalisation
    // -------------------------------------------------------------------------
    /// <summary>
    /// Converts all separators to forward-slash and lowercases the result so that
    /// lookups are consistent across platforms and calling conventions.
    /// </summary>
    private static string NormalisePath(string path)
        => Path.GetFullPath(path).Replace('\\', '/');
}
