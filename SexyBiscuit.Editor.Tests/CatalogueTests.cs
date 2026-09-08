using System.Text.Json.Nodes;
using SexyBiscuit.Editor.Assistant;
using SexyBiscuit.Engine.Mcp;
using Xunit;

namespace SexyBiscuit.Editor.Tests;

/// <summary>
/// The tool catalogue every session pays for, once, before it does anything.
/// </summary>
/// <remarks>
/// Half of the tools live in this project and had no test at all: SexyBiscuit.Tests
/// references the engine only, so the editor's own tools were described to every agent and
/// checked by nothing but a CI budget on the whole string. These tests build the same catalogue
/// <c>--dump-mcp-tools --all</c> prints and hold it to its shape and its size.
/// </remarks>
public class CatalogueTests : IDisposable
{
    /// <summary>
    /// The ceiling on the compact <c>tools/list</c>, in characters — 40,4xx today, about 10,100
    /// tokens, which every session pays before it does anything. The same number is enforced from
    /// the command line by <c>--dump-mcp-tools --all --budget</c> in CI. It only ever goes down:
    /// lower it when tools merge, never raise it to make a test pass.
    /// </summary>
    private const int Budget = 41_000;

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

        // A range, not a floor: 88 tools merged down to 52, and the surface only shrinks. A drop
        // far below this means a whole class failed to register, which the names above also catch.
        Assert.InRange(names.Count, 45, 55);
    }

    /// <summary>
    /// The merge of 2026-09: 88 tools to 52. A survivor takes the absorbed tool's arguments, so
    /// the absorbed name must be gone — a client that still had it would call a tool that is no
    /// longer served, and a description that still named one would send an agent at nothing.
    /// </summary>
    [Fact]
    public void TheAbsorbedToolNamesAreGoneFromTheCatalogueAndFromItsProse()
    {
        string[] absorbed =
        {
            "spawn_primitive", "place_actor", "list_actor_presets", "rename_actor", "move_to_layer",
            "translate", "rotate", "look_at", "detach_actor", "set_property", "get_property",
            "list_component_types", "describe_component_type", "get_scene_json",
            "play", "pause", "stop", "step_frame", "get_play_state",
            "get_editor_camera", "set_editor_camera", "set_viewport", "focus_actor", "capture_scene_from",
            "get_selection", "read_console", "clear_console", "log_message", "list_scenes", "create_project",
            "get_build_status", "cancel_build", "get_build_report", "stop_standalone", "get_engine_repo",
            "set_auto_reload", "list_actor_classes", "list_cookie_jars", "add_cookie_jar", "refresh_cookie_jar",
            "list_installed_cookies",
        };

        var names = _registry.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var prose = string.Join("\n", _registry.Tools.SelectMany(t => new[] { t.Description, t.InputSchema.ToJsonString() }));

        foreach (string gone in absorbed)
        {
            Assert.DoesNotContain(gone, names);

            // play, pause, stop, rotate and translate are ordinary English too; the rest read as
            // tool names wherever they appear, so prose still using one is a stale pointer.
            if (gone is "play" or "pause" or "stop" or "rotate" or "translate") continue;
            Assert.DoesNotContain(gone, prose, StringComparison.Ordinal);
        }

        foreach (string survivor in new[]
                 {
                     "spawn_actor", "set_actor", "set_transform", "attach_actor", "set_properties", "get_actor",
                     "describe_components", "play_mode", "editor_camera", "capture_viewport", "select_actor",
                     "console", "list_assets", "open_project", "build_project", "run_standalone", "get_project_info",
                     "reload_game_code", "get_code_project", "cookie_jars", "search_cookies", "get_scene_summary",
                     "export_build", "undo", "redo",
                 })
            Assert.Contains(survivor, names);
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
    /// advertising destructiveHint. Merging made this sharper still: console writes and clears as
    /// well as reads, cookie_jars stages a jar and fetches one, and open_project creates.
    /// </remarks>
    [Theory]
    [InlineData("publish_build")]
    [InlineData("export_build")]
    [InlineData("build_project")]
    [InlineData("install_cookie")]
    [InlineData("uninstall_cookie")]
    [InlineData("bake_cookie")]
    [InlineData("save_scene")]
    [InlineData("open_project")]
    [InlineData("create_class")]
    [InlineData("create_code_project")]
    [InlineData("rebuild_engine_and_restart")]
    [InlineData("reload_game_code")]
    [InlineData("run_standalone")]
    [InlineData("run_tests")]
    [InlineData("console")]
    [InlineData("cookie_jars")]
    [InlineData("play_mode")]
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
