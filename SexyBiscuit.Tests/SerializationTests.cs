using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.Localization;
using SexyBiscuit.Engine.Scene;
using Xunit;

namespace SexyBiscuit.Tests;

public class SceneSerializerTests
{
    private sealed class Stats : Component
    {
        public int    Health   { get; set; } = 100;
        public float  Speed    { get; set; } = 3.5f;
        public string Title    { get; set; } = "Grunt";
        public bool   Hostile  { get; set; } = true;
        public Color  Tint     { get; set; } = Color.Red;
        public Vector2 Offset2 { get; set; } = new(1f, 2f);
        public Vector3 Offset3 { get; set; } = new(1f, 2f, 3f);
    }

    [Fact]
    public void ARoundTripPreservesActorIdentity()
    {
        var scene = new Engine.Core.Scene("Level1");
        var actor = scene.AddActor(new Actor("Hero") { Tag = "player", Layer = 3 });
        actor.Transform.LocalPosition = new Vector2(12f, -4f);
        scene.Update(0.016f);

        var restored = SceneSerializer.Deserialize(SceneSerializer.Serialize(scene));
        restored.Update(0.016f);

        var hero = restored.FindByName("Hero");
        Assert.NotNull(hero);
        Assert.Equal("player", hero!.Tag);
        Assert.Equal(3, hero.Layer);
        Assert.Equal(new Vector2(12f, -4f), hero.Transform.LocalPosition);
    }

    [Fact]
    public void ComponentPropertiesRoundTrip()
    {
        var scene = new Engine.Core.Scene();
        var actor = scene.AddActor(new Actor("Enemy"));
        var stats = actor.AddComponent<Stats>();
        stats.Health  = 55;
        stats.Speed   = 9.25f;
        stats.Title   = "Captain";
        stats.Hostile = false;
        stats.Tint    = new Color(10, 20, 30, 40);
        stats.Offset3 = new Vector3(7f, 8f, 9f);
        scene.Update(0.016f);

        var restored = SceneSerializer.Deserialize(SceneSerializer.Serialize(scene));
        restored.Update(0.016f);

        var copy = restored.FindByName("Enemy")!.GetComponent<Stats>();
        Assert.NotNull(copy);
        Assert.Equal(55, copy!.Health);
        Assert.Equal(9.25f, copy.Speed, 4);
        Assert.Equal("Captain", copy.Title);
        Assert.False(copy.Hostile);
        Assert.Equal(new Color(10, 20, 30, 40), copy.Tint);
        Assert.Equal(new Vector3(7f, 8f, 9f), copy.Offset3);
    }

    [Fact]
    public void A3DTransformRoundTrips()
    {
        var scene = new Engine.Core.Scene();
        var actor = scene.AddActor(new Actor("Prop"));
        var t3d = actor.AddComponent<Transform3D>();
        t3d.LocalPosition = new Vector3(1f, 2f, 3f);
        t3d.LocalRotation = Quaternion.CreateFromYawPitchRoll(0.5f, 0.25f, -0.125f);
        t3d.LocalScale    = new Vector3(2f, 2f, 2f);
        scene.Update(0.016f);

        var restored = SceneSerializer.Deserialize(SceneSerializer.Serialize(scene));
        restored.Update(0.016f);

        var copy = restored.FindByName("Prop")!.GetComponent<Transform3D>();
        Assert.NotNull(copy);
        Assert.Equal(1f, copy!.LocalPosition.X, 4);
        Assert.Equal(3f, copy.LocalPosition.Z, 4);
        Assert.Equal(t3d.LocalRotation.W, copy.LocalRotation.W, 4);
        Assert.Equal(2f, copy.LocalScale.Y, 4);
    }

    [Fact]
    public void An2DOnlyActorGainsNo3DTransformOnLoad()
    {
        var scene = new Engine.Core.Scene();
        scene.AddActor(new Actor("Flat"));
        scene.Update(0.016f);

        var restored = SceneSerializer.Deserialize(SceneSerializer.Serialize(scene));
        restored.Update(0.016f);

        Assert.Null(restored.FindByName("Flat")!.GetComponent<Transform3D>());
    }

    [Fact]
    public void LayersAndTheirOrderSurviveARoundTrip()
    {
        var scene = new Engine.Core.Scene("Multi");
        scene.AddLayer("effects", 150);
        scene.AddActor(new Actor("Spark"), "effects");
        scene.Update(0.016f);

        var restored = SceneSerializer.Deserialize(SceneSerializer.Serialize(scene));
        var layer = restored.GetLayer("effects");

        Assert.NotNull(layer);
        Assert.Equal(150, layer!.Order);
    }

    [Fact]
    public void AnUnknownComponentTypeIsSkippedRatherThanThrowing()
    {
        const string json = """
        {
          "name": "Broken",
          "layers": [{
            "name": "default", "order": 0,
            "actors": [{
              "name": "Ghost", "tag": "Untagged", "layer": 0, "active": true,
              "position": [0, 0], "rotation": 0, "scale": [1, 1],
              "components": [{ "type": "Some.Missing.Type, NoSuchAssembly", "properties": {} }]
            }]
          }]
        }
        """;

        var scene = SceneSerializer.Deserialize(json);
        scene.Update(0.016f);
        Assert.NotNull(scene.FindByName("Ghost"));
    }
}

public class LocalizationTests
{
    [Fact]
    public void GetReturnsTheTranslationForTheCurrentLanguage()
    {
        Loc.Clear();
        Loc.LoadTable("en", """{ "ui.start": "Start" }""");
        Loc.LoadTable("fr", """{ "ui.start": "Commencer" }""");

        Assert.True(Loc.SetLanguage("fr"));
        Assert.Equal("Commencer", Loc.Get("ui.start"));
    }

    [Fact]
    public void AMissingKeyFallsBackToTheFallbackLanguage()
    {
        Loc.Clear();
        Loc.LoadTable("en", """{ "ui.quit": "Quit" }""");
        Loc.LoadTable("de", """{ }""");

        Loc.FallbackLanguage = "en";
        Loc.SetLanguage("de");

        Assert.Equal("Quit", Loc.Get("ui.quit"));
    }

    [Fact]
    public void AKeyMissingEverywhereReturnsTheKeyItself()
    {
        Loc.Clear();
        Loc.LoadTable("en", "{ }");
        Loc.SetLanguage("en");
        Assert.Equal("ui.nothing", Loc.Get("ui.nothing"));
    }

    [Fact]
    public void FormatSubstitutesNamedPlaceholders()
    {
        Loc.Clear();
        Loc.LoadTable("en", """{ "hud.score": "Score: {score} ({rank})" }""");
        Loc.SetLanguage("en");

        Assert.Equal("Score: 1200 (Gold)",
            Loc.Format("hud.score", ("score", 1200), ("rank", "Gold")));
    }

    [Fact]
    public void SetLanguageForAnUnloadedCodeIsRejected()
    {
        Loc.Clear();
        Loc.LoadTable("en", "{ }");
        Loc.SetLanguage("en");

        Assert.False(Loc.SetLanguage("jp"));
        Assert.Equal("en", Loc.CurrentLanguage);
    }

    [Fact]
    public void MalformedJsonIsReportedNotThrown()
    {
        Loc.Clear();
        Loc.LoadTable("bad", "{ not json");
        Assert.DoesNotContain("bad", Loc.AvailableLanguages);
    }
}

public class SceneSerializerClassTests
{
    private sealed class Marker : Component
    {
        public int Value { get; set; } = 1;
    }

    private sealed class Turret : Actor
    {
        public float Range { get; set; } = 3f;

        public Turret() : base("Turret")
        {
            // Added by the constructor, so a naive loader would duplicate it.
            AddComponent<Marker>();
        }
    }

    private sealed class Sensitive : Component
    {
        public int Shown { get; set; } = 2;

        [SceneIgnore]
        public int Hidden { get; set; } = 5;

        public int Runtime { get; private set; } = 3;

        public void Touch() => Runtime++;
    }

    [Fact]
    public void AnActorSubclassSurvivesARoundTrip()
    {
        var scene = new Engine.Core.Scene("classes");
        try
        {
            scene.AddActor(new Turret { Range = 9.5f });
            scene.FlushPendingActors();

            string json = SceneSerializer.Serialize(scene);
            Assert.Contains("\"class\": \"Turret\"", json);

            var restored = SceneSerializer.Deserialize(json);
            restored.FlushPendingActors();

            var turret = Assert.IsType<Turret>(restored.FindByName("Turret"));
            Assert.Equal(9.5f, turret.Range, 4);
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Fact]
    public void ComponentsAddedByASubclassConstructorAreNotDuplicatedOnLoad()
    {
        var scene = new Engine.Core.Scene("merge");
        try
        {
            var turret = scene.AddActor(new Turret());
            turret.GetComponent<Marker>()!.Value = 42;
            scene.FlushPendingActors();

            var restored = SceneSerializer.Deserialize(SceneSerializer.Serialize(scene));
            restored.FlushPendingActors();

            var copy = restored.FindByName("Turret")!;
            var markers = copy.GetAllComponents().OfType<Marker>().ToList();
            Assert.Single(markers);
            Assert.Equal(42, markers[0].Value);
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Fact]
    public void APlainActorWritesNoClassField()
    {
        var scene = new Engine.Core.Scene("plain");
        scene.AddActor(new Actor("Box"));
        scene.FlushPendingActors();

        Assert.DoesNotContain("\"class\"", SceneSerializer.Serialize(scene));
    }

    [Fact]
    public void AnUnknownActorClassFallsBackToActorAndKeepsItsComponents()
    {
        const string json = """
        {
          "name": "Lost",
          "layers": [{ "name": "default", "order": 0, "actors": [{
            "name": "Ghost", "class": "NoSuch.Turret",
            "components": [{ "type": "Camera3D", "properties": { "FieldOfView": 72.0 } }]
          }]}]
        }
        """;

        var scene = SceneSerializer.Deserialize(json);
        scene.FlushPendingActors();

        var ghost = scene.FindByName("Ghost");
        Assert.NotNull(ghost);
        Assert.IsType<Actor>(ghost);
        Assert.Equal(72f, ghost!.GetComponent<Camera3D>()!.FieldOfView, 3);

        // The class name is written back so the file is not silently downgraded.
        string again = SceneSerializer.Serialize(scene);
        Assert.Contains("\"class\": \"NoSuch.Turret\"", again);
        Assert.DoesNotContain("MissingActorClass", again);
    }

    [Fact]
    public void AnUnknownComponentRoundTripsAsAMissingComponentPlaceholder()
    {
        const string json = """
        {
          "name": "Lost",
          "layers": [{ "name": "default", "order": 0, "actors": [{
            "name": "Widget",
            "components": [{ "type": "Frobnicator", "properties": { "Speed": 3 } }]
          }]}]
        }
        """;

        var scene = SceneSerializer.Deserialize(json);
        scene.FlushPendingActors();

        var widget = scene.FindByName("Widget")!;
        var missing = Assert.Single(widget.GetAllComponents().OfType<MissingComponent>());
        Assert.Equal("Frobnicator", missing.TypeName);

        string again = SceneSerializer.Serialize(scene);
        Assert.Contains("\"type\": \"Frobnicator\"", again);
        Assert.Contains("\"Speed\": 3", again);
        Assert.DoesNotContain("MissingComponent", again);

        // Placeholders are for the loader, not the Add Component menu.
        Assert.DoesNotContain(ReflectionUtil.FindComponentTypes(), t => t == typeof(MissingComponent));
    }

    [Fact]
    public void ShortTypeNamesAreWrittenByDefaultAndResolve()
    {
        var scene = new Engine.Core.Scene("names");
        var actor = scene.AddActor(new Actor("Cam"));
        actor.AddComponent<Camera3D>();
        scene.FlushPendingActors();

        string json = SceneSerializer.Serialize(scene);
        Assert.Contains("\"type\": \"Camera3D\"", json);
        Assert.DoesNotContain("SexyBiscuit.Engine.Rendering.Camera3D,", json);

        var restored = SceneSerializer.Deserialize(json);
        restored.FlushPendingActors();
        Assert.NotNull(restored.FindByName("Cam")!.GetComponent<Camera3D>());

        string qualified = SceneSerializer.Serialize(scene, new SceneSerializerOptions { TypeNames = TypeNameStyle.AssemblyQualified });
        Assert.Contains("SexyBiscuit.Engine.Rendering.Camera3D, SexyBiscuit.Engine", qualified);
    }

    [Fact]
    public void ResolveComponentTypeAcceptsAssemblyQualifiedNamesFromOtherBuilds()
    {
        // A file saved by a different build carries a version the running assembly does not
        // have. The qualifier is stripped and the full name resolves anyway.
        var type = SceneSerializer.ResolveComponentType(
            "SexyBiscuit.Engine.Rendering.Camera3D, SexyBiscuit.Engine, Version=9.9.9.9, Culture=neutral, PublicKeyToken=null");

        Assert.Equal(typeof(Camera3D), type);
        Assert.Null(SceneSerializer.ResolveComponentType("Some.Missing.Type, NoSuchAssembly"));

        SceneSerializer.ClearTypeCache();
        Assert.Equal(typeof(Camera3D), SceneSerializer.ResolveComponentType("Camera3D"));
    }

    [Fact]
    public void IgnoredAndPrivateSetterPropertiesAreNotWritten()
    {
        var scene = new Engine.Core.Scene("filter");
        var actor = scene.AddActor(new Actor("S"));
        var sensitive = actor.AddComponent<Sensitive>();
        sensitive.Touch();
        scene.FlushPendingActors();

        string json = SceneSerializer.Serialize(scene);
        Assert.Contains("\"Shown\": 2", json);
        Assert.DoesNotContain("\"Hidden\"", json);
        Assert.DoesNotContain("\"Runtime\"", json);
    }
}

public class AssetPathAndScriptTests
{
    [Fact]
    public void TexturePathRoundTripsWithoutAnAssetManager()
    {
        var scene = new Engine.Core.Scene("sprites");
        var actor = scene.AddActor(new Actor("Hero"));
        actor.AddComponent<SpriteRenderer>().TexturePath = "Assets/Sprites/hero.png";
        scene.FlushPendingActors();

        var restored = SceneSerializer.Deserialize(SceneSerializer.Serialize(scene));
        restored.FlushPendingActors();

        var sprite = restored.FindByName("Hero")!.GetComponent<SpriteRenderer>()!;
        Assert.Equal("Assets/Sprites/hero.png", sprite.TexturePath);
        Assert.Null(sprite.Texture);     // no host, so the path waits
    }

    [Fact]
    public void MaterialProxiesWriteToAnOwnedMaterialThatSerialises()
    {
        var scene = new Engine.Core.Scene("materials");
        try
        {
            var actor = scene.AddActor(new Actor("Cube"));
            actor.AddComponent<Transform3D>();
            var mesh = actor.AddComponent<MeshRenderer>();
            scene.FlushPendingActors();

            mesh.AlbedoColor = Color.Red;
            mesh.Roughness   = 0.25f;

            // The shared default material must never pick up a scene's colour.
            Assert.NotSame(Material3D.Default, mesh.Materials[0]);
            Assert.Equal(Color.White, Material3D.Default.AlbedoColor);

            string json = SceneSerializer.Serialize(scene);
            Assert.DoesNotContain("\"AlbedoColor\": \"#FF0000FF\",\n          \"Metallic\"", json); // proxies are not written as component properties
            Assert.Contains("#FF0000FF", json);                                                     // the material itself is

            var restored = SceneSerializer.Deserialize(json);
            restored.FlushPendingActors();
            try
            {
                var copy = restored.FindByName("Cube")!.GetComponent<MeshRenderer>()!;
                Assert.Equal(Color.Red, copy.AlbedoColor);
                Assert.Equal(0.25f, copy.Roughness, 4);
            }
            finally
            {
                restored.Destroy();
            }
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Fact]
    public void ModelPathIsWrittenAndReadBack()
    {
        var scene = new Engine.Core.Scene("models");
        try
        {
            var actor = scene.AddActor(new Actor("Tree"));
            actor.AddComponent<Transform3D>();
            actor.AddComponent<MeshRenderer>().ModelPath = "Assets/Models/tree.obj";
            scene.FlushPendingActors();

            string json = SceneSerializer.Serialize(scene);
            Assert.Contains("\"ModelPath\": \"Assets/Models/tree.obj\"", json);

            var restored = SceneSerializer.Deserialize(json);
            restored.FlushPendingActors();
            try
            {
                Assert.Equal("Assets/Models/tree.obj", restored.FindByName("Tree")!.GetComponent<MeshRenderer>()!.ModelPath);
            }
            finally
            {
                restored.Destroy();
            }
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Fact]
    public void ScriptPathSetAfterAttachStartsTheScript()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sb-scripts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string script = Path.Combine(dir, "spin.js");
        File.WriteAllText(script, "var started = false;\nfunction onStart() { started = true; }\n");

        string? previousRoot = SexyBiscuit.Engine.Core.ProjectPaths.Root;
        try
        {
            SexyBiscuit.Engine.Core.ProjectPaths.Root = dir;

            var scene = new Engine.Core.Scene("scripts");
            var actor = scene.AddActor(new Actor("Spinner"));

            // Attach first, then set the path — the order every loader and tool uses.
            var component = actor.AddComponent<SexyBiscuit.Engine.Scripting.ScriptComponent>();
            component.ScriptPath = "spin.js";

            Assert.NotNull(component.Runtime);
            Assert.True(component.Runtime!.HasFunction("onStart"));

            scene.FlushPendingActors();
            Assert.Equal("true", component.Runtime.Evaluate("String(started)")?.ToString());
        }
        finally
        {
            SexyBiscuit.Engine.Core.ProjectPaths.Root = previousRoot;
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadSceneReadsTheFileOnTheNextUpdate()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sb-scenes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Scenes"));

        var authored = new Engine.Core.Scene("Level1");
        authored.AddActor(new Actor("Hero"));
        authored.FlushPendingActors();
        SceneSerializer.SaveToFile(authored, Path.Combine(dir, "Scenes", "Level1.scene"));

        string? previousRoot = SexyBiscuit.Engine.Core.ProjectPaths.Root;
        var manager = new SceneManager();
        try
        {
            SexyBiscuit.Engine.Core.ProjectPaths.Root = dir;

            // The extension is optional, as ProjectSettings.json's StartScene omits it.
            manager.LoadScene("Scenes/Level1");
            manager.Update(0.016f);

            Assert.NotNull(manager.ActiveScene);
            Assert.Equal("Level1", manager.ActiveScene!.Name);
            Assert.NotNull(manager.ActiveScene.FindByName("Hero"));

            // A missing file still yields an empty scene rather than an exception.
            manager.LoadScene("Scenes/Nowhere");
            manager.Update(0.016f);
            Assert.Equal("Nowhere", manager.ActiveScene!.Name);
            Assert.Empty(manager.ActiveScene.Layers.SelectMany(l => l.Actors));
        }
        finally
        {
            manager.ActiveScene?.Destroy();
            SexyBiscuit.Engine.Core.ProjectPaths.Root = previousRoot;
            Directory.Delete(dir, recursive: true);
        }
    }
}
