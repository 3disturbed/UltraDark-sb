using System.Text.Json;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.UI;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Holds directional navigation to the golden cases the browser engine reads from the
/// same file.
/// </summary>
/// <remarks>
/// Navigation is what makes a gamepad, a D-pad and a TV remote work at all, and it is
/// decided entirely by numbers. A scoring constant that differs between the two engines
/// would make the same menu feel right on one and arbitrary on the other, and none of
/// the source-reading parity tests in this repository could see it.
/// </remarks>
public class UiNavigationTests
{
    private static string RepoRoot
    {
        get
        {
            var repo = EngineRepoLocator.Find();
            Assert.NotNull(repo);
            return repo!.Root;
        }
    }

    public static TheoryData<string> CaseNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (JsonElement c in Cases().EnumerateArray())
                names.Add(c.GetProperty("name").GetString()!);
            return names;
        }
    }

    private static JsonElement Cases()
    {
        string path = Path.Combine(RepoRoot, "html5", "tests", "fixtures", "nav-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("cases").Clone();
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ADirectionLandsWhereBothEnginesAgreeItShould(string name)
    {
        JsonElement test = FindCase(name);

        RectangleF from = Rect(test.GetProperty("from"));

        var candidates = new List<NavCandidate>();
        int index = 0;
        foreach (JsonElement c in test.GetProperty("candidates").EnumerateArray())
            candidates.Add(new NavCandidate(index++, Rect(c)));

        foreach (JsonProperty expectation in test.GetProperty("expect").EnumerateObject())
        {
            var direction = Enum.Parse<NavDirection>(expectation.Name, ignoreCase: true);
            int expected = expectation.Value.GetInt32();
            int actual = UiNavigation.Find(from, candidates, direction);

            Assert.True(expected == actual,
                $"{direction} expected candidate {expected} but landed on {actual}.");
        }
    }

    private static JsonElement FindCase(string name)
    {
        foreach (JsonElement c in Cases().EnumerateArray())
            if (c.GetProperty("name").GetString() == name) return c;

        throw new InvalidOperationException($"No navigation case named \"{name}\".");
    }

    private static RectangleF Rect(JsonElement e)
        => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle());

    /// <summary>
    /// Moving one way and back again has to return to where it started, or a player
    /// walking a menu with a D-pad ends up somewhere they cannot get back from.
    /// </summary>
    [Fact]
    public void SteppingOneWayAndBackAgainReturnsToWhereItStarted()
    {
        var cells = new List<NavCandidate>();
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                cells.Add(new NavCandidate(row * 3 + col, new RectangleF(col * 110f, row * 60f, 100f, 50f)));

        foreach (NavCandidate start in cells)
        {
            List<NavCandidate> others = cells.FindAll(c => c.Index != start.Index);

            foreach ((NavDirection go, NavDirection back) in new[]
            {
                (NavDirection.Right, NavDirection.Left),
                (NavDirection.Down,  NavDirection.Up),
            })
            {
                int stepped = UiNavigation.Find(start.Rect, others, go);
                if (stepped < 0) continue;

                NavCandidate landed = others[stepped];
                List<NavCandidate> fromThere = cells.FindAll(c => c.Index != landed.Index);

                int returned = UiNavigation.Find(landed.Rect, fromThere, back);
                Assert.True(returned >= 0 && fromThere[returned].Index == start.Index,
                    $"{go} from {start.Index} landed on {landed.Index}, but {back} did not come back.");
            }
        }
    }
}
