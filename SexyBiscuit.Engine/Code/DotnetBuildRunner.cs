using System.Diagnostics;
using System.Text;

namespace SexyBiscuit.Engine.Code;

/// <summary>What to build and how.</summary>
public sealed record BuildRequest(string ProjectPath, string Configuration = "Debug")
{
    /// <summary>Extra <c>-p:Name=Value</c> properties.</summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }

    /// <summary>An explicit <c>-o</c> output directory; the staging build for an editor restart uses it.</summary>
    public string? OutputDirectory { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    public bool NoRestore { get; init; }

    /// <summary>
    /// False makes a game compile against the engine output it already has instead of
    /// rebuilding the engine — which is what keeps the game and the running editor on the same
    /// engine build.
    /// </summary>
    public bool BuildProjectReferences { get; init; } = true;

    public string? WorkingDirectory { get; init; }
}

/// <summary>The outcome of a build: diagnostics first, the raw log for everything else.</summary>
public sealed record BuildResult(
    bool                           Success,
    int                            ExitCode,
    IReadOnlyList<BuildDiagnostic> Diagnostics,
    TimeSpan                       Duration,
    string?                        OutputAssemblyPath,
    string                         RawLog,
    bool                           Cancelled,
    bool                           TimedOut)
{
    public IEnumerable<BuildDiagnostic> Errors   => Diagnostics.Where(d => d.Severity == BuildSeverity.Error);
    public IEnumerable<BuildDiagnostic> Warnings => Diagnostics.Where(d => d.Severity == BuildSeverity.Warning);
    public int ErrorCount   => Errors.Count();
    public int WarningCount => Warnings.Count();
}

/// <summary>
/// Runs <c>dotnet build</c> asynchronously with event-based output capture — never
/// <c>ReadToEnd</c> before <c>WaitForExit</c>, which deadlocks on a chatty build — and turns the
/// log into structured diagnostics.
/// </summary>
public sealed class DotnetBuildRunner
{
    private readonly DotnetInfo _dotnet;

    public DotnetBuildRunner(DotnetInfo dotnet) => _dotnet = dotnet;

    public DotnetInfo Dotnet => _dotnet;

    /// <summary>The argument list for a request, exposed so it can be tested and logged.</summary>
    public static IReadOnlyList<string> BuildArguments(BuildRequest request)
    {
        var args = new List<string>
        {
            "build", request.ProjectPath,
            "-c", request.Configuration,
            "-nologo", "-v:q", "-tl:off",
            "-clp:NoSummary;ForceNoAlign",
            "-p:GenerateFullPaths=true",
        };

        if (!request.BuildProjectReferences) args.Add("-p:BuildProjectReferences=false");
        if (request.NoRestore) args.Add("--no-restore");
        if (!string.IsNullOrEmpty(request.OutputDirectory)) { args.Add("-o"); args.Add(request.OutputDirectory); }

        if (request.Properties != null)
            foreach (var (name, value) in request.Properties)
                args.Add($"-p:{name}={value}");

        return args;
    }

    /// <summary>Environment that keeps the output parseable and quiet.</summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironment { get; } = new Dictionary<string, string>
    {
        ["DOTNET_CLI_UI_LANGUAGE"]      = "en",
        ["DOTNET_NOLOGO"]               = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["MSBUILDTERMINALLOGGER"]       = "off",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
    };

    public async Task<BuildResult> BuildAsync(BuildRequest request, IProgress<string>? lines = null, CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var run = await RunAsync(BuildArguments(request), request.WorkingDirectory ?? Path.GetDirectoryName(request.ProjectPath),
                                 request.Timeout, lines, cancellation).ConfigureAwait(false);
        stopwatch.Stop();

        var diagnostics = MsBuildDiagnosticParser.Parse(run.Lines);
        bool success = run.ExitCode == 0 && !run.TimedOut && !run.Cancelled;

        string? output = null;
        if (success)
        {
            try
            {
                output = await GetPropertyAsync(request.ProjectPath, "TargetPath", request.Configuration, cancellation).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(request.OutputDirectory) && output != null)
                    output = Path.Combine(request.OutputDirectory, Path.GetFileName(output));
            }
            catch (Exception)
            {
                output = null;
            }
        }

        // A failed build with no parseable diagnostic still needs to say something.
        if (!success && diagnostics.Count == 0)
        {
            string reason = run.TimedOut ? "the build timed out"
                          : run.Cancelled ? "the build was cancelled"
                          : $"dotnet exited with code {run.ExitCode}";
            string tail = string.Join(" | ", run.Lines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(3));
            diagnostics = new[] { new BuildDiagnostic(null, 0, 0, null, BuildSeverity.Error, $"{reason}. {tail}".Trim(), request.ProjectPath) };
        }

        return new BuildResult(success, run.ExitCode, diagnostics, stopwatch.Elapsed, output,
                               string.Join('\n', run.Lines), run.Cancelled, run.TimedOut);
    }

    /// <summary>Evaluates one MSBuild property without building. Null when evaluation fails.</summary>
    public async Task<string?> GetPropertyAsync(string projectPath, string property, string configuration, CancellationToken cancellation = default)
    {
        var args = new[] { "msbuild", projectPath, $"-getProperty:{property}", $"-p:Configuration={configuration}", "-nologo" };
        var run = await RunAsync(args, Path.GetDirectoryName(projectPath), TimeSpan.FromMinutes(2), null, cancellation).ConfigureAwait(false);
        if (run.ExitCode != 0) return null;

        string? value = run.Lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>What a dotnet invocation produced.</summary>
    public sealed record DotnetRun(int ExitCode, IReadOnlyList<string> Lines, bool TimedOut, bool Cancelled);

    /// <summary>
    /// Runs any <c>dotnet</c> command — publish, test — with the same capture and timeout
    /// handling as a build. The publisher and the test runner share this rather than each
    /// spawning their own process.
    /// </summary>
    public async Task<DotnetRun> RunDotnetAsync(IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout,
                                                IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var run = await RunAsync(args, workingDirectory, timeout, progress, cancellation).ConfigureAwait(false);
        return new DotnetRun(run.ExitCode, run.Lines, run.TimedOut, run.Cancelled);
    }

    private sealed record RunOutcome(int ExitCode, IReadOnlyList<string> Lines, bool TimedOut, bool Cancelled);

    private async Task<RunOutcome> RunAsync(IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout,
                                            IProgress<string>? progress, CancellationToken cancellation)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = _dotnet.Path,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            WorkingDirectory       = workingDirectory ?? Environment.CurrentDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        foreach (var (name, value) in BuildEnvironment) psi.Environment[name] = value;

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

        lock (gate) return new RunOutcome(process.HasExited ? process.ExitCode : -1, lines.ToArray(), timedOut, cancelled);
    }
}
