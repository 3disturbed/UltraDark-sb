using System.Text.Json;
using SexyBiscuit.Engine.Code;
using Xunit;

namespace SexyBiscuit.Editor.Tests;

/// <summary>Holds the native editor's transform vocabulary to the shared workbench contract.</summary>
public class WorkbenchContractTests
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

    [Fact]
    public void NativeAndBrowserReserveTheSameTransformShortcuts()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "editor-workbench.json")));
        var commands = contract.RootElement.GetProperty("commands");

        Assert.Equal("W", commands.GetProperty("editor.transform.translate").GetProperty("shortcut").GetString());
        Assert.Equal("E", commands.GetProperty("editor.transform.rotate").GetProperty("shortcut").GetString());
        Assert.Equal("R", commands.GetProperty("editor.transform.scale").GetProperty("shortcut").GetString());

        string viewport = File.ReadAllText(Path.Combine(RepoRoot, "SexyBiscuit.Editor", "Panels", "ViewportPanel.cs"));
        Assert.Contains("ImGuiKey.W", viewport);
        Assert.Contains("ImGuiKey.E", viewport);
        Assert.Contains("ImGuiKey.R", viewport);
    }

    [Fact]
    public void ContractMakesUnsupportedPropertyEditorsVisible()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "editor-workbench.json")));
        var coverage = contract.RootElement.GetProperty("propertyTypeCoverage");

        Assert.Equal("read-only-with-reason", coverage.GetProperty("list").GetProperty("native").GetString());
        Assert.Equal("editable", coverage.GetProperty("list").GetProperty("browser").GetString());
        Assert.Equal("read-only-with-reason", coverage.GetProperty("material").GetProperty("browser").GetString());
    }
}
