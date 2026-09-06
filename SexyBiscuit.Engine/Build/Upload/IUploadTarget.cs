namespace SexyBiscuit.Engine.Build.Upload;

/// <summary>A file to upload and where it came from.</summary>
public sealed record UploadArtifact(string FilePath, string Platform, string? Rid, long Bytes);

/// <summary>What a download page needs to know about a build. The same nine fields <c>html5/tools/upload.js</c> sends.</summary>
public sealed record UploadMetadata(
    string   AppName,
    string   Version,
    string   Configuration,
    string?  GitSha,
    string   Channel,
    DateTime BuiltUtc);

/// <summary>The outcome of one upload.</summary>
public sealed record UploadResult(
    bool     Success,
    string   Artifact,
    string   Platform,
    string?  Url,
    int?     Status,
    string?  Error,
    TimeSpan Duration);

/// <summary>Somewhere a build can be sent. Implementations are chosen by <see cref="UploadTargets.FromConfig"/>.</summary>
public interface IUploadTarget
{
    /// <summary>A short name for the report line.</summary>
    string Name { get; }

    Task<UploadResult> UploadAsync(UploadArtifact artifact, UploadMetadata metadata, IProgress<string>? progress, CancellationToken cancellation);
}

/// <summary>
/// The <c>upload</c> section of <c>BuildSettings.json</c>: where builds go and how the request
/// is shaped. Read by the C# pipeline and by <c>html5/tools/upload.js</c> alike.
/// </summary>
public sealed class UploadSettings
{
    /// <summary>The endpoint. <c>https://…</c> is a multipart POST; the <c>SB_UPLOAD_URL</c> environment variable overrides.</summary>
    public string? Url { get; set; }

    /// <summary>The environment variable holding the bearer token. Never the token itself.</summary>
    public string TokenVariable { get; set; } = "SB_UPLOAD_TOKEN";

    /// <summary>The release channel sent with every upload.</summary>
    public string Channel { get; set; } = "dev";

    /// <summary>Renames the contract's field names to whatever the site expects, e.g. <c>{ "game": "title" }</c>.</summary>
    public Dictionary<string, string> Fields { get; set; } = new();

    /// <summary>The name of the file part.</summary>
    public string FileField { get; set; } = "file";
}
