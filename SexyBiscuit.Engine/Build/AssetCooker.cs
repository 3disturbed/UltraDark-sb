using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SexyBiscuit.Engine.Build;

/// <summary>Result returned by <see cref="AssetCooker.Cook"/>.</summary>
public record CookResult(
    int           FilesProcessed,
    int           FilesSkipped,
    int           ErrorCount,
    List<string>  Errors);

/// <summary>
/// Processes source assets for a target platform.
/// Supports incremental cooking via SHA-256 file hash comparison.
/// </summary>
public class AssetCooker
{
    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------

    /// <summary>
    /// When true, compares source file hashes against a cache stored in
    /// <c>{outputDir}/_cookCache.json</c> and skips unchanged files.
    /// </summary>
    public bool IsIncrementalCook { get; set; } = true;

    // -------------------------------------------------------------------------
    // Internal state
    // -------------------------------------------------------------------------

    private readonly List<string> _log    = new();
    private readonly List<string> _errors = new();
    private int _processed = 0;
    private int _skipped   = 0;

    // Hash cache: normalised relative path -> SHA256 hex of source file
    private Dictionary<string, string> _hashCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheModified = false;

    // -------------------------------------------------------------------------
    // Main entry point
    // -------------------------------------------------------------------------

    /// <summary>
    /// Walks <paramref name="sourceDir"/> recursively, processes each asset,
    /// and writes outputs to <paramref name="outputDir"/>.
    /// </summary>
    public CookResult Cook(string sourceDir, string outputDir, PlatformConfig config)
    {
        _log.Clear();
        _errors.Clear();
        _processed = 0;
        _skipped   = 0;
        _hashCache.Clear();
        _cacheModified = false;

        if (!Directory.Exists(sourceDir))
        {
            _errors.Add($"Source directory not found: '{sourceDir}'");
            return new CookResult(0, 0, 1, _errors);
        }

        Directory.CreateDirectory(outputDir);

        // Load existing hash cache (incremental cook)
        string cachePath = Path.Combine(outputDir, "_cookCache.json");
        if (IsIncrementalCook && File.Exists(cachePath))
        {
            try
            {
                string cacheJson = File.ReadAllText(cachePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(cacheJson);
                if (loaded != null) _hashCache = loaded;
                Log($"Loaded hash cache with {_hashCache.Count} entries.");
            }
            catch (Exception ex)
            {
                Log($"Warning: could not load hash cache: {ex.Message}. Starting fresh.");
                _hashCache.Clear();
            }
        }

        // Walk source tree
        string[] files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        foreach (string srcFile in files)
        {
            // Skip the cache file itself
            if (Path.GetFileName(srcFile).Equals("_cookCache.json", StringComparison.OrdinalIgnoreCase))
                continue;

            string relPath = Path.GetRelativePath(sourceDir, srcFile);
            string dstFile = Path.Combine(outputDir, relPath);

            try
            {
                ProcessFile(srcFile, dstFile, relPath, config);
            }
            catch (Exception ex)
            {
                string msg = $"Error processing '{relPath}': {ex.Message}";
                _errors.Add(msg);
                Log($"ERROR: {msg}");
            }
        }

        // Save updated hash cache
        if (IsIncrementalCook && _cacheModified)
        {
            try
            {
                string cacheJson = JsonSerializer.Serialize(_hashCache,
                    new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(cachePath, cacheJson);
            }
            catch (Exception ex)
            {
                Log($"Warning: could not save hash cache: {ex.Message}");
            }
        }

        Log($"Cook complete. Processed={_processed} Skipped={_skipped} Errors={_errors.Count}");

        return new CookResult(_processed, _skipped, _errors.Count, new List<string>(_errors));
    }

    // -------------------------------------------------------------------------
    // Per-file dispatch
    // -------------------------------------------------------------------------

    private void ProcessFile(string srcFile, string dstFile, string relPath, PlatformConfig config)
    {
        string ext = Path.GetExtension(srcFile).ToLowerInvariant();

        // Incremental check
        if (IsIncrementalCook && IsUnchanged(srcFile, relPath))
        {
            Log($"SKIP (unchanged): {relPath}");
            _skipped++;
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);

        switch (ext)
        {
            // Textures
            case ".png":
            case ".jpg":
            case ".bmp":
                ProcessTexture(srcFile, dstFile, relPath, config);
                break;

            // Audio
            case ".ogg":
            case ".mp3":
            case ".wav":
                ProcessAudio(srcFile, dstFile, relPath);
                break;

            // Scripts
            case ".js":
                ProcessScript(srcFile, dstFile, relPath, config);
                break;

            // Pass-through formats
            case ".json":
            case ".scene":
            case ".prefab":
            case ".ttf":
            case ".otf":
                CopyFile(srcFile, dstFile, relPath);
                break;

            default:
                // Unknown extension — copy as-is with a note
                Log($"COPY (unknown ext '{ext}'): {relPath}");
                File.Copy(srcFile, dstFile, overwrite: true);
                _processed++;
                break;
        }

        // Update hash cache after successful processing
        if (IsIncrementalCook)
            UpdateHash(srcFile, relPath);
    }

    // -------------------------------------------------------------------------
    // Texture processing
    // -------------------------------------------------------------------------

    private void ProcessTexture(string src, string dst, string rel, PlatformConfig config)
    {
        bool isWindowsTarget = config.Platform is BuildPlatform.Windows_x64
                                              or BuildPlatform.Windows_x86
                                              or BuildPlatform.Steam_Windows;

        if (isWindowsTarget)
        {
            // Attempt texconv.exe for DXT compression
            string? texconvPath = FindOnPath("texconv.exe");
            if (texconvPath != null)
            {
                bool ok = RunTexconv(texconvPath, src, Path.GetDirectoryName(dst)!, rel);
                if (ok) return;
            }
            else
            {
                Log($"NOTE: texconv.exe not found in PATH — DXT compression skipped for '{rel}'. " +
                    "Install Microsoft Texconv to enable GPU-compressed textures.");
            }
        }

        // Fallback: copy as-is
        CopyFile(src, dst, rel);
    }

    private bool RunTexconv(string texconvPath, string src, string dstDir, string rel)
    {
        try
        {
            // texconv -f DXT5 -o <dstDir> -y <src>
            var psi = new ProcessStartInfo(texconvPath,
                $"-f DXT5 -o \"{dstDir}\" -y \"{src}\"")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode == 0)
            {
                Log($"DXT5 (texconv): {rel}");
                _processed++;
                return true;
            }

            Log($"Warning: texconv failed for '{rel}' (exit {proc.ExitCode}): {stderr.Trim()}");
            return false;
        }
        catch (Exception ex)
        {
            Log($"Warning: texconv error for '{rel}': {ex.Message}");
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Audio processing
    // -------------------------------------------------------------------------

    private void ProcessAudio(string src, string dst, string rel)
    {
        string ext = Path.GetExtension(src).ToLowerInvariant();

        if (ext == ".ogg")
        {
            // OGG is already compressed — copy as-is
            Log($"COPY (ogg, already compressed): {rel}");
            File.Copy(src, dst, overwrite: true);
            _processed++;
            return;
        }

        if (ext == ".wav")
        {
            // Attempt to encode to OGG using oggenc if present
            string? oggencPath = FindOnPath("oggenc") ?? FindOnPath("oggenc.exe");
            if (oggencPath != null)
            {
                string dstOgg = Path.ChangeExtension(dst, ".ogg");
                try
                {
                    var psi = new ProcessStartInfo(oggencPath,
                        $"-q 6 -o \"{dstOgg}\" \"{src}\"")
                    {
                        UseShellExecute        = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        CreateNoWindow         = true,
                    };
                    using var proc = Process.Start(psi)!;
                    proc.WaitForExit();
                    if (proc.ExitCode == 0)
                    {
                        Log($"OGG encode (oggenc): {rel} -> {Path.GetFileName(dstOgg)}");
                        _processed++;
                        return;
                    }
                    Log($"Warning: oggenc failed for '{rel}' (exit {proc.ExitCode}). Copying .wav as-is.");
                }
                catch (Exception ex)
                {
                    Log($"Warning: oggenc error for '{rel}': {ex.Message}. Copying .wav as-is.");
                }
            }
            else
            {
                Log($"NOTE: oggenc not found in PATH — .wav copied as-is for '{rel}'. " +
                    "Install Ogg Vorbis Tools to enable WAV-to-OGG encoding.");
            }
        }

        // Fallback: copy as-is
        CopyFile(src, dst, rel);
    }

    // -------------------------------------------------------------------------
    // Script processing
    // -------------------------------------------------------------------------

    private void ProcessScript(string src, string dst, string rel, PlatformConfig config)
    {
        string source = File.ReadAllText(src, Encoding.UTF8);

        if (config.MinifyScripts && config.Configuration == BuildConfiguration.Release)
        {
            source = MinifyJavaScript(source);
            Log($"MINIFY (js): {rel}");
        }
        else
        {
            Log($"COPY (js): {rel}");
        }

        File.WriteAllText(dst, source, Encoding.UTF8);
        _processed++;
    }

    /// <summary>
    /// Simple regex-based JS minifier:
    /// - Removes // line comments
    /// - Removes /* */ block comments
    /// - Collapses runs of whitespace (spaces, tabs, newlines) to a single space
    /// - Trims the result
    /// This is intentionally minimal — for production use, invest in esbuild/terser.
    /// </summary>
    private static string MinifyJavaScript(string source)
    {
        // Remove /* ... */ block comments (non-greedy, dot matches newline)
        source = Regex.Replace(source, @"/\*.*?\*/", " ",
                               RegexOptions.Singleline);

        // Remove // line comments
        source = Regex.Replace(source, @"//[^\r\n]*", " ");

        // Collapse whitespace runs to a single space
        source = Regex.Replace(source, @"[\s]+", " ");

        return source.Trim();
    }

    // -------------------------------------------------------------------------
    // Generic copy
    // -------------------------------------------------------------------------

    private void CopyFile(string src, string dst, string rel)
    {
        File.Copy(src, dst, overwrite: true);
        Log($"COPY: {rel}");
        _processed++;
    }

    // -------------------------------------------------------------------------
    // Incremental cook — hash helpers
    // -------------------------------------------------------------------------

    private bool IsUnchanged(string srcFile, string relPath)
    {
        if (!_hashCache.TryGetValue(relPath, out string? cachedHash))
            return false;

        string currentHash = ComputeHash(srcFile);
        return string.Equals(cachedHash, currentHash, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateHash(string srcFile, string relPath)
    {
        string hash = ComputeHash(srcFile);
        _hashCache[relPath] = hash;
        _cacheModified      = true;
    }

    private static string ComputeHash(string filePath)
    {
        using var sha = SHA256.Create();
        using var fs  = File.OpenRead(filePath);
        byte[] hashBytes = sha.ComputeHash(fs);
        return Convert.ToHexString(hashBytes);
    }

    // -------------------------------------------------------------------------
    // PATH search helper
    // -------------------------------------------------------------------------

    private static string? FindOnPath(string executableName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string fullPath = Path.Combine(dir, executableName);
            if (File.Exists(fullPath))
                return fullPath;
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // Logging
    // -------------------------------------------------------------------------

    private void Log(string message)
    {
        _log.Add(message);
        Console.WriteLine($"[AssetCooker] {message}");
    }

    /// <summary>Returns all log messages emitted during the last <see cref="Cook"/> run.</summary>
    public IReadOnlyList<string> GetLog() => _log;
}
