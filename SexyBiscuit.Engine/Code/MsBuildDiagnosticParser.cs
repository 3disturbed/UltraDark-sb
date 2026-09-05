using System.Text.RegularExpressions;

namespace SexyBiscuit.Engine.Code;

public enum BuildSeverity
{
    Warning,
    Error,
}

/// <summary>One compiler or MSBuild diagnostic in a form a tool can hand straight to a model.</summary>
public sealed record BuildDiagnostic(
    string?       File,
    int           Line,
    int           Column,
    string?       Code,
    BuildSeverity Severity,
    string        Message,
    string?       Project)
{
    public override string ToString()
        => $"{(File != null ? $"{File}({Line},{Column}): " : "")}{Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
}

/// <summary>
/// Parses MSBuild's canonical diagnostic lines, on every platform's path style:
/// <c>/x/Foo.cs(12,9): error CS1002: ; expected [/x/MyGame.csproj]</c>,
/// <c>C:\x\Foo.cs(12,9,12,14): warning CS0168: ...</c>, and the locationless
/// <c>MSBUILD : error MSB1009: Project file does not exist.</c>
/// </summary>
public static partial class MsBuildDiagnosticParser
{
    private static readonly Regex Line = new(
        @"^\s*(?:(?<origin>.+?)(?:\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\))?\s*:\s+)?" +
        @"(?<sev>error|warning)\s+(?<code>[A-Za-z]{1,10}\d{3,6})?\s*:\s*" +
        @"(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static BuildDiagnostic? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var match = Line.Match(line);
        if (!match.Success) return null;

        string? origin = match.Groups["origin"].Success ? match.Groups["origin"].Value.Trim() : null;
        bool hasLocation = match.Groups["line"].Success;

        // "MSBUILD : error ..." names a tool, not a file.
        string? file = origin != null && (hasLocation || origin.Contains('.') || origin.Contains(Path.DirectorySeparatorChar))
                     && !string.Equals(origin, "MSBUILD", StringComparison.OrdinalIgnoreCase)
            ? origin
            : null;

        return new BuildDiagnostic(
            File:     file,
            Line:     hasLocation ? int.Parse(match.Groups["line"].Value) : 0,
            Column:   match.Groups["col"].Success ? int.Parse(match.Groups["col"].Value) : 0,
            Code:     match.Groups["code"].Success ? match.Groups["code"].Value.ToUpperInvariant() : null,
            Severity: match.Groups["sev"].Value.Equals("error", StringComparison.OrdinalIgnoreCase) ? BuildSeverity.Error : BuildSeverity.Warning,
            Message:  match.Groups["msg"].Value.Trim(),
            Project:  match.Groups["proj"].Success ? match.Groups["proj"].Value.Trim() : null);
    }

    /// <summary>
    /// Parses every line, dropping the repeats MSBuild prints in its summary and once per
    /// target framework, in first-seen order.
    /// </summary>
    public static IReadOnlyList<BuildDiagnostic> Parse(IEnumerable<string> lines)
    {
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<BuildDiagnostic>();

        foreach (var line in lines)
        {
            var diagnostic = ParseLine(line);
            if (diagnostic == null) continue;

            string key = $"{diagnostic.File}|{diagnostic.Line}|{diagnostic.Column}|{diagnostic.Code}|{diagnostic.Message}";
            if (seen.Add(key)) result.Add(diagnostic);
        }

        return result;
    }
}
