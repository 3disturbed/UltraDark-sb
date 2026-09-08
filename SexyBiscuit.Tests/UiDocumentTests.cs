using System.Text.Json;
using System.Text.Json.Nodes;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.UI;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Holds the UI document codec to the golden cases the browser engine reads from the same file.
/// </summary>
/// <remarks>
/// The strongest parity check available for the script-facing UI. The other bridge tests compare
/// member <em>names</em>, which catches a missing property and cannot catch one that means
/// something slightly different on each engine — a colour that loses its alpha, a size shorthand
/// that resolves differently, a loose enum spelling one side accepts and the other does not.
/// Feeding one spec through both codecs and comparing what comes back out catches exactly that.
/// </remarks>
public class UiDocumentTests
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
        string path = Path.Combine(RepoRoot, "html5", "tests", "fixtures", "ui-build-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("cases").Clone();
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ASpecRoundTripsToTheJsonBothEnginesAgreeOn(string name)
    {
        JsonElement test = FindCase(name);

        if (test.TryGetProperty("throws", out JsonElement expectedMessage))
        {
            var thrown = Assert.Throws<UiDocumentException>(
                () => UiDocument.FromElement(test.GetProperty("spec")));
            Assert.Contains(expectedMessage.GetString()!, thrown.Message);
            return;
        }

        UiNode node = UiDocument.FromElement(test.GetProperty("spec"));
        JsonObject written = UiDocument.ToNode(node);

        // Compared as parsed JSON rather than as text, so neither engine's number
        // formatting decides whether the two agree.
        Assert.Equal(
            Canonical(JsonNode.Parse(test.GetProperty("expect").GetRawText())),
            Canonical(written));
    }

    private static JsonElement FindCase(string name)
    {
        foreach (JsonElement c in Cases().EnumerateArray())
            if (c.GetProperty("name").GetString() == name) return c;

        throw new InvalidOperationException($"No build case named \"{name}\".");
    }

    /// <summary>A stable text form, so 0.72 and 0.7200 do not read as a disagreement.</summary>
    private static string Canonical(JsonNode? node)
    {
        if (node is null) return "null";
        if (node is JsonValue value)
            return value.TryGetValue(out double number)
                ? number.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)
                : value.ToJsonString();

        if (node is JsonArray array)
            return "[" + string.Join(",", array.Select(Canonical)) + "]";

        var obj = (JsonObject)node;
        return "{" + string.Join(",", obj.Select(p => $"\"{p.Key}\":{Canonical(p.Value)}")) + "}";
    }

    /// <summary>
    /// A tree a script built has to survive being written out and read back, or a UI authored
    /// in a script could never be saved as a <c>.ui</c> document and reopened.
    /// </summary>
    [Fact]
    public void WritingATreeAndReadingItBackGivesTheSameTree()
    {
        UiNode original = UiDocument.FromJson("""
            {
              "name": "hud", "layout": "column", "gap": 6, "padding": 12,
              "background": "#161920e6",
              "children": [
                { "name": "hp", "kind": "bar", "width": "*", "height": 8, "value": 0.4, "tint": "#c63832" },
                { "name": "go", "kind": "button", "text": "Go", "anchor": "bottomright", "x": -12, "y": -12 }
              ]
            }
            """);

        string once  = UiDocument.ToJson(original);
        string twice = UiDocument.ToJson(UiDocument.FromJson(once));

        Assert.Equal(once, twice);
        Assert.Equal(2, UiDocument.FromJson(once).Children.Count);
    }
}
