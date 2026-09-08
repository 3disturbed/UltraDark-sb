using System.Globalization;
using System.Text.Json;

namespace SexyBiscuit.Engine.Code;

/// <summary>The most recent CI run on a branch, reduced to what a session needs before it builds on main.</summary>
public sealed record CiStatus(string Branch, string Sha, string Conclusion, DateTimeOffset UpdatedAt, string? Url)
{
    /// <summary>The first seven characters of the commit, as git prints it.</summary>
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;

    /// <summary>One line for get_context: <c>main@9f7fc4f success 2h ago</c>.</summary>
    public string Summary(DateTimeOffset now)
        => $"{Branch}@{ShortSha} {Conclusion} {GitHubActionsStatus.Age(now - UpdatedAt)} ago";
}

/// <summary>
/// Asks the GitHub CLI for the last run of the CI workflow on a branch. <c>gh</c> is optional:
/// without it, or without a network, the answer is null and get_context simply has no ci line.
/// </summary>
/// <remarks>
/// Seven pushes once went out under a CI that failed at startup with no jobs, and nothing told the
/// session, because nothing in its context said what CI thought of main. This is that line.
/// </remarks>
public static class GitHubActionsStatus
{
    /// <summary>The workflow file the gate lives in.</summary>
    public const string Workflow = "ci.yml";

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The gh binary, or null when it is not installed. Homebrew's folders are checked for an editor launched from Finder.</summary>
    public static string? FindGh() => ProcessRunner.FindOnPath("gh", "/opt/homebrew/bin", "/usr/local/bin");

    /// <summary>The last run of <see cref="Workflow"/> on <paramref name="branch"/>, or null when gh is missing, offline, or the query fails.</summary>
    public static async Task<CiStatus?> QueryAsync(string repoRoot, string branch = "main", CancellationToken cancellation = default)
    {
        string? gh = FindGh();
        if (gh == null) return null;

        var args = new[]
        {
            "run", "list", "--workflow", Workflow, "--branch", branch, "--limit", "1",
            "--json", "conclusion,status,headSha,headBranch,updatedAt,url",
        };

        try
        {
            var run = await ProcessRunner.RunAsync(gh, args, repoRoot, QueryTimeout, null, null, cancellation).ConfigureAwait(false);
            return run.Success ? Parse(string.Join("\n", run.Lines)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses gh's JSON array. No run yet is null; a run still going reports its status
    /// (<c>in_progress</c>, <c>queued</c>) in place of the conclusion it does not have.
    /// </summary>
    public static CiStatus? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;

            var run = root[0];
            string conclusion = Text(run, "conclusion") is { Length: > 0 } done ? done
                              : Text(run, "status") is { Length: > 0 } status ? status
                              : "unknown";
            var updated = DateTimeOffset.TryParse(Text(run, "updatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                ? when
                : DateTimeOffset.MinValue;

            return new CiStatus(Text(run, "headBranch") ?? "", Text(run, "headSha") ?? "", conclusion, updated, Text(run, "url"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>An age in its largest whole unit: <c>35s</c>, <c>12m</c>, <c>2h</c>, <c>3d</c>.</summary>
    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalMinutes < 1) return $"{(int)age.TotalSeconds}s";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
