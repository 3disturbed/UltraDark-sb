using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.Build.Upload;

/// <summary>
/// Publishes a build to the DarksGames publish API: one POST whose body is the archive's raw
/// bytes and whose metadata rides in a base64url <c>X-Build-Meta</c> header.
/// </summary>
/// <remarks>
/// The body is the file, so nothing is buffered — the archive is streamed from disk and
/// <c>Content-Length</c> comes from the file, which lets the server reject an oversize upload
/// before a byte is written. The token is read from the environment (or a file the user owns) by
/// <see cref="UploadTargets.FromConfig"/> and only ever appears in an <c>Authorization</c> header.
/// The identity of a build is the triple slug + version + platform: re-publishing it replaces the
/// previous build in place and keeps its id, so a link already given to a playtester keeps working.
/// </remarks>
public sealed class DarksGamesUploadTarget : IUploadTarget
{
    /// <summary>The publish endpoint used when nothing overrides it.</summary>
    public const string Endpoint = "https://darksgames.app/api/v1/builds/publish";

    /// <summary>The API's file-size ceiling, checked before any request.</summary>
    public const long MaxFileBytes = 1_073_741_824;

    /// <summary>The API's ceiling on the encoded metadata header.</summary>
    public const int MaxMetaBytes = 6144;

    /// <summary>What the API accepts. Archives only: a build is served back from the site's own origin.</summary>
    public static readonly string[] AllowedExtensions =
    {
        "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "exe", "msi",
        "dmg", "pkg", "apk", "aab", "appimage", "deb", "jar", "love", "bin",
    };

    /// <summary>The three channels the API accepts.</summary>
    public static readonly string[] Channels = { "alpha", "beta", "demo" };

    private readonly HttpClient     _client;
    private readonly UploadSettings _settings;
    private readonly string         _url;
    private readonly string         _token;

    /// <param name="url">The endpoint.</param>
    /// <param name="token">The bearer token, already read from the environment or the token file.</param>
    /// <param name="settings">The project's upload section.</param>
    /// <param name="handler">Injected by tests so no socket is opened.</param>
    public DarksGamesUploadTarget(string url, string token, UploadSettings settings, HttpMessageHandler? handler = null)
    {
        _url      = url;
        _token    = token;
        _settings = settings;
        _client   = handler != null ? new HttpClient(handler) : new HttpClient();
        _client.Timeout = TimeSpan.FromMinutes(30);
    }

    public string Name => "darksgames";

    // -------------------------------------------------------------------------
    // The metadata header
    // -------------------------------------------------------------------------

    /// <summary>
    /// The API's five platform names. Every desktop target maps onto one of them; the web build
    /// is not a downloadable game build and is never published, which is why this returns null.
    /// </summary>
    public static string? PlatformFor(BuildPlatform platform, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (overrides != null && overrides.TryGetValue(platform.ToString(), out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            return mapped.Trim().ToLowerInvariant();

        return platform switch
        {
            BuildPlatform.Windows_x64 or BuildPlatform.Windows_x86 or BuildPlatform.Steam_Windows => "windows",
            BuildPlatform.macOS_x64   or BuildPlatform.macOS_ARM64 or BuildPlatform.Steam_macOS   => "macos",
            BuildPlatform.Linux_x64   or BuildPlatform.Steam_Linux                                => "linux",
            BuildPlatform.Android                                                                 => "android",
            BuildPlatform.iOS                                                                     => "other",
            _                                                                                     => null,
        };
    }

    /// <summary>
    /// The channel to send. The API takes three names; the older <c>dev</c> spellings a project
    /// may still carry are mapped rather than rejected, because a 422 at upload time after a
    /// twenty-minute publish is a poor way to learn about a config typo.
    /// </summary>
    public static string ChannelFor(string? channel, out string? warning)
    {
        warning = null;
        string value = (channel ?? string.Empty).Trim().ToLowerInvariant();
        if (Channels.Contains(value)) return value;

        string mapped = value switch
        {
            "dev" or "development" or "nightly" or "" => "alpha",
            "playtest" or "rc" or "candidate"         => "beta",
            "release" or "public" or "stable"         => "demo",
            _                                         => "alpha",
        };
        if (value.Length > 0) warning = $"channel '{value}' is not one of alpha, beta or demo; sent as '{mapped}'";
        return mapped;
    }

    /// <summary>The file name the API will accept, sanitised the way it sanitises server-side.</summary>
    public static string SanitiseFileName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in Path.GetFileName(name))
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        string clean = sb.ToString().Trim('-');
        if (clean.Length > 120) clean = clean[^120..];
        return clean.Length > 0 ? clean : "build.zip";
    }

    /// <summary>What a player needs, when the project has not said. Our players are self-contained .NET 8.</summary>
    public static string DefaultRequirements(string platform) => platform switch
    {
        "windows" => "Windows 10 or later, 64-bit",
        "macos"   => "macOS 12 or later",
        "linux"   => "64-bit Linux, glibc 2.23 or later",
        "android" => "Android 8.0 or later",
        _         => "",
    };

    /// <summary>The metadata object, exactly as it is encoded into the header. Public so a test can read it.</summary>
    public JsonObject BuildMeta(UploadArtifact artifact, UploadMetadata metadata, out IReadOnlyList<string> warnings)
    {
        var notes = new List<string>();
        string platform = PlatformFor(artifact.Platform, _settings.PlatformMap) ?? "other";
        string channel  = ChannelFor(metadata.Channel, out var channelWarning);
        if (channelWarning != null) notes.Add(channelWarning);

        // Provenance the API has no field for: which build this actually is.
        string built = metadata.BuiltUtc.ToString("yyyy-MM-dd HH:mm'Z'");
        string line  = $"Build: {artifact.Rid ?? artifact.Platform.ToString().ToLowerInvariant()} · {metadata.Configuration}" +
                       (metadata.GitSha is { Length: >= 7 } sha ? $" · {sha[..7]}" : "") + $" · {built}";
        string body  = metadata.Notes.Trim();
        string all   = body.Length > 0 ? body + "\n\n" + line : line;

        var meta = new JsonObject
        {
            ["appSlug"]      = Cap(metadata.AppSlug.Trim().ToLowerInvariant(), 64),
            ["title"]        = Cap(metadata.Title.Trim(), 80),
            ["version"]      = Cap(metadata.Version.Trim(), 40),
            ["fileName"]     = SanitiseFileName(artifact.FilePath),
            ["channel"]      = channel,
            ["platform"]     = platform,
            ["notes"]        = Cap(all, 4000),
            ["requirements"] = Cap(string.IsNullOrWhiteSpace(metadata.Requirements) ? DefaultRequirements(platform) : metadata.Requirements.Trim(), 200),
            ["replace"]      = metadata.Replace,
            ["publish"]      = metadata.Publish,
        };

        warnings = notes;
        return meta;
    }

    /// <summary>
    /// base64url of the compact JSON, unpadded. Notes are trimmed until the header fits the
    /// API's 6144 bytes, because a long release note is the only field that can overflow it.
    /// </summary>
    public static string EncodeMeta(JsonObject meta)
    {
        string encoded = Encode(meta);
        if (encoded.Length <= MaxMetaBytes) return encoded;

        // Give back the two free-text fields, longest first, until it fits.
        foreach (var field in new[] { "notes", "requirements" })
        {
            while (encoded.Length > MaxMetaBytes && meta[field]?.GetValue<string>() is { Length: > 0 } text)
            {
                // Trim in proportion to the overflow: a character is one byte or four, so
                // subtracting the overflow directly would empty a multi-byte note in one step.
                int keep = (int)(text.Length * (MaxMetaBytes / (double)encoded.Length)) - 8;
                keep = Math.Clamp(keep, 0, text.Length - 1);
                meta[field] = keep > 0 ? text[..keep].TrimEnd() + "…" : "";
                encoded = Encode(meta);
            }
        }
        return encoded;
    }

    private static string Encode(JsonObject meta)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(meta.ToJsonString()))
                  .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];

    // -------------------------------------------------------------------------
    // The request
    // -------------------------------------------------------------------------

    /// <summary>Everything wrong with this publish before a byte goes over the wire, or null.</summary>
    public string? Validate(UploadArtifact artifact, UploadMetadata metadata)
    {
        if (!File.Exists(artifact.FilePath)) return $"{artifact.FilePath} does not exist";

        long bytes = new FileInfo(artifact.FilePath).Length;
        if (bytes == 0)            return $"{Path.GetFileName(artifact.FilePath)} is empty";
        if (bytes > MaxFileBytes)  return $"{Path.GetFileName(artifact.FilePath)} is {bytes / 1048576} MB; the limit is 1024 MB";

        string extension = Path.GetExtension(artifact.FilePath).TrimStart('.').ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
            return $"'.{extension}' is not an accepted extension; wrap the build in a .zip";

        string slug = metadata.AppSlug.Trim().ToLowerInvariant();
        if (slug.Length == 0 || !slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            return $"app slug '{metadata.AppSlug}' must be lowercase letters, numbers and dashes";
        if (string.IsNullOrWhiteSpace(metadata.Title))   return "the build has no title";
        if (string.IsNullOrWhiteSpace(metadata.Version)) return "the build has no version";

        if (PlatformFor(artifact.Platform, _settings.PlatformMap) == null)
            return $"{artifact.Platform} is not a downloadable build and is not published";

        return null;
    }

    /// <summary>The request as it goes out, streamed from disk. Public so a test can look at it without a server.</summary>
    public HttpRequestMessage BuildRequest(UploadArtifact artifact, UploadMetadata metadata, out IReadOnlyList<string> warnings)
    {
        var meta    = BuildMeta(artifact, metadata, out warnings);
        var stream  = File.OpenRead(artifact.FilePath);
        var content = new StreamContent(stream);
        content.Headers.ContentType   = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = stream.Length;

        var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.TryAddWithoutValidation("X-Build-Meta", EncodeMeta(meta));
        return request;
    }

    // -------------------------------------------------------------------------
    // Publishing
    // -------------------------------------------------------------------------

    public async Task<UploadResult> UploadAsync(UploadArtifact artifact, UploadMetadata metadata, IProgress<string>? progress, CancellationToken cancellation)
    {
        var stopwatch = Stopwatch.StartNew();
        string name   = Path.GetFileName(artifact.FilePath);
        string label  = PlatformFor(artifact.Platform, _settings.PlatformMap) ?? artifact.Platform.ToString().ToLowerInvariant();

        if (Validate(artifact, metadata) is { } invalid)
            return new UploadResult(false, artifact.FilePath, label, null, null, invalid, stopwatch.Elapsed);

        string localSha = await LocalChecksumAsync(artifact.FilePath, cancellation).ConfigureAwait(false);
        string? lastError = null, lastCode = null;
        int? lastStatus = null;
        var warnings = new List<string>();

        // The API's own guidance: retry a broken stream and a rate limit, nothing else.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request  = BuildRequest(artifact, metadata, out var requestWarnings);
                if (attempt == 0) warnings.AddRange(requestWarnings);

                progress?.Report($"  publish {label}: {name} ({artifact.Bytes / 1048576} MB)");
                using var response = await _client.SendAsync(request, cancellation).ConfigureAwait(false);

                int status = (int)response.StatusCode;
                lastStatus = status;
                string body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var (code, message) = ReadError(body);
                    lastError = message ?? $"HTTP {status}";
                    lastCode  = code;

                    bool retryable = status == 429 || (status == 400 && code == "upload_failed");
                    if (retryable && attempt == 0)
                    {
                        var wait = RetryAfter(response) ?? TimeSpan.FromSeconds(status == 429 ? 30 : 2);
                        progress?.Report($"  publish {label}: {code ?? status.ToString()}, retrying in {wait.TotalSeconds:0}s");
                        await Task.Delay(wait, cancellation).ConfigureAwait(false);
                        continue;
                    }

                    return new UploadResult(false, artifact.FilePath, label, null, status, lastError, stopwatch.Elapsed)
                        { ErrorCode = code, Warnings = warnings };
                }

                var json = ReadObject(body);
                string? remoteSha = json?["checksum"]?.GetValue<string>();
                if (remoteSha != null && !string.Equals(remoteSha, localSha, StringComparison.OrdinalIgnoreCase))
                {
                    lastError = "checksum mismatch: the upload was corrupted in transit";
                    if (attempt == 0)
                    {
                        progress?.Report($"  publish {label}: {lastError}, re-publishing the same build");
                        continue;
                    }
                    return new UploadResult(false, artifact.FilePath, label, null, status, lastError, stopwatch.Elapsed)
                        { Warnings = warnings };
                }

                return new UploadResult(true, artifact.FilePath, label, json?["url"]?.GetValue<string>(), status, null, stopwatch.Elapsed)
                {
                    Id        = json?["id"]?.GetValue<string>(),
                    Page      = json?["page"]?.GetValue<string>(),
                    SizeLabel = json?["sizeLabel"]?.GetValue<string>(),
                    Checksum  = remoteSha,
                    Replaced  = json?["replaced"]?.GetValue<bool>() ?? status == 200,
                    Published = json?["published"]?.GetValue<bool>() ?? metadata.Publish,
                    Ready     = json?["ready"]?.GetValue<bool>() ?? true,
                    Warnings  = warnings,
                };
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }
            catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
            {
                lastError = "timed out";
            }
        }

        return new UploadResult(false, artifact.FilePath, label, null, lastStatus, lastError ?? "publish failed", stopwatch.Elapsed)
            { ErrorCode = lastCode, Warnings = warnings };
    }

    /// <summary>The SHA-256 of the bytes we are about to send, to compare with the one the site reports.</summary>
    private static async Task<string> LocalChecksumAsync(string path, CancellationToken cancellation)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var after = response.Headers.RetryAfter;
        var wait   = after?.Delta ?? (after?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (wait is not { } value || value <= TimeSpan.Zero) return null;
        return value > TimeSpan.FromSeconds(120) ? TimeSpan.FromSeconds(120) : value;
    }

    private static (string? Code, string? Message) ReadError(string body)
    {
        var json = ReadObject(body);
        string? code    = json?["error"]?.GetValue<string>();
        string? message = json?["message"]?.GetValue<string>();
        return (code, code != null && message != null ? $"{code}: {message}" : message ?? code);
    }

    private static JsonObject? ReadObject(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return null; }
    }
}
