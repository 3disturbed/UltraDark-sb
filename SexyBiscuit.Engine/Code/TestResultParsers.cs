using System.Text.RegularExpressions;

namespace SexyBiscuit.Engine.Code;

/// <summary>One failing test: its name and, when the runner printed one, the first line of its message.</summary>
public sealed record FailingTest(string Name, string? Message);

/// <summary>What a test run came to, in the few numbers an agent needs.</summary>
public sealed record TestSummary(
    int                    Passed,
    int                    Failed,
    int                    Skipped,
    int                    Total,
    string?                Duration,
    IReadOnlyList<FailingTest> Failing,
    IReadOnlyList<string>  BuildErrors)
{
    /// <summary>True when the run completed with no failures and no build errors.</summary>
    public bool Success => Failed == 0 && BuildErrors.Count == 0 && Total > 0;

    /// <summary>A run that produced no summary at all — the runner crashed, or the project did not build.</summary>
    public bool Incomplete => Total == 0;
}

/// <summary>
/// Reads test runner output into a <see cref="TestSummary"/>: the counts, the failing names and
/// their first message line, and nothing else.
/// </summary>
/// <remarks>
/// A test run prints thousands of lines; the agent that asked for it needs five numbers and the
/// names that failed. The parsers work on the runners' console output so the tool can stream
/// it, and cover both engines' runners: <c>dotnet test</c>'s console logger and node's built-in
/// runner in either of its two reporters.
/// </remarks>
public static class TestResultParsers
{
    // "Failed!  - Failed:     2, Passed:   190, Skipped:     0, Total:   192, Duration: 3 s - SexyBiscuit.Tests.dll (net8.0)"
    private static readonly Regex DotnetSummary = new(
        @"(?:Passed|Failed)!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)(?:,\s+Duration:\s+([^-]+?))?\s*(?:-|$)",
        RegexOptions.Compiled);

    // "  Failed SexyBiscuit.Tests.Foo.Bar [12 ms]"
    private static readonly Regex DotnetFailed = new(@"^\s+Failed\s+(\S+)(?:\s+\[[^\]]*\])?\s*$", RegexOptions.Compiled);

    // "  Error Message:" then the message on the next non-empty line
    private static readonly Regex DotnetErrorMessage = new(@"^\s+Error Message:\s*$", RegexOptions.Compiled);

    // "/path/File.cs(12,3): error CS1002: ; expected [/path/Proj.csproj]"
    private static readonly Regex BuildError = new(@"error\s+(?:CS|MSB|NU)\d+", RegexOptions.Compiled);

    // node --test: "# tests 93" / "# pass 92" / "# fail 1" (TAP) or "ℹ tests 93" (spec reporter)
    private static readonly Regex NodeCount = new(@"^(?:#|ℹ)\s+(tests|pass|fail|skipped)\s+(\d+)", RegexOptions.Compiled);

    // TAP: "not ok 12 - the name"; spec: "✖ the name (1.2ms)"
    private static readonly Regex NodeNotOk = new(@"^not ok\s+\d+\s+-\s+(.+?)\s*(?:#.*)?$", RegexOptions.Compiled);
    private static readonly Regex NodeCross = new(@"^\s*✖\s+(.+?)(?:\s+\(\d+(?:\.\d+)?ms\))?\s*$", RegexOptions.Compiled);
    private static readonly Regex NodeDuration = new(@"^(?:#|ℹ)\s+duration_ms\s+([\d.]+)", RegexOptions.Compiled);

    /// <summary>Parses <c>dotnet test</c> console output.</summary>
    public static TestSummary ParseDotnet(IEnumerable<string> lines)
    {
        int passed = 0, failed = 0, skipped = 0, total = 0;
        string? duration = null;
        var failing = new List<FailingTest>();
        var buildErrors = new List<string>();
        bool summarySeen = false;
        FailingTest? pending = null;
        bool expectingMessage = false;

        foreach (var raw in lines)
        {
            string line = raw.TrimEnd();

            if (expectingMessage)
            {
                if (line.Trim().Length == 0) continue;
                if (pending != null)
                {
                    int index = failing.IndexOf(pending);
                    var withMessage = pending with { Message = line.Trim() };
                    if (index >= 0) failing[index] = withMessage;
                    pending = null;
                }
                expectingMessage = false;
                continue;
            }

            var summary = DotnetSummary.Match(line);
            if (summary.Success)
            {
                summarySeen = true;
                failed  += int.Parse(summary.Groups[1].Value);
                passed  += int.Parse(summary.Groups[2].Value);
                skipped += int.Parse(summary.Groups[3].Value);
                total   += int.Parse(summary.Groups[4].Value);
                if (summary.Groups[5].Success) duration = summary.Groups[5].Value.Trim();
                continue;
            }

            var failedMatch = DotnetFailed.Match(line);
            if (failedMatch.Success && !line.Contains("Failed!"))
            {
                pending = new FailingTest(failedMatch.Groups[1].Value, null);
                if (!failing.Any(f => f.Name == pending.Name)) failing.Add(pending);
                continue;
            }

            if (DotnetErrorMessage.IsMatch(line))
            {
                expectingMessage = true;
                continue;
            }

            if (BuildError.IsMatch(line) && !buildErrors.Contains(line.Trim()))
                buildErrors.Add(line.Trim());
        }

        // A crash before the summary leaves every count at zero; the names still say what ran.
        if (!summarySeen) total = 0;
        return new TestSummary(passed, failed, skipped, total, duration, failing, buildErrors);
    }

    /// <summary>Parses node's built-in test runner output, TAP or spec reporter.</summary>
    public static TestSummary ParseNode(IEnumerable<string> lines)
    {
        int tests = 0, pass = 0, fail = 0, skipped = 0;
        string? duration = null;
        var failing = new List<FailingTest>();
        var errors  = new List<string>();
        bool summaryList = false;   // the spec reporter repeats the failures under "✖ failing tests:"

        foreach (var raw in lines)
        {
            string line = raw.TrimEnd();

            var count = NodeCount.Match(line);
            if (count.Success)
            {
                int n = int.Parse(count.Groups[2].Value);
                switch (count.Groups[1].Value)
                {
                    case "tests":   tests = n; break;
                    case "pass":    pass = n; break;
                    case "fail":    fail = n; break;
                    case "skipped": skipped = n; break;
                }
                continue;
            }

            var seconds = NodeDuration.Match(line);
            if (seconds.Success)
            {
                duration = $"{double.Parse(seconds.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) / 1000:F1} s";
                continue;
            }

            if (line.Contains("failing tests:")) { summaryList = true; continue; }

            var notOk = NodeNotOk.Match(line);
            if (notOk.Success) { AddFailing(notOk.Groups[1].Value); continue; }

            var cross = NodeCross.Match(line);
            if (cross.Success && !summaryList) { AddFailing(cross.Groups[1].Value); continue; }

            if (line.Contains("SyntaxError") || line.Contains("Cannot find module"))
                if (!errors.Contains(line.Trim())) errors.Add(line.Trim());
        }

        return new TestSummary(pass, fail, skipped, tests, duration, failing, errors);

        void AddFailing(string name)
        {
            if (!failing.Any(f => f.Name == name)) failing.Add(new FailingTest(name, null));
        }
    }
}
