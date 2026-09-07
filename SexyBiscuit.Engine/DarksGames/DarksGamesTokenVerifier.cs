using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.DarksGames;

// ---------------------------------------------------------------------------
// DarksGamesTokenVerifier.cs
// Verifying a hub access token, so a listen server knows who joined.
// ---------------------------------------------------------------------------

/// <summary>Who a verified token says its holder is.</summary>
/// <param name="UserId">The hub's stable id, <c>u_…</c>. The subject of every s2s call.</param>
/// <param name="Name">The display name, truncated as the hub truncates it.</param>
/// <param name="Handle">The unique <c>Darko#4821</c>, when the token carries one.</param>
public sealed record DarksGamesIdentity(string UserId, string? Name, string? Handle);

/// <summary>
/// Verifies RS256 access tokens minted by the Darks Games hub against its JWKS.
/// </summary>
/// <remarks>
/// <para>
/// A game server that lets players sign in has to check the token itself: the client
/// sends it in the hello, and the client is the one thing that cannot be trusted. This is
/// the C# counterpart of the <c>dgVerify.js</c> every browser title copies, and it makes
/// the same checks in the same order.
/// </para>
/// <para>
/// Failure is never fatal to a session. A player whose token does not verify plays as a
/// guest, because an outage at the hub must not stop people playing.
/// </para>
/// </remarks>
public sealed class DarksGamesTokenVerifier : IDisposable
{
    /// <summary>Where the hub publishes its signing keys.</summary>
    public const string DefaultJwksUrl = "https://darksgames.app/api/v1/jwks";

    /// <summary>The issuer every hub token carries.</summary>
    public const string DefaultIssuer = "https://darksgames.app";

    private readonly HttpClient _http;
    private readonly bool       _ownsHttp;
    private readonly string     _jwksUrl;
    private readonly string     _issuer;
    private readonly string     _audience;
    private readonly TimeSpan   _cacheFor;
    private readonly Func<DateTimeOffset> _now;

    private readonly SemaphoreSlim                  _refreshLock = new(1, 1);
    private Dictionary<string, RSA>                 _keys        = new();
    private DateTimeOffset                          _fetchedAt   = DateTimeOffset.MinValue;

    /// <param name="audience">This game's catalogue slug. A token for another app is refused.</param>
    /// <param name="jwksUrl">Overridable for a staging hub.</param>
    /// <param name="issuer">Overridable for a staging hub.</param>
    /// <param name="handler">Injected by tests so no socket is opened.</param>
    /// <param name="now">Injected by tests so expiry can be exercised.</param>
    /// <param name="cacheFor">How long a fetched key set is trusted before it is re-fetched.</param>
    public DarksGamesTokenVerifier(
        string audience,
        string? jwksUrl = null,
        string? issuer = null,
        HttpMessageHandler? handler = null,
        Func<DateTimeOffset>? now = null,
        TimeSpan? cacheFor = null)
    {
        _audience = audience;
        _jwksUrl  = jwksUrl ?? DefaultJwksUrl;
        _issuer   = issuer  ?? DefaultIssuer;
        _cacheFor = cacheFor ?? TimeSpan.FromMinutes(5);
        _now      = now ?? (() => DateTimeOffset.UtcNow);
        _ownsHttp = handler == null;
        _http     = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>
    /// Verifies a token and returns who it belongs to, or <c>null</c> when it does not verify.
    /// </summary>
    /// <remarks>
    /// Returning null rather than throwing is deliberate: every call site wants "guest", and a
    /// verifier that throws makes an unauthenticated join look like a server fault.
    /// </remarks>
    public async Task<DarksGamesIdentity?> VerifyAsync(string? token, CancellationToken cancellation = default)
    {
        try
        {
            return await VerifyOrThrowAsync(token, cancellation).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The same check, with the reason. For diagnostics and for tests.</summary>
    public async Task<DarksGamesIdentity> VerifyOrThrowAsync(string? token, CancellationToken cancellation = default)
    {
        string[] parts = (token ?? string.Empty).Split('.');
        if (parts.Length != 3) throw new InvalidOperationException("malformed token");

        var header = ParseSegment(parts[0]);
        if (header?["alg"]?.GetValue<string>() != "RS256")
            throw new InvalidOperationException("unexpected algorithm");

        string kid = header["kid"]?.GetValue<string>()
            ?? throw new InvalidOperationException("token names no key");

        var key = await KeyForAsync(kid, cancellation).ConfigureAwait(false);

        byte[] signed    = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        byte[] signature = FromBase64Url(parts[2]);
        if (!key.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidOperationException("bad signature");

        var claims = ParseSegment(parts[1]) ?? throw new InvalidOperationException("no claims");

        // A party launch token is signed by the same key, with the same issuer and the same
        // audience, and carries typ:"dg-party". Without this check it would pass as a player
        // identity -- which is the one mistake the hub's own guide calls out by name.
        if (claims["typ"] != null) throw new InvalidOperationException("not an access token");

        long now = _now().ToUnixTimeSeconds();
        if (claims["exp"]?.GetValue<long>() is { } exp && exp < now)
            throw new InvalidOperationException("expired");
        if (claims["nbf"]?.GetValue<long>() is { } nbf && nbf > now + 30)
            throw new InvalidOperationException("not yet valid");
        if (claims["iss"]?.GetValue<string>() != _issuer)
            throw new InvalidOperationException("wrong issuer");
        if (!Audiences(claims).Contains(_audience))
            throw new InvalidOperationException("wrong audience");

        string subject = claims["sub"]?.GetValue<string>()
            ?? throw new InvalidOperationException("no subject");

        string? name = claims["name"]?.GetValue<string>()
                    ?? claims["username"]?.GetValue<string>()
                    ?? claims["display_name"]?.GetValue<string>();
        if (name is { Length: > 18 }) name = name[..18];

        return new DarksGamesIdentity(subject, string.IsNullOrEmpty(name) ? null : name,
                                      claims["handle"]?.GetValue<string>());
    }

    // -------------------------------------------------------------------------
    // Keys
    // -------------------------------------------------------------------------

    private async Task<RSA> KeyForAsync(string kid, CancellationToken cancellation)
    {
        if (IsFresh() && _keys.TryGetValue(kid, out var cached)) return cached;

        await _refreshLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while this one waited.
            if (IsFresh() && _keys.TryGetValue(kid, out var current)) return current;

            string body = await _http.GetStringAsync(_jwksUrl, cancellation).ConfigureAwait(false);
            var keys = new Dictionary<string, RSA>(StringComparer.Ordinal);

            foreach (var entry in JsonNode.Parse(body)?["keys"] as JsonArray ?? new JsonArray())
            {
                string? id = entry?["kid"]?.GetValue<string>();
                string? n  = entry?["n"]?.GetValue<string>();
                string? e  = entry?["e"]?.GetValue<string>();
                if (id == null || n == null || e == null) continue;

                var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters { Modulus = FromBase64Url(n), Exponent = FromBase64Url(e) });
                keys[id] = rsa;
            }

            foreach (var old in _keys.Values) old.Dispose();
            _keys      = keys;
            _fetchedAt = _now();
        }
        finally
        {
            _refreshLock.Release();
        }

        return _keys.TryGetValue(kid, out var found)
            ? found
            : throw new InvalidOperationException($"unknown key '{kid}'");
    }

    private bool IsFresh() => _now() - _fetchedAt < _cacheFor;

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static JsonObject? ParseSegment(string segment)
    {
        try { return JsonNode.Parse(Encoding.UTF8.GetString(FromBase64Url(segment))) as JsonObject; }
        catch (JsonException) { return null; }
        catch (FormatException) { return null; }
    }

    private static IEnumerable<string> Audiences(JsonObject claims) => claims["aud"] switch
    {
        JsonArray many => many.Select(a => a?.GetValue<string>() ?? string.Empty),
        { } one        => new[] { one.GetValue<string>() },
        _              => Array.Empty<string>(),
    };

    /// <summary>base64url, which is base64 with two characters swapped and the padding dropped.</summary>
    internal static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values) key.Dispose();
        _keys.Clear();
        _refreshLock.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
