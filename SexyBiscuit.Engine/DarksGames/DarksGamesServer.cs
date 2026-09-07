using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.DarksGames;

// ---------------------------------------------------------------------------
// DarksGamesServer.cs
// The server-to-server client: achievements a player cannot forge.
// ---------------------------------------------------------------------------

/// <summary>One achievement a game defines.</summary>
/// <param name="Key">Lower-case, digits and underscores. Renaming it makes a new achievement rather than migrating one.</param>
/// <param name="Name">Up to 80 characters.</param>
/// <param name="Description">Up to 300.</param>
/// <param name="Points">0..10000. Ten by default.</param>
/// <param name="Target">The count an incrementing achievement unlocks at. One by default.</param>
/// <param name="Hidden">Kept out of lists until it is unlocked.</param>
/// <param name="Icon">A URL or emoji, up to 300 characters.</param>
public sealed record DarksGamesAchievement(
    string Key,
    string Name,
    string? Description = null,
    int Points = 10,
    int Target = 1,
    bool Hidden = false,
    string? Icon = null);

/// <summary>
/// A game server's client for the hub, authenticated with the app's own secret.
/// </summary>
/// <remarks>
/// <para>
/// This is the trusted path. An unlock sent from here is a fact — the server watched it happen —
/// where one reported by a client is a claim. That difference is the whole reason the two exist:
/// anything that gates a ranking, an entitlement or a leaderboard place has to come through here.
/// </para>
/// <para>
/// Every call is a silent no-op when <c>DG_APP_SECRET</c> is unset, and nothing here ever throws.
/// A development box, a copy of the server without the secret, and an outage at the hub all have
/// to leave the game running; a missing achievement is not worth a crash.
/// </para>
/// </remarks>
public sealed class DarksGamesServer : IDisposable
{
    /// <summary>The s2s API root.</summary>
    public const string DefaultApi = "https://darksgames.app/api/v1/social/s2s";

    /// <summary>The variable the app's secret is read from.</summary>
    public const string SecretVariable = "DG_APP_SECRET";

    private readonly HttpClient _http;
    private readonly bool       _ownsHttp;
    private readonly string     _api;
    private readonly string     _app;
    private readonly string     _secret;

    /// <param name="app">The catalogue slug. Everything is scoped to it.</param>
    /// <param name="secret">The app secret. Read from <c>DG_APP_SECRET</c> when null.</param>
    /// <param name="api">Overridable for a staging hub.</param>
    /// <param name="handler">Injected by tests so no socket is opened.</param>
    public DarksGamesServer(string app, string? secret = null, string? api = null,
                            HttpMessageHandler? handler = null)
    {
        _app      = app;
        _secret   = secret ?? Environment.GetEnvironmentVariable(SecretVariable) ?? string.Empty;
        _api      = (api ?? DefaultApi).TrimEnd('/');
        _ownsHttp = handler == null;

        // Three seconds, not the default hundred: this is called from a running match, and a
        // hub that is slow must cost a dropped achievement rather than a stalled tick.
        _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(3) };
    }

    /// <summary>False when no secret was supplied, in which case every call does nothing.</summary>
    public bool Enabled => _secret.Length > 0;

    // -------------------------------------------------------------------------
    // Achievements
    // -------------------------------------------------------------------------

    /// <summary>
    /// Declares the game's achievements. Upserts, so it belongs in the server's boot path and
    /// can be called on every start.
    /// </summary>
    public Task<JsonObject?> RegisterAchievementsAsync(IEnumerable<DarksGamesAchievement> achievements,
                                                       CancellationToken cancellation = default)
    {
        var list = new JsonArray();
        foreach (var achievement in achievements)
        {
            var entry = new JsonObject
            {
                ["key"]    = achievement.Key,
                ["name"]   = achievement.Name,
                ["points"] = achievement.Points,
                ["target"] = achievement.Target,
                ["hidden"] = achievement.Hidden,
            };
            if (achievement.Description != null) entry["description"] = achievement.Description;
            if (achievement.Icon != null)        entry["icon"]        = achievement.Icon;
            list.Add(entry);
        }

        return CallAsync(HttpMethod.Put, "/achievements", new JsonObject { ["achievements"] = list }, cancellation);
    }

    /// <summary>Unlocks an achievement outright. Idempotent: only the first unlock fans out.</summary>
    public Task<JsonObject?> UnlockAsync(string userId, string key, CancellationToken cancellation = default)
        => CallAsync(HttpMethod.Post, AchievementPath(userId, key),
                     new JsonObject { ["unlock"] = true }, cancellation);

    /// <summary>Advances a counting achievement. It unlocks when the progress reaches its target.</summary>
    public Task<JsonObject?> ProgressAsync(string userId, string key, int increment = 1,
                                           CancellationToken cancellation = default)
        => CallAsync(HttpMethod.Post, AchievementPath(userId, key),
                     new JsonObject { ["increment"] = increment }, cancellation);

    /// <summary>Sets a counting achievement to an absolute value, for a stat the game already tracks.</summary>
    public Task<JsonObject?> SetProgressAsync(string userId, string key, int progress,
                                              CancellationToken cancellation = default)
        => CallAsync(HttpMethod.Post, AchievementPath(userId, key),
                     new JsonObject { ["progress"] = progress }, cancellation);

    private string AchievementPath(string userId, string key)
        => $"/users/{Uri.EscapeDataString(userId)}/achievements/{Uri.EscapeDataString(key)}";

    // -------------------------------------------------------------------------
    // Presence and activity
    // -------------------------------------------------------------------------

    /// <summary>
    /// Publishes presence on a player's behalf, from the server that can actually see what they
    /// are doing.
    /// </summary>
    /// <remarks>
    /// A server entry outranks the player's own and survives a tab that froze or a client that
    /// crashed, which is what stops a friends list showing somebody in a match that ended twenty
    /// minutes ago. It expires after 120 seconds, so re-send it at most every 60 while the
    /// player is in a room, and clear it when they leave.
    /// </remarks>
    public Task<JsonObject?> PublishPresenceAsync(string userId, string state, string detail = "",
                                                  string? joinCode = null, bool joinable = true,
                                                  int? players = null, int? max = null,
                                                  CancellationToken cancellation = default)
    {
        var presence = new JsonObject { ["state"] = Cap(state, 40), ["detail"] = Cap(detail, 80) };

        if (joinCode is { Length: > 0 })
        {
            var join = new JsonObject { ["joinCode"] = joinCode.ToUpperInvariant(), ["joinable"] = joinable };
            if (players.HasValue) join["players"] = players.Value;
            if (max.HasValue)     join["max"]     = max.Value;
            presence["join"] = join;
        }

        return CallAsync(HttpMethod.Post, $"/users/{Uri.EscapeDataString(userId)}/presence", presence, cancellation);
    }

    /// <summary>Clears a player's server-side presence when they leave.</summary>
    public Task<JsonObject?> ClearPresenceAsync(string userId, CancellationToken cancellation = default)
        => CallAsync(HttpMethod.Delete, $"/users/{Uri.EscapeDataString(userId)}/presence", null, cancellation);

    /// <summary>Posts a line to the player's activity feed, where their friends see it.</summary>
    public Task<JsonObject?> MilestoneAsync(string userId, string title, string? detail = null,
                                            string? icon = null, CancellationToken cancellation = default)
    {
        var body = new JsonObject { ["title"] = Cap(title, 80) };
        if (detail != null) body["detail"] = Cap(detail, 160);
        if (icon != null)   body["icon"]   = Cap(icon, 300);
        return CallAsync(HttpMethod.Post, $"/users/{Uri.EscapeDataString(userId)}/activity", body, cancellation);
    }

    /// <summary>A player's friend ids — enough to answer "which of my friends are on this server?".</summary>
    public async Task<IReadOnlyList<string>> FriendIdsAsync(string userId, CancellationToken cancellation = default)
    {
        var body = await CallAsync(HttpMethod.Get, $"/users/{Uri.EscapeDataString(userId)}/friends", null, cancellation)
            .ConfigureAwait(false);

        return (body?["friends"] as JsonArray)?
            .Select(f => f?.GetValue<string>() ?? string.Empty)
            .Where(f => f.Length > 0).ToArray() ?? Array.Empty<string>();
    }

    // -------------------------------------------------------------------------
    // Party Launch
    // -------------------------------------------------------------------------

    /// <summary>
    /// The party a launch token belongs to, so a server can seat its members together.
    /// </summary>
    /// <remarks>
    /// The token is verified by the hub, not here — the party id is only read out of it to
    /// address the request. A token for another app, another party, or a holder who is not a
    /// member is refused there.
    /// </remarks>
    public async Task<JsonObject?> PartyAsync(string launchToken, CancellationToken cancellation = default)
    {
        string? party = Claim(launchToken, "pty");
        if (party == null) return null;

        var body = await CallAsync(HttpMethod.Get,
            $"/parties/{Uri.EscapeDataString(party)}?launchToken={Uri.EscapeDataString(launchToken)}",
            null, cancellation).ConfigureAwait(false);
        return body?["party"] as JsonObject;
    }

    /// <summary>
    /// Tells a party which room the host opened, from the server rather than from the host's
    /// client — the room the server made is the room the server should announce.
    /// </summary>
    public Task<JsonObject?> SetPartyRoomAsync(string launchToken, string joinCode,
                                               CancellationToken cancellation = default)
    {
        string? party = Claim(launchToken, "pty");
        if (party == null) return Task.FromResult<JsonObject?>(null);

        return CallAsync(HttpMethod.Post, $"/parties/{Uri.EscapeDataString(party)}/room",
            new JsonObject { ["launchToken"] = launchToken, ["joinCode"] = joinCode.ToUpperInvariant() },
            cancellation);
    }

    // -------------------------------------------------------------------------
    // Plumbing
    // -------------------------------------------------------------------------

    private async Task<JsonObject?> CallAsync(HttpMethod method, string path, JsonNode? body,
                                              CancellationToken cancellation)
    {
        if (!Enabled) return null;

        try
        {
            using var request = new HttpRequestMessage(method, _api + path);
            request.Headers.TryAddWithoutValidation("X-DG-App", _app);
            request.Headers.TryAddWithoutValidation("X-DG-App-Secret", _secret);
            if (body != null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);

            // 204 is the documented success for a write with nothing to say back.
            if ((int)response.StatusCode == 204) return new JsonObject();

            string text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            var json = Parse(text);

            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(
                    $"[DarksGames] {method} {path} -> {(int)response.StatusCode} {json?["error"]?.GetValue<string>() ?? ""}");
                return null;
            }
            return json;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"[DarksGames] {method} {path} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>One claim out of a token, without verifying it. Only used to address a request.</summary>
    internal static string? Claim(string token, string name)
    {
        var claims = DarksGamesAccount.ReadClaims(token);
        return claims?[name]?.GetValue<string>();
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
