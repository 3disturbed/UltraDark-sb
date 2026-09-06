using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SexyBiscuit.Engine.CookieJar;

/// <summary>One file an install wrote, and what it looked like when it was written.</summary>
public sealed record InstalledFile(
    [property: JsonPropertyName("path")]   string Path,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>A cookie a project has installed, recorded well enough to remove it again exactly.</summary>
public sealed class InstalledCookie
{
    [JsonPropertyName("id")]           public string Id           { get; set; } = "";
    [JsonPropertyName("version")]      public string Version      { get; set; } = "0.0.0";
    [JsonPropertyName("name")]         public string Name         { get; set; } = "";

    /// <summary>The jar's name, never its path: this file is committed and a path is per machine.</summary>
    [JsonPropertyName("jar")]          public string Jar          { get; set; } = "";

    [JsonPropertyName("sourceCommit")] public string? SourceCommit { get; set; }
    [JsonPropertyName("installedUtc")] public DateTime InstalledUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("namespace")]    public string Namespace    { get; set; } = "";
    [JsonPropertyName("engines")]      public List<string> Engines { get; set; } = new();

    /// <summary>Copied from the manifest, so a collision check needs no catalogue.</summary>
    [JsonPropertyName("provides")]     public CookieProvides Provides { get; set; } = new();

    /// <summary>Other cookies this one needs, so an uninstall can refuse to break them.</summary>
    [JsonPropertyName("requires")]     public List<string> Requires { get; set; } = new();

    [JsonPropertyName("files")]        public List<InstalledFile> Files { get; set; } = new();

    /// <summary>Directories the install created, removed bottom-up and only when empty.</summary>
    [JsonPropertyName("directories")]  public List<string> Directories { get; set; } = new();
}

/// <summary>
/// <c>CookieJar.lock.json</c>: what this project has installed. It sits at the project root and is
/// meant to be committed, which is why it is not in <c>.sexybiscuit/</c> — that folder is
/// gitignored, so the record would vanish the moment somebody cloned the game.
/// </summary>
public sealed class CookieLockFile
{
    /// <summary>The file name, at the project root beside the <c>.sbproject</c>.</summary>
    public const string FileName = "CookieJar.lock.json";

    [JsonPropertyName("schema")]  public int Schema { get; set; } = 1;
    [JsonPropertyName("cookies")] public List<InstalledCookie> Cookies { get; set; } = new();

    /// <summary>Problems met while reading, so a corrupt file is reported rather than thrown.</summary>
    [JsonIgnore] public IReadOnlyList<CookieProblem> Problems { get; private set; } = Array.Empty<CookieProblem>();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented               = true,
    };

    /// <summary>The lock file's path for a project root.</summary>
    public static string PathFor(string projectRoot) => System.IO.Path.Combine(projectRoot, FileName);

    /// <summary>
    /// Reads the lock file. A missing one is an empty lock; an unreadable one is an empty lock plus
    /// a problem, because refusing to open the project over a stray comma helps nobody.
    /// </summary>
    public static CookieLockFile Load(string projectRoot)
    {
        string path = PathFor(projectRoot);
        if (!File.Exists(path)) return new CookieLockFile();

        try
        {
            var loaded = JsonSerializer.Deserialize<CookieLockFile>(File.ReadAllText(path), Json);
            if (loaded == null) throw new JsonException("the file is empty.");
            return loaded;
        }
        catch (Exception ex)
        {
            return new CookieLockFile
            {
                Problems = new[]
                {
                    new CookieProblem(CookieSeverity.Warning, FileName,
                                      "could not be read, so no cookie is recorded as installed: " + ex.Message),
                },
            };
        }
    }

    /// <summary>Writes the lock file, or deletes it when nothing is installed.</summary>
    public void Save(string projectRoot)
    {
        string path = PathFor(projectRoot);

        if (Cookies.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        Cookies.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public InstalledCookie? Find(string? id)
        => id == null ? null : Cookies.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    /// <summary>Adds or replaces the record for a cookie.</summary>
    public void Set(InstalledCookie entry)
    {
        Remove(entry.Id);
        Cookies.Add(entry);
    }

    public bool Remove(string id)
        => Cookies.RemoveAll(c => string.Equals(c.Id, id, StringComparison.Ordinal)) > 0;

    /// <summary>Installed cookies that need <paramref name="id"/>, which an uninstall must not orphan.</summary>
    public IReadOnlyList<string> Dependents(string id)
        => Cookies.Where(c => c.Requires.Contains(id, StringComparer.Ordinal))
                  .Select(c => c.Id)
                  .ToList();

    /// <summary>Lower-case hex SHA-256 of a file, or null when it is gone.</summary>
    public static string? HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
