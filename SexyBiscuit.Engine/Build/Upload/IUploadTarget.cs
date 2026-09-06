namespace SexyBiscuit.Engine.Build.Upload;

/// <summary>One file to publish: where it is, which target built it, and how big it is.</summary>
/// <param name="FilePath">The archive on disk.</param>
/// <param name="Platform">The build target, so the uploader can map it to the site's platform names.</param>
/// <param name="Rid">The .NET runtime identifier, when the target has one. Provenance only.</param>
/// <param name="Bytes">The archive's size, checked against the endpoint's limit before any request.</param>
public sealed record UploadArtifact(string FilePath, BuildPlatform Platform, string? Rid, long Bytes);

/// <summary>
/// What the site is told about a build. The first eight members are the publish API's own
/// fields; the last three are provenance the API has no field for, and end up as a line in
/// <see cref="Notes"/>.
/// </summary>
public sealed record UploadMetadata(
    string   AppSlug,
    string   Title,
    string   Version,
    string   Channel,
    string   Notes,
    string   Requirements,
    bool     Replace,
    bool     Publish,
    string   Configuration,
    string?  GitSha,
    DateTime BuiltUtc);

/// <summary>What came back from publishing one artifact.</summary>
public sealed record UploadResult(
    bool     Success,
    string   Artifact,
    string   Platform,
    string?  Url,
    int?     Status,
    string?  Error,
    TimeSpan Duration)
{
    /// <summary>The site's build id, <c>bld_&lt;uuid&gt;</c>, stable across a replace.</summary>
    public string? Id { get; init; }

    /// <summary>The human downloads page.</summary>
    public string? Page { get; init; }

    /// <summary>The size the site reported, e.g. "36 MB".</summary>
    public string? SizeLabel { get; init; }

    /// <summary>The SHA-256 the site computed over the bytes it received.</summary>
    public string? Checksum { get; init; }

    /// <summary>The API's error code, e.g. <c>build_too_large</c>, so a report can say more than the status.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>True when this publish overwrote an existing build with the same slug, version and platform.</summary>
    public bool Replaced { get; init; }

    /// <summary>False when the build was uploaded hidden.</summary>
    public bool Published { get; init; }

    /// <summary>True when the site says the file is ready to download.</summary>
    public bool Ready { get; init; }

    /// <summary>Anything worth telling the user that did not stop the publish.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Somewhere a finished build can be sent.</summary>
public interface IUploadTarget
{
    /// <summary>A short name for the report line.</summary>
    string Name { get; }

    /// <summary>Publishes one artifact. Never throws for a rejected upload: the failure is in the result.</summary>
    Task<UploadResult> UploadAsync(UploadArtifact artifact, UploadMetadata metadata, IProgress<string>? progress, CancellationToken cancellation);
}

/// <summary>
/// The <c>upload</c> section of <c>BuildSettings.json</c>: where builds go and how they are
/// described. It holds the <em>name</em> of the environment variable that carries the token and
/// the path of a file that may hold it, never the token itself, so the file stays committable.
/// </summary>
public sealed class UploadSettings
{
    /// <summary>The endpoint. Empty means the DarksGames publish API; <c>DG_BUILD_URL</c> overrides.</summary>
    public string? Url { get; set; }

    /// <summary>The environment variable holding the bearer token. Never the token itself.</summary>
    public string TokenVariable { get; set; } = "DG_BUILD_TOKEN";

    /// <summary>A file whose first line is the token, read only when the environment has none. Empty means <c>~/.sexybiscuit/dg-token</c>.</summary>
    public string? TokenFile { get; set; }

    /// <summary>The catalog slug to publish under. Empty derives one from the app name.</summary>
    public string? AppSlug { get; set; }

    /// <summary>alpha for nightlies, beta for playtest candidates, demo for anything public.</summary>
    public string Channel { get; set; } = "alpha";

    /// <summary>Release notes shown on the download card. A provenance line is always appended.</summary>
    public string Notes { get; set; } = "";

    /// <summary>What a player needs, e.g. "Windows 10+, 4 GB RAM". Blank gets a per-platform default.</summary>
    public string Requirements { get; set; } = "";

    /// <summary>Re-publishing the same slug, version and platform overwrites in place. False makes a collision an error.</summary>
    public bool Replace { get; set; } = true;

    /// <summary>False uploads the build hidden, to be made live from the site's admin page.</summary>
    public bool Publish { get; set; } = true;

    /// <summary>Overrides the platform name sent for a build target, e.g. <c>{ "macOS_x64": "other" }</c>.</summary>
    public Dictionary<string, string> PlatformMap { get; set; } = new();

    /// <summary>Where the token file lives when <see cref="TokenFile"/> is empty.</summary>
    public static string DefaultTokenFile
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sexybiscuit", "dg-token");

    /// <summary>The token file this configuration will read, with <c>~</c> expanded.</summary>
    public string EffectiveTokenFile
    {
        get
        {
            string path = string.IsNullOrWhiteSpace(TokenFile) ? DefaultTokenFile : TokenFile.Trim();
            if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
            return path;
        }
    }
}
