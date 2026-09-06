using System.Diagnostics;
using System.Text;

namespace SexyBiscuit.Engine.Code;

/// <summary>The outcome of a finished process: exit code, every line it printed, and whether it was stopped early.</summary>
public sealed record ProcessRun(int ExitCode, IReadOnlyList<string> Lines, bool TimedOut, bool Cancelled)
{
    /// <summary>True when the process exited on its own with code 0.</summary>
    public bool Success => ExitCode == 0 && !TimedOut && !Cancelled;
}

/// <summary>
/// Runs a command line tool, captures its output line by line, and kills it on timeout or
/// cancellation. <see cref="DotnetBuildRunner"/> uses it for <c>dotnet</c>; the test runner
/// uses it for <c>npm</c> and <c>node</c>.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Starts <paramref name="fileName"/> with <paramref name="args"/> and waits for it, reporting
    /// each output line to <paramref name="progress"/> as it arrives. On timeout or cancellation the
    /// whole process tree is killed and the run says which of the two happened.
    /// </summary>
    public static async Task<ProcessRun> RunAsync(string fileName, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout,
                                                  IProgress<string>? progress = null, IReadOnlyDictionary<string, string>? environment = null,
                                                  CancellationToken cancellation = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = fileName,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            WorkingDirectory       = workingDirectory ?? Environment.CurrentDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (environment != null)
            foreach (var (name, value) in environment) psi.Environment[name] = value;

        var lines = new List<string>();
        var gate  = new object();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (gate) lines.Add(e.Data); progress?.Report(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data == null) return; lock (gate) lines.Add(e.Data); progress?.Report(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked     = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeoutCts.Token);

        bool timedOut = false, cancelled = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut  = timeoutCts.IsCancellationRequested && !cancellation.IsCancellationRequested;
            cancelled = cancellation.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }

        // Drain the async readers before reading the lines.
        try { process.WaitForExit(); } catch { }

        lock (gate) return new ProcessRun(process.HasExited ? process.ExitCode : -1, lines.ToArray(), timedOut, cancelled);
    }

    /// <summary>
    /// Finds an executable by name on PATH, then in <paramref name="extraDirectories"/>. A GUI
    /// application on macOS inherits a PATH without Homebrew or nvm, so callers pass the usual
    /// places as well. An absolute path is returned as is when the file exists.
    /// </summary>
    public static string? FindOnPath(string name, params string[] extraDirectories)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(extraDirectories);
        var candidates = OperatingSystem.IsWindows() && !Path.HasExtension(name)
            ? new[] { name + ".cmd", name + ".exe", name }
            : new[] { name };

        foreach (var directory in directories)
        {
            foreach (var candidate in candidates)
            {
                string path = Path.Combine(directory, candidate);
                if (File.Exists(path)) return path;
            }
        }

        return null;
    }
}
