using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SexyBiscuit.Engine.DarksGames;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The Darks Games layer: verifying who a player is, and the two clients that speak to the hub.
/// </summary>
public class DarksGamesTests
{
    // -------------------------------------------------------------------------
    // A hub, in a test
    // -------------------------------------------------------------------------

    /// <summary>An RSA key, its JWKS, and the ability to mint tokens signed by it.</summary>
    private sealed class FakeHub : IDisposable
    {
        public const string Issuer   = "https://darksgames.app";
        public const string KeyId    = "k1";

        private readonly RSA _rsa = RSA.Create(2048);

        public string Jwks
        {
            get
            {
                var parameters = _rsa.ExportParameters(includePrivateParameters: false);
                return new JsonObject
                {
                    ["keys"] = new JsonArray(new JsonObject
                    {
                        ["kty"] = "RSA",
                        ["kid"] = KeyId,
                        ["n"]   = Base64Url(parameters.Modulus!),
                        ["e"]   = Base64Url(parameters.Exponent!),
                    }),
                }.ToJsonString();
            }
        }

        public string Token(JsonObject claims, string keyId = KeyId, string algorithm = "RS256")
        {
            string header  = Base64Url(Encoding.UTF8.GetBytes(
                new JsonObject { ["alg"] = algorithm, ["kid"] = keyId }.ToJsonString()));
            string payload = Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));

            byte[] signature = _rsa.SignData(Encoding.UTF8.GetBytes($"{header}.{payload}"),
                                             HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return $"{header}.{payload}.{Base64Url(signature)}";
        }

        /// <summary>A well-formed player token, with whatever a test wants to change.</summary>
        public JsonObject Claims(string audience = "my-game") => new()
        {
            ["iss"]    = Issuer,
            ["aud"]    = audience,
            ["sub"]    = "u_123",
            ["exp"]    = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["name"]   = "Darko",
            ["handle"] = "Darko#4821",
        };

        private static string Base64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        public void Dispose() => _rsa.Dispose();
    }

    /// <summary>Answers whatever a test says, and records what was asked.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _reply;

        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> reply) => _reply = reply;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                     CancellationToken cancellationToken)
        {
            // The body has to be read now: the request is disposed before a test can look.
            if (request.Content != null)
                Body = await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(request);
            var (status, body) = _reply(request);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }

        public string? Body { get; private set; }
    }

    // -------------------------------------------------------------------------
    // Verifying a token
    // -------------------------------------------------------------------------

    private static DarksGamesTokenVerifier VerifierFor(FakeHub hub, string audience = "my-game",
                                                       Func<DateTimeOffset>? now = null)
        => new(audience,
               jwksUrl: "https://darksgames.app/api/v1/jwks",
               issuer: FakeHub.Issuer,
               handler: new StubHandler(_ => (HttpStatusCode.OK, hub.Jwks)),
               now: now);

    [Fact]
    public async Task AGenuineTokenIsAcceptedAndReadsAsTheRightPlayer()
    {
        using var hub = new FakeHub();
        using var verifier = VerifierFor(hub);

        var identity = await verifier.VerifyAsync(hub.Token(hub.Claims()));

        Assert.NotNull(identity);
        Assert.Equal("u_123", identity!.UserId);
        Assert.Equal("Darko", identity.Name);
        Assert.Equal("Darko#4821", identity.Handle);
    }

    [Fact]
    public async Task APartyLaunchTokenIsNotAPlayerIdentity()
    {
        // The one mistake the hub's own guide calls out by name. A launch token is signed by
        // the same key, with the same issuer and the same audience, and carries typ:"dg-party".
        // Without the typ check it would pass as whoever holds it.
        using var hub = new FakeHub();
        using var verifier = VerifierFor(hub);

        var claims = hub.Claims();
        claims["typ"] = "dg-party";
        claims["pty"] = "p_9";

        Assert.Null(await verifier.VerifyAsync(hub.Token(claims)));
    }

    [Fact]
    public async Task ATokenForAnotherGameIsRefused()
    {
        // Tokens are per-app. Accepting another app's would let any DarksGames title's token
        // sign somebody in here.
        using var hub = new FakeHub();
        using var verifier = VerifierFor(hub, audience: "my-game");

        Assert.Null(await verifier.VerifyAsync(hub.Token(hub.Claims(audience: "another-game"))));
    }

    [Fact]
    public async Task AnExpiredTokenIsRefused()
    {
        using var hub = new FakeHub();
        var claims = hub.Claims();
        claims["exp"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();

        using var verifier = VerifierFor(hub);
        Assert.Null(await verifier.VerifyAsync(hub.Token(claims)));
    }

    [Fact]
    public async Task ATamperedPayloadIsRefused()
    {
        // The whole point of the signature. Editing the subject is the obvious attack: a token
        // for somebody else's account, re-encoded.
        using var hub = new FakeHub();
        using var verifier = VerifierFor(hub);

        string[] parts = hub.Token(hub.Claims()).Split('.');
        var claims = JsonNode.Parse(
            Encoding.UTF8.GetString(DarksGamesTokenVerifier.FromBase64Url(parts[1])))!.AsObject();
        claims["sub"] = "u_someone_else";

        string forged = Convert.ToBase64String(Encoding.UTF8.GetBytes(claims.ToJsonString()))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        Assert.Null(await verifier.VerifyAsync($"{parts[0]}.{forged}.{parts[2]}"));
    }

    [Fact]
    public async Task ATokenSignedWithAnUnknownKeyIsRefused()
    {
        using var hub = new FakeHub();
        using var verifier = VerifierFor(hub);
        Assert.Null(await verifier.VerifyAsync(hub.Token(hub.Claims(), keyId: "not-a-key")));
    }

    [Fact]
    public async Task AnAlgorithmSwapIsRefused()
    {
        // "alg": "none" and HMAC-with-the-public-key are the two classic JWT forgeries. Pinning
        // RS256 before anything else is read closes both.
        using var hub = new FakeHub();
        using var verifier = VerifierFor(hub);
        Assert.Null(await verifier.VerifyAsync(hub.Token(hub.Claims(), algorithm: "none")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    public async Task RubbishIsRefusedRatherThanThrowing(string token)
    {
        // Every call site wants "guest". A verifier that throws makes an unauthenticated join
        // look like a server fault.
        using var hub = new FakeHub();
        using var verifier = VerifierFor(hub);
        Assert.Null(await verifier.VerifyAsync(token));
    }

    // -------------------------------------------------------------------------
    // The player's own client
    // -------------------------------------------------------------------------

    [Fact]
    public void SigningInReadsTheIdentityOutOfTheToken()
    {
        using var hub = new FakeHub();
        var claims = hub.Claims();
        claims["ents"] = new JsonArray("supporter");

        using var account = new DarksGamesAccount("my-game",
            handler: new StubHandler(_ => (HttpStatusCode.OK, "{}")));

        Assert.True(account.SignIn(hub.Token(claims)));
        Assert.Equal("u_123", account.User.Id);
        Assert.Equal("Darko#4821", account.User.Handle);
        Assert.Contains("supporter", account.User.Entitlements);
    }

    [Fact]
    public void AnExpiredTokenLeavesTheAccountSignedOut()
    {
        using var hub = new FakeHub();
        var claims = hub.Claims();
        claims["exp"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();

        using var account = new DarksGamesAccount("my-game",
            handler: new StubHandler(_ => (HttpStatusCode.OK, "{}")));

        Assert.False(account.SignIn(hub.Token(claims)));
        Assert.False(account.SignedIn);
    }

    [Fact]
    public async Task EverySignedOutCallIsHarmless()
    {
        // A player with no hub account has to be able to play. Nothing here may throw, and
        // nothing may reach the network.
        var handler = new StubHandler(_ => (HttpStatusCode.OK, "{}"));
        using var account = new DarksGamesAccount("my-game", handler: handler);

        Assert.Null(await account.LoadSaveAsync());
        Assert.Null(await account.WriteSaveAsync(new JsonObject { ["hp"] = 1 }));
        Assert.False(await account.HasEntitlementAsync("supporter"));
        Assert.Empty(await account.FriendsAsync());
        Assert.False(await account.PublishPresenceAsync("lobby"));
        Assert.False(await account.ReportAchievementAsync("first_win"));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AConflictingSaveCarriesTheServersCopySoTheGameCanMerge()
    {
        // The alternative -- last write wins -- silently loses whichever device saved first,
        // which is the bug players describe as "it deleted my progress".
        using var hub = new FakeHub();
        var handler = new StubHandler(_ => (HttpStatusCode.Conflict, """
            {"server":{"gameState":{"hp":90},"version":2,"checksum":"abc","updatedAt":"2026-09-01T00:00:00Z"}}
            """));

        using var account = new DarksGamesAccount("my-game", handler: handler);
        account.SignIn(hub.Token(hub.Claims()));

        var conflict = await Assert.ThrowsAsync<DarksGamesSaveConflict>(
            () => account.WriteSaveAsync(new JsonObject { ["hp"] = 50 }, baseUpdatedAt: "2026-08-01T00:00:00Z"));

        Assert.Equal(90, conflict.Server.Data?["hp"]?.GetValue<int>());
        Assert.Equal("2026-09-01T00:00:00Z", conflict.Server.UpdatedAt);
    }

    [Fact]
    public async Task PresenceUpperCasesItsJoinCode()
    {
        // Codes are compared case-sensitively after extraction, and the hub upper-cases a
        // path-shaped join URL. A lower-case code gives a Join button that resolves to a room
        // nobody is in.
        using var hub = new FakeHub();
        var handler = new StubHandler(_ => (HttpStatusCode.NoContent, ""));

        using var account = new DarksGamesAccount("my-game", handler: handler);
        account.SignIn(hub.Token(hub.Claims()));

        await account.PublishPresenceAsync("lobby", "waiting", joinCode: "abc234", players: 2, max: 4);

        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("ABC234", body["join"]?["joinCode"]?.GetValue<string>());
        Assert.Equal(2, body["join"]?["players"]?.GetValue<int>());
        Assert.Equal("my-game", body["app"]?.GetValue<string>());
    }

    [Fact]
    public async Task AnEntitlementOnTheTokenCostsNoRoundTrip()
    {
        using var hub = new FakeHub();
        var claims = hub.Claims();
        claims["ents"] = new JsonArray("supporter");

        var handler = new StubHandler(_ => (HttpStatusCode.OK, """{"entitlements":[]}"""));
        using var account = new DarksGamesAccount("my-game", handler: handler);
        account.SignIn(hub.Token(claims));

        Assert.True(await account.HasEntitlementAsync("supporter"));
        Assert.Empty(handler.Requests);

        // One not on the token does need the call.
        Assert.False(await account.HasEntitlementAsync("deluxe"));
        Assert.Single(handler.Requests);
    }

    // -------------------------------------------------------------------------
    // The server's client
    // -------------------------------------------------------------------------

    [Fact]
    public async Task WithoutASecretEveryServerCallIsASilentNoOp()
    {
        // Development boxes and staging copies have no secret. They have to run, and they have
        // to run without filling a log with failures.
        var handler = new StubHandler(_ => (HttpStatusCode.OK, "{}"));
        using var server = new DarksGamesServer("my-game", secret: "", handler: handler);

        Assert.False(server.Enabled);
        Assert.Null(await server.UnlockAsync("u_1", "first_clear"));
        Assert.Empty(await server.FriendIdsAsync("u_1"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnUnlockCarriesTheAppAndItsSecret()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler(request => { seen = request; return (HttpStatusCode.OK, """{"key":"first_clear"}"""); });

        using var server = new DarksGamesServer("my-game", secret: "s3cret", handler: handler);
        var result = await server.UnlockAsync("u_1", "first_clear");

        Assert.NotNull(result);
        Assert.Equal("my-game", seen!.Headers.GetValues("X-DG-App").Single());
        Assert.Equal("s3cret", seen.Headers.GetValues("X-DG-App-Secret").Single());
        Assert.EndsWith("/users/u_1/achievements/first_clear", seen.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ARegisteredAchievementCarriesTheDefaultsTheHubDocuments()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, """{"count":1}"""));
        using var server = new DarksGamesServer("my-game", secret: "s3cret", handler: handler);

        await server.RegisterAchievementsAsync(new[] { new DarksGamesAchievement("first_clear", "First clear") });

        var body = JsonNode.Parse(handler.Body!)!["achievements"]![0]!;
        Assert.Equal("first_clear", body["key"]!.GetValue<string>());
        Assert.Equal(10, body["points"]!.GetValue<int>());
        Assert.Equal(1, body["target"]!.GetValue<int>());
    }

    [Fact]
    public async Task APartyLookupAddressesTheRequestWithTheTokensOwnPartyId()
    {
        // The token is verified by the hub, not here. The party id is only read out of it to
        // address the call -- which is why reading it unverified is safe.
        using var hub = new FakeHub();
        var claims = hub.Claims();
        claims["typ"] = "dg-party";
        claims["pty"] = "p_42";
        string launchToken = hub.Token(claims);

        HttpRequestMessage? seen = null;
        var handler = new StubHandler(request => { seen = request; return (HttpStatusCode.OK, """{"party":{"id":"p_42"}}"""); });

        using var server = new DarksGamesServer("my-game", secret: "s3cret", handler: handler);
        var party = await server.PartyAsync(launchToken);

        Assert.Equal("p_42", party?["id"]?.GetValue<string>());
        Assert.Contains("/parties/p_42", seen!.RequestUri!.ToString());
    }

    [Fact]
    public async Task A204IsSuccessNotAFailure()
    {
        // The hub answers 204 for a write with nothing to say back. Treating an empty body as
        // a failure would make every presence update look like an error.
        var handler = new StubHandler(_ => (HttpStatusCode.NoContent, ""));
        using var server = new DarksGamesServer("my-game", secret: "s3cret", handler: handler);

        Assert.NotNull(await server.ClearPresenceAsync("u_1"));
    }

    // -------------------------------------------------------------------------
    // The runtime a script sees
    // -------------------------------------------------------------------------

    [Fact]
    public void EventsAreRaisedOnTheGameThreadWhenTickIsPumped()
    {
        // A handler that ran from a socket callback would be running mid-frame, which is the
        // one thing the engine's threading model promises never happens.
        using var hub = new FakeHub();
        using var account = new DarksGamesAccount("my-game",
            handler: new StubHandler(_ => (HttpStatusCode.OK, "{}")));
        using var runtime = new DarksGamesRuntime("my-game", account);

        DarksGamesUser? seen = null;
        runtime.UserChanged += user => seen = user;

        runtime.SignIn(hub.Token(hub.Claims()));
        Assert.Null(seen);                    // queued, not delivered

        runtime.Tick(1f / 60f);
        Assert.Equal("u_123", seen?.Id);
    }

    [Fact]
    public void PublishingTheSamePresenceTwiceSendsItOnce()
    {
        // The hub's entry lasts ninety seconds, and a game whose room fills and empties every
        // few seconds would otherwise spend a request on each one.
        using var hub = new FakeHub();
        var handler = new StubHandler(_ => (HttpStatusCode.NoContent, ""));
        using var account = new DarksGamesAccount("my-game", handler: handler);
        using var runtime = new DarksGamesRuntime("my-game", account);
        runtime.SignIn(hub.Token(hub.Claims()));

        runtime.Presence("lobby", joinCode: "ABC234");
        runtime.Presence("lobby", joinCode: "ABC234");
        runtime.Presence("lobby", joinCode: "ABC234");

        // The requests go out on the thread pool; one poll is enough to see how many started.
        SpinWait.SpinUntil(() => handler.Requests.Count >= 1, TimeSpan.FromSeconds(2));
        Thread.Sleep(50);
        Assert.Single(handler.Requests);
    }
}
