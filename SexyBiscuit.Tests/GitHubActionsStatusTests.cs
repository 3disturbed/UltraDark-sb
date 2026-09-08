using SexyBiscuit.Engine.Code;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The parser behind get_context's <c>ci</c> line: gh's JSON for the last run on main becomes
/// one short line, so a session can see a red main before it builds on it.
/// </summary>
public class GitHubActionsStatusTests
{
    [Fact]
    public void ACompletedRunBecomesOneLineWithTheShortShaAndItsAge()
    {
        const string json = """
            [{"conclusion":"success","status":"completed","headSha":"9f7fc4f2984d85dcd7ed8b6e86f611895aa39d85",
              "headBranch":"main","updatedAt":"2026-09-08T03:10:00Z","url":"https://github.com/x/y/actions/runs/1"}]
            """;

        var status = GitHubActionsStatus.Parse(json);

        Assert.NotNull(status);
        Assert.Equal("main@9f7fc4f success 2h ago", status!.Summary(new DateTimeOffset(2026, 9, 8, 5, 10, 0, TimeSpan.Zero)));
        Assert.Equal("https://github.com/x/y/actions/runs/1", status.Url);
    }

    [Fact]
    public void ARunStillGoingReportsItsStatusWhereTheConclusionWouldBe()
    {
        // gh prints an empty conclusion while a run is in progress; "unknown" would hide that it is running.
        const string json = """[{"conclusion":"","status":"in_progress","headSha":"abcdef0123","headBranch":"main","updatedAt":"2026-09-08T05:09:30Z","url":null}]""";

        var status = GitHubActionsStatus.Parse(json)!;

        Assert.Equal("main@abcdef0 in_progress 30s ago", status.Summary(new DateTimeOffset(2026, 9, 8, 5, 10, 0, TimeSpan.Zero)));
        Assert.Null(status.Url);
    }

    [Fact]
    public void AStartupFailureIsReportedAsSuchNotAsSuccess()
    {
        const string json = """[{"conclusion":"startup_failure","status":"completed","headSha":"9f7fc4f2","headBranch":"main","updatedAt":"2026-09-08T03:07:37Z","url":"u"}]""";

        Assert.Equal("startup_failure", GitHubActionsStatus.Parse(json)!.Conclusion);
    }

    [Fact]
    public void NoRunAndGarbageAreBothNullRatherThanAnException()
    {
        Assert.Null(GitHubActionsStatus.Parse("[]"));
        Assert.Null(GitHubActionsStatus.Parse("not json"));
        Assert.Null(GitHubActionsStatus.Parse("{\"unexpected\":true}"));
    }

    [Theory]
    [InlineData(35, "35s")]
    [InlineData(12 * 60, "12m")]
    [InlineData(2 * 3600 + 59 * 60, "2h")]
    [InlineData(3 * 86400 + 5, "3d")]
    [InlineData(-10, "0s")]
    public void AgeReadsInItsLargestWholeUnit(int seconds, string expected)
        => Assert.Equal(expected, GitHubActionsStatus.Age(TimeSpan.FromSeconds(seconds)));
}
