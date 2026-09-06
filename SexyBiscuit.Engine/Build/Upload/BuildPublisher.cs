namespace SexyBiscuit.Engine.Build.Upload;

/// <summary>
/// Turns a finished build into published builds: which archives are worth sending, what to say
/// about them, and the sending itself.
/// </summary>
/// <remarks>
/// The CLI's <c>--upload</c>, the editor's Publish tab and the <c>publish_build</c> MCP tool all
/// come through here, so there is one implementation and one set of messages. Publishing is
/// deliberately separable from building: a macOS publish takes twenty minutes, and re-sending an
/// archive that is already on disk should not repeat it.
/// </remarks>
public static class BuildPublisher
{
    /// <summary>
    /// The artifacts in a report that can be published: a target that succeeded, produced an
    /// archive that still exists, and maps to one of the API's platforms. The web build is not a
    /// downloadable game build, so it is skipped with a note rather than reported as a failure.
    /// </summary>
    public static List<UploadArtifact> ArtifactsFrom(BuildReport report, UploadSettings settings, out List<string> skipped)
    {
        var artifacts = new List<UploadArtifact>();
        skipped = new List<string>();

        foreach (var target in report.Targets)
        {
            if (!TryParsePlatform(target.Platform, out var platform)) { skipped.Add($"{target.Platform}: not a known build target"); continue; }
            if (!target.Success)      { skipped.Add($"{target.Platform}: the build failed"); continue; }
            if (target.Archive == null) { skipped.Add($"{target.Platform}: no archive (build with packaging on)"); continue; }
            if (!File.Exists(target.Archive)) { skipped.Add($"{target.Platform}: {Path.GetFileName(target.Archive)} is gone"); continue; }
            if (DarksGamesUploadTarget.PlatformFor(platform, settings.PlatformMap) == null)
            {
                skipped.Add($"{target.Platform}: not a downloadable build, so it is not published");
                continue;
            }

            long bytes = target.ArchiveBytes > 0 ? target.ArchiveBytes : new FileInfo(target.Archive).Length;
            artifacts.Add(new UploadArtifact(target.Archive, platform, target.Rid, bytes));
        }

        return artifacts;
    }

    /// <summary>What the site is told, from the project's settings plus this run's version and commit.</summary>
    public static UploadMetadata MetadataFor(PlatformConfig config, string version, string? gitSha, string? appSlug = null, string? channel = null,
                                             string? notes = null, string? requirements = null, bool? replace = null, bool? publish = null)
        => new(
            AppSlug:       Slug(appSlug ?? config.Upload.AppSlug, config.AppName),
            Title:         config.AppName,
            Version:       version,
            Channel:       channel      ?? config.Upload.Channel,
            Notes:         notes        ?? config.Upload.Notes,
            Requirements:  requirements ?? config.Upload.Requirements,
            Replace:       replace      ?? config.Upload.Replace,
            Publish:       publish      ?? config.Upload.Publish,
            Configuration: config.Configuration.ToString(),
            GitSha:        gitSha,
            BuiltUtc:      DateTime.UtcNow);

    /// <summary>The catalog slug: what the project asked for, else one derived from the app name.</summary>
    public static string Slug(string? configured, string appName)
        => !string.IsNullOrWhiteSpace(configured) ? configured.Trim().ToLowerInvariant() : ExportPipeline.Slugify(appName);

    /// <summary>
    /// Publishes each artifact in turn. Two artifacts that map to the same API platform would
    /// replace each other under the identity triple, so that is called out before anything is
    /// sent rather than discovered as a missing build later.
    /// </summary>
    public static async Task<List<UploadResult>> PublishAsync(IUploadTarget target, IReadOnlyList<UploadArtifact> artifacts, UploadMetadata metadata,
                                                              UploadSettings settings, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        foreach (var clash in artifacts.GroupBy(a => DarksGamesUploadTarget.PlatformFor(a.Platform, settings.PlatformMap)).Where(g => g.Count() > 1))
        {
            progress?.Report($"warning: {string.Join(" and ", clash.Select(a => a.Rid ?? a.Platform.ToString()))} both publish as '{clash.Key}' " +
                             $"and share the slug/version/platform identity, so the later one replaces the earlier. " +
                             $"Give one a different version, or set upload.platformMap.");
        }

        var results = new List<UploadResult>();
        foreach (var artifact in artifacts)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = await target.UploadAsync(artifact, metadata, progress, cancellation).ConfigureAwait(false);
            foreach (var warning in result.Warnings) progress?.Report($"warning: {warning}");
            progress?.Report($"publish {result.Platform}  {result.Status?.ToString() ?? "ERR"}  {result.Url ?? result.Error}");
            results.Add(result);
        }
        return results;
    }

    /// <summary>Reads a <see cref="TargetReport.Platform"/> string back into the enum it came from.</summary>
    public static bool TryParsePlatform(string name, out BuildPlatform platform)
    {
        if (Enum.TryParse(name, ignoreCase: true, out platform)) return true;
        if (RuntimeIdentifiers.Parse(name) is { } parsed) { platform = parsed; return true; }
        platform = default;
        return false;
    }
}
