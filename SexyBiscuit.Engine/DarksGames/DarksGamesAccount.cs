using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.DarksGames;

// ---------------------------------------------------------------------------
// DarksGamesAccount.cs
// The player's own identity, saves and social state, from a native build.
// ---------------------------------------------------------------------------

/// <summary>The signed-in player, or a signed-out placeholder.</summary>
public sealed class DarksGamesUser
{
    /// <summary>The hub's stable id, <c>u_…</c>. The subject of every s2s call.</summary>
    public string? Id { get; init; }

    /// <summary>The display name the player chose.</summary>
    public string? Name { get; init; }

    /// <summary><c>Darko#4821</c> — unique, and what a friend request is addressed to.</summary>
    public string? Handle { get; init; }

    /// <summary>Entitlement keys carried on the token, so a check costs no round trip.</summary>
    public IReadOnlyList<string> Entitlements { get; init; } = Array.Empty<string>();

    public bool SignedIn => Id != null;

    /// <summary>What to show above a player's head.</summary>
    public string DisplayName => Name ?? Handle ?? "Player";

    /// <summary>The signed-out user. Never null, so no call site needs a null check.</summary>
    public static readonly DarksGamesUser SignedOut = new();
}

/// <summary>What a cloud save round trip carries.</summary>
public sealed record DarksGamesSave(JsonNode? Data, int Version, string? Checksum, string? UpdatedAt);

/// <summary>Thrown when a save was written from somewhere else since this copy was read.</summary>
public sealed class DarksGamesSaveConflict : Exception
{
    public DarksGamesSaveConflict(DarksGamesSave server)
        : base("the cloud save moved since this copy was read") => Server = server;

    /// <summary>The server's copy, so the game can merge or ask the player.</summary>
    public DarksGamesSave Server { get; }
}

/// <summary>
/// A native game's client for the Darks Games hub: who the player is, their cloud save, their
/// entitlements, their friends, and what they are doing.
/// </summary>
/// <remarks>
/// <para>
/// The browser SDK gets its token from a cookie the hub sets on <c>*.darksgames.app</c>. A
/// desktop build has no such cookie, so the token is supplied to it: by the launcher, by the
/// <c>DG_ACCESS_TOKEN</c> environment variable, or by a sign-in flow the game runs itself. Until
/// one arrives the client is signed out, and every call returns an empty answer rather than
/// throwing — a player with no hub account has to be able to play.
/// </para>
/// <para>
/// Nothing here retries or caches beyond the profile: a game loop must never block on the
/// network, so every method is async and every failure is a null.
/// </para>
/// </remarks>
public sealed class DarksGamesAccount : IDisposable
{
    /// <summary>The hub's API root.</summary>
    public const string DefaultApi = "https://darksgames.app/api/v1";

    /// <summary>The variable a launcher writes the player's token into.</summary>
    public const string TokenVariable = "DG_ACCESS_TOKEN";

    private readonly HttpClient _http;
    private readonly bool       _ownsHttp;
    private readonly string     _api;

    private string?         _token;
    private DarksGamesUser  _user = DarksGamesUser.SignedOut;

    /// <param name="game">This game's catalogue slug. Also the token audience the hub enforces.</param>
    /// <param name="api">Overridable for a staging hub.</param>
    /// <param name="handler">Injected by tests so no socket is opened.</param>
    public DarksGamesAccount(string game, string? api = null, HttpMessageHandler? handler = null)
    {
        Game      = game;
        _api      = (api ?? DefaultApi).TrimEnd('/');
        _ownsHttp = handler == null;
        _http     = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>The catalogue slug this client speaks for.</summary>
    public string Game { get; }

    /// <summary>The signed-in player. Never null.</summary>
    public DarksGamesUser User => _user;

    public bool SignedIn => _user.SignedIn;

    /// <summary>Raised when the player signs in or out.</summary>
    public event Action<DarksGamesUser>? UserChanged;

    // -------------------------------------------------------------------------
    // Signing in
    // -------------------------------------------------------------------------

    /// <summary>
    /// Adopts an access token and reads the identity out of it.
    /// </summary>
    /// <remarks>
    /// The claims are read without verifying the signature, which is correct here and would not
    /// be on a server: this is the player's own token, held by the player's own process, and the
    /// only thing at stake is what name their own game shows them. A <em>server</em> checking a
    /// token somebody sent it uses <see cref="DarksGamesTokenVerifier"/>, which verifies.
    /// </remarks>
    /// <returns>False when the token is malformed or expired.</returns>
    public bool SignIn(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) { SignOut(); return false; }

        var claims = ReadClaims(token);
        if (claims?["sub"]?.GetValue<string>() is not { Length: > 0 } subject) { SignOut(); return false; }

        if (claims["exp"]?.GetValue<long>() is { } exp && exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            SignOut();
            return false;
        }

        _token = token;
        _user  = new DarksGamesUser
        {
            Id     = subject,
            Name   = claims["name"]?.GetValue<string>(),
            Handle = claims["handle"]?.GetValue<string>(),
            Entitlements = (claims["ents"] as JsonArray)?
                .Select(e => e?.GetValue<string>() ?? string.Empty)
                .Where(e => e.Length > 0).ToArray() ?? Array.Empty<string>(),
        };

        UserChanged?.Invoke(_user);
        return true;
    }

    /// <summary>
    /// Signs in from <c>DG_ACCESS_TOKEN</c>, or from a file a launcher wrote.
    /// </summary>
    /// <remarks>
    /// The file path is checked after the variable so a launcher can override a stale one. A
    /// token is a credential: the file is read, never written, and never logged.
    /// </remarks>
    public bool SignInFromEnvironment(string? tokenFile = null)
    {
        if (Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } fromEnv && SignIn(fromEnv))
            return true;

        if (tokenFile != null && File.Exists(tokenFile))
        {
            try { return SignIn(File.ReadAllText(tokenFile).Trim()); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        return false;
    }

    public void SignOut()
    {
        bool had = _user.SignedIn;
        _token = null;
        _user  = DarksGamesUser.SignedOut;
        if (had) UserChanged?.Invoke(_user);
    }

    // -------------------------------------------------------------------------
    // Cloud saves
    // -------------------------------------------------------------------------

    /// <summary>The player's cloud save, or null when there is none — or they are signed out.</summary>
    public async Task<DarksGamesSave?> LoadSaveAsync(CancellationToken cancellation = default)
    {
        var body = await GetAsync($"/apps/{Uri.EscapeDataString(Game)}/save", cancellation).ConfigureAwait(false);
        if (body == null) return null;

        return new DarksGamesSave(
            body["gameState"],
            body["version"]?.GetValue<int>() ?? 1,
            body["checksum"]?.GetValue<string>(),
            body["updatedAt"]?.GetValue<string>());
    }

    /// <summary>
    /// Writes the cloud save.
    /// </summary>
    /// <param name="data">Anything JSON can express.</param>
    /// <param name="version">The game's own save version.</param>
    /// <param name="baseUpdatedAt">
    /// The timestamp this edit was based on. Passing it turns a lost update into a rejected
    /// write the game can resolve, rather than one device silently overwriting the other.
    /// </param>
    /// <param name="cancellation">Cancels the request.</param>
    /// <exception cref="DarksGamesSaveConflict">Another device wrote first.</exception>
    public async Task<DarksGamesSave?> WriteSaveAsync(JsonNode? data, int version = 1,
                                                      string? baseUpdatedAt = null,
                                                      CancellationToken cancellation = default)
    {
        if (!SignedIn) return null;

        var payload = new JsonObject { ["version"] = version, ["gameState"] = data?.DeepClone() };
        if (baseUpdatedAt != null) payload["baseUpdatedAt"] = baseUpdatedAt;

        using var request = Request(HttpMethod.Put, $"/apps/{Uri.EscapeDataString(Game)}/save", payload);
        using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

        if ((int)response.StatusCode == 409)
        {
            var server = Parse(body)?["server"];
            throw new DarksGamesSaveConflict(new DarksGamesSave(
                server?["gameState"],
                server?["version"]?.GetValue<int>() ?? version,
                server?["checksum"]?.GetValue<string>(),
                server?["updatedAt"]?.GetValue<string>()));
        }

        if (!response.IsSuccessStatusCode) return null;

        var result = Parse(body);
        return new DarksGamesSave(data, version, result?["checksum"]?.GetValue<string>(),
                                  result?["updatedAt"]?.GetValue<string>());
    }

    // -------------------------------------------------------------------------
    // Entitlements
    // -------------------------------------------------------------------------

    /// <summary>Whether the player owns something — a DLC, a supporter pack.</summary>
    /// <remarks>
    /// The token's own claim answers first, so the common case costs nothing. Only an
    /// entitlement granted since the token was minted needs the round trip.
    /// </remarks>
    public async Task<bool> HasEntitlementAsync(string key, CancellationToken cancellation = default)
    {
        if (_user.Entitlements.Contains(key)) return true;
        if (!SignedIn) return false;

        var body = await GetAsync($"/apps/{Uri.EscapeDataString(Game)}/entitlements", cancellation).ConfigureAwait(false);
        return (body?["entitlements"] as JsonArray)?
            .Any(e => e?["key"]?.GetValue<string>() == key) ?? false;
    }

    // -------------------------------------------------------------------------
    // Social
    // -------------------------------------------------------------------------

    /// <summary>The player's friends, each with the presence this player is allowed to see.</summary>
    public async Task<IReadOnlyList<JsonObject>> FriendsAsync(CancellationToken cancellation = default)
    {
        var body = await GetAsync("/social/friends", cancellation).ConfigureAwait(false);
        return (body?["friends"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? Array.Empty<JsonObject>();
    }

    /// <summary>
    /// Publishes what the player is doing, so friends see it and can join.
    /// </summary>
    /// <remarks>
    /// The REST route rather than the social WebSocket: a native game already has a socket to
    /// its own server, and one HTTP call every time the room changes is far less machinery than
    /// a second live connection for a line of text. The entry expires after 90 seconds, so a
    /// game that stops publishing stops showing as joinable on its own.
    /// </remarks>
    public Task<bool> PublishPresenceAsync(string state, string detail = "", string? joinCode = null,
                                           bool joinable = true, int? players = null, int? max = null,
                                           CancellationToken cancellation = default)
    {
        var presence = new JsonObject
        {
            ["app"]    = Game,
            ["state"]  = Cap(state, 40),
            ["detail"] = Cap(detail, 80),
        };

        if (joinCode is { Length: > 0 })
        {
            // Codes are compared case-sensitively after extraction, and the hub upper-cases a
            // path-shaped join URL. Publishing lower case gives a Join button that resolves to
            // a room nobody is in.
            var join = new JsonObject { ["joinCode"] = joinCode.ToUpperInvariant(), ["joinable"] = joinable };
            if (players.HasValue) join["players"] = players.Value;
            if (max.HasValue)     join["max"]     = max.Value;
            presence["join"] = join;
        }
        else
        {
            presence["join"] = null;
        }

        return SendAsync(HttpMethod.Put, "/social/presence", presence, cancellation);
    }

    /// <summary>Stops showing the player as in this game.</summary>
    public Task<bool> ClearPresenceAsync(CancellationToken cancellation = default)
        => SendAsync(HttpMethod.Delete, "/social/presence", null, cancellation);

    /// <summary>
    /// Reports an achievement from the client.
    /// </summary>
    /// <remarks>
    /// Only accepted for games the catalogue marks <c>clientAchievements: true</c>. A game with a
    /// server unlocks over s2s instead — see <see cref="DarksGamesServer"/> — because a
    /// self-reported unlock is a claim, not a fact. Never hang an entitlement or a ranking off one.
    /// </remarks>
    public Task<bool> ReportAchievementAsync(string key, int? increment = null,
                                             CancellationToken cancellation = default)
    {
        var body = increment.HasValue ? new JsonObject { ["increment"] = increment.Value } : new JsonObject();
        return SendAsync(HttpMethod.Post,
            $"/social/achievements/{Uri.EscapeDataString(Game)}/{Uri.EscapeDataString(key)}/report",
            body, cancellation);
    }

    // -------------------------------------------------------------------------
    // Plumbing
    // -------------------------------------------------------------------------

    private async Task<JsonObject?> GetAsync(string path, CancellationToken cancellation)
    {
        if (!SignedIn) return null;
        try
        {
            using var request = Request(HttpMethod.Get, path, null);
            using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<bool> SendAsync(HttpMethod method, string path, JsonNode? body,
                                       CancellationToken cancellation)
    {
        if (!SignedIn) return false;
        try
        {
            using var request = Request(method, path, body);
            using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, JsonNode? body)
    {
        var request = new HttpRequestMessage(method, _api + path);
        if (_token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body != null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>The claims out of a JWT, without verifying it. Public so a test can look.</summary>
    internal static JsonObject? ReadClaims(string token)
    {
        string[] parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            return JsonNode.Parse(
                Encoding.UTF8.GetString(DarksGamesTokenVerifier.FromBase64Url(parts[1]))) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    private static JsonObject? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
