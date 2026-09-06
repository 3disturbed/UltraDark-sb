using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SexyBiscuit.Engine.CookieJar;

/// <summary>What one git invocation produced.</summary>
public sealed record GitCommandResult(
    int                   ExitCode,
    IReadOnlyList<string> Output,
    IReadOnlyList<string> Errors,
    TimeSpan              Duration,
    bool                  TimedOut,
    bool                  Cancelled)
{
    public bool Success => ExitCode == 0 && !TimedOut && !Cancelled;

    /// <summary>Everything git said, for a log line or an error message.</summary>
    public string Text => string.Join("\n", Output.Concat(Errors)).Trim();

    /// <summary>The first line of output, which is all most commands produce.</summary>
    public string? FirstLine => Output.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
}

/// <summary>
/// Runs git for the CookieJar: cloning and refreshing jars, which the editor's existing helper
/// cannot do. That one is synchronous with a five-second timeout and builds its arguments by
/// string interpolation, which is fine for the local commands the Git panel runs and hopeless for
/// a network clone. This one is async, cancellable, and passes arguments as a list.
/// </summary>
public sealed class GitRunner
{
    private readonly string _git;

    public GitRunner(string? gitPath = null) => _git = gitPath ?? "git";

    /// <summary>How long a network command may take before it is killed.</summary>
    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a local query may take.</summary>
    public TimeSpan LocalTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The environment every git call runs in. The prompt variables are the important ones: a
    /// private repository must fail in a second, not block the editor on a credential prompt no
    /// one can see.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment { get; } = new Dictionary<string, string>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_ASKPASS"]         = "",
        ["SSH_ASKPASS"]         = "",
        ["GCM_INTERACTIVE"]     = "never",
        ["LC_ALL"]              = "C",
    };

    /// <summary>True when git is on the path and answers.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellation = default)
    {
        try
        {
            var result = await RunAsync(null, new[] { "--version" }, LocalTimeout, null, cancellation).ConfigureAwait(false);
            return result.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The arguments a shallow clone uses. Separated so it can be asserted without a network.</summary>
    public static IReadOnlyList<string> CloneArguments(string url, string destination, string? branch, int depth)
    {
        var args = new List<string> { "clone", "--no-tags", "--recurse-submodules=no" };

        if (depth > 0)
        {
            args.Add("--depth");
            args.Add(depth.ToString());
            args.Add("--single-branch");
        }

        if (!string.IsNullOrWhiteSpace(branch))
        {
            args.Add("--branch");
            args.Add(branch);
        }

        args.Add("--");
        args.Add(url);
        args.Add(destination);
        return args;
    }

    /// <summary>
    /// Whether a URL is one this runner will touch. Only https, ssh and plain local paths: git's
    /// transport helpers can run arbitrary commands, and a jar URL is untrusted input.
    /// </summary>
    public static bool IsAllowedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.Contains("::", StringComparison.Ordinal)) return false;   // ext::, transport helpers

        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            return true;

        // scp-like: git@host:owner/repo.git
        if (Regex.IsMatch(url, @"^[A-Za-z0-9_.\-]+@[A-Za-z0-9_.\-]+:[^\s]+$")) return true;

        return !url.Contains("://", StringComparison.Ordinal) && Path.IsPathRooted(url);
    }

    /// <summary>Clones into <paramref name="destination"/>, which must not already exist.</summary>
    public Task<GitCommandResult> CloneAsync(
        string url, string destination, string? branch = null, int depth = 1,
        IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        if (!IsAllowedUrl(url))
            throw new CookieException($"'{url}' is not a git URL this editor will clone.",
                                      "Use an https, ssh or local-path URL.");

        return RunAsync(null, CloneArguments(url, destination, branch, depth), NetworkTimeout, progress, cancellation);
    }

    /// <summary>Fetches without changing the working tree.</summary>
    public Task<GitCommandResult> FetchAsync(string repository, IProgress<string>? progress = null, CancellationToken cancellation = default)
        => RunAsync(repository, new[] { "fetch", "--no-tags", "--prune" }, NetworkTimeout, progress, cancellation);

    /// <summary>
    /// Fast-forwards to the tracked branch. Never merges: a jar is a mirror of somebody else's
    /// repository, and a conflict there should be a refusal rather than a half-merged library.
    /// </summary>
    public Task<GitCommandResult> PullFastForwardAsync(string repository, IProgress<string>? progress = null, CancellationToken cancellation = default)
        => RunAsync(repository, new[] { "pull", "--ff-only", "--no-tags" }, NetworkTimeout, progress, cancellation);

    /// <summary>The commit a jar is currently at, or null.</summary>
    public async Task<string?> RevParseAsync(string repository, string revision = "HEAD", CancellationToken cancellation = default)
    {
        var result = await RunAsync(repository, new[] { "rev-parse", revision }, LocalTimeout, null, cancellation).ConfigureAwait(false);
        return result.Success ? result.FirstLine : null;
    }

    /// <summary>True when <paramref name="directory"/> is the top of a git working tree.</summary>
    public async Task<bool> IsRepositoryAsync(string directory, CancellationToken cancellation = default)
    {
        if (!Directory.Exists(directory)) return false;
        var result = await RunAsync(directory, new[] { "rev-parse", "--is-inside-work-tree" }, LocalTimeout, null, cancellation).ConfigureAwait(false);
        return result.Success && string.Equals(result.FirstLine, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The files that changed between two commits, repository-relative.</summary>
    public async Task<IReadOnlyList<string>> ChangedFilesAsync(
        string repository, string fromCommit, string toCommit, CancellationToken cancellation = default)
    {
        var result = await RunAsync(repository, new[] { "diff", "--name-only", fromCommit, toCommit }, LocalTimeout, null, cancellation)
                          .ConfigureAwait(false);

        return result.Success
            ? result.Output.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList()
            : Array.Empty<string>();
    }

    /// <summary>Runs git with an argument list, never a command line.</summary>
    public async Task<GitCommandResult> RunAsync(
        string?               workingDirectory,
        IReadOnlyList<string> args,
        TimeSpan?             timeout    = null,
        IProgress<string>?    progress   = null,
        CancellationToken     cancellation = default)
    {
        var started = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName               = _git,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            WorkingDirectory       = workingDirectory ?? System.Environment.CurrentDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };

        foreach (string arg in args) psi.ArgumentList.Add(arg);
        foreach (var (name, value) in Environment) psi.Environment[name] = value;

        var output = new List<string>();
        var errors = new List<string>();
        var gate   = new object();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (gate) output.Add(e.Data); progress?.Report(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data == null) return; lock (gate) errors.Add(e.Data); progress?.Report(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new CookieException("git could not be run: " + ex.Message,
                                      "Install git, or set its path in the Cookie Jar settings.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout ?? LocalTimeout);
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
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
        }

        // Let the async readers drain before the lines are read.
        try { process.WaitForExit(); } catch (Exception) { }

        lock (gate)
            return new GitCommandResult(process.HasExited ? process.ExitCode : -1,
                                        output.ToArray(), errors.ToArray(), started.Elapsed, timedOut, cancelled);
    }
}
