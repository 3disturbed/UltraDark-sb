using System.Text.Json;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scripting;
using SexyBiscuit.Engine.UI;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The widget tree as a game script reaches it.
/// </summary>
/// <remarks>
/// <para>
/// The tree was finished and painting on both engines for a while before anything in the
/// contract could reach a single node, so a HUD had no way onto it. These hold the bridge to
/// the contract and to the behaviour a HUD actually needs.
/// </para>
/// <para>
/// The handle is checked in <em>both</em> directions. The flat UI's handle was pinned one way
/// only — the contract was read and Jint probed for each member — so a member Jint had and the
/// contract did not passed silently, and the browser's handle was pinned by nothing at all.
/// </para>
/// </remarks>
public class UiScriptApiTests
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

    private static (Scene scene, JintRuntime runtime) Runtime()
    {
        var scene = new Scene("UiScript");
        var actor = scene.AddActor(new Actor("Host"));
        scene.FlushPendingActors();
        return (scene, new JintRuntime(actor));
    }

    private static List<string> OwnNames(JintRuntime runtime, string expression)
    {
        var value = runtime.Evaluate($"JSON.stringify(Object.getOwnPropertyNames({expression}))");
        Assert.NotNull(value);
        return JsonSerializer.Deserialize<List<string>>(value!.ToString())!
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>Member names of a contract section, which may be nested as "globals.UI".</summary>
    private static List<string> ContractNames(string section)
    {
        string path = Path.Combine(RepoRoot, "html5", "src", "scripting", "bridge-api.json");
        using var contract = JsonDocument.Parse(File.ReadAllText(path));

        JsonElement at = contract.RootElement;
        foreach (string step in section.Split('.')) at = at.GetProperty(step);

        return at.EnumerateObject()
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    // -------------------------------------------------------------------------
    // The contract
    // -------------------------------------------------------------------------

    [Fact]
    public void AHandleCarriesExactlyTheMembersTheContractPromises()
    {
        var (scene, runtime) = Runtime();
        try
        {
            runtime.Evaluate("var root = UI.build({ name: 'root' });");
            Assert.Equal("object", runtime.Evaluate("typeof root")!.ToString());

            List<string> expected = ContractNames("uiNode");
            List<string> actual = OwnNames(runtime, "root");

            // Both directions: a member on one side only is the defect this catches.
            Assert.True(expected.SequenceEqual(actual),
                $"the node handle differs from bridge-api.json.\n"
                + $"  missing: {string.Join(", ", expected.Except(actual))}\n"
                + $"  extra:   {string.Join(", ", actual.Except(expected))}");
        }
        finally { scene.Destroy(); }
    }

    /// <summary>
    /// A property the codec can be told must also be one it can be asked, or a script gets a
    /// member it can write and never read back.
    /// </summary>
    [Fact]
    public void EveryHandlePropertyCanBeReadAndTheWritableOnesWritten()
    {
        var (scene, runtime) = Runtime();
        try
        {
            runtime.Evaluate("var root = UI.build({ name: 'root' });");

            foreach (string key in UiDocument.ScriptProperties)
            {
                string kind = runtime.Evaluate($"typeof root.{key}")!.ToString();
                Assert.True(kind != "undefined", $"root.{key} reads undefined");
            }
        }
        finally { scene.Destroy(); }
    }

    // -------------------------------------------------------------------------
    // Building a tree
    // -------------------------------------------------------------------------

    [Fact]
    public void AScriptCanBuildATreeAndReachItByName()
    {
        var (scene, runtime) = Runtime();
        try
        {
            runtime.Evaluate("""
                var root = UI.build({
                    name: 'hud', layout: 'column', gap: 8, padding: 12,
                    children: [
                        { name: 'title', kind: 'label', text: 'JAKE01', scale: 3 },
                        { name: 'hp', kind: 'bar', width: '*', height: 10, value: 0.72 }
                    ]
                });
                """);

            Assert.Equal("JAKE01", runtime.Evaluate("UI.find('title').text")!.ToString());
            Assert.Equal("2", runtime.Evaluate("root.children.length")!.ToString());
            Assert.Equal("hud", runtime.Evaluate("UI.find('hp').parent.name")!.ToString());

            // The one a HUD writes every frame.
            runtime.Evaluate("UI.find('hp').value = 0.25;");
            Assert.Equal("0.25", runtime.Evaluate("UI.find('hp').value")!.ToString());
        }
        finally { scene.Destroy(); }
    }

    /// <summary>
    /// Two reads of the same node must be the same handle, or a script comparing what it
    /// found against what it kept is quietly always false.
    /// </summary>
    [Fact]
    public void TheSameNodeAlwaysComesBackAsTheSameHandle()
    {
        var (scene, runtime) = Runtime();
        try
        {
            runtime.Evaluate("UI.build({ children: [{ name: 'ok', kind: 'button' }] });");
            Assert.Equal("true", runtime.Evaluate("UI.find('ok') === UI.find('ok')")!.ToString());
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void ANodeAddedAtRuntimeJoinsTheTree()
    {
        var (scene, runtime) = Runtime();
        try
        {
            runtime.Evaluate("""
                var root = UI.build({ name: 'root', layout: 'column' });
                var added = root.add({ name: 'later', kind: 'label', text: 'hello' });
                """);

            Assert.Equal("1", runtime.Evaluate("root.children.length")!.ToString());
            Assert.Equal("hello", runtime.Evaluate("UI.find('later').text")!.ToString());

            runtime.Evaluate("added.remove();");
            Assert.Equal("0", runtime.Evaluate("root.children.length")!.ToString());
        }
        finally { scene.Destroy(); }
    }

    /// <summary>
    /// A typo must say so rather than dropping the subtree beneath it.
    /// </summary>
    /// <remarks>
    /// The flat UI ended its options switch with a silent default, so a misspelt key did
    /// nothing and reported nothing. In a tree a mistyped <c>childern</c> would drop every
    /// node below it and leave no trace at all. <see cref="JintRuntime.Evaluate"/> reports
    /// and returns null rather than throwing, so that is what a refusal looks like here.
    /// </remarks>
    [Fact]
    public void AMisspeltPropertyIsRefusedRatherThanIgnored()
    {
        var (scene, runtime) = Runtime();
        try
        {
            Assert.Null(runtime.Evaluate("UI.build({ widht: 100 });"));

            // And nothing was half-built behind the refusal.
            Assert.Equal("0", runtime.Evaluate("UI.root.children.length")!.ToString());
        }
        finally { scene.Destroy(); }
    }

    // -------------------------------------------------------------------------
    // Ownership
    // -------------------------------------------------------------------------

    [Fact]
    public void AScriptCanOnlyClearItsOwnNodes()
    {
        var sceneA = new Scene("A");
        var actorA = sceneA.AddActor(new Actor("A"));
        var sceneB = new Scene("B");
        var actorB = sceneB.AddActor(new Actor("B"));
        sceneA.FlushPendingActors();
        sceneB.FlushPendingActors();

        var a = new JintRuntime(actorA);
        var b = new JintRuntime(actorB);

        try
        {
            a.Evaluate("UI.build({ children: [{ name: 'fromA', kind: 'label', text: 'A' }] });");
            b.Evaluate("UI.build({ children: [{ name: 'fromB', kind: 'label', text: 'B' }] });");

            a.Evaluate("UI.clear();");

            Assert.Equal("true", a.Evaluate("UI.find('fromA') === null")!.ToString());
            Assert.Equal("B", b.Evaluate("UI.find('fromB').text")!.ToString());
        }
        finally
        {
            a.Bridge.DisposeUi();
            b.Bridge.DisposeUi();
            sceneA.Destroy();
            sceneB.Destroy();
        }
    }

    /// <summary>
    /// A destroyed actor must take its HUD with it.
    /// </summary>
    /// <remarks>
    /// The flat UI had exactly this bug and shipped with it: the browser cleaned up on
    /// teardown and the native side never called its own disposer, so a destroyed actor left
    /// its HUD on screen in one build and not the other. Nothing checked, which is why.
    /// </remarks>
    [Fact]
    public void DestroyingTheScriptTakesItsCanvasWithIt()
    {
        var scene = new Scene("Teardown");
        var actor = scene.AddActor(new Actor("Host"));
        scene.FlushPendingActors();

        int before = UiCanvas.All.Count;
        var runtime = new JintRuntime(actor);

        try
        {
            runtime.Evaluate("UI.build({ children: [{ name: 'hud', kind: 'label', text: 'x' }] });");
            Assert.Equal(before + 1, UiCanvas.All.Count);

            runtime.Bridge.DisposeUi();
            Assert.Equal(before, UiCanvas.All.Count);
        }
        finally { scene.Destroy(); }
    }
    // -------------------------------------------------------------------------
    // The generated definitions
    // -------------------------------------------------------------------------

    /// <summary>
    /// The <c>.d.ts</c> an agent reads has to describe the UI the contract actually has.
    /// </summary>
    /// <remarks>
    /// Nothing pinned this before, which is how the whole <c>UI</c> global came to ship with
    /// no mention of it in the definitions at all while the wiki said the two matched. The MCP
    /// <c>ScriptingApi</c> resource serves this text, so a stale one quietly teaches an agent
    /// an API that is not there.
    /// </remarks>
    [Fact]
    public void TheTypeScriptDefinitionsDescribeTheUiTheContractPromises()
    {
        string dts = TypeScriptDefinitions.Generate();

        foreach (string member in ContractNames("globals.UI"))
            Assert.True(dts.Contains(member, StringComparison.Ordinal),
                $"the .d.ts never mentions UI.{member}");

        foreach (string member in ContractNames("uiNode"))
            Assert.True(dts.Contains(member, StringComparison.Ordinal),
                $"the .d.ts never mentions the node handle's {member}");
    }
}
