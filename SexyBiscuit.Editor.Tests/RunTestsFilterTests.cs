using SexyBiscuit.Editor.GameCode;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Mcp;
using Xunit;

namespace SexyBiscuit.Editor.Tests;

/// <summary>
/// run_tests' html5 filter: a filter that names files runs those files, and one that names
/// nothing is an error rather than a silent run of zero tests.
/// </summary>
/// <remarks>
/// The JavaScript suite is three seconds whole and a fraction of that per area, so the difference
/// is what an agent is willing to run between edits. A filter matching no file used to be
/// impossible to express; matching none now must fail loudly, because a run of zero tests reads
/// exactly like a green one.
/// </remarks>
public class RunTestsFilterTests
{
    private static string Html5Root
    {
        get
        {
            var repo = EngineRepoLocator.Find();
            Assert.NotNull(repo);
            return Path.Combine(repo!.Root, "html5");
        }
    }

    [Theory]
    [InlineData("ui*", true)]
    [InlineData("tests/net.test.js", true)]
    [InlineData("net.test.js", true)]
    [InlineData("builds a canvas", false)]
    [InlineData("focus", false)]
    public void AFilterIsReadAsFilesOnlyWhenItLooksLikeThem(string filter, bool expected)
        => Assert.Equal(expected, GameCodeTools.IsFileFilter(filter));

    [Fact]
    public void AGlobExpandsToTheAreasFilesRelativeToHtml5()
    {
        var files = GameCodeTools.ExpandTestFiles(Html5Root, "ui*");

        Assert.Contains("tests/ui.test.js", files);
        Assert.Contains("tests/uiLayout.test.js", files);
        Assert.DoesNotContain("tests/net.test.js", files);
        Assert.All(files, f => Assert.StartsWith("tests/", f));
    }

    [Fact]
    public void OneNamedFileExpandsToItself()
        => Assert.Equal(new[] { "tests/net.test.js" }, GameCodeTools.ExpandTestFiles(Html5Root, "tests/net.test.js"));

    [Fact]
    public void AFilterThatMatchesNothingFailsRatherThanRunningNoTests()
    {
        Assert.Throws<McpToolException>(() => GameCodeTools.ExpandTestFiles(Html5Root, "nosuchthing*"));
        Assert.Throws<McpToolException>(() => GameCodeTools.ExpandTestFiles(Html5Root, "nosuchfolder/x*"));
    }
}
