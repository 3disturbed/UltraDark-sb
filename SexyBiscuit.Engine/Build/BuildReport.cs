using System.Text.Json;
using System.Text.Json.Serialization;

namespace SexyBiscuit.Engine.Build;

/// <summary>One target's outcome in a <see cref="BuildReport"/>.</summary>
public sealed record TargetReport
{
    public string       Platform     { get; init; } = "";
    public string?      Rid          { get; init; }
    public bool         Success      { get; init; }
    public double       Seconds      { get; init; }
    public string?      OutputPath   { get; init; }
    public string?      Executable   { get; init; }
    public string?      Archive      { get; init; }
    public long         ArchiveBytes { get; init; }
    public List<string> Errors       { get; init; } = new();
    public string?      UploadUrl    { get; init; }
    public int?         UploadStatus { get; init; }
}

/// <summary>
/// What a multi-target export produced, in a form an agent reads in one glance and a CI job
/// writes beside the artifacts.
/// </summary>
/// <remarks>
/// The build log is thousands of lines that nobody reads unless something failed. The report
/// is one line per target — platform, ok or failed, seconds, archive and size — and the full
/// error list only for a target that failed. <c>html5/tools/export.js</c> writes the same
/// shape for a web-only build.
/// </remarks>
public sealed record BuildReport
{
    public string  AppName       { get; init; } = "";
    public string  Version       { get; init; } = "";
    public string  Configuration { get; init; } = "";
    public string? GitSha        { get; init; }
    public DateTime BuiltUtc     { get; init; } = DateTime.UtcNow;
    public List<TargetReport> Targets { get; init; } = new();

    [JsonIgnore] public bool Success => Targets.Count > 0 && Targets.All(t => t.Success);

    /// <summary>The default file name beside the archives.</summary>
    public const string FileName = "build-report.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented          = true,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One line per target, then the errors of any that failed.</summary>
    public IEnumerable<string> ToSummaryLines()
    {
        foreach (var target in Targets)
        {
            string name    = (target.Rid ?? target.Platform.ToLowerInvariant()).PadRight(10);
            string verdict = target.Success ? "ok    " : "FAILED";
            string seconds = $"{target.Seconds,7:F1}s";
            string artifact = target.Archive != null
                ? $"{Path.GetFileName(target.Archive)} ({FormatBytes(target.ArchiveBytes)})"
                : target.OutputPath ?? string.Empty;
            string upload = target.UploadUrl != null ? $"  -> {target.UploadUrl}" : target.UploadStatus is { } s ? $"  upload HTTP {s}" : string.Empty;
            yield return $"{name} {verdict} {seconds}  {artifact}{upload}";
        }

        foreach (var target in Targets.Where(t => !t.Success))
        {
            foreach (var error in target.Errors.Take(5))
                yield return $"  {target.Platform}: {error}";
            if (target.Errors.Count > 5)
                yield return $"  {target.Platform}: … {target.Errors.Count - 5} more";
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson() + "\n", new System.Text.UTF8Encoding(false));
    }

    public static BuildReport Load(string path)
        => JsonSerializer.Deserialize<BuildReport>(File.ReadAllText(path), Options)
           ?? throw new InvalidDataException($"BuildReport: could not parse '{path}'.");

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes / 1024.0 / 1024.0:F1} MB";
    }
}
