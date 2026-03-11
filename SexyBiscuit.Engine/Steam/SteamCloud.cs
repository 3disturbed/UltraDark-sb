#if STEAMWORKS
using Steamworks;
#endif

namespace SexyBiscuit.Engine.Steam;

/// <summary>
/// Static helpers for Steam Remote Storage (Steam Cloud).
/// Provides simple file I/O, existence checks, deletion, quota, and listing.
/// All calls are no-ops when Steam is not initialised.
/// </summary>
public static class SteamCloud
{
    // -------------------------------------------------------------------------
    // Write
    // -------------------------------------------------------------------------

    /// <summary>
    /// Writes raw bytes to a Steam Cloud file.
    /// Returns true on success.
    /// </summary>
    public static bool Write(string filename, byte[] data)
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return false;
        if (data == null || data.Length == 0) return false;
        return SteamRemoteStorage.FileWrite(filename, data, data.Length);
#else
        return false;
#endif
    }

    // -------------------------------------------------------------------------
    // Read
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads all bytes from a Steam Cloud file.
    /// Returns null if the file does not exist or Steam is unavailable.
    /// </summary>
    public static byte[]? Read(string filename)
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return null;
        if (!SteamRemoteStorage.FileExists(filename)) return null;

        int size = SteamRemoteStorage.GetFileSize(filename);
        if (size <= 0) return Array.Empty<byte>();

        var buffer = new byte[size];
        int bytesRead = SteamRemoteStorage.FileRead(filename, buffer, size);
        if (bytesRead <= 0) return null;

        // Trim to actual bytes read if needed
        if (bytesRead < size)
        {
            var trimmed = new byte[bytesRead];
            Array.Copy(buffer, trimmed, bytesRead);
            return trimmed;
        }

        return buffer;
#else
        return null;
#endif
    }

    // -------------------------------------------------------------------------
    // Exists
    // -------------------------------------------------------------------------

    /// <summary>Returns true if the named file exists in Steam Cloud.</summary>
    public static bool Exists(string filename)
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return false;
        return SteamRemoteStorage.FileExists(filename);
#else
        return false;
#endif
    }

    // -------------------------------------------------------------------------
    // Delete
    // -------------------------------------------------------------------------

    /// <summary>Deletes a file from Steam Cloud. Returns true if the deletion succeeded.</summary>
    public static bool Delete(string filename)
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return false;
        return SteamRemoteStorage.FileDelete(filename);
#else
        return false;
#endif
    }

    // -------------------------------------------------------------------------
    // Quota
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns (bytesUsed, bytesTotal) for this app's Steam Cloud quota.
    /// Returns (0, 0) when Steam is unavailable.
    /// </summary>
    public static (ulong used, ulong total) GetQuota()
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return (0, 0);
        SteamRemoteStorage.GetQuota(out ulong total, out ulong available);
        ulong used = total > available ? total - available : 0;
        return (used, total);
#else
        return (0, 0);
#endif
    }

    // -------------------------------------------------------------------------
    // List files
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns an array of all filenames currently stored in Steam Cloud for this app.
    /// Returns an empty array if Steam is unavailable.
    /// </summary>
    public static string[] ListFiles()
    {
#if STEAMWORKS
        if (SteamManager.Instance?.IsInitialised != true) return Array.Empty<string>();

        int count = SteamRemoteStorage.GetFileCount();
        if (count <= 0) return Array.Empty<string>();

        var files = new string[count];
        for (int i = 0; i < count; i++)
            files[i] = SteamRemoteStorage.GetFileNameAndSize(i, out int _size);

        return files;
#else
        return Array.Empty<string>();
#endif
    }
}
