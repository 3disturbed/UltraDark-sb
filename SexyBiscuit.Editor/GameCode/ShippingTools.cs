using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SexyBiscuit.Engine.Build;
using SexyBiscuit.Engine.Build.Upload;
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
        "(upload=true publishes the native archives to DarksGames; needs DG_BUILD_TOKEN). platforms " +
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

    [McpTool("publish_build",
        "Publish archives that already exist to DarksGames — no rebuild, so a twenty-minute desktop publish is not " +
        "repeated just to send the file. Takes the archives from the last export_build in this session, or from the " +
        "project's dist/build-report.json, or one explicit archive path. Only native builds publish: the web build is " +
        "not a downloadable game build and is skipped. Re-publishing the same slug, version and platform replaces that " +
        "build in place and keeps its download link working, so bump version for a genuinely new build. Needs " +
        "DG_BUILD_TOKEN in the environment or on the first line of ~/.sexybiscuit/dg-token.",
        MainThread = false, Label = "Publish to DarksGames")]
    public async Task<McpToolResult> PublishBuild(
        [McpParam("Limit to these targets, e.g. [\"osx-arm64\"]")] string[]? platforms = null,
        [McpParam("alpha, beta or demo")] string? channel = null,
        [McpParam("Release notes for the download card")] string? notes = null,
        [McpParam("What a player needs, e.g. 'Windows 10+, 4 GB RAM'")] string? requirements = null,
        [McpParam("Catalog slug; defaults to one derived from the app name")] string? appSlug = null,
        [McpParam("Version override")] string? version = null,
        [McpParam("Upload but leave it hidden on the site")] bool hidden = false,
        [McpParam("False makes a version collision an error instead of replacing")] bool replace = true,
        [McpParam("Publish this one file instead of the last build's archives")] string? archive = null,
        [McpParam("Seconds to wait before giving up")] int waitSeconds = 900,
        CancellationToken cancellation = default)
    {
        if (EditorState.CurrentProject == null)
            throw new McpToolException("No project is open.", "Call open_project first.");

        string root   = EditorState.ProjectPath;
        var    config = PlatformConfig.ForProject(root, BuildPlatform.Windows_x64);

        var (artifacts, skipped, reportVersion) = ResolveArtifacts(config, root, archive, platforms);
        if (artifacts.Count == 0)
        {
            throw new McpToolException(
                skipped.Count > 0 ? "Nothing to publish: " + string.Join("; ", skipped) : "Nothing to publish: no build has been packaged.",
                "Run export_build first, or pass archive with a path to a .zip or .tar.gz.");
        }

        var target = UploadTargets.FromConfig(config.Upload, out var why)
            ?? throw new McpToolException($"Cannot publish: {why}", "The token is read from the environment or the token file; it is never stored in the project.");

        var metadata = BuildPublisher.MetadataFor(config, version ?? reportVersion ?? config.Version, GitInfo.TryReadHeadSha(root),
                                                  appSlug, channel, notes, requirements, replace, hidden ? false : null);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 10, 3600)));

        var progress = new Progress<string>(line => ConsoleLog.Add("[publish] " + line, LogLevel.Info));
        List<UploadResult> results;
        try
        {
            results = await BuildPublisher.PublishAsync(target, artifacts, metadata, config.Upload, progress, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new McpToolException($"The publish did not finish within {waitSeconds} s.", "Raise waitSeconds, or publish one platform at a time.");
        }

        var rows = new JsonArray();
        foreach (var r in results)
        {
            var row = new JsonObject { ["platform"] = r.Platform, ["ok"] = r.Success, ["file"] = Path.GetFileName(r.Artifact) };
            if (r.Status    != null) row["status"]  = r.Status;
            if (r.Url       != null) row["url"]     = r.Url;
            if (r.Id        != null) row["id"]      = r.Id;
            if (r.SizeLabel != null) row["size"]    = r.SizeLabel;
            if (r.Replaced)          row["replaced"] = true;
            if (!r.Published)        row["hidden"]   = true;
            if (r.Error     != null) row["error"]   = r.Error;
            rows.Add(row);
        }

        var view = new JsonObject
        {
            ["app"]      = metadata.Title,
            ["slug"]     = metadata.AppSlug,
            ["version"]  = metadata.Version,
            ["channel"]  = DarksGamesUploadTarget.ChannelFor(metadata.Channel, out _),
            ["builds"]   = rows,
            ["page"]     = results.FirstOrDefault(r => r.Page != null)?.Page,
        };
        if (skipped.Count > 0) view["skipped"] = new JsonArray(skipped.Select(x => (JsonNode)x).ToArray());

        int ok = results.Count(r => r.Success);
        string summary = string.Join("\n", results.Select(r =>
            $"{r.Platform,-8} {(r.Success ? "ok    " : "FAILED")} {r.Url ?? r.Error}"));
        ConsoleLog.Add($"[publish] {ok}/{results.Count} published for {metadata.AppSlug} {metadata.Version}.", ok == results.Count ? LogLevel.Info : LogLevel.Warning);

        var result = McpToolResult.Json(view, summary);
        if (ok == 0) result.IsError = true;
        return result;
    }

    /// <summary>
    /// The archives to publish: one explicit file, else the last export in this session, else the
    /// report the CLI left in the project's output folder.
    /// </summary>
    private (List<UploadArtifact> Artifacts, List<string> Skipped, string? Version) ResolveArtifacts(
        PlatformConfig config, string root, string? archive, string[]? platforms)
    {
        var skipped = new List<string>();

        if (!string.IsNullOrWhiteSpace(archive))
        {
            string path = Path.IsPathRooted(archive) ? archive : Path.Combine(root, archive);
            if (!File.Exists(path)) throw new McpToolException($"{archive} does not exist.");
            var platform = PlatformFromFileName(path)
                ?? (platforms is { Length: 1 } ? (BuildPlatform?)ResolveTargets(platforms)[0] : null)
                ?? throw new McpToolException($"Cannot tell which platform '{Path.GetFileName(path)}' is for.", "Pass platforms with one entry, e.g. [\"osx-arm64\"].");
            return (new List<UploadArtifact> { new(path, platform, RuntimeIdentifiers.For(platform), new FileInfo(path).Length) }, skipped, null);
        }

        BuildReport? report = null;
        if (_latest is { Completion.IsCompletedSuccessfully: true } job) report = job.Completion.Result;

        if (report == null)
        {
            string file = Path.Combine(ExportPipeline.ResolveOutputRoot(config), BuildReport.FileName);
            if (!File.Exists(file))
                throw new McpToolException("No build to publish.", $"Run export_build first, or pass archive. Looked for {file}.");
            try { report = BuildReport.Load(file); }
            catch (Exception ex) { throw new McpToolException($"{BuildReport.FileName} could not be read: {ex.Message}"); }
        }

        var artifacts = BuildPublisher.ArtifactsFrom(report, config.Upload, out skipped);
        if (platforms is { Length: > 0 })
        {
            var wanted = ResolveTargets(platforms);
            artifacts = artifacts.Where(a => wanted.Contains(a.Platform)).ToList();
        }
        return (artifacts, skipped, report.Version);
    }

    /// <summary>The target an archive was built for, read back out of the name the packager gave it.</summary>
    private static BuildPlatform? PlatformFromFileName(string path)
    {
        string name = Path.GetFileName(path);
        foreach (var suffix in new[] { ".tar.gz", ".tgz" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name[..^suffix.Length]; break; }
        name = Path.GetFileNameWithoutExtension(name);

        var parts = name.Split('-');
        for (int take = 1; take <= Math.Min(3, parts.Length); take++)
        {
            if (RuntimeIdentifiers.Parse(string.Join('-', parts[^take..])) is { } platform) return platform;
        }
        return null;
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
