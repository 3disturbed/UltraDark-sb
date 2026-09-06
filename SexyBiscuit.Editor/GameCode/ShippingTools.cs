using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SexyBiscuit.Engine.Build;
using SexyBiscuit.Engine.Mcp;

namespace SexyBiscuit.Editor.GameCode;

/// <summary>
/// Shipping as tools: export the open project for every target and read the report back, one
/// line per target. The same pipeline the <c>sbengine</c> CLI and <c>release.yml</c> run.
/// </summary>
public sealed class ShippingTools
{
    // -------------------------------------------------------------------------
    // Jobs
    // -------------------------------------------------------------------------

    private sealed class ExportJob
    {
        public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public IReadOnlyList<BuildPlatform> Targets { get; init; } = Array.Empty<BuildPlatform>();
        public Task<BuildReport> Completion { get; set; } = Task.FromResult(new BuildReport());
        public CancellationTokenSource Cancellation { get; } = new();
    }

    private readonly ConcurrentDictionary<string, ExportJob> _jobs = new();
    private ExportJob? _latest;

    // -------------------------------------------------------------------------
    // Tools
    // -------------------------------------------------------------------------

    [McpTool("export_build",
        "Export the open project from disk: stage assets, scenes and scripts per target, publish self-contained desktop " +
        "players (publish=true; needs the engine source and the .NET SDK), archive them, and upload the archives " +
        "(upload=true; needs SB_UPLOAD_URL and SB_UPLOAD_TOKEN, or the upload section of BuildSettings.json). platforms " +
        "take BuildSettings names or RIDs — web, win-x64, osx-arm64, linux-x64 — and default to those four. Save the " +
        "scene first. Waits up to waitSeconds and returns one line per target; a longer run returns a job id for " +
        "get_build_report.",
        MainThread = false, Label = "Export the build")]
    public async Task<McpToolResult> ExportBuild(
        [McpParam("Targets, e.g. [\"web\", \"osx-arm64\"]")] string[]? platforms = null,
        [McpParam("Debug, Development or Release")] string? configuration = null,
        [McpParam("Publish desktop players")] bool publish = true,
        [McpParam("Upload the archives")] bool upload = false,
        [McpParam("Version override, e.g. 1.2.0")] string? version = null,
        [McpParam("Seconds to wait before returning a job id")] int waitSeconds = 120,
        CancellationToken cancellation = default)
    {
        if (EditorState.CurrentProject == null)
            throw new McpToolException("No project is open.", "Call open_project or create_project first.");
        if (_latest is { } running && !running.Completion.IsCompleted)
            throw new McpToolException($"Export {running.Id} is still running.", "Call get_build_report to follow it.");

        string root    = EditorState.ProjectPath;
        var    targets = ResolveTargets(platforms);
        var    template = PlatformConfig.ForProject(root, targets[0]);

        if (!string.IsNullOrWhiteSpace(configuration))
        {
            if (!Enum.TryParse<BuildConfiguration>(configuration, ignoreCase: true, out var parsed))
                throw new McpToolException($"Unknown configuration '{configuration}'.", "Use Debug, Development or Release.");
            template.Configuration = parsed;
            // The overlay follows the configuration unless a settings file said otherwise.
            if (!File.Exists(Path.Combine(root, PlatformConfig.FileName)))
                template.IncludeDebugOverlay = parsed != BuildConfiguration.Release;
        }

        var options = new ExportOptions
        {
            Publish = publish,
            Package = true,
            Upload  = upload,
            Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
        };

        var job      = new ExportJob { Targets = targets };
        var progress = new Progress<string>(line => ConsoleLog.Add("[export] " + line, LogLevel.Info));
        job.Completion = Task.Run(() => new ExportPipeline { Output = null }.ExportAllAsync(template, targets, options, progress, job.Cancellation.Token));
        _jobs[job.Id] = job;
        _latest       = job;
        ConsoleLog.Add($"[export] {job.Id}: {template.AppName} {options.Version ?? template.Version} ({template.Configuration}) for {string.Join(", ", targets)}.", LogLevel.Info);

        var finished = await Task.WhenAny(job.Completion, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 1, 3600)), cancellation));
        return finished == job.Completion ? Report(job) : Running(job);
    }

    [McpTool("get_build_report",
        "The report of an export_build run — the latest when jobId is omitted: state, and one line per target with the " +
        "archive, its size, and the upload URL or the first errors.",
        MainThread = false)]
    public McpToolResult GetBuildReport([McpParam("A job id from export_build")] string? jobId = null)
    {
        var job = jobId == null ? _latest : _jobs.GetValueOrDefault(jobId);
        if (job == null)
        {
            return McpToolResult.Json(new JsonObject
            {
                ["state"] = "unknown",
                ["note"]  = jobId == null ? "No export has run in this editor session." : "No such export; job ids do not survive an editor restart.",
            });
        }

        return job.Completion.IsCompleted ? Report(job) : Running(job);
    }

    // -------------------------------------------------------------------------
    // Views
    // -------------------------------------------------------------------------

    private static McpToolResult Running(ExportJob job)
    {
        var view = new JsonObject
        {
            ["jobId"]   = job.Id,
            ["state"]   = "running",
            ["seconds"] = Math.Round((DateTime.UtcNow - job.StartedUtc).TotalSeconds),
            ["targets"] = new JsonArray(job.Targets.Select(t => (JsonNode)t.ToString()).ToArray()),
            ["note"]    = "Still running; call get_build_report with this jobId.",
        };
        return McpToolResult.Json(view, $"export {job.Id} running for {string.Join(", ", job.Targets)}");
    }

    private static McpToolResult Report(ExportJob job)
    {
        if (job.Completion.IsFaulted)
            return McpToolResult.Error($"export {job.Id} failed: {job.Completion.Exception?.GetBaseException().Message}");
        if (job.Completion.IsCanceled)
            return McpToolResult.Error($"export {job.Id} was cancelled.");

        var report  = job.Completion.Result;
        var targets = new JsonArray();
        foreach (var t in report.Targets)
        {
            var row = new JsonObject { ["platform"] = t.Platform, ["ok"] = t.Success, ["seconds"] = Math.Round(t.Seconds, 1) };
            if (t.Rid != null)          row["rid"]          = t.Rid;
            if (t.Archive != null)      { row["archive"] = Path.GetFileName(t.Archive); row["mb"] = Math.Round(t.ArchiveBytes / 1048576.0, 1); }
            if (t.UploadUrl != null)    row["url"]          = t.UploadUrl;
            if (t.UploadStatus != null) row["uploadStatus"] = t.UploadStatus;
            if (t.Errors.Count > 0)     row["errors"]       = new JsonArray(t.Errors.Take(3).Select(e => (JsonNode)e).ToArray());
            targets.Add(row);
        }

        var view = new JsonObject
        {
            ["jobId"]         = job.Id,
            ["state"]         = report.Success ? "succeeded" : "failed",
            ["app"]           = report.AppName,
            ["version"]       = report.Version,
            ["configuration"] = report.Configuration,
            ["gitSha"]        = report.GitSha,
            ["targets"]       = targets,
        };

        var result = McpToolResult.Json(view, string.Join("\n", report.ToSummaryLines()));
        if (report.Targets.Count == 0 || report.Targets.All(t => !t.Success)) result.IsError = true;
        return result;
    }

    private static List<BuildPlatform> ResolveTargets(string[]? platforms)
    {
        if (platforms == null || platforms.Length == 0) return RuntimeIdentifiers.DefaultTargets.ToList();

        var list = new List<BuildPlatform>();
        foreach (var text in platforms)
        {
            if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var p in RuntimeIdentifiers.DefaultTargets) if (!list.Contains(p)) list.Add(p);
                continue;
            }

            var platform = RuntimeIdentifiers.Parse(text)
                ?? throw new McpToolException($"Unknown platform '{text}'.", "Use web, win-x64, osx-arm64, linux-x64, all, or a BuildSettings platform name.");
            if (!list.Contains(platform)) list.Add(platform);
        }
        return list;
    }
}
