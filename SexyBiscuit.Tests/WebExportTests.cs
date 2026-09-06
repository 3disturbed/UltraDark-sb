using System.Text.Json;
using SexyBiscuit.Engine.Build;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Exercises the <see cref="BuildPlatform.Web"/> target end to end.
/// </summary>
/// <remarks>
/// A web export stages rather than compiles: the engine is already JavaScript
/// and the project's files are already in the formats it reads, so the whole job
/// is putting the two together correctly. Every failure mode is therefore a
/// missing or misnamed file, which only an actual export catches.
/// </remarks>
public class WebExportTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string _originalCwd;

    public WebExportTests()
    {
        // The pipeline reads "Scenes" and "Scripts" relative to the working
        // directory, so a test has to build a project and stand inside it.
        _projectDir = Path.Combine(Path.GetTempPath(), "sb-web-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_projectDir, "Scenes"));
        Directory.CreateDirectory(Path.Combine(_projectDir, "Scripts"));
        Directory.CreateDirectory(Path.Combine(_projectDir, "Assets"));

        File.WriteAllText(
            Path.Combine(_projectDir, "Scenes", "Main.scene"),
            """{ "name": "Main", "layers": [ { "name": "Default", "actors": [] } ] }""");
        File.WriteAllText(
            Path.Combine(_projectDir, "Scripts", "Thing.js"),
            "function onUpdate(dt) { }\n");

        _originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_projectDir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        try { Directory.Delete(_projectDir, recursive: true); } catch { /* best effort */ }
    }

    private static PlatformConfig WebConfig() => new()
    {
        Platform        = BuildPlatform.Web,
        Configuration   = BuildConfiguration.Release,
        AppName         = "Web Test",
        Version         = "1.2.3",
        StartScene      = "Scenes/Main.scene",
        OutputDirectory = "dist",
    };

    /// <summary>
    /// The runtime has to travel with the game. Without it the export is a folder
    /// of data files and an index.html importing a module that is not there.
    /// </summary>
    [Fact]
    public void AWebExportStagesTheRuntimeBesideTheProject()
    {
        var result = new ExportPipeline().Export(WebConfig());

        Assert.True(result.Success, string.Join("\n", result.Errors));

        string output = result.OutputPath;
        Assert.True(File.Exists(Path.Combine(output, "index.html")), "no index.html");
        Assert.True(File.Exists(Path.Combine(output, "engine", "src", "index.js")), "engine not staged");
        Assert.True(File.Exists(Path.Combine(output, "engine", "runtime", "Runtime.js")), "runtime not staged");
        Assert.True(File.Exists(Path.Combine(output, "engine", "runtime", "runtime.css")), "stylesheet not staged");

        // The project's own content comes across unchanged.
        Assert.True(File.Exists(Path.Combine(output, "Scenes", "Main.scene")), "scene not copied");
        Assert.True(File.Exists(Path.Combine(output, "Scripts", "Thing.js")), "script not copied");
        Assert.True(File.Exists(Path.Combine(output, "ProjectSettings.json")), "no settings");
    }

    /// <summary>
    /// The page points the runtime at the project's own root and start scene; get
    /// either wrong and the build loads an empty world with no error.
    /// </summary>
    [Fact]
    public void TheGeneratedPageBootsTheRuntimeAtTheProjectRoot()
    {
        var result = new ExportPipeline().Export(WebConfig());
        string html = File.ReadAllText(Path.Combine(result.OutputPath, "index.html"));

        Assert.Contains("<title>Web Test</title>", html);
        Assert.Contains("data-project=\"./\"", html);
        Assert.Contains("data-scene=\"Scenes/Main.scene\"", html);
        Assert.Contains("./engine/runtime/Runtime.js", html);

        // A release build should not ship the frame counter.
        Assert.Contains("data-stats=\"false\"", html);
    }

    /// <summary>
    /// A byte order mark before the doctype can push a browser into quirks mode.
    /// </summary>
    [Fact]
    public void TheGeneratedPageHasNoByteOrderMark()
    {
        var result = new ExportPipeline().Export(WebConfig());
        byte[] bytes = File.ReadAllBytes(Path.Combine(result.OutputPath, "index.html"));

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "index.html starts with a UTF-8 BOM");
        Assert.Equal((byte)'<', bytes[0]);
    }

    /// <summary>
    /// Game code gating on a platform needs a define it can actually test.
    /// </summary>
    [Fact]
    public void AWebExportDefinesItsPlatform()
    {
        var result = new ExportPipeline().Export(WebConfig());
        string json = File.ReadAllText(Path.Combine(result.OutputPath, "PlatformDefines.json"));

        var defines = JsonSerializer.Deserialize<List<string>>(json)!;
        Assert.Contains("PLATFORM_WEB", defines);
        Assert.Contains("ARCH_WASM", defines);
        Assert.Contains("BUILD_RELEASE", defines);
    }

    /// <summary>
    /// Web is the last value in the enum on purpose: the editor's platform
    /// dropdown maps its selection by ordinal, so inserting a value anywhere
    /// else would silently retarget every project that had one selected.
    /// </summary>
    [Fact]
    public void WebIsTheLastBuildPlatform()
    {
        var platforms = Enum.GetValues<BuildPlatform>();
        Assert.Equal(BuildPlatform.Web, platforms[^1]);
    }

    /// <summary>Staging is skipped for every other target.</summary>
    [Fact]
    public void ANonWebExportDoesNotStageTheRuntime()
    {
        var config = WebConfig();
        config.Platform = BuildPlatform.Linux_x64;

        var result = new ExportPipeline().Export(config);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.False(Directory.Exists(Path.Combine(result.OutputPath, "engine")));
        Assert.False(File.Exists(Path.Combine(result.OutputPath, "index.html")));
    }
}
