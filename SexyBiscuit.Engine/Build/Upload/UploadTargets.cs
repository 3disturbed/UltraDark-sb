namespace SexyBiscuit.Engine.Build.Upload;

/// <summary>Where the token was found, so a panel can say "set" without ever showing a value.</summary>
/// <param name="Found">True when a token is available.</param>
/// <param name="Source">Where it came from, or where it was looked for.</param>
/// <param name="Hint">What to do about it when it is missing, or a warning when it was found.</param>
public sealed record TokenStatus(bool Found, string Source, string? Hint);

/// <summary>
/// Chooses the upload target for a project. The URL's scheme is the switch, so an
/// <c>s3://</c> or <c>sftp://</c> target slots in here as a new case without the pipeline
/// changing. Missing configuration is a message, not an exception, because "no upload target"
/// is a normal state for a local build.
/// </summary>
public static class UploadTargets
{
    /// <summary>Overrides the endpoint for one run.</summary>
    public const string UrlVariable = "DG_BUILD_URL";

    /// <summary>The endpoint variable this repo used before the publish API was known.</summary>
    public const string LegacyUrlVariable = "SB_UPLOAD_URL";

    /// <summary>The variable the publish API's own instructions name.</summary>
    public const string TokenVariable = "DG_BUILD_TOKEN";

    /// <summary>The token variable this repo used before the publish API was known.</summary>
    public const string LegacyTokenVariable = "SB_UPLOAD_TOKEN";

    /// <summary>
    /// The token, from the first place that has one: the configured variable, <c>DG_BUILD_TOKEN</c>,
    /// the older <c>SB_UPLOAD_TOKEN</c>, then the first line of the token file. The value is
    /// returned to the caller that is about to send it and is never logged or stored.
    /// </summary>
    public static string? FindToken(UploadSettings settings, out TokenStatus status,
                                    Func<string, string?>? environment = null, Func<string, string?>? readFile = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        readFile    ??= path => File.Exists(path) ? File.ReadAllText(path) : null;

        var names = new[] { settings.TokenVariable, TokenVariable, LegacyTokenVariable }
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToArray();

        foreach (var name in names)
        {
            if (environment(name) is { } value && !string.IsNullOrWhiteSpace(value))
            {
                status = new TokenStatus(true, $"the {name} environment variable", null);
                return value.Trim();
            }
        }

        string file = settings.EffectiveTokenFile;
        string? contents = readFile(file);
        if (!string.IsNullOrWhiteSpace(contents))
        {
            string token = contents.Split('\n')[0].Trim();
            if (token.Length > 0)
            {
                status = new TokenStatus(true, file, LooseFilePermissions(file) ? $"{file} is readable by other users; chmod 600 it" : null);
                return token;
            }
        }

        status = new TokenStatus(false, names[0], $"set {names[0]}, or put the token on the first line of {file}");
        return null;
    }

    /// <summary>Whether a token is available, for a status line. Never returns the token.</summary>
    public static TokenStatus DescribeToken(UploadSettings settings, Func<string, string?>? environment = null, Func<string, string?>? readFile = null)
    {
        FindToken(settings, out var status, environment, readFile);
        return status;
    }

    /// <summary>The upload target for these settings, or null with <paramref name="why"/> saying what is missing.</summary>
    public static IUploadTarget? FromConfig(UploadSettings settings, out string? why,
                                            Func<string, string?>? environment = null, Func<string, string?>? readFile = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        why = null;

        string? url = environment(UrlVariable);
        if (string.IsNullOrWhiteSpace(url)) url = environment(LegacyUrlVariable);
        if (string.IsNullOrWhiteSpace(url)) url = settings.Url;
        if (string.IsNullOrWhiteSpace(url)) url = DarksGamesUploadTarget.Endpoint;

        string? token = FindToken(settings, out var status, environment, readFile);
        if (token == null)
        {
            why = $"no publish token: {status.Hint}";
            return null;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new DarksGamesUploadTarget(url, token, settings);

        why = $"no upload target for '{url}': only http(s) endpoints are supported";
        return null;
    }

    /// <summary>True when a Unix token file is readable by anyone but its owner.</summary>
    private static bool LooseFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0; }
        catch (Exception) { return false; }
    }
}
