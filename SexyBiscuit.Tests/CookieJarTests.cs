using System.Text.Json.Nodes;
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
