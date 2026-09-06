using System.Runtime.InteropServices;
using SexyBiscuit.Engine.Build;
using SexyBiscuit.Engine.Code;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>The build CLI's argument parsing, and the pieces a publish is made of.</summary>
public class BuildCliTests
{
    [Fact]
    public void ATrailingBareFlagIsRead()
    {
        // The old loop ran to args.Length - 1, so a flag in the last position was never seen.
        var options = ExportCliOptions.Parse(new[] { "--project", "Games/Foo", "--upload" });
        Assert.Equal("Games/Foo", options.ProjectDir);
        Assert.True(options.Upload);
        Assert.Empty(options.Errors);

        var dangling = ExportCliOptions.Parse(new[] { "--platform" });
        Assert.Contains(dangling.Errors, e => e.Contains("needs a value"));
    }

    [Fact]
    public void PlatformsParseFromDashedUnderscoredAndRidSpellings()
    {
        var options = ExportCliOptions.Parse(new[] { "build", "--platform", "win-x64,osx-arm64", "-p", "Windows_x86", "--platform", "html5" });

        Assert.Equal(new[] { BuildPlatform.Windows_x64, BuildPlatform.macOS_ARM64, BuildPlatform.Windows_x86, BuildPlatform.Web },
                     options.ResolvedTargets);
        Assert.Empty(options.Errors);

        Assert.Contains(ExportCliOptions.Parse(new[] { "--platform", "amiga" }).Errors, e => e.Contains("amiga"));
    }

    [Fact]
    public void AllExpandsToTheFourDefaultTargets()
    {
        var options = ExportCliOptions.Parse(new[] { "--all", "--config", "release", "--no-zip", "--version", "1.2.3" });

        Assert.Equal(RuntimeIdentifiers.DefaultTargets, options.ResolvedTargets);
        Assert.Equal(BuildConfiguration.Release, options.Configuration);
        Assert.False(options.Package);
        Assert.True(options.Publish);
        Assert.Equal("1.2.3", options.Version);

        // With nothing named, the web build is the target that works on any machine.
        Assert.Equal(new[] { BuildPlatform.Web }, ExportCliOptions.Parse(Array.Empty<string>()).ResolvedTargets);
    }

    [Fact]
    public void UnknownArgumentsAreErrorsNotSilence()
    {
        var options = ExportCliOptions.Parse(new[] { "--projekt", "x", "--depot", "abc" });
        Assert.Contains(options.Errors, e => e.Contains("--projekt"));
        Assert.Contains(options.Errors, e => e.Contains("--depot"));
        Assert.True(ExportCliOptions.Parse(new[] { "--help" }).Help);
    }

    [Fact]
    public void RuntimeIdentifiersMapEveryDesktopPlatform()
    {
        foreach (var platform in Enum.GetValues<BuildPlatform>())
        {
            string? rid = RuntimeIdentifiers.For(platform);
            bool desktop = platform is not (BuildPlatform.Web or BuildPlatform.Android or BuildPlatform.iOS);
            Assert.Equal(desktop, rid != null);
            if (rid != null) Assert.NotNull(RuntimeIdentifiers.Parse(rid));
        }

        Assert.Equal(BuildPlatform.macOS_ARM64, RuntimeIdentifiers.Parse("osx-arm64"));
        Assert.Equal(BuildPlatform.macOS_ARM64, RuntimeIdentifiers.Parse("macos_arm64"));
        Assert.Equal(BuildPlatform.Web, RuntimeIdentifiers.Parse("HTML5"));
        Assert.Null(RuntimeIdentifiers.Parse("wasm"));
    }

    [Fact]
    public void PublishArgumentsCarryTheRidAndSingleFileFlags()
    {
        var args = DesktopPublisher.PublishArguments("/games/foo/Foo.csproj", "Release", "osx-arm64", "/games/foo/dist/macOS_ARM64", "/engine/SexyBiscuit.Engine/SexyBiscuit.Engine.csproj");

        Assert.Equal("publish", args[0]);
        Assert.Contains("-r", args);
        Assert.Equal("osx-arm64", args[args.ToList().IndexOf("-r") + 1]);
        Assert.Contains("--self-contained", args);
        Assert.Contains("-p:PublishSingleFile=true", args);
        // MonoGame's loader finds SDL beside the executable, not inside a bundle.
        Assert.Contains("-p:IncludeNativeLibrariesForSelfExtract=false", args);
        Assert.Contains("-p:PublishTrimmed=false", args);

        // The editor's props file pins a game to the editor's Debug engine binary; a publish must
        // compile the engine for the target instead.
        Assert.Contains("-p:SexyBiscuitEngineDir=", args);
        Assert.Contains("-p:SexyBiscuitEngineProject=/engine/SexyBiscuit.Engine/SexyBiscuit.Engine.csproj", args);
        Assert.Contains("-p:BuildProjectReferences=true", args);
    }

    [Fact]
    public void AJsOnlyProjectGetsAPlayerProjectUnderTheScratchFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "sb-player-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string csproj = DesktopPublisher.EnsurePlayerProject(root, "My Great Game!");

            Assert.Equal(Path.Combine(root, ".sexybiscuit", "player", "MyGreatGame.Player.csproj"), csproj);
            Assert.True(File.Exists(csproj));
            Assert.Contains("<AssemblyName>MyGreatGame</AssemblyName>", File.ReadAllText(csproj));
            Assert.Contains("SEXYBISCUIT_REPO", File.ReadAllText(csproj));

            string program = File.ReadAllText(Path.Combine(root, ".sexybiscuit", "player", "Program.cs"));
            Assert.Contains("ProjectPaths.Root = AppContext.BaseDirectory", program);
            Assert.Contains("SBEngine.Run(config)", program);

            // Below the scratch folder, so the editor still sees a project with no game code.
            Assert.Null(CodeProject.Find(root));
            Assert.Equal(Path.Combine(root, "dist", "MyGreatGame.exe"), DesktopPublisher.ExecutablePathFor(csproj, BuildPlatform.Windows_x64, Path.Combine(root, "dist")));
            Assert.Equal(Path.Combine(root, "dist", "MyGreatGame"), DesktopPublisher.ExecutablePathFor(csproj, BuildPlatform.macOS_ARM64, Path.Combine(root, "dist")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AReportRoundTripsAndSummarisesOneLinePerTarget()
    {
        var report = new BuildReport
        {
            AppName = "Foo", Version = "1.0.0", Configuration = "Release", GitSha = new string('a', 40),
            Targets =
            {
                new TargetReport { Platform = "Web", Success = true, Seconds = 1.5, Archive = "/x/foo-1.0.0-web.zip", ArchiveBytes = 2048 },
                new TargetReport { Platform = "Linux_x64", Rid = "linux-x64", Success = false, Seconds = 30, Errors = { "publish: boom" } },
            },
        };

        string path = Path.Combine(Path.GetTempPath(), "sb-report-" + Guid.NewGuid().ToString("N"), BuildReport.FileName);
        report.Save(path);
        var loaded = BuildReport.Load(path);
        File.Delete(path);

        Assert.False(loaded.Success);
        Assert.Equal(2, loaded.Targets.Count);
        Assert.Equal("linux-x64", loaded.Targets[1].Rid);

        var lines = loaded.ToSummaryLines().ToList();
        Assert.StartsWith("web", lines[0]);
        Assert.Contains("ok", lines[0]);
        Assert.Contains("2 KB", lines[0]);
        Assert.StartsWith("linux-x64", lines[1]);
        Assert.Contains("FAILED", lines[1]);
        Assert.Contains("publish: boom", lines[2]);
    }

    [Fact]
    public void GitInfoReadsTheShaOfThisCheckout()
    {
        var repo = EngineRepoLocator.Find();
        Assert.NotNull(repo);

        string? sha = GitInfo.TryReadHeadSha(repo!.Root);
        if (!repo.IsGitCheckout) { Assert.Null(sha); return; }

        Assert.NotNull(sha);
        Assert.Matches("^[0-9a-f]{40}$", sha!);
        Assert.Null(GitInfo.TryReadHeadSha(Path.GetTempPath()));
    }

    [Fact]
    public void APngIsWrittenWithTheRightSignatureAndDimensions()
    {
        byte[] png = PngWriter.Solid(16, 1, 2, 3);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8).ToArray());
        // IHDR: length (4) + "IHDR" (4) then width, height big-endian.
        Assert.Equal(16, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(16, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
        Assert.Equal((0x12, 0x14, 0x1a, 0xff), PngWriter.ParseHex("#12141a"));
        Assert.Equal((0x12, 0x14, 0x1a, 0xff), PngWriter.ParseHex("nonsense"));
    }

    /// <summary>
    /// A real publish of a JS-only template for this machine. Minutes long and needs the SDK,
    /// so it runs only when asked for: <c>SB_RUN_PUBLISH_TESTS=1 dotnet test</c>.
    /// </summary>
    [Fact]
    public async Task AJsOnlyTemplatePublishesToASingleFileBinary()
    {
        if (Environment.GetEnvironmentVariable("SB_RUN_PUBLISH_TESTS") != "1") return;

        var repo = EngineRepoLocator.Find();
        Assert.NotNull(repo);

        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? BuildPlatform.Windows_x64
                     : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                        ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? BuildPlatform.macOS_ARM64 : BuildPlatform.macOS_x64)
                     : BuildPlatform.Linux_x64;

        var config = PlatformConfig.ForProject(Path.Combine(repo!.Root, "Templates", "Hello World"), platform);
        config.OutputDirectory = Path.Combine(Path.GetTempPath(), "sb-publish-" + Guid.NewGuid().ToString("N"));
        config.Configuration = BuildConfiguration.Release;

        var result = await new ExportPipeline { Output = null }.ExportAsync(config, new ExportOptions { Publish = true, Package = true });

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.NotNull(result.ExecutablePath);
        Assert.True(File.Exists(result.ExecutablePath!), $"no binary at {result.ExecutablePath}");
        Assert.True(File.Exists(Path.Combine(result.OutputPath, "ProjectSettings.json")));
        Assert.NotNull(result.Archive);
    }
}
