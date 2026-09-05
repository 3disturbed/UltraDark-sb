using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scene;
using Xunit;

namespace SexyBiscuit.Tests;

public class BuildDiagnosticTests
{
    [Theory]
    [InlineData("/Users/x/Source/Foo.cs(12,9): error CS1002: ; expected [/Users/x/MyGame.csproj]", "/Users/x/Source/Foo.cs", 12, 9, "CS1002", BuildSeverity.Error, "; expected", "/Users/x/MyGame.csproj")]
    [InlineData(@"C:\x\Foo.cs(12,9,12,14): warning CS0168: The variable 'v' is declared but never used [C:\x\MyGame.csproj]", @"C:\x\Foo.cs", 12, 9, "CS0168", BuildSeverity.Warning, "The variable 'v' is declared but never used", @"C:\x\MyGame.csproj")]
    [InlineData("/x/MyGame.csproj : error NU1101: Unable to find package Nope.", "/x/MyGame.csproj", 0, 0, "NU1101", BuildSeverity.Error, "Unable to find package Nope.", null)]
    [InlineData("error NETSDK1045: The current .NET SDK does not support targeting .NET 9.0.", null, 0, 0, "NETSDK1045", BuildSeverity.Error, "The current .NET SDK does not support targeting .NET 9.0.", null)]
    public void ParseLine_ReadsEveryCanonicalShape(string line, string? file, int row, int col, string code, BuildSeverity severity, string message, string? project)
    {
        var d = MsBuildDiagnosticParser.ParseLine(line);

        Assert.NotNull(d);
        Assert.Equal(file, d!.File);
        Assert.Equal(row, d.Line);
        Assert.Equal(col, d.Column);
        Assert.Equal(code, d.Code);
        Assert.Equal(severity, d.Severity);
        Assert.Equal(message, d.Message);
        Assert.Equal(project, d.Project);
    }

    [Fact]
    public void ParseLine_TreatsMsbuildAsAToolNotAFile()
    {
        var d = MsBuildDiagnosticParser.ParseLine("MSBUILD : error MSB1009: Project file does not exist.");

        Assert.NotNull(d);
        Assert.Null(d!.File);
        Assert.Equal("MSB1009", d.Code);
    }

    [Fact]
    public void Parse_DedupesTheSummaryRepeatsAndIgnoresChatter()
    {
        var lines = new[]
        {
            "  Determining projects to restore...",
            "/x/Foo.cs(1,1): error CS0103: The name 'x' does not exist in the current context [/x/A.csproj]",
            "/x/Foo.cs(1,1): error CS0103: The name 'x' does not exist in the current context [/x/A.csproj]",
            "/x/Foo.cs(2,1): warning CS0219: The variable 'y' is assigned but its value is never used [/x/A.csproj]",
            "    0 Warning(s)",
            "",
        };

        var parsed = MsBuildDiagnosticParser.Parse(lines);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(1, parsed.Count(d => d.Severity == BuildSeverity.Error));
        Assert.Equal(1, parsed.Count(d => d.Severity == BuildSeverity.Warning));
    }
}

public class CodeProjectGeneratorTests
{
    private static string TempDir(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sb-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [InlineData("My Game 2", "MyGame2")]
    [InlineData("3d demo", "_3dDemo")]
    [InlineData("space-shooter", "SpaceShooter")]
    [InlineData("!!!", "Game")]
    [InlineData("Already_Fine", "Already_Fine")]
    public void SanitiseIdentifier_MakesAValidPascalCaseName(string input, string expected)
        => Assert.Equal(expected, CodeProjectGenerator.SanitiseIdentifier(input));

    [Fact]
    public void Generate_WritesCsprojPropsGitignoreAndStartersOnce()
    {
        string root = TempDir("gen");
        try
        {
            var engine = new EngineLocation("/repo/SexyBiscuit.Engine/SexyBiscuit.Engine.csproj", "/repo/SexyBiscuit.Editor/bin/Debug/net8.0", Guid.NewGuid());
            var result = CodeProjectGenerator.Generate(root, "My Game", engine);

            Assert.Equal(Path.Combine(root, "MyGame.csproj"), result.CsprojPath);
            Assert.Contains("MyGame.csproj", result.Created);
            Assert.Contains(".gitignore", result.Created);
            Assert.Contains(Path.Combine("Source", "Program.cs"), result.Created);
            Assert.Contains(Path.Combine("Source", "Gameplay", "MyGameGameMode.cs"), result.Created);
            Assert.Contains(Path.Combine("Source", "Components", "Spinner.cs"), result.Created);
            Assert.Contains(Path.Combine("Source", "Tools", "ProjectTools.cs"), result.Created);
            Assert.Empty(result.Skipped);

            string csproj = File.ReadAllText(result.CsprojPath);
            Assert.Contains("<Compile Include=\"Source/**/*.cs\" />", csproj);
            Assert.Contains("<RollForward>LatestMajor</RollForward>", csproj);
            Assert.Contains("<ProjectReference Include=\"$(SexyBiscuitEngineProject)\" />", csproj);
            Assert.Contains("HintPath", csproj);
            Assert.Contains("<GenerateFullPaths>true</GenerateFullPaths>", csproj);

            string props = File.ReadAllText(Path.Combine(root, CodeProjectGenerator.PropsFileName));
            Assert.Contains("/repo/SexyBiscuit.Engine/SexyBiscuit.Engine.csproj", props);
            Assert.Contains(engine.EngineMvid!.Value.ToString(), props);

            Assert.Contains("SexyBiscuit.props", File.ReadAllText(Path.Combine(root, ".gitignore")));

            // A second run never overwrites what a person may have edited.
            File.WriteAllText(Path.Combine(root, "Source", "Components", "Spinner.cs"), "// edited");
            var again = CodeProjectGenerator.Generate(root, "My Game", engine);
            Assert.Empty(again.Created);
            Assert.Equal(result.Created.Count, again.Skipped.Count);
            Assert.Equal("// edited", File.ReadAllText(Path.Combine(root, "Source", "Components", "Spinner.cs")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(ClassKind.Component, ": Component")]
    [InlineData(ClassKind.Actor, ": Actor")]
    [InlineData(ClassKind.GameMode, ": GameMode")]
    [InlineData(ClassKind.PlayerController, ": PlayerController")]
    [InlineData(ClassKind.Character, ": Character")]
    [InlineData(ClassKind.Tool, "[McpTool(")]
    public void RenderClass_UsesTheRightBaseClassPerKind(ClassKind kind, string marker)
    {
        string source = CodeProjectGenerator.RenderClass(kind, "Widget", "MyGame");

        Assert.Contains(marker, source);
        Assert.Contains("namespace MyGame", source);
        Assert.Contains("Widget", source);
    }

    [Fact]
    public void CodeProject_Find_LocatesTheRootCsprojAndIgnoresBinObj()
    {
        string root = TempDir("find");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bin", "Debug"));
            File.WriteAllText(Path.Combine(root, "bin", "Debug", "Stale.csproj"), "<Project />");
            Assert.Null(CodeProject.Find(root));

            File.WriteAllText(Path.Combine(root, "Other.csproj"), "<Project><PropertyGroup><RootNamespace>Other.Ns</RootNamespace></PropertyGroup></Project>");
            var project = CodeProject.Find(root);

            Assert.NotNull(project);
            Assert.Equal("Other", project!.Name);
            Assert.Equal("Other.Ns", project.RootNamespace);
            Assert.Equal("Other", project.AssemblyName);
            Assert.Equal(Path.Combine(root, "Source"), project.SourceDirectory);
            Assert.Equal(Path.Combine(root, "bin", "Debug", "net8.0", "Other.dll"), project.OutputAssemblyPath("Debug"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class EngineConfigSettingsTests
{
    [Fact]
    public void FromProjectSettings_ReadsPascalAndCamelCaseKeys()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sb-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string? previous = ProjectPaths.Root;
        try
        {
            ProjectPaths.Root = dir;

            File.WriteAllText(Path.Combine(dir, "ProjectSettings.json"),
                """{ "WindowTitle": "Pascal", "WindowWidth": 640, "WindowHeight": 480, "VSync": false, "StartScene": "Scenes/Main3D" }""");
            var pascal = EngineConfig.FromProjectSettings("ProjectSettings.json");
            Assert.Equal("Pascal", pascal.WindowTitle);
            Assert.Equal(640, pascal.WindowWidth);
            Assert.False(pascal.VSync);
            Assert.Equal("Scenes/Main3D", pascal.StartScene);

            Directory.CreateDirectory(Path.Combine(dir, "Assets"));
            File.Delete(Path.Combine(dir, "ProjectSettings.json"));
            File.WriteAllText(Path.Combine(dir, "Assets", "ProjectSettings.json"),
                """{ "appName": "Camel", "windowWidth": 800, "startScene": "Scenes/MainMenu" }""");
            var camel = EngineConfig.FromProjectSettings("ProjectSettings.json");
            Assert.Equal("Camel", camel.WindowTitle);
            Assert.Equal(800, camel.WindowWidth);
            Assert.Equal("Scenes/MainMenu", camel.StartScene);

            var missing = EngineConfig.FromProjectSettings("Nowhere.json");
            Assert.Equal(new EngineConfig().WindowTitle, missing.WindowTitle);
        }
        finally
        {
            ProjectPaths.Root = previous;
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class DotnetToolingTests
{
    [Fact]
    public void DotnetLocator_FindsTheMuxerRunningTheTests()
    {
        var dotnet = DotnetLocator.Find();

        Assert.NotNull(dotnet);
        Assert.True(File.Exists(dotnet!.Path), dotnet.Path);
        Assert.False(string.IsNullOrEmpty(dotnet.SdkVersion));
        Assert.True(char.IsDigit(dotnet.SdkVersion![0]));
    }

    [Fact]
    public void BuildArguments_ReflectTheRequest()
    {
        var plain = DotnetBuildRunner.BuildArguments(new BuildRequest("/x/Game.csproj"));
        Assert.Equal("build", plain[0]);
        Assert.Contains("-p:GenerateFullPaths=true", plain);
        Assert.DoesNotContain("-p:BuildProjectReferences=false", plain);

        var editorTriggered = DotnetBuildRunner.BuildArguments(new BuildRequest("/x/Game.csproj", "Release")
        {
            BuildProjectReferences = false,
            OutputDirectory        = "/x/staging",
            Properties             = new Dictionary<string, string> { ["Foo"] = "Bar" },
        });
        Assert.Contains("-p:BuildProjectReferences=false", editorTriggered);
        Assert.Contains("Release", editorTriggered);
        Assert.Contains("/x/staging", editorTriggered);
        Assert.Contains("-p:Foo=Bar", editorTriggered);
    }

    [Fact]
    public void AssemblyIdentity_ReadMvid_MatchesTheLoadedEngine()
    {
        string location = typeof(Actor).Assembly.Location;
        Assert.Equal(AssemblyIdentity.RunningEngineMvid, AssemblyIdentity.TryReadMvid(location));
        Assert.Null(AssemblyIdentity.TryReadMvid(Path.Combine(Path.GetTempPath(), "nope.dll")));
    }

    [Fact]
    public void EngineRepoLocator_FindsTheRepoFromTheTestBinaryAndNothingElsewhere()
    {
        var repo = EngineRepoLocator.Find();

        Assert.NotNull(repo);
        Assert.True(File.Exists(repo!.Solution));
        Assert.True(File.Exists(repo.EngineCsproj));
        Assert.True(File.Exists(repo.EditorCsproj));

        string elsewhere = Path.Combine(Path.GetTempPath(), "sb-norepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        try
        {
            Assert.Null(EngineRepoLocator.Probe(elsewhere));
        }
        finally
        {
            Directory.Delete(elsewhere);
        }
    }

    [Fact]
    public void ReflectionUtil_RetiredAssembliesAreExcludedFromDiscovery()
    {
        var self = typeof(McpBattery).Assembly;
        Assert.Contains(ReflectionUtil.FindComponentTypes(), t => t == typeof(McpBattery));

        ReflectionUtil.RetireAssembly(self);
        try
        {
            Assert.DoesNotContain(ReflectionUtil.FindComponentTypes(), t => t == typeof(McpBattery));
            SceneSerializer.ClearTypeCache();
            Assert.Null(SceneSerializer.ResolveComponentType("McpBattery"));
        }
        finally
        {
            ReflectionUtil.UnretireAssembly(self);
            SceneSerializer.ClearTypeCache();
        }

        Assert.Contains(ReflectionUtil.FindComponentTypes(), t => t == typeof(McpBattery));
    }
}

public class RelaunchTests
{
    [Fact]
    public void RelaunchState_RoundTripsThroughJsonAndIsDeletedAfterARead()
    {
        string path = Path.Combine(Path.GetTempPath(), "sb-resume-" + Guid.NewGuid().ToString("N") + ".json");
        var state = new RelaunchState
        {
            Reason             = "engine-rebuild",
            EditorPid          = 4242,
            WorkingDirectory   = "/work",
            ProjectFile        = "/p/Game.sbproject",
            ScenePath          = "Scenes/Main.scene",
            SelectedActorName  = "Player",
            AssistantSessionId = "abc",
            McpPort            = 7331,
        };

        RelaunchStateFile.Save(state, path);
        var read = RelaunchStateFile.TryReadAndDelete(path);

        Assert.NotNull(read);
        Assert.Equal("engine-rebuild", read!.Reason);
        Assert.Equal(4242, read.EditorPid);
        Assert.Equal("Scenes/Main.scene", read.ScenePath);
        Assert.Equal("abc", read.AssistantSessionId);
        Assert.False(File.Exists(path));
        Assert.Null(RelaunchStateFile.TryReadAndDelete(path));
    }

    [Fact]
    public void RelaunchScript_QuotesPathsWithSpaces()
    {
        var plan = new RelaunchPlan(123, "/usr/local/share/dotnet/dotnet", "/repo dir", "/repo dir/SexyBiscuit.Editor/SexyBiscuit.Editor.csproj",
            "Debug", "/repo dir/bin/Editor.dll", "/repo dir/bin/staging/Editor.dll", "/work dir", "/resume file.json", "/log file.txt");

        string sh = RelaunchScript.RenderSh(plan);
        Assert.Contains("kill -0 123", sh);
        Assert.Contains("'/repo dir/SexyBiscuit.Editor/SexyBiscuit.Editor.csproj'", sh);
        Assert.Contains("--resume '/resume file.json'", sh);
        Assert.Contains("'/repo dir/bin/staging/Editor.dll'", sh);

        string ps = RelaunchScript.RenderPowerShell(plan);
        Assert.Contains("Wait-Process -Id 123", ps);
        Assert.Contains("'/work dir'", ps);
        Assert.Contains("'--resume'", ps);

        Assert.Equal("'it'\\''s'", RelaunchScript.Sh("it's"));
        Assert.Equal("'it''s'", RelaunchScript.Ps("it's"));
    }
}

public class ExceptionIsolationTests
{
    private sealed class Thrower : Component
    {
        public override void Update(float dt) => throw new InvalidOperationException("boom");
    }

    private sealed class Counter : Component
    {
        public int Updates;
        public override void Update(float dt) => Updates++;
    }

    [Fact]
    public void RoutesComponentExceptionsToTheHandlerAndKeepsTicking()
    {
        var seen = new List<(object owner, string phase)>();
        ExceptionIsolation.Handler = (ex, owner, phase) => { seen.Add((owner, phase)); return true; };

        var scene = new Scene("isolate");
        try
        {
            var actor   = scene.AddActor(new Actor("Fragile"));
            var thrower = actor.AddComponent<Thrower>();
            var counter = actor.AddComponent<Counter>();

            scene.Update(0.016f);
            scene.Update(0.016f);

            Assert.Equal(2, counter.Updates);
            Assert.Equal(2, seen.Count);
            Assert.Same(thrower, seen[0].owner);
            Assert.Equal("Update", seen[0].phase);
        }
        finally
        {
            ExceptionIsolation.Handler = null;
            scene.Destroy();
        }
    }

    [Fact]
    public void PropagatesWhenNoHandlerIsSet()
    {
        ExceptionIsolation.Handler = null;

        var scene = new Scene("loud");
        try
        {
            scene.AddActor(new Actor("Fragile")).AddComponent<Thrower>();
            scene.FlushPendingActors();

            Assert.Throws<InvalidOperationException>(() => scene.Update(0.016f));
        }
        finally
        {
            scene.Destroy();
        }
    }
}

/// <summary>
/// The real thing: generate a project, build it with dotnet, load the DLL into this process and
/// check the types unify with the engine's. Slow (a full build), so it runs only when
/// SEXYBISCUIT_SLOW_TESTS=1.
/// </summary>
public class GameAssemblyLoaderTests
{
    [Fact]
    public async Task ABuiltProjectLoadsUnifiesWithTheEngineAndUnloads()
    {
        if (Environment.GetEnvironmentVariable("SEXYBISCUIT_SLOW_TESTS") != "1") return;

        var repo   = EngineRepoLocator.Find();
        var dotnet = DotnetLocator.Find();
        Assert.NotNull(repo);
        Assert.NotNull(dotnet);

        string root = Path.Combine(Path.GetTempPath(), "sb-loader-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engineBin = Path.GetDirectoryName(typeof(Actor).Assembly.Location)!;
            var generated = CodeProjectGenerator.Generate(root, "LoaderProbe", new EngineLocation(repo!.EngineCsproj, engineBin, AssemblyIdentity.RunningEngineMvid));

            var runner = new DotnetBuildRunner(dotnet!);
            var result = await runner.BuildAsync(new BuildRequest(generated.CsprojPath) { BuildProjectReferences = false });
            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())) + "\n" + result.RawLog);
            Assert.NotNull(result.OutputAssemblyPath);

            var loader   = new GameAssemblyLoader();
            var snapshot = loader.Load(result.OutputAssemblyPath!);

            var spinner = snapshot.ComponentTypes.Single(t => t.Name == "Spinner");
            Assert.True(typeof(Component).IsAssignableFrom(spinner), "the game's Component must be the engine's Component");
            Assert.Contains(snapshot.ActorTypes, t => t.Name == "LoaderProbeCharacter");
            Assert.Contains(snapshot.ToolHolders, t => t.Name == "ProjectTools");
            Assert.Equal(AssemblyIdentity.RunningEngineMvid, snapshot.EngineMvidCompiledAgainst);

            Assert.Contains(ReflectionUtil.FindComponentTypes(), t => t == spinner);
            SceneSerializer.ClearTypeCache();
            Assert.Equal(spinner, SceneSerializer.ResolveComponentType("Spinner"));

            // A scene with the game component round-trips by short name.
            var scene = new Scene("probe");
            var actor = scene.AddActor(new Actor("Top"));
            actor.AddComponent<Transform3D>();
            actor.AddComponent(spinner);
            scene.FlushPendingActors();
            string json = SceneSerializer.Serialize(scene);
            Assert.Contains("\"type\": \"Spinner\"", json);
            scene.Destroy();

            var report = loader.Unload();
            SceneSerializer.ClearTypeCache();
            Assert.DoesNotContain(ReflectionUtil.FindComponentTypes(), t => t.Name == "Spinner");
            Assert.Null(SceneSerializer.ResolveComponentType("Spinner"));

            // Collection is best effort; the report just must not lie about it.
            Assert.True(report.GcPasses >= 0);
        }
        finally
        {
            SceneSerializer.ClearTypeCache();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
