using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SexyBiscuit.Engine.Build;
using SexyBiscuit.Engine.Build.Upload;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Publishing a build to DarksGames: the request shape the API documents, the limits it enforces,
/// and the retry policy it asks for. No test opens a socket.
/// </summary>
public class UploadTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public List<(HttpRequestMessage Request, byte[] Body)> Seen { get; } = new();

        public StubHandler(params Func<HttpResponseMessage>[] responses) => _responses = new(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = request.Content != null ? await request.Content.ReadAsByteArrayAsync(cancellationToken) : Array.Empty<byte>();
            Seen.Add((request, body));
            return _responses.Count > 0 ? _responses.Dequeue()() : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static readonly byte[] Payload = "not really a zip"u8.ToArray();

    private static string TempArchive(string name = "hello-world-1.0.0-osx-arm64.tar.gz")
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{name}");
        File.WriteAllBytes(path, Payload);
        return path;
    }

    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static UploadMetadata Metadata(string channel = "alpha", string notes = "", bool publish = true, bool replace = true)
        => new("hello-world", "Hello World", "1.0.0", channel, notes, "", replace, publish,
               "Release", new string('b', 40), new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc));

    private static UploadArtifact Artifact(string path, BuildPlatform platform = BuildPlatform.macOS_ARM64, string? rid = "osx-arm64")
        => new(path, platform, rid, new FileInfo(path).Length);

    private static HttpResponseMessage Ok(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Success(string checksum, bool replaced = false) => new JsonObject
    {
        ["id"]        = "bld_11111111-2222-3333-4444-555555555555",
        ["url"]       = "https://darksgames.app/api/v1/builds/bld_11111111/download",
        ["page"]      = "https://darksgames.app/downloads",
        ["sizeLabel"] = "16 B",
        ["checksum"]  = checksum,
        ["published"] = true,
        ["replaced"]  = replaced,
        ["ready"]     = true,
    }.ToJsonString();

    private static JsonObject DecodeMeta(HttpRequestMessage request)
    {
        string header = Assert.Single(request.Headers.GetValues("X-Build-Meta"));
        string padded = header.Replace('-', '+').Replace('_', '/').PadRight((header.Length + 3) / 4 * 4, '=');
        return (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded)))!;
    }

    [Fact] // Why: the body is the file and the metadata rides in one header — get either wrong and every publish 4xxs.
    public async Task TheRequestCarriesTheTokenTheRawBytesAndTheEncodedMeta()
    {
        string archive = TempArchive();
        var handler = new StubHandler(() => Ok(HttpStatusCode.Created, Success(Sha256Of(Payload))));
        var target  = new DarksGamesUploadTarget(DarksGamesUploadTarget.Endpoint, "secret", new UploadSettings(), handler);

        var result = await target.UploadAsync(Artifact(archive), Metadata(notes: "First cut."), null, default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(201, result.Status);

        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
        Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(Payload, body);                                   // the body is the file, not a wrapper
        Assert.Equal(Payload.Length, request.Content.Headers.ContentLength);

        var meta = DecodeMeta(request);
        Assert.Equal("hello-world", meta["appSlug"]!.GetValue<string>());
        Assert.Equal("Hello World", meta["title"]!.GetValue<string>());
        Assert.Equal("1.0.0", meta["version"]!.GetValue<string>());
        Assert.Equal("alpha", meta["channel"]!.GetValue<string>());
        Assert.Equal("macos", meta["platform"]!.GetValue<string>());
        Assert.EndsWith(".tar.gz", meta["fileName"]!.GetValue<string>());
        Assert.True(meta["replace"]!.GetValue<bool>());
        Assert.True(meta["publish"]!.GetValue<bool>());

        // The provenance the API has no field for rides in the notes.
        string notes = meta["notes"]!.GetValue<string>();
        Assert.StartsWith("First cut.", notes);
        Assert.Contains("osx-arm64", notes);
        Assert.Contains("Release", notes);
        Assert.Contains("bbbbbbb", notes);
        Assert.Equal("macOS 12 or later", meta["requirements"]!.GetValue<string>());
    }

    [Fact] // Why: the header is capped at 6144 bytes and notes are the only field that can overflow it; a 431 after a long build is a poor way to find out.
    public void MetaIsTrimmedUntilItFitsTheHeaderBudget()
    {
        var target = new DarksGamesUploadTarget("https://x", "t", new UploadSettings());
        string archive = TempArchive();

        var meta = target.BuildMeta(Artifact(archive), Metadata(notes: new string('n', 8000)), out _);
        Assert.True(meta["notes"]!.GetValue<string>().Length <= 4000, "the API's own cap is applied first");
        Assert.True(DarksGamesUploadTarget.EncodeMeta(meta).Length <= DarksGamesUploadTarget.MaxMetaBytes);

        // Multi-byte notes fit the 4000-character cap and still overflow the 6144-byte header,
        // so the encoder has to give characters back until it fits.
        meta["notes"] = new string('—', 3900);
        string header = DarksGamesUploadTarget.EncodeMeta(meta);
        Assert.True(header.Length <= DarksGamesUploadTarget.MaxMetaBytes, $"header was {header.Length} bytes");
        Assert.EndsWith("…", meta["notes"]!.GetValue<string>());
    }

    [Fact] // Why: the API takes five platform names; a web build is not a downloadable game build and must never be sent.
    public void EveryDesktopTargetMapsOntoTheApisPlatformsAndTheWebBuildIsNotPublished()
    {
        Assert.Equal("windows", DarksGamesUploadTarget.PlatformFor(BuildPlatform.Windows_x64));
        Assert.Equal("windows", DarksGamesUploadTarget.PlatformFor(BuildPlatform.Steam_Windows));
        Assert.Equal("macos",   DarksGamesUploadTarget.PlatformFor(BuildPlatform.macOS_ARM64));
        Assert.Equal("macos",   DarksGamesUploadTarget.PlatformFor(BuildPlatform.macOS_x64));
        Assert.Equal("linux",   DarksGamesUploadTarget.PlatformFor(BuildPlatform.Linux_x64));
        Assert.Equal("android", DarksGamesUploadTarget.PlatformFor(BuildPlatform.Android));
        Assert.Equal("other",   DarksGamesUploadTarget.PlatformFor(BuildPlatform.iOS));
        Assert.Null(DarksGamesUploadTarget.PlatformFor(BuildPlatform.Web));

        // A project can override the mapping when two of its targets would otherwise collide.
        var overrides = new Dictionary<string, string> { ["macOS_x64"] = "other" };
        Assert.Equal("other", DarksGamesUploadTarget.PlatformFor(BuildPlatform.macOS_x64, overrides));
    }

    [Fact] // Why: the limits are documented, so a request that cannot succeed should cost nothing.
    public async Task AnOversizeOrDisallowedArtifactFailsBeforeAnyRequest()
    {
        var handler = new StubHandler();
        var target  = new DarksGamesUploadTarget("https://x", "t", new UploadSettings(), handler);

        string html = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-game.html");
        File.WriteAllBytes(html, Payload);
        var refused = await target.UploadAsync(Artifact(html), Metadata(), null, default);
        Assert.False(refused.Success);
        Assert.Contains("not an accepted extension", refused.Error);

        string empty = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-empty.zip");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Assert.Contains("is empty", (await target.UploadAsync(Artifact(empty), Metadata(), null, default)).Error);

        string archive = TempArchive();
        var badSlug = await target.UploadAsync(Artifact(archive), Metadata() with { AppSlug = "Hello World!" }, null, default);
        Assert.Contains("lowercase letters", badSlug.Error);

        var web = await target.UploadAsync(Artifact(archive, BuildPlatform.Web, null), Metadata(), null, default);
        Assert.Contains("not a downloadable build", web.Error);

        Assert.Empty(handler.Seen);
    }

    [Fact] // Why: the API says retry a rate limit and a broken stream, and never blind-retry the deterministic errors.
    public async Task ARateLimitAndABrokenStreamAreRetriedOnceButA4xxIsNot()
    {
        string archive = TempArchive();
        string checksum = Sha256Of(Payload);

        var limited = new StubHandler(
            () => Ok((HttpStatusCode)429, """{"error":"rate_limited","message":"slow down"}"""),
            () => Ok(HttpStatusCode.Created, Success(checksum)));
        var settings = new UploadSettings();
        var afterWait = await new DarksGamesUploadTarget("https://x", "t", settings, limited)
            .UploadAsync(Artifact(archive), Metadata(), null, default);
        Assert.True(afterWait.Success);
        Assert.Equal(2, limited.Seen.Count);

        var broken = new StubHandler(
            () => Ok(HttpStatusCode.BadRequest, """{"error":"upload_failed","message":"the stream broke"}"""),
            () => Ok(HttpStatusCode.OK, Success(checksum, replaced: true)));
        var recovered = await new DarksGamesUploadTarget("https://x", "t", settings, broken)
            .UploadAsync(Artifact(archive), Metadata(), null, default);
        Assert.True(recovered.Success);
        Assert.True(recovered.Replaced);
        Assert.Equal(2, broken.Seen.Count);

        var refused = new StubHandler(() => Ok(HttpStatusCode.Unauthorized, """{"error":"unauthorized","message":"revoked"}"""));
        var denied = await new DarksGamesUploadTarget("https://x", "t", settings, refused)
            .UploadAsync(Artifact(archive), Metadata(), null, default);
        Assert.False(denied.Success);
        Assert.Equal(401, denied.Status);
        Assert.Equal("unauthorized", denied.ErrorCode);
        Assert.Contains("revoked", denied.Error);
        Assert.Single(refused.Seen);
    }

    [Fact] // Why: the id and the link are what a person and a report actually need out of a publish.
    public async Task TheResponseIdUrlAndFlagsLandInTheResult()
    {
        string archive = TempArchive();
        var handler = new StubHandler(() => Ok(HttpStatusCode.OK, Success(Sha256Of(Payload), replaced: true)));
        var result = await new DarksGamesUploadTarget("https://x", "t", new UploadSettings(), handler)
            .UploadAsync(Artifact(archive), Metadata(), null, default);

        Assert.True(result.Success);
        Assert.Equal("bld_11111111-2222-3333-4444-555555555555", result.Id);
        Assert.Equal("https://darksgames.app/api/v1/builds/bld_11111111/download", result.Url);
        Assert.Equal("https://darksgames.app/downloads", result.Page);
        Assert.Equal("16 B", result.SizeLabel);
        Assert.True(result.Replaced);
        Assert.True(result.Ready);
        Assert.Equal("macos", result.Platform);
    }

    [Fact] // Why: the API tells us to compare checksums; a corrupted upload is re-sent to the same triple, which replaces in place.
    public async Task AChecksumMismatchIsRepublishedOnce()
    {
        string archive = TempArchive();
        var handler = new StubHandler(
            () => Ok(HttpStatusCode.Created, Success(new string('0', 64))),
            () => Ok(HttpStatusCode.OK, Success(Sha256Of(Payload), replaced: true)));

        var result = await new DarksGamesUploadTarget("https://x", "t", new UploadSettings(), handler)
            .UploadAsync(Artifact(archive), Metadata(), null, default);

        Assert.True(result.Success);
        Assert.Equal(2, handler.Seen.Count);
        Assert.Equal(Sha256Of(Payload), result.Checksum);
    }

    [Fact] // Why: a 'dev' channel left over from before the API was known should not 422 after a twenty-minute build.
    public void LegacyChannelNamesMapOntoTheThreeTheApiAccepts()
    {
        Assert.Equal("alpha", DarksGamesUploadTarget.ChannelFor("alpha", out var none));
        Assert.Null(none);

        Assert.Equal("alpha", DarksGamesUploadTarget.ChannelFor("dev", out var warning));
        Assert.Contains("not one of alpha", warning);
        Assert.Equal("beta", DarksGamesUploadTarget.ChannelFor("playtest", out _));
        Assert.Equal("demo", DarksGamesUploadTarget.ChannelFor("release", out _));
        Assert.Equal("alpha", DarksGamesUploadTarget.ChannelFor(null, out _));
    }

    [Fact] // Why: the token is the one secret in this pipeline; it comes from the environment or a file the user owns, and never from the project.
    public void AMissingTokenIsAConfigurationErrorAndTheTokenFileIsTheFallback()
    {
        var settings = new UploadSettings();

        Assert.Null(UploadTargets.FromConfig(settings, out var why, _ => null, _ => null));
        Assert.Contains("DG_BUILD_TOKEN", why);
        Assert.Contains(".sexybiscuit", why);

        // The environment wins.
        Assert.NotNull(UploadTargets.FromConfig(settings, out _, n => n == "DG_BUILD_TOKEN" ? "tok" : null, _ => null));

        // The older variable still works, so an existing CI secret is not a silent failure.
        Assert.NotNull(UploadTargets.FromConfig(settings, out _, n => n == "SB_UPLOAD_TOKEN" ? "tok" : null, _ => null));

        // Then the file, first line only.
        var fromFile = UploadTargets.FromConfig(settings, out _, _ => null, _ => "dgb_secret\n# a comment\n");
        Assert.NotNull(fromFile);
        Assert.Equal("darksgames", fromFile!.Name);

        var status = UploadTargets.DescribeToken(settings, _ => null, _ => null);
        Assert.False(status.Found);
        Assert.NotNull(status.Hint);

        // A configured variable name is honoured, and named in the message when it is missing.
        Assert.Null(UploadTargets.FromConfig(new UploadSettings { TokenVariable = "MY_TOKEN" }, out var named, _ => null, _ => null));
        Assert.Contains("MY_TOKEN", named);

        Assert.Null(UploadTargets.FromConfig(new UploadSettings { Url = "s3://bucket" }, out var scheme,
                                             n => n == "DG_BUILD_TOKEN" ? "tok" : null, _ => null));
        Assert.Contains("s3://", scheme);
    }

    [Fact] // Why: only a target that built, packaged and maps to a platform can be published; everything else is a note, not a failure.
    public void OnlyNativeArchivesThatExistArePublished()
    {
        string archive = TempArchive("game-1.0.0-win-x64.zip");
        var report = new BuildReport
        {
            AppName = "Game", Version = "1.0.0",
            Targets =
            {
                new TargetReport { Platform = "Windows_x64", Rid = "win-x64", Success = true, Archive = archive, ArchiveBytes = Payload.Length },
                new TargetReport { Platform = "Web",         Success = true, Archive = archive },
                new TargetReport { Platform = "Linux_x64",   Rid = "linux-x64", Success = false, Archive = archive },
                new TargetReport { Platform = "macOS_ARM64", Rid = "osx-arm64", Success = true, Archive = "/nowhere/gone.tar.gz" },
            },
        };

        var artifacts = BuildPublisher.ArtifactsFrom(report, new UploadSettings(), out var skipped);

        var only = Assert.Single(artifacts);
        Assert.Equal(BuildPlatform.Windows_x64, only.Platform);
        Assert.Equal(3, skipped.Count);
        Assert.Contains(skipped, s => s.Contains("Web") && s.Contains("not a downloadable build"));
        Assert.Contains(skipped, s => s.Contains("Linux_x64") && s.Contains("failed"));
        Assert.Contains(skipped, s => s.Contains("macOS_ARM64") && s.Contains("gone"));
    }

    [Fact] // Why: two artifacts that publish as the same platform share the identity triple, so one silently replaces the other.
    public async Task TwoArtifactsThatPublishAsOnePlatformAreCalledOut()
    {
        string archive = TempArchive();
        var handler = new StubHandler(
            () => Ok(HttpStatusCode.Created, Success(Sha256Of(Payload))),
            () => Ok(HttpStatusCode.OK, Success(Sha256Of(Payload), replaced: true)));
        var settings = new UploadSettings();
        var lines = new List<string>();

        await BuildPublisher.PublishAsync(
            new DarksGamesUploadTarget("https://x", "t", settings, handler),
            new[] { Artifact(archive, BuildPlatform.macOS_ARM64, "osx-arm64"), Artifact(archive, BuildPlatform.macOS_x64, "osx-x64") },
            Metadata(), settings, new Progress<string>(lines.Add));

        // Progress is reported asynchronously; give the callbacks a moment to land.
        for (int i = 0; i < 50 && !lines.Any(l => l.Contains("both publish as")); i++) await Task.Delay(10);
        Assert.Contains(lines, l => l.Contains("both publish as 'macos'"));
    }

    [Fact] // Why: the slug is the game's identity on the site; it defaults from the app name but a project must be able to pin it.
    public void TheSlugComesFromTheProjectOrIsDerivedFromTheAppName()
    {
        Assert.Equal("hello-world", BuildPublisher.Slug(null, "Hello World"));
        Assert.Equal("hello-world", BuildPublisher.Slug("", "Hello World!"));
        Assert.Equal("ultradark",   BuildPublisher.Slug("UltraDark", "Anything"));
    }
}
