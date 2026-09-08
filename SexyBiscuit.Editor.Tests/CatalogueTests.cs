using System.Text.Json.Nodes;
using SexyBiscuit.Editor.Assistant;
using SexyBiscuit.Engine.Mcp;
using Xunit;

namespace SexyBiscuit.Editor.Tests;

/// <summary>
/// The tool catalogue every session pays for, once, before it does anything.
/// </summary>
/// <remarks>
/// Forty-four of the tools live in this project and had no test at all: SexyBiscuit.Tests
/// references the engine only, so the editor's own tools were described to every agent and
/// checked by nothing but a CI budget on the whole string. These tests build the same catalogue
/// <c>--dump-mcp-tools --all</c> prints and hold it to its shape and its size.
/// </remarks>
public class CatalogueTests : IDisposable
{
    /// <summary>
    /// The ceiling on the compact <c>tools/list</c>, in characters — 47,368 today, about 11,800
    /// tokens, which every session pays before it does anything. The same number is enforced from
    /// the command line by <c>--dump-mcp-tools --all --budget</c> in CI. It only ever goes down:
    /// lower it when tools merge, never raise it to make a test pass.
    /// </summary>
    private const int Budget = 48_000;

    private readonly HeadlessSceneHost _scene = AssistantSelfTest.CatalogueScene();
    private readonly McpToolRegistry   _registry;

    public CatalogueTests() => _registry = AssistantSelfTest.BuildCatalogue(_scene, new AssistantSettings(), all: true);

    public void Dispose() => _scene.Dispose();

    private JsonArray ToolsList() => _registry.DescribeForToolsList();

    [Fact]
    public void TheCatalogueCarriesTheEditorsOwnToolsAndNotTheSelfTestStandIns()
    {
        var names = _registry.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        // One from each class that only exists in this project.
        Assert.Contains("get_context", names);          // EditorTools
        Assert.Contains("run_tests", names);            // GameCodeTools
        Assert.Contains("export_build", names);         // ShippingTools
        Assert.Contains("ask_user", names);             // UserInteraction
        Assert.Contains("search_cookies", names);       // CookieTools
        Assert.Contains("apply_scene_edits", names);    // the engine's own, over a headless scene

        Assert.True(names.Count >= 80, $"the catalogue has only {names.Count} tools; something failed to register");
    }

    [Fact]
    public void EveryToolIsNamedOnceAndDescribesItself()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tool in _registry.Tools)
        {
            Assert.True(seen.Add(tool.Name), $"two tools are called {tool.Name}");
            Assert.Matches("^[a-z][a-z0-9_]*$", tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"{tool.Name} has no description");

            // Nothing may be advertised as changing nothing and destroying something at once.
            if (tool.ReadOnly) Assert.False(tool.Destructive, $"{tool.Name} claims to be read-only and destructive");
        }
    }

    /// <summary>
    /// <c>readOnlyHint</c> is what a client reads to decide whether a call needs confirming, so a
    /// tool that writes a file, restarts the editor or publishes a build must never carry it.
    /// </summary>
    /// <remarks>
    /// It used to be inferred from Mutating, which only means "edits the scene, snapshot undo
    /// first". 65 of the 88 tools therefore claimed to be read-only, including publish_build,
    /// which uploads to Darks Games, and uninstall_cookie, which deletes project files while also
    /// advertising destructiveHint.
    /// </remarks>
    [Theory]
    [InlineData("publish_build")]
    [InlineData("export_build")]
    [InlineData("build_project")]
    [InlineData("install_cookie")]
    [InlineData("uninstall_cookie")]
    [InlineData("bake_cookie")]
    [InlineData("save_scene")]
    [InlineData("create_project")]
    [InlineData("create_class")]
    [InlineData("create_code_project")]
    [InlineData("rebuild_engine_and_restart")]
    [InlineData("reload_game_code")]
    [InlineData("run_standalone")]
    [InlineData("run_tests")]
    [InlineData("undo")]
    [InlineData("redo")]
    public void AToolWithSideEffectsIsNotAdvertisedReadOnly(string name)
    {
        var tool = _registry.Find(name);
        Assert.NotNull(tool);
        Assert.False(tool!.ReadOnly, $"{name} changes something; it must not carry readOnlyHint");

        var entry = ToolsList().Select(t => t!.AsObject()).First(t => t["name"]!.GetValue<string>() == name);
        Assert.Null(entry["annotations"]?["readOnlyHint"]);
    }

    [Fact]
    public void EveryPureQueryIsAdvertisedReadOnly()
    {
        // The get_/list_/describe_ family is what an agent may call freely; saying so is what
        // keeps a cautious client from confirming every read.
        foreach (var tool in _registry.Tools)
        {
            bool query = tool.Name.StartsWith("get_", StringComparison.Ordinal)
                      || tool.Name.StartsWith("list_", StringComparison.Ordinal)
                      || tool.Name.StartsWith("describe_", StringComparison.Ordinal);

            if (query) Assert.True(tool.ReadOnly, $"{tool.Name} reads; mark it ReadOnly = true so a client need not confirm it");
        }
    }

    [Fact]
    public void EveryToolsListEntryHasTheThreeFieldsAClientReads()
    {
        foreach (var entry in ToolsList().Select(t => t!.AsObject()))
        {
            string name = entry["name"]!.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(entry["description"]?.GetValue<string>()), $"{name} lost its description");

            var schema = entry["inputSchema"]?.AsObject();
            Assert.NotNull(schema);
            Assert.Equal("object", schema!["type"]!.GetValue<string>());
            Assert.Null(schema["additionalProperties"]);   // scaffolding an agent cannot act on
        }
    }

    [Fact]
    public void TheCatalogueStaysUnderTheSessionBudget()
    {
        string compact = new JsonObject { ["tools"] = ToolsList() }.ToJsonString();

        Assert.True(compact.Length <= Budget,
            $"the catalogue is {compact.Length:N0} characters, {compact.Length - Budget:N0} over the {Budget:N0} budget. " +
            "Merge tools or shorten descriptions; do not raise the budget.");
    }

    [Fact]
    public void TheLongestDescriptionsAreTheOnesWorthTheirLength()
    {
        // Not a cap yet: the tool surface is being merged down. This fails if any single
        // description runs away, which is the shape the budget alone would not catch until late.
        var worst = _registry.Tools.OrderByDescending(t => t.Description.Length).First();
        Assert.True(worst.Description.Length <= 700,
            $"{worst.Name}'s description is {worst.Description.Length} characters. Move the how-to into wiki/25-ai-assistant-mcp.md.");
    }
}
