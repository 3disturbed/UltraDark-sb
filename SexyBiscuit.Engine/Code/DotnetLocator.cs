using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SexyBiscuit.Engine.Code;

/// <summary>A usable <c>dotnet</c> executable and the SDK version it reports.</summary>
public sealed record DotnetInfo(string Path, string? SdkVersion);

/// <summary>
/// Finds the .NET SDK. A GUI editor launched from Finder or a shortcut gets a minimal PATH,
/// so the muxer that is running this very process is tried first: the runtime directory sits
/// three levels below the install root.
/// </summary>
public static class DotnetLocator
{
    private static readonly object _lock = new();
    private static DotnetInfo? _cached;
    private static bool        _searched;

    public static DotnetInfo? Find(bool refresh = false)
    {
        lock (_lock)
        {
            if (_searched && !refresh) return _cached;

            _cached   = null;
            _searched = true;

            foreach (var candidate in Candidates().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(candidate)) continue;

                string? version = Probe(candidate);
                if (version == null) continue;

                _cached = new DotnetInfo(candidate, version);
                break;
            }

            return _cached;
        }
    }

    /// <summary>Every place a dotnet executable might be, most trustworthy first.</summary>
    public static IEnumerable<string> Candidates()
    {
        string exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        // 1. The install this process runs on: <root>/shared/Microsoft.NETCore.App/<ver>/
        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        string? root = SafeParent(SafeParent(SafeParent(runtimeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
        if (root != null) yield return Path.Combine(root, exe);

        // 2. An explicit override.
        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot)) yield return Path.Combine(dotnetRoot, exe);

        // 3. PATH.
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                yield return Path.Combine(dir.Trim(), exe);
        }

        // 4. Well-known install locations.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", exe);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", exe);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet", exe);
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/usr/local/share/dotnet/dotnet";
            yield return "/opt/homebrew/bin/dotnet";
            yield return "/usr/local/bin/dotnet";
            yield return Path.Combine(home, ".dotnet", "dotnet");
        }
        else
        {
            yield return "/usr/share/dotnet/dotnet";
            yield return "/usr/lib/dotnet/dotnet";
            yield return "/usr/bin/dotnet";
            yield return Path.Combine(home, ".dotnet", "dotnet");
        }
    }

    /// <summary>
    /// Runs <c>dotnet --version</c>. Null when the executable is not an SDK: a bare runtime
    /// prints an error instead of a version, and cannot build anything.
    /// </summary>
    public static string? Probe(string dotnetPath, int timeoutMs = 15_000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = dotnetPath,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add("--version");
            psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
            psi.Environment["DOTNET_NOLOGO"]          = "1";

            using var process = Process.Start(psi);
            if (process == null) return null;

            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            if (process.ExitCode != 0) return null;

            string version = output.Trim();
            return version.Length > 0 && char.IsDigit(version[0]) ? version : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SafeParent(string? path)
        => string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
}
