using SexyBiscuit.Engine.Code;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The parsers behind the editor's run_tests tool: a test run's thousands of lines become five
/// numbers and the names that failed.
/// </summary>
public class TestRunnerTests
{
    [Fact]
    public void ParseDotnetReadsTheSummaryAndTheFailingNamesWithTheirFirstMessageLine()
    {
        var lines = new[]
        {
            "  Determining projects to restore...",
            "[xUnit.net 00:00:01.14]     SexyBiscuit.Tests.Foo.Bar [FAIL]",
            "  Failed SexyBiscuit.Tests.Foo.Bar [12 ms]",
            "  Error Message:",
            "   Assert.Equal() Failure: Values differ",
            "  Stack Trace:",
            "     at SexyBiscuit.Tests.Foo.Bar() in Foo.cs:line 10",
            "  Failed SexyBiscuit.Tests.Foo.Baz [3 ms]",
            "  Error Message:",
            "",
            "   boom",
            "",
            "Failed!  - Failed:     2, Passed:   190, Skipped:     1, Total:   193, Duration: 3 s - SexyBiscuit.Tests.dll (net8.0)",
        };

        var summary = TestResultParsers.ParseDotnet(lines);

        Assert.Equal(2, summary.Failed);
        Assert.Equal(190, summary.Passed);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(193, summary.Total);
        Assert.Equal("3 s", summary.Duration);
        Assert.False(summary.Success);
        Assert.Equal(2, summary.Failing.Count);
        Assert.Equal("SexyBiscuit.Tests.Foo.Bar", summary.Failing[0].Name);
        Assert.Equal("Assert.Equal() Failure: Values differ", summary.Failing[0].Message);
        Assert.Equal("boom", summary.Failing[1].Message);
        Assert.Empty(summary.BuildErrors);
    }

    [Fact]
    public void ParseDotnetReportsAPassAndABuildFailure()
    {
        var passed = TestResultParsers.ParseDotnet(new[] { "Passed!  - Failed:     0, Passed:   500, Skipped:     0, Total:   500, Duration: 4 s - SexyBiscuit.Tests.dll (net8.0)" });
        Assert.True(passed.Success);
        Assert.Equal(500, passed.Total);
        Assert.Empty(passed.Failing);

        var broken = TestResultParsers.ParseDotnet(new[]
        {
            "/repo/SexyBiscuit.Tests/X.cs(12,3): error CS1002: ; expected [/repo/SexyBiscuit.Tests/SexyBiscuit.Tests.csproj]",
            "Build FAILED.",
        });
        Assert.True(broken.Incomplete);
        Assert.False(broken.Success);
        Assert.Single(broken.BuildErrors);
        Assert.Contains("CS1002", broken.BuildErrors[0]);
    }

    [Fact]
    public void ParseNodeReadsBothReporters()
    {
        var tap = TestResultParsers.ParseNode(new[]
        {
            "TAP version 13",
            "ok 1 - a passing test",
            "not ok 2 - the failing one",
            "  ---",
            "  error: 'boom'",
            "  ...",
            "# tests 2",
            "# pass 1",
            "# fail 1",
            "# duration_ms 151.5",
        });
        Assert.Equal(2, tap.Total);
        Assert.Equal(1, tap.Failed);
        Assert.Equal("the failing one", Assert.Single(tap.Failing).Name);
        Assert.Equal("0.2 s", tap.Duration);

        var spec = TestResultParsers.ParseNode(new[]
        {
            "✔ a passing test (1.2ms)",
            "✖ the failing one (3.4ms)",
            "ℹ tests 2",
            "ℹ pass 1",
            "ℹ fail 1",
            "✖ failing tests:",
            "✖ the failing one (3.4ms)",
        });
        Assert.Equal(1, spec.Failed);
        Assert.Equal("the failing one", Assert.Single(spec.Failing).Name);
        Assert.False(spec.Success);

        Assert.True(TestResultParsers.ParseNode(new[] { "ℹ tests 3", "ℹ pass 3", "ℹ fail 0" }).Success);
    }
}
