using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SexyBiscuit.Engine.Code;

namespace SexyBiscuit.Engine.CookieJar;

// -----------------------------------------------------------------------------
// Problems
// -----------------------------------------------------------------------------

/// <summary>How much a <see cref="CookieProblem"/> matters.</summary>
public enum CookieSeverity
{
    /// <summary>Worth saying, but the cookie still loads and installs.</summary>
    Warning,

    /// <summary>The cookie is unusable and is kept out of the catalogue.</summary>
    Error,
}

/// <summary>One thing wrong with a cookie, a jar or an install, in a form a person can read.</summary>
/// <remarks>
/// Problems are returned, never thrown. A jar with one broken cookie in it still has to produce a
/// catalogue: an exception here would blank the whole library because of a stray comma.
/// </remarks>
public sealed record CookieProblem(CookieSeverity Severity, string Subject, string Message)
{
    public override string ToString() => $"{Severity}: {Subject} — {Message}";
}

// -----------------------------------------------------------------------------
// Manifest
// -----------------------------------------------------------------------------

/// <summary>One entry in a cookie's optional explicit file map.</summary>
public sealed class CookieFileRule
{
    /// <summary>A path or glob relative to the cookie folder, such as <c>Source/**/*.cs</c>.</summary>
    [JsonPropertyName("from")] public string From { get; set; } = "";

    /// <summary>Where it lands, relative to the project root. A trailing slash means a directory.</summary>
    [JsonPropertyName("to")] public string To { get; set; } = "";
}

/// <summary>The range of engine versions a cookie declares itself good for.</summary>
public sealed class CookieEngineRange
{
    [JsonPropertyName("min")] public string? Min { get; set; }
    [JsonPropertyName("max")] public string? Max { get; set; }
}

/// <summary>
/// What a cookie adds to a project. Searchable without opening a single source file, which is the
/// point: this is also what lets a type-name collision be found before anything is written.
/// </summary>
public sealed class CookieProvides
{
    [JsonPropertyName("components")]   public List<string> Components   { get; set; } = new();
    [JsonPropertyName("actorClasses")] public List<string> ActorClasses { get; set; } = new();
    [JsonPropertyName("scripts")]      public List<string> Scripts      { get; set; } = new();
    [JsonPropertyName("prefabs")]      public List<string> Prefabs      { get; set; } = new();
    [JsonPropertyName("tools")]        public List<string> Tools        { get; set; } = new();

    /// <summary>Every C# type name the cookie declares, which is what can collide.</summary>
    public IEnumerable<string> TypeNames => Components.Concat(ActorClasses);
}

/// <summary><c>cookie.json</c>: everything about a cookie that can be known without reading its files.</summary>
public sealed class CookieManifest
{
    /// <summary>The manifest format. Only 1 exists; a newer one is refused rather than guessed at.</summary>
    [JsonPropertyName("schema")] public int Schema { get; set; } = CurrentSchema;

    /// <summary>Kebab-case, unique across the catalogue, and equal to the folder name.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Semver. Recorded in the lock file, and compared when a cookie is reinstalled.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = "0.0.0";

    /// <summary>One line. This is what a search returns, so it is what lands in an agent's context.</summary>
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";

    [JsonPropertyName("description")] public string? Description { get; set; }

    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();

    /// <summary>Which engines the cookie runs on: <c>csharp</c>, <c>js</c>, or both.</summary>
    [JsonPropertyName("engines")] public List<string> Engines { get; set; } = new();

    [JsonPropertyName("engineVersion")] public CookieEngineRange? EngineVersion { get; set; }

    /// <summary>Other cookie ids this one needs, installed first and resolved transitively.</summary>
    [JsonPropertyName("requires")] public List<string> Requires { get; set; } = new();

    /// <summary>The C# namespace the cookie's source is authored in. Defaults to <c>Cookies.{PascalId}</c>.</summary>
    [JsonPropertyName("namespace")] public string? Namespace { get; set; }

    [JsonPropertyName("provides")] public CookieProvides Provides { get; set; } = new();

    /// <summary>Overrides or extends the convention file map. Omitted by a cookie that fits the convention.</summary>
    [JsonPropertyName("files")] public List<CookieFileRule>? Files { get; set; }

    /// <summary>What to do after installing, handed straight back in the install result.</summary>
    [JsonPropertyName("nextSteps")] public List<string> NextSteps { get; set; } = new();

    // -------------------------------------------------------------------------
    // Derived
    // -------------------------------------------------------------------------

    /// <summary>The only manifest schema this engine understands.</summary>
    public const int CurrentSchema = 1;

    /// <summary>The C# engine identifier used in <see cref="Engines"/>.</summary>
    public const string EngineCSharp = "csharp";

    /// <summary>The JavaScript engine identifier used in <see cref="Engines"/>.</summary>
    public const string EngineJavaScript = "js";

    /// <summary><c>double-jump</c> becomes <c>DoubleJump</c>.</summary>
    public string PascalId => CodeProjectGenerator.SanitiseIdentifier(Id);

    /// <summary>The namespace the cookie's C# is authored in, declared or derived.</summary>
    public string EffectiveNamespace =>
        string.IsNullOrWhiteSpace(Namespace) ? "Cookies." + PascalId : Namespace!;

    /// <summary>True when the cookie declares support for <paramref name="engine"/>.</summary>
    public bool SupportsEngine(string engine)
        => Engines.Any(e => string.Equals(e, engine, StringComparison.OrdinalIgnoreCase));

    // -------------------------------------------------------------------------
    // Reading
    // -------------------------------------------------------------------------

    /// <summary>The manifest's own JSON rules. Deliberately not the scene serialiser's.</summary>
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        WriteIndented               = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    /// <summary>Parses manifest JSON. Throws <see cref="CookieException"/> on malformed input.</summary>
    public static CookieManifest Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CookieManifest>(json, Json)
                   ?? throw new CookieException("cookie.json is empty.");
        }
        catch (JsonException ex)
        {
            throw new CookieException("cookie.json is not valid JSON: " + ex.Message, ex);
        }
    }

    /// <summary>Reads and parses <c>cookie.json</c> from <paramref name="path"/>.</summary>
    public static CookieManifest Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Writes this manifest as <c>cookie.json</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    // -------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------

    private static readonly Regex IdPattern        = new("^[a-z0-9][a-z0-9-]{1,47}$", RegexOptions.Compiled);
    private static readonly Regex NamespacePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);

    /// <summary>Kebab-case, 2 to 48 characters, starting with a letter or digit.</summary>
    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && IdPattern.IsMatch(id);

    /// <summary>
    /// Everything wrong with this manifest. <paramref name="folderName"/> is checked against
    /// <see cref="Id"/>, because the folder name is how a jar addresses a cookie on disk.
    /// </summary>
    public IReadOnlyList<CookieProblem> Validate(string? folderName = null)
    {
        var problems = new List<CookieProblem>();
        string subject = string.IsNullOrEmpty(Id) ? folderName ?? "(unnamed cookie)" : Id;

        void Error(string message)   => problems.Add(new CookieProblem(CookieSeverity.Error, subject, message));
        void Warning(string message) => problems.Add(new CookieProblem(CookieSeverity.Warning, subject, message));

        if (Schema != CurrentSchema)
            Error($"schema {Schema} is not supported; this engine reads schema {CurrentSchema}.");

        if (!IsValidId(Id))
            Error("id must be kebab-case, 2 to 48 characters: lower-case letters, digits and hyphens.");
        else if (folderName != null && !string.Equals(Id, folderName, StringComparison.Ordinal))
            Error($"id '{Id}' does not match its folder name '{folderName}'.");

        if (string.IsNullOrWhiteSpace(Name)) Error("name is required.");

        if (CookieVersion.TryParse(Version, out _) is false)
            Error($"version '{Version}' is not a semantic version such as 1.2.0.");

        if (string.IsNullOrWhiteSpace(Summary))
            Error("summary is required: one line saying what this cookie is.");
        else if (Summary.Contains('\n') || Summary.Length > 200)
            Warning("summary should be a single line under 200 characters; it is what a search returns.");

        if (Engines.Count == 0)
            Error("engines must list at least one of 'csharp' or 'js'.");
        foreach (string engine in Engines)
            if (engine is not (EngineCSharp or EngineJavaScript))
                Error($"unknown engine '{engine}'; expected 'csharp' or 'js'.");

        foreach (string required in Requires)
        {
            if (!IsValidId(required)) Error($"requires '{required}' is not a valid cookie id.");
            else if (required == Id)  Error("a cookie cannot require itself.");
        }

        if (Namespace != null && !NamespacePattern.IsMatch(Namespace))
            Error($"namespace '{Namespace}' is not a valid C# namespace.");

        if (EngineVersion?.Min is { } min && !CookieVersion.TryParse(min, out _))
            Error($"engineVersion.min '{min}' is not a semantic version.");
        if (EngineVersion?.Max is { } max && !CookieVersion.TryParse(max, out _))
            Error($"engineVersion.max '{max}' is not a semantic version.");

        if (Files != null)
        {
            foreach (var rule in Files)
            {
                if (string.IsNullOrWhiteSpace(rule.From)) Error("a files entry has no 'from'.");
                if (string.IsNullOrWhiteSpace(rule.To))   Error("a files entry has no 'to'.");
                else if (Path.IsPathRooted(rule.To))      Error($"files 'to' must be relative to the project root: '{rule.To}'.");
            }
        }

        return problems;
    }
}

// -----------------------------------------------------------------------------
// Versions
// -----------------------------------------------------------------------------

/// <summary>
/// Just enough semantic version to order two cookies with the same id. Pre-release and build
/// metadata are parsed and then ignored for ordering, which is all a private library needs.
/// </summary>
public readonly record struct CookieVersion(int Major, int Minor, int Patch) : IComparable<CookieVersion>
{
    public static bool TryParse(string? text, out CookieVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Drop pre-release and build metadata: 1.2.0-beta+7 orders as 1.2.0.
        string core = text.Split('-', '+')[0];
        string[] parts = core.Split('.');
        if (parts.Length is < 1 or > 3) return false;

        int[] numbers = new int[3];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0) return false;

        version = new CookieVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>Parses, or returns 0.0.0 for anything unparseable.</summary>
    public static CookieVersion Parse(string? text) => TryParse(text, out var v) ? v : default;

    public int CompareTo(CookieVersion other)
    {
        int major = Major.CompareTo(other.Major); if (major != 0) return major;
        int minor = Minor.CompareTo(other.Minor); if (minor != 0) return minor;
        return Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

// -----------------------------------------------------------------------------
// Exception
// -----------------------------------------------------------------------------

/// <summary>A cookie could not be read or applied. Carries a hint the MCP layer surfaces.</summary>
public sealed class CookieException : Exception
{
    public CookieException(string message, Exception? inner = null) : base(message, inner) { }

    public CookieException(string message, string hint) : base(message) => Hint = hint;

    /// <summary>What to do about it, when there is something useful to say.</summary>
    public string? Hint { get; }
}
