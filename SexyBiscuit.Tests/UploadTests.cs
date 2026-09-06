using System.Net;
using SexyBiscuit.Engine.Build.Upload;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>The HTTP upload target, without a network.</summary>
public class UploadTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = new();

        public StubHandler(params Func<HttpResponseMessage>[] responses) => _responses = new(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : "";
            Seen.Add((request, body));
            return _responses.Count > 0 ? _responses.Dequeue()() : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static string TempZip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hello-world-1.0.0-web-{Guid.NewGuid():N}.zip");
        File.WriteAllText(path, "not really a zip");
        return path;
    }

    private static UploadMetadata Metadata() => new("Hello World", "1.0.0", "Release", new string('b', 40), "dev", new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task HttpUploadTargetPostsMultipartWithBearerAndMetadata()
    {
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{ "url": "https://darksgames.app/builds/42" }""", System.Text.Encoding.UTF8, "application/json"),
        });
        var settings = new UploadSettings { Fields = { ["game"] = "title" } };
        var target   = new HttpUploadTarget("https://darksgames.app/api/upload", "secret", settings, handler);
        string zip   = TempZip();

        try
        {
            var result = await target.UploadAsync(new UploadArtifact(zip, "web", null, new FileInfo(zip).Length), Metadata(), null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(201, result.Status);
            Assert.Equal("https://darksgames.app/builds/42", result.Url);

            var (request, body) = Assert.Single(handler.Seen);
            Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
            Assert.StartsWith("multipart/form-data", request.Content!.Headers.ContentType!.MediaType);
            Assert.Contains("name=title", body);                 // the field map renamed game
            Assert.Contains("Hello World", body);
            Assert.Contains("name=sha256", body);
            Assert.Contains("name=gitSha", body);
            Assert.Contains("2026-09-06T12:00:00", body);
            Assert.Contains(Path.GetFileName(zip), body);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public async Task A5xxIsRetriedOnceAndA4xxIsNot()
    {
        var handler = new StubHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK) { Headers = { Location = new Uri("https://darksgames.app/builds/43") } });
        var target = new HttpUploadTarget("https://darksgames.app/api/upload", "t", new UploadSettings(), handler);
        string zip = TempZip();

        try
        {
            var result = await target.UploadAsync(new UploadArtifact(zip, "web", null, 1), Metadata(), null, CancellationToken.None);
            Assert.True(result.Success);
            Assert.Equal("https://darksgames.app/builds/43", result.Url);
            Assert.Equal(2, handler.Seen.Count);

            var forbidden = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.Forbidden));
            var denied = await new HttpUploadTarget("https://x", "t", new UploadSettings(), forbidden)
                .UploadAsync(new UploadArtifact(zip, "web", null, 1), Metadata(), null, CancellationToken.None);
            Assert.False(denied.Success);
            Assert.Equal(403, denied.Status);
            Assert.Single(forbidden.Seen);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void AMissingTokenIsAConfigurationErrorNotAnException()
    {
        var settings = new UploadSettings { Url = "https://darksgames.app/api/upload", TokenVariable = "MY_TOKEN" };

        Assert.Null(UploadTargets.FromConfig(settings, out var why, _ => null));
        Assert.Contains("MY_TOKEN", why);

        Assert.Null(UploadTargets.FromConfig(new UploadSettings(), out why, _ => null));
        Assert.Contains("SB_UPLOAD_URL", why);

        var target = UploadTargets.FromConfig(settings, out why, name => name == "MY_TOKEN" ? "tok" : null);
        Assert.NotNull(target);
        Assert.Null(why);

        // The environment's URL wins over the file's.
        Assert.NotNull(UploadTargets.FromConfig(new UploadSettings(), out _, name => name switch
        {
            "SB_UPLOAD_URL" => "https://elsewhere.test/u",
            "SB_UPLOAD_TOKEN" => "tok",
            _ => null,
        }));

        Assert.Null(UploadTargets.FromConfig(new UploadSettings { Url = "s3://bucket/prefix" }, out why, name => name == "SB_UPLOAD_TOKEN" ? "tok" : null));
        Assert.Contains("s3://", why);
    }
}
