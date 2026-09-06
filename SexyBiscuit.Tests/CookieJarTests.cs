using System.Text.Json;
using System.Text.Json.Nodes;
using SexyBiscuit.Engine.Mcp;
using SexyBiscuit.Engine.Mcp.Tools;
using SexyBiscuit.Engine.CookieJar;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// A scratch jar and a scratch project on disk. Everything here is real files: the CookieJar's job
/// is moving files around safely, and a fake file system would test the fake.
/// </summary>
internal sealed class CookieFixture : IDisposable
{
    public string Root        { get; }
    public string JarPath     { get; }
    public string ProjectRoot { get; }

    public CookieFixture(string tag)
    {
        Root        = Path.Combine(Path.GetTempPath(), $"sb-cookie-{tag}-" + Guid.NewGuid().ToString("N"));
        JarPath     = Path.Combine(Root, "jar");
        ProjectRoot = Path.Combine(Root, "project");
        Directory.CreateDirectory(JarPath);
        Directory.CreateDirectory(ProjectRoot);
    }

    /// <summary>Writes a cookie folder. <paramref name="files"/> are cookie-relative.</summary>
    public string WriteCookie(
        string                       id,
        string?                      manifestJson = null,
        IDictionary<string, string>? files        = null,
        bool                         withAgent    = true,
        string?                      jarPath      = null)
    {
        string dir = Path.Combine(jarPath ?? JarPath, id);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, Cookie.ManifestFileName), manifestJson ?? Manifest(id));
        if (withAgent) File.WriteAllText(Path.Combine(dir, Cookie.AgentFileName), $"# {id}\n\nDrop it on an actor.\n");

        foreach (var (relative, content) in files ?? new Dictionary<string, string>())
        {
            string path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return dir;
    }

    public static string Manifest(
        string  id,
        string  version   = "1.0.0",
        string? requires  = null,
        string? provides  = null,
        string  engines   = "\"csharp\"",
        string  summary   = "A test cookie.",
        string? tags      = null,
        string? files     = null)
        => $$"""
        {
          "schema": 1,
          "id": "{{id}}",
          "name": "{{id}}",
          "version": "{{version}}",
          "summary": "{{summary}}",
          "tags": [{{tags ?? ""}}],
          "engines": [{{engines}}],
          "requires": [{{requires ?? ""}}],
          "provides": { "components": [{{provides ?? ""}}] }
          {{(files == null ? "" : ", \"files\": " + files)}}
        }
        """;

    public CookieCatalogue Scan(params CookieJarSource[] jars)
        => CookieCatalogue.Scan(jars.Length > 0 ? jars : new[] { CookieJarSource.Folder("test", JarPath) });

    public CookieProjectContext Project() => new(ProjectRoot, "TestGame");

    public string ProjectFile(string relative)
        => Path.Combine(ProjectRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class CookieManifestTests
{
    [Fact]
    public void AManifestWithoutAnIdIsRejected()
    {
        var manifest = CookieManifest.Parse("""{ "schema": 1, "name": "X", "version": "1.0.0", "summary": "s", "engines": ["csharp"] }""");
        Assert.Contains(manifest.Validate(), p => p.Severity == CookieSeverity.Error && p.Message.Contains("id must be"));
    }

    [Fact]
    public void AnIdThatDoesNotMatchItsFolderIsRejected()
    {
        // The folder name is how a jar addresses a cookie, so the two disagreeing means one of
        // them is a lie and the catalogue cannot know which.
        var manifest = CookieManifest.Parse(CookieFixture.Manifest("double-jump"));
        Assert.Contains(manifest.Validate("triple-jump"), p => p.Message.Contains("does not match its folder"));
        Assert.Empty(manifest.Validate("double-jump"));
    }

    [Theory]
    [InlineData("double-jump", true)]
    [InlineData("a1", true)]
    [InlineData("Double-Jump", false)]
    [InlineData("-leading", false)]
    [InlineData("x", false)]
    [InlineData("has space", false)]
    public void AnIdIsKebabCaseAndAtLeastTwoCharacters(string id, bool valid)
        => Assert.Equal(valid, CookieManifest.IsValidId(id));

    [Fact]
    public void ANamespaceIsDerivedFromTheIdWhenItIsNotDeclared()
    {
        var manifest = CookieManifest.Parse(CookieFixture.Manifest("double-jump"));
        Assert.Equal("Cookies.DoubleJump", manifest.EffectiveNamespace);
        Assert.Equal("DoubleJump", manifest.PascalId);
    }

    [Fact]
    public void AVersionOrdersByMajorThenMinorThenPatch()
    {
        Assert.True(CookieVersion.Parse("1.2.0").CompareTo(CookieVersion.Parse("1.10.0")) < 0);
        Assert.True(CookieVersion.Parse("2.0.0").CompareTo(CookieVersion.Parse("1.99.99")) > 0);
        Assert.Equal(0, CookieVersion.Parse("1.2.0").CompareTo(CookieVersion.Parse("1.2.0-beta+9")));
        Assert.False(CookieVersion.TryParse("not-a-version", out _));
    }
}

public class CookieCatalogueTests
{
    [Fact]
    public void ABrokenCookieDoesNotBlankTheCatalogue()
    {
        // One stray comma in one jar must not cost the user their whole library.
        using var fixture = new CookieFixture("broken");
        fixture.WriteCookie("good");
        fixture.WriteCookie("broken", manifestJson: "{ this is not json");

        var catalogue = fixture.Scan();

        Assert.Single(catalogue.All);
        Assert.Equal("good", catalogue.All[0].Id);
        Assert.Contains(catalogue.Problems, p => p.Subject == "broken" && p.Severity == CookieSeverity.Error);
    }

    [Fact]
    public void ACookieWithoutAgentInstructionsIsRejected()
    {
        using var fixture = new CookieFixture("noagent");
        fixture.WriteCookie("silent", withAgent: false);

        var catalogue = fixture.Scan();

        Assert.Empty(catalogue.All);
        Assert.Contains(catalogue.Problems, p => p.Message.Contains("AGENT.md is required"));
    }

    [Fact]
    public void TwoJarsWithTheSameIdResolveToTheFirstAndRecordTheOther()
    {
        using var fixture = new CookieFixture("shadow");
        string second = Path.Combine(fixture.Root, "jar2");
        Directory.CreateDirectory(second);

        fixture.WriteCookie("jump", CookieFixture.Manifest("jump", version: "1.0.0"));
        fixture.WriteCookie("jump", CookieFixture.Manifest("jump", version: "9.0.0"), jarPath: second);

        var catalogue = CookieCatalogue.Scan(new[]
        {
            CookieJarSource.Folder("first", fixture.JarPath),
            CookieJarSource.Folder("second", second),
        });

        Assert.Equal("first", catalogue.Find("jump")!.JarName);
        Assert.Contains(catalogue.Shadowed, s => s.Id == "jump" && s.ShadowedJar == "second");
    }

    [Fact]
    public void SearchRanksAnIdMatchAboveASummaryMatch()
    {
        using var fixture = new CookieFixture("search");
        fixture.WriteCookie("dash", CookieFixture.Manifest("dash", summary: "A quick burst of speed."));
        fixture.WriteCookie("wall-run", CookieFixture.Manifest("wall-run", summary: "Run along a wall, then dash off it."));

        var hits = fixture.Scan().Search("dash");

        Assert.Equal("dash", hits[0].Cookie.Id);
        Assert.Equal("wall-run", hits[1].Cookie.Id);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public void SearchFiltersByTagAndEngine()
    {
        using var fixture = new CookieFixture("filter");
        fixture.WriteCookie("dash", CookieFixture.Manifest("dash", tags: "\"movement\"", engines: "\"csharp\""));
        fixture.WriteCookie("hud",  CookieFixture.Manifest("hud",  tags: "\"ui\"",       engines: "\"js\""));

        var catalogue = fixture.Scan();

        Assert.Equal("dash", Assert.Single(catalogue.Search(tags: new[] { "movement" })).Cookie.Id);
        Assert.Equal("hud",  Assert.Single(catalogue.Search(engine: "js")).Cookie.Id);
        Assert.Equal(2, catalogue.Search().Count);
    }

    [Fact]
    public void AMissingJarFolderIsAWarningNotACrash()
    {
        using var fixture = new CookieFixture("missing");
        var catalogue = CookieCatalogue.Scan(new[] { CookieJarSource.Folder("gone", Path.Combine(fixture.Root, "nope")) });

        Assert.Empty(catalogue.All);
        Assert.Contains(catalogue.Problems, p => p.Severity == CookieSeverity.Warning);
    }
}

public class CookieDependencyTests
{
    [Fact]
    public void DependenciesComeBeforeTheirDependents()
    {
        using var fixture = new CookieFixture("deps");
        fixture.WriteCookie("base");
        fixture.WriteCookie("middle", CookieFixture.Manifest("middle", requires: "\"base\""));
        fixture.WriteCookie("top",    CookieFixture.Manifest("top",    requires: "\"middle\""));

        var resolved = CookieDependencies.Resolve(fixture.Scan(), new[] { "top" });

        Assert.Equal(new[] { "base", "middle", "top" }, resolved.Ordered.Select(c => c.Id));
        Assert.Empty(resolved.Missing);
        Assert.Empty(resolved.Cycles);
    }

    [Fact]
    public void ACycleInRequiresIsReportedRatherThanFollowed()
    {
        using var fixture = new CookieFixture("cycle");
        fixture.WriteCookie("dash", CookieFixture.Manifest("dash", requires: "\"wall-run\""));
        fixture.WriteCookie("wall-run", CookieFixture.Manifest("wall-run", requires: "\"dash\""));

        var resolved = CookieDependencies.Resolve(fixture.Scan(), new[] { "dash" });

        Assert.NotEmpty(resolved.Cycles);
        Assert.Contains(resolved.Cycles, cycle => cycle.Contains("dash") && cycle.Contains("wall-run"));
    }

    [Fact]
    public void AMissingRequirementIsNamed()
    {
        using var fixture = new CookieFixture("missingdep");
        fixture.WriteCookie("top", CookieFixture.Manifest("top", requires: "\"absent\""));

        var resolved = CookieDependencies.Resolve(fixture.Scan(), new[] { "top" });

        Assert.Equal("absent", Assert.Single(resolved.Missing));
    }
}

public class CookiePlannerTests
{
    private static IDictionary<string, string> SourceFile(string id = "dash") =>
        new Dictionary<string, string> { [$"Source/{CookieManifest.Parse(CookieFixture.Manifest(id)).PascalId}.cs"] = "// code\n" };

    [Fact]
    public void APlanRefusesADestinationOutsideTheProjectRoot()
    {
        // A manifest is data from a jar the user may have added a minute ago. It does not get to
        // choose where in the file system it lands.
        using var fixture = new CookieFixture("escape");
        fixture.WriteCookie("dash",
            CookieFixture.Manifest("dash", files: """[{ "from": "Source/Dash.cs", "to": "../../evil.cs" }]"""),
            new Dictionary<string, string> { ["Source/Dash.cs"] = "// code\n" });

        var catalogue = fixture.Scan();
        var plan = CookiePlanner.Plan(catalogue.Find("dash")!, fixture.Project(), new CookieLockFile());

        Assert.False(plan.IsApplicable);
        Assert.Contains(plan.Conflicts, c => c.Kind == CookieConflictKind.PathEscape);
        Assert.All(plan.Files, f => Assert.Equal(PlannedFileAction.Blocked, f.Action));
    }

    [Theory]
    [InlineData("bin/sneaky.cs")]
    [InlineData("obj/sneaky.cs")]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData(".sexybiscuit/autosave/x.scene")]
    [InlineData("Game.csproj")]
    public void APlanRefusesTheFoldersAndFilesAProjectOwns(string destination)
    {
        using var fixture = new CookieFixture("forbidden");
        fixture.WriteCookie("dash",
            CookieFixture.Manifest("dash", files: $$"""[{ "from": "Source/Dash.cs", "to": "{{destination}}" }]"""),
            new Dictionary<string, string> { ["Source/Dash.cs"] = "// code\n" });

        var plan = CookiePlanner.Plan(fixture.Scan().Find("dash")!, fixture.Project(), new CookieLockFile());

        Assert.Contains(plan.Conflicts, c => c.Kind == CookieConflictKind.PathEscape);
    }

    [Fact]
    public void APlanReportsAConflictWhenADifferentFileIsAlreadyThere()
    {
        using var fixture = new CookieFixture("exists");
        fixture.WriteCookie("dash", files: new Dictionary<string, string> { ["Source/Dash.cs"] = "// new\n" });

        string existing = fixture.ProjectFile("Source/Cookies/Dash/Dash.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "// mine, thanks\n");

        var cookie = fixture.Scan().Find("dash")!;

        var blocked = CookiePlanner.Plan(cookie, fixture.Project(), new CookieLockFile());
        Assert.False(blocked.IsApplicable);
        Assert.Contains(blocked.Conflicts, c => c.Kind == CookieConflictKind.FileExists);

        var forced = CookiePlanner.Plan(cookie, fixture.Project(), new CookieLockFile(), new CookieInstallOptions(Overwrite: true));
        Assert.True(forced.IsApplicable);
        Assert.Equal(PlannedFileAction.Overwrite, forced.Files.Single().Action);
    }

    [Fact]
    public void APlanSkipsAFileThatIsAlreadyByteForByteIdentical()
    {
        using var fixture = new CookieFixture("identical");
        fixture.WriteCookie("dash", files: new Dictionary<string, string> { ["Source/Dash.cs"] = "// same\n" });

        string existing = fixture.ProjectFile("Source/Cookies/Dash/Dash.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "// same\n");

        var plan = CookiePlanner.Plan(fixture.Scan().Find("dash")!, fixture.Project(), new CookieLockFile());

        Assert.True(plan.IsApplicable);
        Assert.Equal(PlannedFileAction.SkipIdentical, plan.Files.Single().Action);
        Assert.Empty(plan.Writable);
    }

    [Fact]
    public void APlanReportsATypeNameCollisionWithAnInstalledCookie()
    {
        // A scene names a component by its short name, so two cookies exporting the same one is
        // ambiguous at load time. Better to say so before anything is written.
        using var fixture = new CookieFixture("collide");
        fixture.WriteCookie("dash", CookieFixture.Manifest("dash", provides: "\"Dash\""));

        var lockFile = new CookieLockFile();
        lockFile.Set(new InstalledCookie { Id = "sprint", Provides = new CookieProvides { Components = { "Dash" } } });

        var plan = CookiePlanner.Plan(fixture.Scan().Find("dash")!, fixture.Project(), lockFile);

        Assert.False(plan.IsApplicable);
        var clash = Assert.Single(plan.Conflicts, c => c.Kind == CookieConflictKind.TypeNameCollision);
        Assert.Contains("sprint", clash.Detail);
    }

    [Fact]
    public void AnUntrustedGitJarBlocksInstalling()
    {
        // Installing compiles and runs the cookie's code, and the assistant runs without prompts.
        using var fixture = new CookieFixture("untrusted");
        fixture.WriteCookie("dash");

        var jar = new CookieJarSource("remote", CookieJarKind.Git, fixture.JarPath, RemoteUrl: "https://example.invalid/jar.git");
        var plan = CookiePlanner.Plan(CookieCatalogue.Scan(new[] { jar }).Find("dash")!, fixture.Project(), new CookieLockFile(), jar: jar);

        Assert.False(plan.IsApplicable);
        Assert.Contains(plan.Conflicts, c => c.Kind == CookieConflictKind.UntrustedJar);

        var trusted = jar with { Trusted = true };
        Assert.True(CookiePlanner.Plan(CookieCatalogue.Scan(new[] { trusted }).Find("dash")!,
                                       fixture.Project(), new CookieLockFile(), jar: trusted).IsApplicable);
    }

    [Fact]
    public void AFileMatchingNoRuleIsReportedRatherThanSilentlyDropped()
    {
        using var fixture = new CookieFixture("unmapped");
        fixture.WriteCookie("dash", files: new Dictionary<string, string> { ["Notes/todo.txt"] = "later\n" });

        var plan = CookiePlanner.Plan(fixture.Scan().Find("dash")!, fixture.Project(), new CookieLockFile());

        Assert.True(plan.IsApplicable);   // a stray file is a caution, not a refusal
        Assert.Contains(plan.Conflicts, c => c.Kind == CookieConflictKind.UnmappedFile && !c.Blocking);
    }
}

public class CookieInstallTests
{
    private const string Prefab = """
    { "name": "Brick", "components": [ { "type": "MeshRenderer",
      "properties": { "AlbedoTexturePath": "Assets/brick.png" } } ] }
    """;

    private static CookieFixture WithAssetCookie(string tag)
    {
        var fixture = new CookieFixture(tag);
        fixture.WriteCookie("brick-wall", CookieFixture.Manifest("brick-wall", provides: "\"BrickWall\""),
            new Dictionary<string, string>
            {
                ["Source/BrickWall.cs"] = "namespace Cookies.BrickWall;\npublic sealed class BrickWall { }\n",
                ["Assets/brick.png"]    = "not really a png",
                ["Scenes/Brick.prefab"] = Prefab,
            });
        return fixture;
    }

    [Fact]
    public void InstallingRewritesCookieRelativeAssetPathsToProjectRelativeOnes()
    {
        // The cookie refers to its own asset as Assets/brick.png; once installed that file lives
        // under Assets/Cookies/brick-wall/, and the prefab has to follow it.
        using var fixture = WithAssetCookie("rewrite");
        var lockFile = new CookieLockFile();
        var plan = CookiePlanner.Plan(fixture.Scan().Find("brick-wall")!, fixture.Project(), lockFile);

        var outcome = CookieInstaller.Apply(plan, fixture.Project(), lockFile);

        string installed = File.ReadAllText(fixture.ProjectFile("Scenes/Cookies/brick-wall/Brick.prefab"));
        var node = JsonNode.Parse(installed)!;
        Assert.Equal("Assets/Cookies/brick-wall/brick.png",
                     node["components"]![0]!["properties"]!["AlbedoTexturePath"]!.GetValue<string>());
        Assert.True(File.Exists(fixture.ProjectFile("Assets/Cookies/brick-wall/brick.png")));
        Assert.Empty(outcome.Unresolved);
    }

    [Fact]
    public void InstallingWritesALockEntryWithAHashForEveryFile()
    {
        using var fixture = WithAssetCookie("lock");
        var lockFile = new CookieLockFile();
        var plan = CookiePlanner.Plan(fixture.Scan().Find("brick-wall")!, fixture.Project(), lockFile);

        CookieInstaller.Apply(plan, fixture.Project(), lockFile);

        var reloaded = CookieLockFile.Load(fixture.ProjectRoot);
        var entry = Assert.Single(reloaded.Cookies);
        Assert.Equal("brick-wall", entry.Id);
        Assert.Equal("Cookies.BrickWall", entry.Namespace);
        Assert.Equal(3, entry.Files.Count);
        Assert.All(entry.Files, f => Assert.Equal(64, f.Sha256.Length));
        Assert.True(plan.RequiresBuild);
    }

    [Fact]
    public void InstallingTwiceChangesNothingTheSecondTime()
    {
        using var fixture = WithAssetCookie("idempotent");
        var lockFile = new CookieLockFile();
        var cookie   = fixture.Scan().Find("brick-wall")!;

        CookieInstaller.Apply(CookiePlanner.Plan(cookie, fixture.Project(), lockFile), fixture.Project(), lockFile);
        string prefabAfterFirst = File.ReadAllText(fixture.ProjectFile("Scenes/Cookies/brick-wall/Brick.prefab"));

        var second = CookiePlanner.Plan(cookie, fixture.Project(), lockFile);
        Assert.Contains(second.Conflicts, c => c.Kind == CookieConflictKind.AlreadyInstalled);

        var forced = CookiePlanner.Plan(cookie, fixture.Project(), lockFile, new CookieInstallOptions(Overwrite: true));
        CookieInstaller.Apply(forced, fixture.Project(), lockFile);

        Assert.Equal(prefabAfterFirst, File.ReadAllText(fixture.ProjectFile("Scenes/Cookies/brick-wall/Brick.prefab")));
        Assert.Single(CookieLockFile.Load(fixture.ProjectRoot).Cookies);
    }

    [Fact]
    public void AFailedInstallLeavesNothingBehind()
    {
        using var fixture = WithAssetCookie("rollback");
        var lockFile = new CookieLockFile();
        var plan = CookiePlanner.Plan(fixture.Scan().Find("brick-wall")!, fixture.Project(), lockFile);

        // Pull a source file out from under the plan, the way a jar refresh mid-install would.
        File.Delete(Path.Combine(fixture.JarPath, "brick-wall", "Assets", "brick.png"));

        Assert.Throws<CookieException>(() => CookieInstaller.Apply(plan, fixture.Project(), lockFile));

        Assert.False(File.Exists(fixture.ProjectFile("Source/Cookies/BrickWall/BrickWall.cs")));
        Assert.False(File.Exists(CookieLockFile.PathFor(fixture.ProjectRoot)));
        Assert.Empty(lockFile.Cookies);
    }

    [Fact]
    public void UninstallingRemovesOnlyTheFilesItInstalled()
    {
        using var fixture = WithAssetCookie("uninstall");
        var lockFile = new CookieLockFile();
        CookieInstaller.Apply(CookiePlanner.Plan(fixture.Scan().Find("brick-wall")!, fixture.Project(), lockFile),
                              fixture.Project(), lockFile);

        // Something of the author's, in a folder the cookie also uses.
        string mine = fixture.ProjectFile("Assets/Cookies/brick-wall/mine.png");
        File.WriteAllText(mine, "mine");

        var plan = CookieUninstaller.Plan(fixture.ProjectRoot, lockFile, "brick-wall");
        var outcome = CookieUninstaller.Apply(fixture.ProjectRoot, lockFile, plan);

        Assert.Equal(3, outcome.Removed.Count);
        Assert.True(File.Exists(mine));                                   // not ours, not touched
        Assert.False(File.Exists(fixture.ProjectFile("Source/Cookies/BrickWall/BrickWall.cs")));
        Assert.Empty(CookieLockFile.Load(fixture.ProjectRoot).Cookies);
        Assert.DoesNotContain("Assets/Cookies/brick-wall", outcome.RemovedDirectories);  // still has mine.png
    }

    [Fact]
    public void UninstallingKeepsAFileTheAuthorHasEdited()
    {
        using var fixture = WithAssetCookie("edited");
        var lockFile = new CookieLockFile();
        CookieInstaller.Apply(CookiePlanner.Plan(fixture.Scan().Find("brick-wall")!, fixture.Project(), lockFile),
                              fixture.Project(), lockFile);

        string edited = fixture.ProjectFile("Source/Cookies/BrickWall/BrickWall.cs");
        File.WriteAllText(edited, "// I improved this\n");

        var plan = CookieUninstaller.Plan(fixture.ProjectRoot, lockFile, "brick-wall");
        var outcome = CookieUninstaller.Apply(fixture.ProjectRoot, lockFile, plan);

        Assert.True(File.Exists(edited));
        Assert.Contains(outcome.Kept, k => k.Path.EndsWith("BrickWall.cs") && k.Reason.Contains("edited"));
    }

    [Fact]
    public void UninstallingIsRefusedWhileAnotherCookieNeedsIt()
    {
        using var fixture = new CookieFixture("needed");
        var lockFile = new CookieLockFile();
        lockFile.Set(new InstalledCookie { Id = "base" });
        lockFile.Set(new InstalledCookie { Id = "top", Requires = { "base" } });

        var plan = CookieUninstaller.Plan(fixture.ProjectRoot, lockFile, "base");

        Assert.False(plan.IsApplicable);
        Assert.Equal("top", Assert.Single(plan.Dependents));
        Assert.Throws<CookieException>(() => CookieUninstaller.Apply(fixture.ProjectRoot, lockFile, plan));
        Assert.True(CookieUninstaller.Plan(fixture.ProjectRoot, lockFile, "base", force: true).IsApplicable);
    }
}

public class CookieBakeTests
{
    private const string Prefab = """
    { "name": "Thing", "components": [ { "type": "MeshRenderer",
      "properties": { "AlbedoTexturePath": "Assets/tex.png" } } ] }
    """;

    private static void WriteProjectFile(CookieFixture fixture, string relative, string content)
    {
        string path = fixture.ProjectFile(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static BakeRequest Request(CookieFixture fixture) => new(
        Project:           fixture.Project(),
        Id:                "spinner",
        Name:              "Spinner",
        Summary:           "Spins an actor.",
        AgentInstructions: "Add a Spinner to any actor and set DegreesPerSecond.",
        DestinationJar:    fixture.JarPath,
        Files:             new[] { "Source/Spinner.cs", "Assets/tex.png", "Scenes/Thing.prefab" },
        NextSteps:         new[] { "Add a Spinner component to an actor." });

    private static CookieFixture ProjectToBake(string tag)
    {
        var fixture = new CookieFixture(tag);
        WriteProjectFile(fixture, "Source/Spinner.cs",
            "namespace TestGame.Components;\n\npublic sealed class Spinner : Component\n{\n    public float DegreesPerSecond { get; set; } = 90f;\n}\n");
        WriteProjectFile(fixture, "Assets/tex.png", "not really a png");
        WriteProjectFile(fixture, "Scenes/Thing.prefab", Prefab);
        return fixture;
    }

    [Fact]
    public void BakingDerivesWhatTheCookieProvidesAndMovesItToItsOwnNamespace()
    {
        using var fixture = ProjectToBake("bake");

        var plan = CookieBaker.Plan(Request(fixture));
        var outcome = CookieBaker.Apply(plan);

        Assert.Equal(new[] { "csharp" }, outcome.Manifest.Engines);
        Assert.Equal("Spinner", Assert.Single(outcome.Manifest.Provides.Components));
        Assert.Equal("Thing.prefab", Assert.Single(outcome.Manifest.Provides.Prefabs));
        Assert.Contains("Source/Spinner.cs", outcome.Renamespaced);
        Assert.Contains("namespace Cookies.Spinner;",
                        File.ReadAllText(Path.Combine(outcome.Directory, "Source", "Spinner.cs")));
        Assert.DoesNotContain(outcome.Validation, p => p.Severity == CookieSeverity.Error);
    }

    [Fact]
    public void BakingThenInstallingIntoAFreshProjectRoundTrips()
    {
        // The whole point of the jar: work done in one game turns up, wired the same way, in the
        // next one. Asset references have to survive both directions.
        using var fixture = ProjectToBake("roundtrip");
        CookieBaker.Apply(CookieBaker.Plan(Request(fixture)));

        // In the cookie, the reference is the cookie's own copy.
        string baked = File.ReadAllText(Path.Combine(fixture.JarPath, "spinner", "Scenes", "Thing.prefab"));
        Assert.Equal("Assets/tex.png",
                     JsonNode.Parse(baked)!["components"]![0]!["properties"]!["AlbedoTexturePath"]!.GetValue<string>());

        // A different project entirely.
        string freshRoot = Path.Combine(fixture.Root, "fresh");
        Directory.CreateDirectory(freshRoot);
        var fresh    = new CookieProjectContext(freshRoot, "FreshGame");
        var lockFile = new CookieLockFile();

        var cookie = fixture.Scan().Find("spinner")!;
        CookieInstaller.Apply(CookiePlanner.Plan(cookie, fresh, lockFile), fresh, lockFile);

        string installed = File.ReadAllText(Path.Combine(freshRoot, "Scenes", "Cookies", "spinner", "Thing.prefab"));
        Assert.Equal("Assets/Cookies/spinner/tex.png",
                     JsonNode.Parse(installed)!["components"]![0]!["properties"]!["AlbedoTexturePath"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(freshRoot, "Assets", "Cookies", "spinner", "tex.png")));
        Assert.Contains("namespace Cookies.Spinner;",
                        File.ReadAllText(Path.Combine(freshRoot, "Source", "Cookies", "Spinner", "Spinner.cs")));
    }

    [Fact]
    public void BakingStripsTheCookiesFolderFromFilesAnotherCookieInstalled()
    {
        // Re-baking something installed from a cookie must not nest Cookies/x/Cookies/y.
        using var fixture = new CookieFixture("nested");
        WriteProjectFile(fixture, "Source/Cookies/Dash/Dash.cs", "namespace Cookies.Dash;\npublic sealed class Dash : Component { }\n");

        var request = Request(fixture) with { Files = new[] { "Source/Cookies/Dash/Dash.cs" }, Id = "dash-two" };
        var outcome = CookieBaker.Apply(CookieBaker.Plan(request));

        Assert.Equal("Source/Dash.cs", Assert.Single(outcome.Files));
    }

    [Fact]
    public void AFileOutsideTheKnownFoldersIsReportedRatherThanBaked()
    {
        using var fixture = ProjectToBake("stray");
        WriteProjectFile(fixture, "Notes/todo.txt", "later");

        var plan = CookieBaker.Plan(Request(fixture) with { Files = new[] { "Notes/todo.txt" } });

        Assert.True(plan.IsApplicable);
        Assert.Contains(plan.Conflicts, c => c.Subject == "Notes/todo.txt" && !c.Blocking);
        Assert.Empty(plan.Files);
    }
}

public class CookieValidatorTests
{
    [Fact]
    public void AProvidedPrefabThatIsNotInTheCookieIsAnError()
    {
        using var fixture = new CookieFixture("validate");
        string dir = fixture.WriteCookie("dash", CookieFixture.Manifest("dash"));
        File.WriteAllText(Path.Combine(dir, Cookie.ManifestFileName),
            CookieFixture.Manifest("dash").Replace("\"provides\": { \"components\": [] }",
                                                   "\"provides\": { \"prefabs\": [\"Absent.prefab\"] }"));

        var cookie = fixture.Scan().Find("dash")!;
        Assert.Contains(CookieValidator.Validate(cookie),
                        p => p.Severity == CookieSeverity.Error && p.Message.Contains("Absent.prefab"));
    }

    [Fact]
    public void DeclaringAnEngineWithoutShippingItsCodeIsAWarning()
    {
        using var fixture = new CookieFixture("engines");
        fixture.WriteCookie("dash", CookieFixture.Manifest("dash", engines: "\"csharp\", \"js\""),
                            new Dictionary<string, string> { ["Source/Dash.cs"] = "namespace Cookies.Dash;\n" });

        var problems = CookieValidator.Validate(fixture.Scan().Find("dash")!);

        Assert.Contains(problems, p => p.Severity == CookieSeverity.Warning && p.Message.Contains("'js' engine"));
        Assert.DoesNotContain(problems, p => p.Message.Contains("'csharp' engine"));
    }

    [Fact]
    public void DeepValidationFlagsAReferenceTheCookieDoesNotShip()
    {
        using var fixture = new CookieFixture("deep");
        fixture.WriteCookie("dash", CookieFixture.Manifest("dash"), new Dictionary<string, string>
        {
            ["Source/Dash.cs"]      = "namespace Cookies.Dash;\n",
            ["Scenes/Dash.prefab"]  = """{ "name": "Dash", "components": [ { "type": "SpriteRenderer", "properties": { "TexturePath": "Assets/missing.png" } } ] }""",
        });

        var problems = CookieValidator.Validate(fixture.Scan().Find("dash")!, deep: true);

        Assert.Contains(problems, p => p.Message.Contains("Assets/missing.png"));
    }
}

/// <summary>
/// The tools, exercised the way a client reaches them: by name through a registry, so the schema
/// and the argument binding stay in the loop.
/// </summary>
internal sealed class CookieToolHarness : IDisposable
{
    public CookieFixture     Fixture  { get; }
    public TestCookieHost    Host     { get; }
    public McpToolRegistry   Registry { get; }

    public CookieToolHarness(string tag)
    {
        Fixture  = new CookieFixture(tag);
        Host     = new TestCookieHost(Fixture);
        Registry = new McpToolRegistry(InlineMcpDispatcher.Instance);
        Registry.RegisterInstance(new CookieTools(Host));
    }

    public McpToolResult Call(string tool, object? args = null)
        => Registry.InvokeAsync(tool, args == null ? null : JsonSerializer.SerializeToElement(args), McpCallContext.None)
                   .GetAwaiter().GetResult();

    public JsonNode Ok(string tool, object? args = null)
    {
        var result = Call(tool, args);
        Assert.False(result.IsError, result.FirstText);
        Assert.NotNull(result.StructuredContent);
        return result.StructuredContent!;
    }

    public McpToolResult Fails(string tool, object? args = null)
    {
        var result = Call(tool, args);
        Assert.True(result.IsError, "expected an error but got: " + result.FirstText);
        return result;
    }

    public void Dispose() => Fixture.Dispose();
}

/// <summary>A host over the scratch fixture, recording what the editor would have been asked to do.</summary>
internal sealed class TestCookieHost : ICookieHost
{
    private readonly CookieFixture _fixture;
    private CookieCatalogue? _catalogue;

    public TestCookieHost(CookieFixture fixture)
    {
        _fixture = fixture;
        Jars     = new[] { CookieJarSource.Folder("test", fixture.JarPath) };
        Lock     = CookieLockFile.Load(fixture.ProjectRoot);
    }

    public IReadOnlyList<CookieJarSource> Jars           { get; set; }
    public CookieProjectContext?          Project        { get; set; }
    public CookieLockFile                 Lock           { get; }
    public string                         DefaultBakeJar => _fixture.JarPath;

    public bool BuildRequested  { get; private set; }
    public bool ConfirmAnswer   { get; set; } = true;
    public int  ConfirmsAsked   { get; private set; }

    public CookieCatalogue Catalogue(bool refresh = false)
    {
        if (refresh || _catalogue == null) _catalogue = CookieCatalogue.Scan(Jars);
        return _catalogue;
    }

    public Task<CookieBuildReport> AfterFilesChangedAsync(bool needsBuild, CancellationToken cancellation = default)
    {
        BuildRequested |= needsBuild;
        return Task.FromResult(needsBuild
            ? new CookieBuildReport(Required: true, Ran: true, Succeeded: true, Generation: 1)
            : CookieBuildReport.NotNeeded);
    }

    public Task<bool> ConfirmInstallAsync(Cookie cookie, CookieInstallPlan plan, CancellationToken cancellation = default)
    {
        ConfirmsAsked++;
        return Task.FromResult(ConfirmAnswer);
    }

    public CookieJarSource StageJar(string urlOrPath, string? name = null)
        => new(name ?? CookieJarLocator.SlugFor(urlOrPath), CookieJarKind.Git, "", RemoteUrl: urlOrPath, Trusted: false);

    public Task<CookieJarRefresh> RefreshJarAsync(string name, CancellationToken cancellation = default)
        => throw new CookieException($"Jar '{name}' is not a trusted git jar.");
}

public class CookieToolTests
{
    private static void Seed(CookieToolHarness harness)
    {
        harness.Fixture.WriteCookie("double-jump",
            CookieFixture.Manifest("double-jump", provides: "\"DoubleJump\"", tags: "\"movement\"",
                                   summary: "Jump a second time in mid air."),
            new Dictionary<string, string> { ["Source/DoubleJump.cs"] = "namespace Cookies.DoubleJump;\npublic sealed class DoubleJump : Component { }\n" });
        harness.Host.Project = harness.Fixture.Project();
    }

    [Fact]
    public void SearchReturnsWhatACookieProvidesWithoutOpeningIt()
    {
        using var harness = new CookieToolHarness("toolsearch");
        Seed(harness);

        var result = harness.Ok("search_cookies", new { query = "jump" });

        var first = result["cookies"]!.AsArray()[0]!;
        Assert.Equal("double-jump", first["id"]!.GetValue<string>());
        Assert.Equal("DoubleJump", first["provides"]!["components"]!.AsArray()[0]!.GetValue<string>());
        Assert.False(first["installed"]!.GetValue<bool>());
    }

    [Fact]
    public void AnUnknownCookieSuggestsWhatWasNearby()
    {
        using var harness = new CookieToolHarness("toolunknown");
        Seed(harness);

        var result = harness.Fails("get_cookie", new { id = "double-jumping" });

        Assert.Contains("double-jump", result.FirstText);
    }

    [Fact]
    public void InstallingReturnsTheAgentInstructionsAndTheNamespace()
    {
        // The point of the result shape: whatever comes next needs no further reads.
        using var harness = new CookieToolHarness("toolinstall");
        Seed(harness);

        var result = harness.Ok("install_cookie", new { id = "double-jump" });

        Assert.Equal("installed", result["status"]!.GetValue<string>());
        Assert.Equal("Cookies.DoubleJump", result["namespace"]!.GetValue<string>());
        Assert.Contains("Drop it on an actor", result["agent"]!.GetValue<string>());
        Assert.True(result["build"]!["required"]!.GetValue<bool>());
        Assert.True(harness.Host.BuildRequested);
        Assert.True(File.Exists(harness.Fixture.ProjectFile("Source/Cookies/DoubleJump/DoubleJump.cs")));
    }

    [Fact]
    public void ADryRunWritesNothing()
    {
        using var harness = new CookieToolHarness("tooldry");
        Seed(harness);

        var result = harness.Ok("install_cookie", new { id = "double-jump", dryRun = true });

        Assert.Equal("planned", result["status"]!.GetValue<string>());
        Assert.False(File.Exists(harness.Fixture.ProjectFile("Source/Cookies/DoubleJump/DoubleJump.cs")));
        Assert.Empty(harness.Host.Lock.Cookies);
    }

    [Fact]
    public void InstallingWithoutAProjectSaysSo()
    {
        using var harness = new CookieToolHarness("toolnoproject");
        harness.Fixture.WriteCookie("double-jump");

        Assert.Contains("No project is open", harness.Fails("install_cookie", new { id = "double-jump" }).FirstText);
    }

    [Fact]
    public void InstallingFromANonBuiltinJarAsksTheEditorFirst()
    {
        // The gate that matters: the assistant runs without prompts, so a jar that is not the
        // engine's own has to be confirmed by the person watching.
        using var harness = new CookieToolHarness("toolconfirm");
        Seed(harness);
        harness.Host.ConfirmAnswer = false;

        var result = harness.Fails("install_cookie", new { id = "double-jump" });

        Assert.Equal(1, harness.Host.ConfirmsAsked);
        Assert.Contains("declined in the editor", result.FirstText);
        Assert.False(File.Exists(harness.Fixture.ProjectFile("Source/Cookies/DoubleJump/DoubleJump.cs")));
    }

    [Fact]
    public void AddingAJarRecordsItAndClonesNothing()
    {
        using var harness = new CookieToolHarness("tooljar");
        Seed(harness);

        var result = harness.Ok("add_cookie_jar", new { urlOrPath = "https://example.invalid/team.git" });

        Assert.Equal("awaiting_approval", result["status"]!.GetValue<string>());
        Assert.False(result["jar"]!["trusted"]!.GetValue<bool>());
        Assert.Contains("Trust and clone", result["message"]!.GetValue<string>());
    }

    [Fact]
    public void UninstallingReportsWhatItKept()
    {
        using var harness = new CookieToolHarness("tooluninstall");
        Seed(harness);
        harness.Ok("install_cookie", new { id = "double-jump" });

        File.WriteAllText(harness.Fixture.ProjectFile("Source/Cookies/DoubleJump/DoubleJump.cs"), "// mine now\n");
        var result = harness.Ok("uninstall_cookie", new { id = "double-jump" });

        Assert.Equal("removed", result["status"]!.GetValue<string>());
        Assert.Empty(result["removed"]!.AsArray());
        Assert.Single(result["kept"]!.AsArray());
    }

    [Fact]
    public void InstalledCookiesReportFilesThatHaveDrifted()
    {
        using var harness = new CookieToolHarness("tooldrift");
        Seed(harness);
        harness.Ok("install_cookie", new { id = "double-jump" });

        File.Delete(harness.Fixture.ProjectFile("Source/Cookies/DoubleJump/DoubleJump.cs"));
        var result = harness.Ok("list_installed_cookies");

        Assert.Equal("missing", result["drift"]!.AsArray()[0]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void BakingWritesACookieTheCatalogueThenFinds()
    {
        using var harness = new CookieToolHarness("toolbake");
        harness.Host.Project = harness.Fixture.Project();

        string source = harness.Fixture.ProjectFile("Source/Spinner.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "namespace Game;\npublic sealed class Spinner : Component { }\n");

        var result = harness.Ok("bake_cookie", new
        {
            id                = "spinner",
            name              = "Spinner",
            summary           = "Spins an actor.",
            agentInstructions = "Add a Spinner to any actor.",
            files             = new[] { "Source/Spinner.cs" },
            tags              = new[] { "movement" },
        });

        Assert.Equal("baked", result["status"]!.GetValue<string>());
        Assert.Equal("spinner", harness.Ok("search_cookies", new { query = "spinner" })["cookies"]!.AsArray()[0]!["id"]!.GetValue<string>());
    }
}

/// <summary>
/// The jar that ships with the engine. These are the tests that stop an engine change quietly
/// breaking every cookie, and stop a cookie being committed that no project could install.
/// </summary>
public class BundledCookieTests
{
    private static CookieCatalogue? Bundled()
    {
        string? jar = CookieJarLocator.FindBuiltinJar();
        return jar == null ? null : CookieCatalogue.Scan(new[] { CookieJarSource.Builtin(jar) });
    }

    [Fact]
    public void TheBundledJarIsFoundFromTheTestBinary()
    {
        // If this fails the other tests here silently pass by finding nothing to check.
        Assert.NotNull(CookieJarLocator.FindBuiltinJar());
    }

    [Fact]
    public void EveryBundledCookieValidates()
    {
        var catalogue = Bundled();
        if (catalogue == null) return;

        Assert.NotEmpty(catalogue.All);

        var errors = CookieValidator.ValidateAll(catalogue, deep: true)
                                    .Where(p => p.Severity == CookieSeverity.Error)
                                    .ToList();

        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void EveryBundledCookieInstallsIntoAFreshProject()
    {
        var catalogue = Bundled();
        if (catalogue == null) return;

        string root = Path.Combine(Path.GetTempPath(), "sb-cookie-bundled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var project  = new CookieProjectContext(root, "FreshGame");
            var lockFile = new CookieLockFile();

            foreach (var cookie in catalogue.All)
            {
                var plan = CookiePlanner.Plan(cookie, project, lockFile, jar: catalogue.JarOf(cookie));
                Assert.True(plan.IsApplicable, $"{cookie.Id}: {plan.FirstBlocker?.Detail}");

                var outcome = CookieInstaller.Apply(plan, project, lockFile, catalogue.JarOf(cookie));
                Assert.Empty(outcome.Unresolved);
            }

            Assert.Equal(catalogue.All.Count, CookieLockFile.Load(root).Cookies.Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void EveryBundledCookieCompilesAgainstThisEngine()
    {
        // Slow: it runs dotnet build. Gated the way the assembly-loader integration test is.
        if (Environment.GetEnvironmentVariable("SEXYBISCUIT_SLOW_TESTS") != "1") return;

        var catalogue = Bundled();
        if (catalogue == null) return;

        var repo = SexyBiscuit.Engine.Code.EngineRepoLocator.Find();
        Assert.NotNull(repo);

        string root = Path.Combine(Path.GetTempPath(), "sb-cookie-compile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Source"));

        try
        {
            // Every C# cookie into one project, so this is one build rather than one per cookie.
            var project  = new CookieProjectContext(root, "CookieCompile");
            var lockFile = new CookieLockFile();

            foreach (var cookie in catalogue.All.Where(c => c.Manifest.SupportsEngine(CookieManifest.EngineCSharp)))
                CookieInstaller.Apply(CookiePlanner.Plan(cookie, project, lockFile, jar: catalogue.JarOf(cookie)),
                                      project, lockFile, catalogue.JarOf(cookie));

            File.WriteAllText(Path.Combine(root, "CookieCompile.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <RollForward>LatestMajor</RollForward>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Source/**/*.cs" /></ItemGroup>
                  <ItemGroup><ProjectReference Include="{repo!.EngineCsproj}" /></ItemGroup>
                </Project>
                """);

            var dotnet = SexyBiscuit.Engine.Code.DotnetLocator.Find();
            Assert.NotNull(dotnet);

            var runner = new SexyBiscuit.Engine.Code.DotnetBuildRunner(dotnet!);
            var result = runner.BuildAsync(new SexyBiscuit.Engine.Code.BuildRequest(Path.Combine(root, "CookieCompile.csproj")))
                               .GetAwaiter().GetResult();

            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
