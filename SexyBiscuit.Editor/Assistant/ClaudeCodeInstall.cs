using System.Diagnostics;
using SexyBiscuit.Engine.Mcp.ClaudeCode;

namespace SexyBiscuit.Editor.Assistant;

/// <summary>A <c>claude</c> binary that answered <c>--version</c>.</summary>
public sealed record ClaudeInstallInfo(string Path, Version? Version, string VersionText, string Source)
{
    public string Display => Version != null ? $"Claude Code {Version}" : "Claude Code";
}

/// <summary>What happened when a candidate was probed.</summary>
public sealed record ClaudeCandidateVerdict(ClaudeCandidate Candidate, bool Works, string Detail);

/// <summary>
/// Finds a working Claude Code binary. Every candidate the locator returns is run with
/// <c>--version</c>, because a shim can sit on PATH and still fail (the homebrew npm shim prints
/// "claude native binary not installed"), and the first one that answers wins.
/// </summary>
public sealed class ClaudeCodeInstall
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private Task? _detecting;

    public ClaudeInstallInfo?                    Info          { get; private set; }
    public IReadOnlyList<ClaudeCandidateVerdict> Verdicts      { get; private set; } = Array.Empty<ClaudeCandidateVerdict>();
    public DateTime?                             LastDetectUtc { get; private set; }

    /// <summary>True while a detection pass is running.</summary>
    public bool IsDetecting
    {
        get { lock (_lock) return _detecting is { IsCompleted: false }; }
    }

    /// <summary>True once at least one pass finished.</summary>
    public bool HasDetected => LastDetectUtc != null;

    /// <summary>Starts (or joins) a detection pass. Safe to call from any thread.</summary>
    public Task DetectAsync(string? overridePath)
    {
        lock (_lock)
        {
            if (_detecting is { IsCompleted: false }) return _detecting;
            _detecting = Task.Run(() => Detect(overridePath));
            return _detecting;
        }
    }

    private void Detect(string? overridePath)
    {
        var verdicts = new List<ClaudeCandidateVerdict>();
        ClaudeInstallInfo? found = null;

        foreach (var candidate in ClaudeCodeLocator.Candidates(LocatorEnvironment.Current(overridePath)))
        {
            var (works, detail, version) = Probe(candidate.Path, ProbeTimeout);
            verdicts.Add(new ClaudeCandidateVerdict(candidate, works, detail));
            if (works && found == null)
                found = new ClaudeInstallInfo(candidate.Path, version, detail, candidate.Source);
        }

        Info          = found;
        Verdicts      = verdicts;
        LastDetectUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Opens the platform's terminal running the binary interactively, so the user can type
    /// <c>/login</c>. Returns false when no terminal could be opened; the caller then shows the
    /// command to run by hand.
    /// </summary>
    public static bool OpenSignInTerminal(string claudePath)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                string dir    = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SexyBiscuit");
                Directory.CreateDirectory(dir);
                string script = Path.Combine(dir, "claude-sign-in.command");
                File.WriteAllText(script,
                    "#!/bin/sh\n" +
                    "echo 'Claude Code sign-in for the SexyBiscuit editor.'\n" +
                    "echo 'When the prompt appears, type /login and follow the instructions.'\n" +
                    "echo 'Afterwards close this window and press Resume in the editor.'\n" +
                    "echo\n" +
                    "exec '" + claudePath.Replace("'", "'\\''") + "'\n");
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                Process.Start(new ProcessStartInfo("open", new[] { script }) { UseShellExecute = false });
                return true;
            }

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{claudePath}\"\"") { UseShellExecute = true });
                return true;
            }

            foreach (var (terminal, args) in new[] { ("x-terminal-emulator", new[] { "-e", claudePath }), ("gnome-terminal", new[] { "--", claudePath }), ("konsole", new[] { "-e", claudePath }), ("xterm", new[] { "-e", claudePath }) })
            {
                try
                {
                    var psi = new ProcessStartInfo(terminal) { UseShellExecute = false };
                    foreach (var a in args) psi.ArgumentList.Add(a);
                    Process.Start(psi);
                    return true;
                }
                catch (Exception)
                {
                }
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Runs <c>path --version</c> with a scrubbed environment and reports what came back.</summary>
    public static (bool Works, string Detail, Version? Version) Probe(string path, TimeSpan timeout)
    {
        try
        {
            var psi = ClaudeProcess.StartInfo(path, new[] { "--version" }, Environment.CurrentDirectory);
            using var process = Process.Start(psi);
            if (process == null) return (false, "could not start", null);

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, $"no answer to --version within {timeout.TotalSeconds:F0} s", null);
            }

            string output = (stdout.Result + "\n" + stderr.Result).Trim();
            var version = ClaudeCodeLocator.ParseVersion(output);

            if (process.ExitCode != 0 || version == null)
            {
                string firstLine = output.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "(no output)";
                return (false, $"exit {process.ExitCode}: {firstLine}", null);
            }

            return (true, output.Split('\n')[0].Trim(), version);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }
}

/// <summary>Builds <see cref="ProcessStartInfo"/> for the <c>claude</c> binary the way every caller needs it.</summary>
public static class ClaudeProcess
{
    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> args, string workingDirectory)
    {
        bool isCmdShim = OperatingSystem.IsWindows()
                      && (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        var psi = new ProcessStartInfo(isCmdShim ? "cmd.exe" : executable)
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = workingDirectory,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding  = new System.Text.UTF8Encoding(false),
            StandardInputEncoding  = new System.Text.UTF8Encoding(false),
        };

        if (isCmdShim)
        {
            // An npm .cmd shim cannot be exec'd directly; go through the command interpreter.
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(executable);
        }

        foreach (var a in args) psi.ArgumentList.Add(a);

        // Scrubbed of nesting markers, PATH widened: see ClaudeEnvironment.
        var current = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            current[(string)entry.Key] = entry.Value as string;

        var scrubbed = ClaudeEnvironment.Scrub(current);

        string? dotnetRoot = null;
        try
        {
            var dotnet = Engine.Code.DotnetLocator.Find();
            if (dotnet != null) dotnetRoot = Path.GetDirectoryName(dotnet.Path);
        }
        catch (Exception)
        {
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string pathKey = scrubbed.Keys.FirstOrDefault(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
        scrubbed[pathKey] = ClaudeEnvironment.ExtendPath(scrubbed.GetValueOrDefault(pathKey), ClaudeEnvironment.DefaultPathEntries(dotnetRoot, home), Path.PathSeparator);

        psi.Environment.Clear();
        foreach (var (key, value) in scrubbed)
            if (value != null) psi.Environment[key] = value;

        return psi;
    }
}
