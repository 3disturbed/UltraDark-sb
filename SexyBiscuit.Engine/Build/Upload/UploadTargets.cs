namespace SexyBiscuit.Engine.Build.Upload;

/// <summary>
/// Picks the upload target a build settings file names.
/// </summary>
/// <remarks>
/// Today that is one target, the HTTP one, and the scheme of the URL is the switch: an
/// <c>s3://</c> bucket or an <c>sftp://</c> host slot in here as new cases without the
/// pipeline changing. Missing configuration is a message, not an exception, because "no
/// upload target" is a normal state for a local build.
/// </remarks>
public static class UploadTargets
{
    /// <summary>The environment variable that overrides the configured URL.</summary>
    public const string UrlVariable = "SB_UPLOAD_URL";

    /// <summary>Resolves a target, or explains in <paramref name="why"/> what is missing.</summary>
    public static IUploadTarget? FromConfig(UploadSettings settings, out string? why, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        why = null;

        string? url = environment(UrlVariable);
        if (string.IsNullOrWhiteSpace(url)) url = settings.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            why = $"no upload URL: set {UrlVariable} or upload.url in BuildSettings.json";
            return null;
        }

        string tokenVariable = string.IsNullOrWhiteSpace(settings.TokenVariable) ? "SB_UPLOAD_TOKEN" : settings.TokenVariable;
        string? token = environment(tokenVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            why = $"no upload token: set {tokenVariable}";
            return null;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new HttpUploadTarget(url, token, settings);

        why = $"no upload target for '{url}': only http(s) endpoints are supported";
        return null;
    }
}
