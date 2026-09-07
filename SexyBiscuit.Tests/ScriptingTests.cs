using Jint;
using Jint.Native;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Input;
using SexyBiscuit.Engine.Physics;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.Scripting;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// A throwaway project folder that is the script root while it lives.
/// </summary>
internal sealed class ScriptProject : IDisposable
{
    private readonly string? _previousRoot;

    public string Root { get; }

    public ScriptProject()
    {
        Root = Path.Combine(Path.GetTempPath(), "sb-scripts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "Scripts"));
        _previousRoot = ProjectPaths.Root;
        ProjectPaths.Root = Root;
    }

    /// <summary>Writes a script and returns its project-relative path.</summary>
    public string Write(string name, string source)
    {
        File.WriteAllText(Path.Combine(Root, "Scripts", name), source);
        return "Scripts/" + name;
    }

    public void Dispose()
    {
        ProjectPaths.Root = _previousRoot;
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// The C# side of the scripting contract: what a project script can do under Jint.
/// </summary>
/// <remarks>
/// The bundled template scripts had never run natively — they use <c>log()</c>,
/// <c>actor.getComponent</c>, <c>Input.isKeyHeld</c> and the Stay/Exit hooks, none of which
/// the bridge had — so a game playtested in the browser silently did nothing when shipped.
/// These tests exercise the same constructs the templates use.
/// </remarks>
public class ScriptingTests
{
    private static Engine.Core.Scene NewScene(string name = "Scripting") => new(name);

    private static ScriptComponent Attach(Engine.Core.Scene scene, Actor actor, string scriptPath)
    {
        scene.AddActor(actor);
        var script = actor.AddComponent<ScriptComponent>();
        script.ScriptPath = scriptPath;
        return script;
    }

    private static void Tick(Engine.Core.Scene scene, int frames = 1)
    {
        for (int i = 0; i < frames; i++) scene.Update(1f / 60f);
    }

    private static string Eval(ScriptComponent script, string expression)
        => script.Runtime!.Evaluate(expression)!.ToString();

    [Fact]
    public void AScriptErrorIsReportedThroughTheDiagnosticsSink()
    {
        // Errors used to go to Debug.WriteLine, which a Release build removes, so neither a
        // shipped game nor a Release test run could see a failing script.
        using var project = new ScriptProject();
        var diagnostics = new List<ScriptDiagnostic>();
        using var capture = ScriptDiagnostics.Capture(diagnostics);

        var scene  = NewScene();
        var script = Attach(scene, new Actor("Broken"), project.Write("Broken.js",
            "function onUpdate(dt) { thisFunctionDoesNotExist(); }"));

        try
        {
            Tick(scene);

            var error = Assert.Single(diagnostics, d => d.Level == ScriptDiagnosticLevel.Error);
            Assert.Equal("onUpdate", error.Hook);
            Assert.Equal("Scripts/Broken.js", error.ScriptPath);
            Assert.Contains("thisFunctionDoesNotExist", error.Message);
            Assert.NotNull(script.Runtime);   // one bad frame does not unload the script
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void AMissingScriptSetsErrorInsteadOfThrowing()
    {
        using var project = new ScriptProject();
        var diagnostics = new List<ScriptDiagnostic>();
        using var capture = ScriptDiagnostics.Capture(diagnostics);

        var scene  = NewScene();
        var script = Attach(scene, new Actor("Lost"), "Scripts/DoesNotExist.js");

        try
        {
            Tick(scene);
            Assert.Null(script.Runtime);
            Assert.NotNull(script.Error);
            Assert.Contains(diagnostics, d => d.Level == ScriptDiagnosticLevel.Error && d.Message.Contains("could not read"));
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void ATemplateStylePlatformerScriptRunsUnderJint()
    {
        // The same constructs as Templates/2D Platformer/Scripts/PlayerController.js: a bare
        // log(), a key query, getComponent with the velocity shorthand, actor.transform, and
        // the collision Exit hook with the data.tag forward.
        using var project = new ScriptProject();
        var diagnostics = new List<ScriptDiagnostic>();
        using var capture = ScriptDiagnostics.Capture(diagnostics);

        var scene = NewScene();
        var player = new Actor("Player");
        player.AddComponent<Rigidbody2D>();
        var script = Attach(scene, player, project.Write("PlayerController.js", """
            var moveSpeed = 200;
            var grounded = false;
            var exits = 0;
            function onStart() { log("PlayerController started on: " + actor.name); }
            function onUpdate(dt) {
                var vx = 0;
                if (Input.isHeld("Left") || Input.isKeyHeld("A")) { vx = -moveSpeed; }
                var rb = actor.getComponent("Rigidbody2D");
                rb.velocityX = vx + 42;
                actor.transform.x = actor.transform.x + 1;
            }
            function onCollisionEnter(data) { if (data.tag === "Ground") grounded = true; }
            function onCollisionExit(data) { if (data.tag === "Ground") { grounded = false; exits++; } }
            """));

        try
        {
            Tick(scene, 2);

            Assert.Empty(diagnostics.Where(d => d.Level == ScriptDiagnosticLevel.Error).Select(d => d.Message));
            Assert.Contains(diagnostics, d => d.Level == ScriptDiagnosticLevel.Log && d.Message.Contains("started on: Player"));
            Assert.Equal(42f, player.GetComponent<Rigidbody2D>()!.LinearVelocity.X, 3);
            Assert.Equal(2f, player.Transform.Position.X, 3);

            var ground = new Actor("Floor") { Tag = "Ground" };
            var contact = new CollisionData { Other = ground, ContactPoint = Vector2.Zero, Normal = Vector2.UnitY };
            player.OnCollisionEnter(contact);
            Assert.Equal("true", Eval(script, "grounded"));
            player.OnCollisionExit(contact);
            Assert.Equal("false", Eval(script, "grounded"));
            Assert.Equal("1", Eval(script, "exits"));
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void GetComponentAcceptsBothSpellingsAndConvertsValues()
    {
        // A script written against the browser reads `rb.gravityScale`; one written against
        // the C# names reads `rb.GravityScale`. Both must be one property, and a file-form
        // value ("#FF0000", {x, y}) must land as the typed value.
        using var project = new ScriptProject();
        var scene = NewScene();
        var crate = new Actor("Crate");
        crate.AddComponent<Rigidbody2D>();
        crate.AddComponent<BoxCollider2D>();
        crate.AddComponent<SpriteRenderer>();
        var script = Attach(scene, crate, project.Write("Crate.js", """
            var readBack = 0;
            var typeName = "";
            function onStart() {
                var rb = actor.getComponent("Rigidbody2D");
                rb.GravityScale = 2.5;
                readBack = rb.gravityScale;
                rb.velocity = { x: 3, y: 4 };
                typeName = rb.type;

                var col = actor.getComponent("BoxCollider2D");
                col.size = { x: 10, y: 20 };
                col.IsTrigger = true;

                var sprite = actor.getComponent("SpriteRenderer");
                sprite.tint = "#FF0000";
                red = sprite.tint.r;
                sprite.tint = { R: 0, G: 255, B: 0, A: 255 };
                green = sprite.tint.g;
            }
            var red = 0, green = 0;
            """));

        try
        {
            Tick(scene);

            Assert.Equal("2.5", Eval(script, "readBack"));
            Assert.Equal("Rigidbody2D", Eval(script, "typeName"));
            Assert.Equal(new Vector2(3, 4), crate.GetComponent<Rigidbody2D>()!.LinearVelocity);
            Assert.Equal(new Vector2(10, 20), crate.GetComponent<BoxCollider2D>()!.Size);
            Assert.True(crate.GetComponent<BoxCollider2D>()!.IsTrigger);
            // Colours read back as {r, g, b, a}, the shape the browser's Color has, and are
            // accepted in every file form.
            Assert.Equal("255", Eval(script, "red"));
            Assert.Equal("255", Eval(script, "green"));
            Assert.Equal(new Color(0, 255, 0, 255), crate.GetComponent<SpriteRenderer>()!.Tint);

            // The same component is the same proxy, so a script can keep state on it.
            Assert.Equal("true", Eval(script, "actor.getComponent('Rigidbody2D') === actor.getComponent('Rigidbody2D')"));
            Assert.Equal("null", Eval(script, "actor.getComponent('NoSuchComponent')"));
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void SceneQueriesResolveThroughTheActorsOwnSceneWithoutAnEngineHost()
    {
        // Scene.* used to go through EngineHost.Current, which is null in a test, a headless
        // tick or any scene that is not the active one — so every query returned nothing.
        using var project = new ScriptProject();
        var scene = NewScene("Level");
        scene.AddActor(new Actor("Other") { Tag = "Enemy" });
        var script = Attach(scene, new Actor("Seeker"), project.Write("Seeker.js", """
            var found = "";
            var sceneName = "";
            var tagged = 0;
            var spawnedName = "";
            var extended = false;
            function onStart() {
                sceneName = Scene.name;
                found = Scene.find("Other").name;
                tagged = Scene.findByTag("Enemy").length;
                var spawned = Scene.createActor("Spawned", 7, 8);
                spawnedName = spawned.name;
                var col = Scene.addComponent(spawned, "BoxCollider2D", { Size: [5, 6], IsTrigger: true });
                extended = col !== null && col.isTrigger;
            }
            """));

        try
        {
            Tick(scene, 2);   // the second frame flushes the spawned actor

            Assert.Equal("Level", Eval(script, "sceneName"));
            Assert.Equal("Other", Eval(script, "found"));
            Assert.Equal("1", Eval(script, "tagged"));
            Assert.Equal("Spawned", Eval(script, "spawnedName"));
            Assert.Equal("true", Eval(script, "extended"));

            var spawned = scene.FindByName("Spawned");
            Assert.NotNull(spawned);
            Assert.Equal(new Vector2(7, 8), spawned!.Transform.Position);
            Assert.Equal(new Vector2(5, 6), spawned.GetComponent<BoxCollider2D>()!.Size);
            Assert.Equal("1", Eval(script, "Scene.findAll('Spawned').length"));

            // Destroy through the proxy, then the next flush removes it.
            script.Runtime!.Evaluate("Scene.destroy(Scene.find('Spawned'))");
            Tick(scene);
            Assert.Null(scene.FindByName("Spawned"));
            Assert.Equal("null", Eval(script, "Scene.find('Spawned')"));
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void EveryCollisionHookReceivesTheOtherActorsTag()
    {
        using var project = new ScriptProject();
        var scene  = NewScene();
        var actor  = new Actor("Ball");
        var script = Attach(scene, actor, project.Write("Ball.js", """
            var seen = [];
            function onCollisionEnter(d) { seen.push("enter:" + d.tag + ":" + d.other.name); }
            function onCollisionStay(d)  { seen.push("stay:" + d.tag); }
            function onCollisionExit(d)  { seen.push("exit:" + d.name); }
            function onTriggerEnter(o)   { seen.push("tenter:" + o.tag); }
            function onTriggerStay(o)    { seen.push("tstay:" + o.tag); }
            function onTriggerExit(o)    { seen.push("texit:" + o.name); }
            """));

        try
        {
            Tick(scene);
            var wall = new Actor("Wall") { Tag = "Solid" };
            var data = new CollisionData { Other = wall, Normal = Vector2.UnitX };

            actor.OnCollisionEnter(data);
            actor.OnCollisionStay(data);
            actor.OnCollisionExit(data);
            actor.OnTriggerEnter(wall);
            actor.OnTriggerStay(wall);
            actor.OnTriggerExit(wall);

            Assert.Equal("enter:Solid:Wall,stay:Solid,exit:Wall,tenter:Solid,tstay:Solid,texit:Wall",
                Eval(script, "seen.join(',')"));
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void AProxyForADestroyedActorIsNull()
    {
        using var project = new ScriptProject();
        var scene = NewScene();
        var other = scene.AddActor(new Actor("Other"));
        var script = Attach(scene, new Actor("Watcher"), project.Write("Watcher.js", "function onStart() {}"));

        try
        {
            Tick(scene);
            Assert.False(script.Runtime!.Bridge.WrapActorAsProxy(other).IsNull());

            other.Destroy();
            Tick(scene);
            Assert.True(script.Runtime.Bridge.WrapActorAsProxy(other).IsNull());
        }
        finally { scene.Destroy(); }
    }

    [Theory]
    [InlineData("a", Keys.A)]
    [InlineData("A", Keys.A)]
    [InlineData("KeyA", Keys.A)]
    [InlineData("1", Keys.D1)]
    [InlineData("Digit1", Keys.D1)]
    [InlineData("D1", Keys.D1)]
    [InlineData("ArrowLeft", Keys.Left)]
    [InlineData("Left", Keys.Left)]
    [InlineData("Space", Keys.Space)]
    [InlineData("ShiftLeft", Keys.LeftShift)]
    [InlineData("LeftShift", Keys.LeftShift)]
    [InlineData("Backspace", Keys.Back)]
    [InlineData("NumpadAdd", Keys.Add)]
    [InlineData("Numpad5", Keys.NumPad5)]
    [InlineData("escape", Keys.Escape)]
    public void KeyNamesAcceptTheSpellingsTheBrowserAccepts(string name, Keys expected)
    {
        // html5/src/input/Keys.js normalises KeyboardEvent.code to XNA's spelling; a script
        // that asks for either spelling must get the same key here.
        Assert.True(KeyNames.TryParse(name, out var key), $"'{name}' did not parse");
        Assert.Equal(expected, key);
        Assert.False(KeyNames.TryParse("NoSuchKey", out _));
    }

    [Fact]
    public void AScriptDefersOnAwakeUntilItsActorHasAScene()
    {
        // Components awake when attached, which is before the actor joins a scene, so an
        // onAwake that calls Scene.find used to see nothing. The load now waits for Start,
        // the order the browser runtime uses, and each hook still fires exactly once.
        using var project = new ScriptProject();
        var path = project.Write("Counter.js", """
            var awakes = 0, starts = 0, sawScene = false;
            function onAwake() { awakes++; sawScene = Scene.find("Marker") !== null; }
            function onStart() { starts++; }
            """);

        var actor  = new Actor("Deferred");
        var script = actor.AddComponent<ScriptComponent>();
        script.ScriptPath = path;
        Assert.Null(script.Runtime);   // no scene yet: nothing loaded

        var scene = NewScene();
        scene.AddActor(new Actor("Marker"));
        scene.AddActor(actor);

        try
        {
            Tick(scene, 3);
            Assert.NotNull(script.Runtime);
            Assert.Equal("1", Eval(script, "awakes"));
            Assert.Equal("1", Eval(script, "starts"));
            Assert.Equal("true", Eval(script, "sawScene"));

            // A component added to an actor already in the scene loads at once.
            var late = scene.FindByName("Marker")!.AddComponent<ScriptComponent>();
            late.ScriptPath = path;
            Assert.NotNull(late.Runtime);
            Assert.Equal("1", Eval(late, "awakes"));
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void InvokeCallsAnyTopLevelFunctionAndReturnsItsResult()
    {
        // Three templates call into another actor's script (`takeDamage`, `harvest`); the
        // hooks are the only functions the engine calls, but invoke reaches the rest.
        using var project = new ScriptProject();
        var scene  = NewScene();
        var target = new Actor("Target");
        var targetScript = Attach(scene, target, project.Write("Target.js", """
            var health = 10;
            function takeDamage(amount) { health -= amount; return health; }
            """));
        var caller = Attach(scene, new Actor("Caller"), project.Write("Caller.js", """
            var result = -1, missing = "?";
            function onStart() {
                var other = Scene.find("Target").getComponent("ScriptComponent");
                result = other.invoke("takeDamage", 4);
                missing = other.call("noSuchFunction");
            }
            """));

        try
        {
            Tick(scene);
            Assert.Equal("6", Eval(caller, "result"));
            Assert.Equal("undefined", Eval(caller, "missing"));
            Assert.Equal(5d, targetScript.Invoke("takeDamage", new JsNumber(1)).AsNumber());
            Assert.True(targetScript.Invoke("nope").IsUndefined());
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void AnInvokeMadeBeforeTheScriptLoadsRunsAfterOnAwakeAndBeforeOnStart()
    {
        // A spawner that attaches a script and configures it in the same breath is early
        // when the actor is not yet in a scene. The call waits for the load — the order the
        // browser gives the same code while it fetches the file.
        using var project = new ScriptProject();
        var path = project.Write("Tower.js", """
            var range = 0, awakeSaw = -1, startSaw = -1;
            function configure(r) { range = r; }
            function onAwake() { awakeSaw = range; }
            function onStart() { startSaw = range; }
            """);

        var actor  = new Actor("Tower");
        var script = actor.AddComponent<ScriptComponent>();
        script.ScriptPath = path;
        Assert.Null(script.Runtime);
        Assert.True(script.Invoke("configure", new JsNumber(7)).IsUndefined());

        var scene = NewScene();
        scene.AddActor(actor);
        try
        {
            Tick(scene);
            Assert.Equal("0", Eval(script, "awakeSaw"));
            Assert.Equal("7", Eval(script, "range"));
            Assert.Equal("7", Eval(script, "startSaw"));

            // Attached to an actor already in the scene, the script loads at once and so does the call.
            var late = scene.AddActor(new Actor("Late"));
            scene.FlushPendingActors();
            var lateScript = late.AddComponent<ScriptComponent>();
            lateScript.ScriptPath = path;
            lateScript.Invoke("configure", new JsNumber(3));
            Assert.Equal("3", Eval(lateScript, "range"));
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void BareLogWarnAndErrorReportThroughTheSink()
    {
        using var project = new ScriptProject();
        var diagnostics = new List<ScriptDiagnostic>();
        using var capture = ScriptDiagnostics.Capture(diagnostics);

        var scene  = NewScene();
        var script = Attach(scene, new Actor("Talker"), project.Write("Talker.js", """
            var local = false;
            function onStart() {
                log("hello", 1, { a: 2 });
                warn("careful");
                error("bad");
                local = Network.isLocalPlayer(Network.localId);
            }
            """));

        try
        {
            Tick(scene);
            Assert.Contains(diagnostics, d => d.Level == ScriptDiagnosticLevel.Log && d.Message == "hello 1 {\"a\":2}");
            Assert.Contains(diagnostics, d => d.Level == ScriptDiagnosticLevel.Warning && d.Message == "careful");
            Assert.Contains(diagnostics, d => d.Level == ScriptDiagnosticLevel.Error && d.Message == "bad");

            // Player 0 is the local player before a server has said otherwise, so a lobby
            // script asking about itself gets a sensible answer with no session running.
            Assert.Equal("true", Eval(script, "local"));
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void TransformThreeDIsNullWithoutATransform3DAndLiveWithOne()
    {
        using var project = new ScriptProject();
        var scene = NewScene();
        var flat  = Attach(scene, new Actor("Flat"), project.Write("Flat.js", "var has = transform3d !== null; function onStart() { has = transform3d !== null; }"));

        var solid = new Actor("Solid");
        solid.AddComponent<Transform3D>();
        var solidScript = Attach(scene, solid, project.Write("Solid.js", """
            function onStart() { transform3d.y = 5; transform3d.rotY = 90; actor.transform3d.z = -2; }
            """));

        try
        {
            Tick(scene);
            Assert.Equal("false", Eval(flat, "has"));
            var t = solid.GetComponent<Transform3D>()!;
            Assert.Equal(5f, t.Position.Y, 3);
            Assert.Equal(-2f, t.Position.Z, 3);
            Assert.Equal(90f, t.EulerAngles.Y, 1);
            Assert.Equal("object", Eval(solidScript, "typeof transform3d"));
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void TimeAndPhysicsGlobalsAnswerWithoutAHost()
    {
        using var project = new ScriptProject();
        var scene  = NewScene();
        var script = Attach(scene, new Actor("Probe"), project.Write("Probe.js", """
            var dtType = "", hits = -1, ray = "?";
            function onStart() {
                dtType = typeof Time.deltaTime;
                hits = Physics.overlapCircle(0, 0, 10).length;
                ray = Physics.raycast(0, 0, 1, 0) === null ? "none" : "hit";
            }
            """));

        try
        {
            Tick(scene);
            Assert.Equal("number", Eval(script, "dtType"));
            Assert.Equal("0", Eval(script, "hits"));
            Assert.Equal("none", Eval(script, "ray"));
        }
        finally { scene.Destroy(); }
    }
}
