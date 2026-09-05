using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
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
