using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using SexyBiscuit.Engine.Build;
using SexyBiscuit.Engine.Code;
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

    public WebExportTests()
    {
        // The pipeline resolves every path against the config's ProjectRoot, so a test builds
        // a project in a temp folder and points the config at it — never at the working directory.
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
        File.WriteAllText(
            Path.Combine(_projectDir, "ProjectSettings.json"),
            """{ "WindowTitle": "Web Test", "WindowWidth": 640, "WindowHeight": 360, "VSync": true, "StartScene": "Scenes/Main.scene" }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectDir, recursive: true); } catch { /* best effort */ }
    }

    private PlatformConfig WebConfig() => new()
    {
        Platform        = BuildPlatform.Web,
        Configuration   = BuildConfiguration.Release,
        AppName         = "Web Test",
        Version         = "1.2.3",
        StartScene      = "Scenes/Main.scene",
        OutputDirectory = "dist",
        ProjectRoot     = _projectDir,
    };

    private static ExportPipeline Quiet() => new() { Output = null };

    /// <summary>
    /// The runtime has to travel with the game. Without it the export is a folder
    /// of data files and an index.html importing a module that is not there.
    /// </summary>
    [Fact]
    public void AWebExportStagesTheRuntimeBesideTheProject()
    {
        var result = Quiet().Export(WebConfig());

        Assert.True(result.Success, string.Join("\n", result.Errors));

        string output = result.OutputPath;
        Assert.True(File.Exists(Path.Combine(output, "index.html")), "no index.html");
        Assert.True(File.Exists(Path.Combine(output, "engine", "src", "index.js")), "engine not staged");
        Assert.True(File.Exists(Path.Combine(output, "engine", "runtime", "Runtime.js")), "runtime not staged");
        Assert.True(File.Exists(Path.Combine(output, "engine", "runtime", "runtime.css")), "stylesheet not staged");
        Assert.False(File.Exists(Path.Combine(output, "engine", "runtime", "export", "index.html.tmpl")), "the templates are not part of a build");

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
        var result = Quiet().Export(WebConfig());
        string html = File.ReadAllText(Path.Combine(result.OutputPath, "index.html"));

        Assert.Contains("<title>Web Test</title>", html);
        Assert.Contains("data-project=\"./\"", html);
        Assert.Contains("data-scene=\"Scenes/Main.scene\"", html);
        Assert.Contains("./engine/runtime/Runtime.js", html);
        Assert.Contains("id=\"fullscreen\"", html);
        Assert.DoesNotContain("{{", html);

        // A release build should not ship the frame counter.
        Assert.Contains("data-stats=\"false\"", html);
    }

    /// <summary>
    /// A byte order mark before the doctype can push a browser into quirks mode.
    /// </summary>
    [Fact]
    public void TheGeneratedPageHasNoByteOrderMark()
    {
        var result = Quiet().Export(WebConfig());
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
        var result = Quiet().Export(WebConfig());
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

        var result = Quiet().Export(config);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.False(Directory.Exists(Path.Combine(result.OutputPath, "engine")));
        Assert.False(File.Exists(Path.Combine(result.OutputPath, "index.html")));
    }

    /// <summary>
    /// The platform folder goes under the output directory once. Older settings files saved
    /// <c>dist\Windows_x64</c> as the output directory, and the pipeline appended the platform
    /// again — <c>dist/Windows_x64/Windows_x64</c> is committed in the editor's own folder.
    /// </summary>
    [Fact]
    public void TheOutputFolderCarriesThePlatformExactlyOnce()
    {
        var plain = WebConfig();
        Assert.Equal(Path.Combine(_projectDir, "dist", "Web"), ExportPipeline.PlatformOutputDirectory(plain));

        var saved = WebConfig();
        saved.OutputDirectory = @"dist\Windows_x64";
        saved.Platform = BuildPlatform.Windows_x64;
        Assert.Equal(Path.Combine(_projectDir, "dist", "Windows_x64"), ExportPipeline.PlatformOutputDirectory(saved));

        // The same saved value with another platform selected lands beside it, not under it.
        saved.Platform = BuildPlatform.Web;
        Assert.Equal(Path.Combine(_projectDir, "dist", "Web"), ExportPipeline.PlatformOutputDirectory(saved));

        var result = Quiet().Export(plain);
        Assert.Equal(Path.Combine(_projectDir, "dist", "Web"), result.OutputPath);
    }

    /// <summary>
    /// A shipped game reads its window size and vsync from the staged settings file exactly as
    /// the editor did, so the project's own keys must survive the build's metadata.
    /// </summary>
    [Fact]
    public void TheStagedProjectSettingsKeepTheProjectsWindowSettings()
    {
        var result = Quiet().Export(WebConfig());
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.OutputPath, "ProjectSettings.json")));
        var root = document.RootElement;

        Assert.Equal(640, root.GetProperty("WindowWidth").GetInt32());
        Assert.True(root.GetProperty("VSync").GetBoolean());
        Assert.Equal("Web Test", root.GetProperty("appName").GetString());
        Assert.Equal("1.2.3", root.GetProperty("version").GetString());
        Assert.Equal("Web", root.GetProperty("platform").GetString());
    }

    /// <summary>The CLI builds a project it has never seen from its two settings files.</summary>
    [Fact]
    public void ForProjectReadsTheTemplateSettings()
    {
        var repo = EngineRepoLocator.Find();
        Assert.NotNull(repo);
        string helloWorld = Path.Combine(repo!.Root, "Templates", "Hello World");

        var config = PlatformConfig.ForProject(helloWorld, BuildPlatform.Web);

        Assert.Equal(helloWorld, config.ProjectRoot);
        Assert.Equal(BuildPlatform.Web, config.Platform);
        Assert.Equal("Hello World — SexyBiscuit Tutorial", config.AppName);
        Assert.Equal("Scenes/Tutorial", config.StartScene);
        Assert.Contains("Scenes/Tutorial.scene", config.Scenes);

        // A BuildSettings.json in the project wins over the defaults.
        File.WriteAllText(Path.Combine(_projectDir, PlatformConfig.FileName),
            """{ "appName": "Saved Name", "version": "2.0.0", "outputDirectory": "out", "upload": { "url": "https://example.test/upload", "fields": { "game": "title" } } }""");
        var saved = PlatformConfig.ForProject(_projectDir, BuildPlatform.Web);
        Assert.Equal("Saved Name", saved.AppName);
        Assert.Equal("2.0.0", saved.Version);
        Assert.Equal("out", saved.OutputDirectory);
        Assert.Equal("https://example.test/upload", saved.Upload.Url);
        Assert.Equal("title", saved.Upload.Fields["game"]);
        Assert.Contains("Scenes/Main.scene", saved.Scenes);
    }

    /// <summary>An installable build is what makes the web export the mobile build.</summary>
    [Fact]
    public void AnInstallableWebExportHasAManifestIconsAndAServiceWorker()
    {
        var result = Quiet().Export(WebConfig());
        string output = result.OutputPath;

        Assert.True(File.Exists(Path.Combine(output, "manifest.webmanifest")));
        Assert.True(File.Exists(Path.Combine(output, "sw.js")));

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "manifest.webmanifest")));
        Assert.Equal("Web Test", manifest.RootElement.GetProperty("name").GetString());
        Assert.Equal("fullscreen", manifest.RootElement.GetProperty("display").GetString());

        foreach (int size in new[] { 192, 512 })
        {
            byte[] png = File.ReadAllBytes(Path.Combine(output, $"icon-{size}.png"));
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8).ToArray());
        }

        string html = File.ReadAllText(Path.Combine(output, "index.html"));
        Assert.Contains("rel=\"manifest\"", html);
        Assert.Contains("serviceWorker.register('./sw.js')", html);
    }

    /// <summary>
    /// The worker caches every file of the build, so the game opens offline. A file missing from
    /// the list is a blank screen on a phone in a tunnel; the worker itself must not be in it.
    /// </summary>
    [Fact]
    public void TheServiceWorkerPrecachesEveryStagedFile()
    {
        var result = Quiet().Export(WebConfig());
        string worker = File.ReadAllText(Path.Combine(result.OutputPath, "sw.js"));

        var match = Regex.Match(worker, @"const PRECACHE = (\[[\s\S]*?\]);");
        Assert.True(match.Success, "no PRECACHE list");
        var precache = JsonSerializer.Deserialize<List<string>>(match.Groups[1].Value)!;

        var expected = ExportPipeline.ListFiles(result.OutputPath)
            .Where(f => f != "sw.js" && f != "HOW-TO-RUN.txt")
            .Select(f => "./" + f)
            .ToList();

        Assert.Equal(expected, precache);
        Assert.Contains("./index.html", precache);
        Assert.Contains("./engine/runtime/Runtime.js", precache);
        Assert.Contains("web-test-1.2.3-", worker);
    }

    [Fact]
    public void WebInstallableFalseOmitsThePwaFiles()
    {
        var config = WebConfig();
        config.WebInstallable = false;

        var result = Quiet().Export(config);
        string output = result.OutputPath;

        Assert.False(File.Exists(Path.Combine(output, "manifest.webmanifest")));
        Assert.False(File.Exists(Path.Combine(output, "sw.js")));
        Assert.False(File.Exists(Path.Combine(output, "icon-192.png")));
        Assert.DoesNotContain("serviceWorker", File.ReadAllText(Path.Combine(output, "index.html")));
    }

    [Fact]
    public void AProvidedIconIsCopiedInsteadOfGenerated()
    {
        byte[] icon = PngWriter.Solid(8, 200, 30, 30);
        File.WriteAllBytes(Path.Combine(_projectDir, "Assets", "icon.png"), icon);

        var config = WebConfig();
        config.WebIconPath = "Assets/icon.png";

        var result = Quiet().Export(config);
        Assert.Equal(icon, File.ReadAllBytes(Path.Combine(result.OutputPath, "icon-512.png")));
    }

    /// <summary>
    /// <c>html5/tools/export.js</c> fills the same three templates. A placeholder one side does
    /// not know is a build with <c>{{</c> in it, so the sets are pinned here and in
    /// <c>html5/tests/tools.test.js</c>.
    /// </summary>
    [Fact]
    public void TheTemplatePlaceholdersMatchTheNodeExporter()
    {
        var repo = EngineRepoLocator.Find();
        Assert.NotNull(repo);
        string templates = Path.Combine(repo!.Root, "html5", "runtime", "export");

        static List<string> Placeholders(string path)
            => Regex.Matches(File.ReadAllText(path), @"\{\{(\w+)\}\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(new[] { "pwaBoot", "pwaHead", "scene", "stats", "themeColor", "title" }, Placeholders(Path.Combine(templates, "index.html.tmpl")));
        Assert.Equal(new[] { "name", "shortName", "themeColor" }, Placeholders(Path.Combine(templates, "manifest.webmanifest.tmpl")));
        Assert.Equal(new[] { "cacheName", "precache" }, Placeholders(Path.Combine(templates, "sw.js.tmpl")));

        Assert.Throws<InvalidOperationException>(() => ExportPipeline.Fill("{{missing}}", new()));
    }

    /// <summary>A download is one file; the archive lists every staged file.</summary>
    [Fact]
    public async Task AWebExportIsPackagedAsAZip()
    {
        var result = await Quiet().ExportAsync(WebConfig(), new ExportOptions { Publish = false, Package = true });

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.NotNull(result.Archive);
        Assert.EndsWith("web-test-1.2.3-web.zip", result.Archive);
        Assert.Equal(Path.Combine(_projectDir, "dist"), Path.GetDirectoryName(result.Archive));

        using var zip = ZipFile.OpenRead(result.Archive!);
        // Empty folders (Logs/) are entries too; compare files with files.
        var entries = zip.Entries.Select(e => e.FullName).Where(n => !n.EndsWith('/')).ToList();
        Assert.Equal(ExportPipeline.ListFiles(result.OutputPath).Count, entries.Count);
        Assert.Contains("index.html", entries);
    }

    /// <summary>The multi-target run is what the CLI and CI read: one line per target, nothing else.</summary>
    [Fact]
    public async Task ExportAllWritesAReportWithOneLinePerTarget()
    {
        var report = await Quiet().ExportAllAsync(WebConfig(), new[] { BuildPlatform.Web }, new ExportOptions { Publish = false, Package = true });

        Assert.True(report.Success);
        Assert.Single(report.Targets);
        Assert.Equal("Web", report.Targets[0].Platform);
        Assert.True(report.Targets[0].ArchiveBytes > 0);

        string path = Path.Combine(_projectDir, "dist", BuildReport.FileName);
        Assert.True(File.Exists(path), "no build-report.json");
        var loaded = BuildReport.Load(path);
        Assert.Equal("Web Test", loaded.AppName);
        Assert.Equal("1.2.3", loaded.Version);

        var line = Assert.Single(report.ToSummaryLines());
        Assert.StartsWith("web", line);
        Assert.Contains("ok", line);
        Assert.Contains("web-test-1.2.3-web.zip", line);
    }
}
