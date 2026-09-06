using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace SexyBiscuit.Engine.Build.Upload;

/// <summary>
/// Sends a build to a site that accepts HTTP uploads with a token: one multipart POST per
/// artifact, the file plus the metadata a download page needs.
/// </summary>
/// <remarks>
/// The token comes from an environment variable, never from a file that might be committed,
/// and the field names are configurable so the request can be shaped to whatever the site
/// expects. <c>html5/tools/upload.js</c> sends the same request, so a build uploaded from
/// either side looks the same to the site.
/// </remarks>
public sealed class HttpUploadTarget : IUploadTarget
{
    /// <summary>The metadata fields every upload carries, in the contract's names and order.</summary>
    public static readonly string[] Fields =
        { "game", "version", "platform", "rid", "configuration", "gitSha", "channel", "builtUtc", "sha256" };

    private readonly HttpClient     _client;
    private readonly UploadSettings _settings;
    private readonly string         _url;
    private readonly string         _token;

    public string Name => "http";

    /// <param name="url">The endpoint to POST to.</param>
    /// <param name="token">The bearer token; read from the environment by <see cref="UploadTargets.FromConfig"/>.</param>
    /// <param name="settings">Field names and the file part name.</param>
    /// <param name="handler">An alternative transport, for tests.</param>
    public HttpUploadTarget(string url, string token, UploadSettings settings, HttpMessageHandler? handler = null)
    {
        _url      = url;
        _token    = token;
        _settings = settings;
        _client   = handler != null ? new HttpClient(handler) : new HttpClient();
        _client.Timeout = TimeSpan.FromMinutes(10);
    }

    public async Task<UploadResult> UploadAsync(UploadArtifact artifact, UploadMetadata metadata, IProgress<string>? progress, CancellationToken cancellation)
    {
        var stopwatch = Stopwatch.StartNew();
        string? lastError = null;

        // One retry on a server error or a dropped connection; a 4xx is final.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request  = BuildRequest(artifact, metadata);
                using var response = await _client.SendAsync(request, cancellation).ConfigureAwait(false);

                int status = (int)response.StatusCode;
                if (status >= 500 && attempt == 0)
                {
                    lastError = $"HTTP {status}";
                    progress?.Report($"  upload {artifact.Platform}: HTTP {status}, retrying");
                    continue;
                }

                string? location = response.Headers.Location?.ToString();
                if (location == null)
                {
                    string body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
                    location = TryReadUrl(body);
                }

                return new UploadResult(response.IsSuccessStatusCode, artifact.FilePath, artifact.Platform,
                    location ?? (response.IsSuccessStatusCode ? _url : null), status,
                    response.IsSuccessStatusCode ? null : $"HTTP {status}", stopwatch.Elapsed);
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

        return new UploadResult(false, artifact.FilePath, artifact.Platform, null, null, lastError ?? "upload failed", stopwatch.Elapsed);
    }

    /// <summary>The request for one artifact, exposed so a test can look at it without a server.</summary>
    public HttpRequestMessage BuildRequest(UploadArtifact artifact, UploadMetadata metadata)
    {
        var bytes  = File.ReadAllBytes(artifact.FilePath);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var values = new Dictionary<string, string>
        {
            ["game"]          = metadata.AppName,
            ["version"]       = metadata.Version,
            ["platform"]      = artifact.Platform,
            ["rid"]           = artifact.Rid ?? string.Empty,
            ["configuration"] = metadata.Configuration,
            ["gitSha"]        = metadata.GitSha ?? string.Empty,
            ["channel"]       = metadata.Channel,
            ["builtUtc"]      = metadata.BuiltUtc.ToString("o"),
            ["sha256"]        = sha256,
        };

        var form = new MultipartFormDataContent();
        foreach (var field in Fields)
        {
            string name = _settings.Fields.TryGetValue(field, out var renamed) ? renamed : field;
            form.Add(new StringContent(values[field]), name);
        }

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, _settings.FileField, Path.GetFileName(artifact.FilePath));

        var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private static string? TryReadUrl(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in new[] { "url", "downloadUrl", "location" })
            {
                if (document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }
}
