using System.Text.Json;
using Jint.Native;
using Jint.Runtime;
using Jint.Native.Object;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scripting;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The actor proxy a script is handed must carry exactly what the shared
/// contract says it does.
/// </summary>
/// <remarks>
/// <para>
/// The browser side of this has always been pinned: <c>bridge.test.js</c> builds
/// a proxy and compares its members to <c>bridge-api.json</c>. This side was not,
/// and the asymmetry is worse than it sounds — a member added to the browser
/// bridge and the contract, and forgotten here, passes every gate in the
/// repository and then reads <c>undefined</c> in a native build. Which is to say
/// the game works in the browser, ships, and is broken on the platform nobody
/// runs during development.
/// </para>
/// <para>
/// It nearly happened with <c>transform3d</c>. The script's own actor has had it
/// for a long time; an actor a script <i>created</i> did not, so a world built
/// from script — which is how any world worth tuning is built — could not be
/// placed in 3D at all.
/// </para>
/// </remarks>
public class ActorProxyParityTests
{
    private static JsonDocument Contract()
    {
        var repo = EngineRepoLocator.Find();
        Assert.NotNull(repo);
        return JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repo!.Root, "html5/src/scripting/bridge-api.json")));
    }

    /// <summary>The member names the contract lists under a section.</summary>
    private static SortedSet<string> ContractMembers(JsonDocument doc, params string[] path)
    {
        var node = doc.RootElement;
        foreach (var step in path) node = node.GetProperty(step);

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var member in node.EnumerateObject()) names.Add(member.Name);
        return names;
    }

    /// <summary>The enumerable own-property names of a Jint object.</summary>
    private static SortedSet<string> MembersOf(ObjectInstance obj)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var pair in obj.GetOwnProperties()) names.Add(pair.Key.ToString());
        return names;
    }

    private static (ScriptProject project, Engine.Core.Scene scene, ObjectInstance proxy) Proxy()
    {
        var project = new ScriptProject();
        var scene = new Engine.Core.Scene("ActorProxyParity");

        var host = scene.AddActor(new Actor("Host"));
        var script = host.AddComponent<ScriptComponent>();
        script.ScriptPath = project.Write("Host.js", "function onStart() {}");

        var subject = scene.AddActor(new Actor("Subject"));
        scene.FlushPendingActors();
        scene.Update(0f);

        var wrapped = script.Runtime!.Bridge.WrapActorAsProxy(subject);
        Assert.True(wrapped is ObjectInstance, "a live actor must wrap to an object");
        return (project, scene, (ObjectInstance)wrapped);
    }

    [Fact]
    public void AWrappedActorCarriesExactlyTheContractsMembers()
    {
        var (project, scene, proxy) = Proxy();
        using (project)
        {
            try
            {
                using var contract = Contract();
                Assert.Equal(ContractMembers(contract, "actorProxy"), MembersOf(proxy));
            }
            finally { scene.Destroy(); }
        }
    }

    [Fact]
    public void AWrappedActorsTransformCarriesExactlyTheContractsMembers()
    {
        var (project, scene, proxy) = Proxy();
        using (project)
        {
            try
            {
                using var contract = Contract();
                Assert.True(proxy.Get("transform") is ObjectInstance, "the proxy must carry a transform");
                var transform = (ObjectInstance)proxy.Get("transform");
                Assert.Equal(ContractMembers(contract, "actorProxyTransform"), MembersOf(transform));
            }
            finally { scene.Destroy(); }
        }
    }

    [Fact]
    public void AnActorWithNoTransform3DReportsNullRatherThanThrowing()
    {
        // Most actors are 2D and always will be. Reaching for transform3d on one
        // has to be answerable — a script that checks for it must get an answer,
        // not an exception, because the check is how it finds out.
        var (project, scene, proxy) = Proxy();
        using (project)
        {
            try
            {
                Assert.Equal(JsValue.Null, proxy.Get("transform3d"));
            }
            finally { scene.Destroy(); }
        }
    }

    [Fact]
    public void AddingATransform3DMakesTheProxyPlaceableInThreeDimensions()
    {
        var project = new ScriptProject();
        var scene = new Engine.Core.Scene("ActorProxyParity3D");

        var host = scene.AddActor(new Actor("Host"));
        var script = host.AddComponent<ScriptComponent>();
        script.ScriptPath = project.Write("Host.js", "function onStart() {}");

        var subject = scene.AddActor(new Actor("Subject"));
        var t3 = subject.AddComponent<Transform3D>();
        scene.FlushPendingActors();
        scene.Update(0f);

        using (project)
        {
            try
            {
                var proxy = (ObjectInstance)script.Runtime!.Bridge.WrapActorAsProxy(subject);
                Assert.True(proxy.Get("transform3d") is ObjectInstance,
                    "an actor with a Transform3D must expose transform3d to a script");
                var t = (ObjectInstance)proxy.Get("transform3d");

                using var contract = Contract();
                Assert.Equal(ContractMembers(contract, "globals", "transform3d"), MembersOf(t));

                // Position and scale both, because a world is built out of unit
                // primitives and an unscaled cube is not a wall.
                t.Set("x", 10);
                t.Set("z", -4);
                t.Set("scaleY", 3);

                Assert.Equal(10f, t3.Position.X, 3);
                Assert.Equal(-4f, t3.Position.Z, 3);
                Assert.Equal(3f, t3.LocalScale.Y, 3);
            }
            finally { scene.Destroy(); }
        }
    }

    [Fact]
    public void AReferenceHeldAcrossADestroyReportsInactive()
    {
        // `active` is the ONLY liveness signal a script has — there is no
        // `isDestroyed` in the contract — so it has to mean what the engine
        // means internally, which is `IsActive && !IsDestroyed` at every check.
        //
        // It did not. `IsActive` is an auto-property that Destroy() never
        // clears, so a script holding a reference to something long gone read
        // `active === true` for the rest of the session. The shape that breaks
        // is the ordinary one: cache a reference, refresh it when it stops being
        // active, and never refresh because it never stops. In UltraDark-sb that
        // made every boss after the first immune to the player's gun — the
        // projectile pool was delivering the damage to the previous boss's
        // script. It read exactly like a balance problem.
        var project = new ScriptProject();
        var scene = new Engine.Core.Scene("Liveness");

        var host = scene.AddActor(new Actor("Host"));
        var script = host.AddComponent<ScriptComponent>();
        script.ScriptPath = project.Write("Host.js", "function onStart() {}");

        var subject = scene.AddActor(new Actor("Boss"));
        scene.FlushPendingActors();
        scene.Update(0f);

        using (project)
        {
            try
            {
                var proxy = (ObjectInstance)script.Runtime!.Bridge.WrapActorAsProxy(subject);
                Assert.True(TypeConverter.ToBoolean(proxy.Get("active")), "a live actor is active");

                subject.Destroy();
                scene.FlushPendingActors();

                Assert.False(TypeConverter.ToBoolean(proxy.Get("active")),
                    "a reference held across a destroy must report inactive, not stale truth");
            }
            finally { scene.Destroy(); }
        }
    }
}