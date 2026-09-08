using System.Text.Json;
using System.Text.RegularExpressions;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scripting;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Holds the Jint bridge to the scripting contract the browser bridge is held to.
/// </summary>
/// <remarks>
/// <c>html5/src/scripting/bridge-api.json</c> names every global, member and hook a script may
/// use. The browser suite checks its bridge against the file; this checks ours, in both
/// directions, so the two bridges can only drift apart by failing a test. Until this existed the
/// browser bridge was a superset and every template script silently failed natively.
/// </remarks>
public class ScriptBridgeParityTests
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

    private static JsonDocument Contract()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "html5", "src", "scripting", "bridge-api.json")));

    private static IReadOnlyList<string> Names(JsonElement section)
        => section.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>A runtime on an actor that has a Transform3D, so transform3d is an object.</summary>
    private static (Engine.Core.Scene scene, JintRuntime runtime) Runtime()
    {
        var scene = new Engine.Core.Scene("Parity");
        var actor = scene.AddActor(new Actor("Host"));
        actor.AddComponent<Transform3D>();
        scene.AddActor(new Actor("Other"));
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

    [Fact]
    public void EveryGlobalTheBrowserBridgeInstallsExistsUnderJintWithTheSameMembers()
    {
        using var contract = Contract();
        var (scene, runtime) = Runtime();

        try
        {
            foreach (var global in contract.RootElement.GetProperty("globals").EnumerateObject())
            {
                string type = runtime.Evaluate($"typeof {global.Name}")!.ToString();
                Assert.True(type == "object", $"the '{global.Name}' global is missing under Jint (typeof was {type})");

                var expected = Names(global.Value);
                var actual   = OwnNames(runtime, global.Name);
                Assert.True(expected.SequenceEqual(actual),
                    $"'{global.Name}' differs from bridge-api.json.\n  missing: {string.Join(", ", expected.Except(actual))}\n  extra: {string.Join(", ", actual.Except(expected))}");

                foreach (var member in global.Value.EnumerateObject())
                {
                    if (member.Value.GetProperty("kind").GetString() != "fn") continue;
                    Assert.Equal("function", runtime.Evaluate($"typeof {global.Name}.{member.Name}")!.ToString());
                }
            }

            foreach (var bare in contract.RootElement.GetProperty("bare").EnumerateObject())
                Assert.Equal("function", runtime.Evaluate($"typeof {bare.Name}")!.ToString());
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void AFoundActorAndACollisionHaveTheContractShapes()
    {
        using var contract = Contract();
        var (scene, runtime) = Runtime();

        try
        {
            Assert.Equal(Names(contract.RootElement.GetProperty("actorProxy")),
                         OwnNames(runtime, "Scene.find('Other')"));
            Assert.Equal(Names(contract.RootElement.GetProperty("actorProxyTransform")),
                         OwnNames(runtime, "Scene.find('Other').transform"));

            var data = runtime.Bridge.WrapCollisionData(new CollisionData { Other = scene.FindByName("Other")! });
            runtime.SetGlobal("__collision", data);
            Assert.Equal(Names(contract.RootElement.GetProperty("collisionData")), OwnNames(runtime, "__collision"));
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void EveryHookTheBrowserDispatchesIsDispatchedNatively()
    {
        using var contract = Contract();
        var expected = Names(contract.RootElement.GetProperty("hooks"));
        var actual   = JintRuntime.KnownHooks.OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);

        // ScriptComponent must forward each one, or a defined hook never fires.
        foreach (var hook in JintRuntime.KnownHooks.Where(h => h.StartsWith("onCollision") || h.StartsWith("onTrigger")))
        {
            string method = "O" + hook.Substring(1);
            Assert.NotNull(typeof(ScriptComponent).GetMethod(method));
        }
    }

    /// <summary>
    /// The declarations an editor and the MCP scripting resource show are generated from the
    /// contract, so every global, member and hook the contract names is declared. Until this
    /// existed the hand-written file was missing thirty-odd members and two whole globals, and
    /// the agent reading it could not see them.
    /// </summary>
    [Fact]
    public void TheGeneratedDeclarationsNameEveryGlobalMemberAndHook()
    {
        using var contract = Contract();
        string dts = TypeScriptDefinitions.Generate();
        Assert.StartsWith("// ====", dts);
        Assert.Contains("Generated from html5/src/scripting/bridge-api.json", dts);

        var meta = contract.RootElement.GetProperty("meta").GetProperty("globals");
        foreach (var global in contract.RootElement.GetProperty("globals").EnumerateObject())
        {
            string block;
            if (meta.GetProperty(global.Name).TryGetProperty("interface", out var iface))
            {
                var m = Regex.Match(dts, @"declare interface " + iface.GetString() + @"\s*\{(.*?)\n\}", RegexOptions.Singleline);
                Assert.True(m.Success, $"no interface {iface.GetString()} for the {global.Name} global");
                block = m.Groups[1].Value;
            }
            else
            {
                var m = Regex.Match(dts, @"declare const " + global.Name + @": \{(.*?)\n\};", RegexOptions.Singleline);
                Assert.True(m.Success, $"no declaration for the {global.Name} global");
                block = m.Groups[1].Value;
            }

            foreach (var member in global.Value.EnumerateObject())
                Assert.True(Regex.IsMatch(block, @"^\s*(readonly )?" + member.Name + @"\b", RegexOptions.Multiline),
                    $"{global.Name}.{member.Name} is not declared");
        }

        foreach (var hook in contract.RootElement.GetProperty("hooks").EnumerateObject())
            Assert.Contains($"declare function {hook.Name}(", dts);
    }
}
