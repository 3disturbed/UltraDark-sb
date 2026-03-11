using System.Collections.Concurrent;
using System.IO.Compression;

namespace SexyBiscuit.Engine.Assets;

/// <summary>
/// A mounted `.sba` archive (ZIP format). When <see cref="AssetManager"/> cannot
/// find a raw file on disk it queries every mounted bundle in mount order.
/// </summary>
public sealed class AssetBundle : IDisposable
{
    // -------------------------------------------------------------------------
    // Global mount registry
    // -------------------------------------------------------------------------
    private static readonly ConcurrentBag<AssetBundle> _mounted = new();

    /// <summary>All currently mounted bundles, in no guaranteed order.</summary>
    public static IEnumerable<AssetBundle> MountedBundles => _mounted;

    // -------------------------------------------------------------------------
    // Instance state
    // -------------------------------------------------------------------------
    private readonly ZipArchive   _archive;
    private readonly string       _archivePath;
    private          bool         _disposed;

    /// <summary>The file-system path of the underlying archive.</summary>
    public string ArchivePath => _archivePath;

    // -------------------------------------------------------------------------
    // Construction (private — use Mount)
    // -------------------------------------------------------------------------
    private AssetBundle(string archivePath, ZipArchive archive)
    {
        _archivePath = archivePath;
        _archive     = archive;
    }

    // -------------------------------------------------------------------------
    // Static factory
    // -------------------------------------------------------------------------
    /// <summary>
    /// Opens the `.sba` archive at <paramref name="archivePath"/>, registers it
    /// as a virtual path resolver, and returns the bundle handle.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the archive file does not exist.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Thrown when the file is not a valid ZIP archive.
    /// </exception>
    public static AssetBundle Mount(string archivePath)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"AssetBundle: archive not found: '{archivePath}'");

        var zip    = ZipFile.OpenRead(archivePath);
        var bundle = new AssetBundle(archivePath, zip);
        _mounted.Add(bundle);

        System.Diagnostics.Debug.WriteLine(
            $"[AssetBundle] Mounted '{archivePath}' ({zip.Entries.Count} entries).");

        return bundle;
    }

    // -------------------------------------------------------------------------
    // Unmount / dispose
    // -------------------------------------------------------------------------
    /// <summary>
    /// Removes this bundle from the mount registry and closes the underlying archive.
    /// Any streams previously opened from this bundle must be closed by callers first.
    /// </summary>
    public void Unmount()
    {
        // ConcurrentBag has no Remove; we mark as disposed and the enumerator
        // in AssetManager skips disposed bundles via OpenEntry returning null.
        Dispose();
        System.Diagnostics.Debug.WriteLine($"[AssetBundle] Unmounted '{_archivePath}'.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _archive.Dispose();
    }

    // -------------------------------------------------------------------------
    // Virtual path resolution
    // -------------------------------------------------------------------------
    /// <summary>
    /// Attempts to open a stream for <paramref name="normalisedPath"/> within
    /// this archive.  The lookup strips any leading drive / root so that both
    /// absolute and relative keys match the relative paths stored inside the zip.
    /// Returns <c>null</c> if this bundle does not contain the entry or is disposed.
    /// </summary>
    public Stream? OpenEntry(string normalisedPath)
    {
        if (_disposed) return null;

        // Zip entries use relative paths with forward slashes.
        var relative = MakeRelative(normalisedPath);

        // Try exact match first.
        var entry = _archive.GetEntry(relative);

        // Fall back to case-insensitive scan (zip is case-sensitive on some platforms).
        if (entry == null)
        {
            entry = _archive.Entries.FirstOrDefault(
                e => string.Equals(e.FullName, relative, StringComparison.OrdinalIgnoreCase));
        }

        return entry?.Open();
    }

    /// <summary>
    /// Returns <c>true</c> if this bundle contains an entry for the given normalised path.
    /// </summary>
    public bool Contains(string normalisedPath)
    {
        if (_disposed) return false;
        var relative = MakeRelative(normalisedPath);
        return _archive.Entries.Any(
            e => string.Equals(e.FullName, relative, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Enumerates all entry paths (as relative forward-slash strings) in the archive.
    /// </summary>
    public IEnumerable<string> EnumerateEntries()
    {
        if (_disposed) return Enumerable.Empty<string>();
        return _archive.Entries.Select(e => e.FullName);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    /// <summary>
    /// Converts an absolute normalised path (e.g. <c>C:/Game/Assets/sprites/hero.png</c>)
    /// to a relative path suitable for zip entry lookup (e.g. <c>sprites/hero.png</c>).
    /// If the path is already relative it is returned as-is.
    /// </summary>
    private static string MakeRelative(string normalisedPath)
    {
        // Strip Windows-style drive letter root  (C:/) or Unix root (/).
        if (normalisedPath.Length >= 2 && normalisedPath[1] == ':')
        {
            // Skip "X:/"
            normalisedPath = normalisedPath[3..];
        }
        else if (normalisedPath.StartsWith('/'))
        {
            normalisedPath = normalisedPath[1..];
        }

        return normalisedPath;
    }
}
