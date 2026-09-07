using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SexyBiscuit.Engine.Build.Upload;

namespace SexyBiscuit.Engine.Build;

/// <summary>Finds the target a build publishes to, or explains why there is none.</summary>
public delegate IUploadTarget? UploadTargetLookup(UploadSettings settings, out string? why);

/// <summary>Result of a full <see cref="ExportPipeline.Export"/> run.</summary>
public record ExportResult(
    bool          Success,
    string        OutputPath,
    List<string>  Log,
    List<string>  Errors,
    TimeSpan      Duration,
    string?       Archive        = null,
    string?       ExecutablePath = null,
    string?       Rid            = null);

/// <summary>What an export does beyond staging content.</summary>
public sealed record ExportOptions
{
    /// <summary>Run <c>dotnet publish</c> for desktop targets. Needs the engine source and the .NET SDK.</summary>
    public bool Publish { get; init; } = true;

    /// <summary>Zip (Windows, web) or tar.gz (Linux, macOS) the platform folder beside it.</summary>
    public bool Package { get; init; } = true;

    /// <summary>Send every archive to the configured upload target.</summary>
    public bool Upload { get; init; }

    /// <summary>Overrides <see cref="PlatformConfig.Version"/> for this run.</summary>
    public string? Version { get; init; }

    /// <summary>False publishes the build hidden, to be made live from the site later. Null follows the project's setting.</summary>
    public bool? PublishListing { get; init; }
}

/// <summary>
/// Orchestrates a complete game export:
/// validation → directory structure → asset cooking → scripts → scenes →
/// project settings → platform defines → Steam VDF → HTML5 runtime → publish → package.
/// </summary>
/// <remarks>
/// Every path resolves against <see cref="PlatformConfig.EffectiveProjectRoot"/>, never the
/// working directory, and the platform's folder is added to <see cref="PlatformConfig.OutputDirectory"/>
/// exactly once. The web page, manifest and service worker come from the templates under
/// <c>html5/runtime/export/</c>, which <c>html5/tools/export.js</c> fills with the same values,
/// so the two exporters produce one build.
/// </remarks>
public class ExportPipeline
{
    // -------------------------------------------------------------------------
    // Internal state reset each Export() call
    // -------------------------------------------------------------------------
    private readonly List<string> _log    = new();
    private readonly List<string> _errors = new();

    /// <summary>Where log lines go as they happen. Null for a quiet run; the result carries the log anyway.</summary>
    public TextWriter? Output { get; set; } = Console.Out;

    /// <summary>How an upload target is found. Replaced by tests so a publish never opens a socket.</summary>
    public UploadTargetLookup UploadTargetFactory { get; set; } = (UploadSettings settings, out string? why) => UploadTargets.FromConfig(settings, out why);

    /// <summary>The theme colour of the exported page and its manifest.</summary>
    public const string ThemeColour = "#12141a";

    // -------------------------------------------------------------------------
    // Paths
    // -------------------------------------------------------------------------

    /// <summary>
    /// The folder the platform folders go under. A saved <c>OutputDirectory</c> that already
    /// ends in a platform name is treated as that platform's folder, so an older settings file
    /// does not produce <c>dist/Windows_x64/Windows_x64</c>.
    /// </summary>
    public static string ResolveOutputRoot(PlatformConfig config)
    {
        string root = config.EffectiveProjectRoot;

        // A settings file written on Windows says dist\Windows_x64; on a Mac the backslash is
        // not a separator, so it is normalised before the path is taken apart.
        string configured = config.OutputDirectory.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        string output = Path.IsPathRooted(configured) ? configured : Path.Combine(root, configured);
        output = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string leaf = Path.GetFileName(output);
        if (Enum.TryParse<BuildPlatform>(leaf, ignoreCase: true, out _))
            return Path.GetDirectoryName(output) ?? output;

        return output;
    }

    /// <summary>The folder one platform's build is staged in.</summary>
    public static string PlatformOutputDirectory(PlatformConfig config)
        => Path.Combine(ResolveOutputRoot(config), config.Platform.ToString());

    /// <summary>A file-name-safe form of the app name: "My Great Game!" to "my-great-game".</summary>
    public static string Slugify(string name)
    {
        var sb = new StringBuilder();
        bool dash = false;
        foreach (char c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) && c < 128) { sb.Append(c); dash = false; }
            else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
        }
        string slug = sb.ToString().TrimEnd('-');
        return slug.Length > 0 ? slug : "game";
    }

    // -------------------------------------------------------------------------
    // Main entry points
    // -------------------------------------------------------------------------

    /// <summary>Stages one platform without publishing or packaging — the editor's Build button and the tests.</summary>
    public ExportResult Export(PlatformConfig config)
        => ExportAsync(config, new ExportOptions { Publish = false, Package = false }).GetAwaiter().GetResult();

    /// <summary>
    /// Runs all export steps sequentially. Each step catches its own exceptions
    /// so that later steps still run and all errors are collected.
    /// </summary>
    public async Task<ExportResult> ExportAsync(PlatformConfig config, ExportOptions? options = null, CancellationToken cancellation = default)
    {
        options ??= new ExportOptions();
        _log.Clear();
        _errors.Clear();

        var sw = Stopwatch.StartNew();
        bool success = true;

        string root              = config.EffectiveProjectRoot;
        string platformOutputDir = PlatformOutputDirectory(config);
        string version           = options.Version ?? config.Version;

        // -- Step 1: Validate config ------------------------------------------
        Log("Step 1: Validating configuration…");
        if (!RunStep("Validate", () => ValidateConfig(config, platformOutputDir)))
            success = false;

        // -- Step 2: Create output directory structure ------------------------
        Log("Step 2: Creating output directory structure…");
        if (!RunStep("CreateDirs", () => CreateDirectoryStructure(platformOutputDir)))
            success = false;

        // -- Step 3: Cook assets ----------------------------------------------
        Log("Step 3: Cooking assets…");
        RunStep("CookAssets", () => CookAssets(config, root, platformOutputDir));
        // Asset cooking failure is non-fatal (we log the errors but continue)

        // -- Step 4: Copy scripts ---------------------------------------------
        Log("Step 4: Copying scripts…");
        RunStep("CopyScripts", () => CopyDirectory(Path.Combine(root, "Scripts"), Path.Combine(platformOutputDir, "Scripts")));

        // -- Step 5: Copy scenes ----------------------------------------------
        Log("Step 5: Copying scenes…");
        RunStep("CopyScenes", () => CopyDirectory(Path.Combine(root, "Scenes"), Path.Combine(platformOutputDir, "Scenes")));

        // -- Step 6: Write ProjectSettings.json -------------------------------
        Log("Step 6: Writing ProjectSettings.json…");
        RunStep("ProjectSettings", () => WriteProjectSettings(config, root, platformOutputDir, version));

        // -- Step 7: Write PlatformDefines.json --------------------------------
        Log("Step 7: Writing PlatformDefines.json…");
        RunStep("PlatformDefines", () => WritePlatformDefines(config, platformOutputDir));

        // -- Step 8: Steam VDF (Steam platforms only) -------------------------
        bool isSteam = config.Platform is BuildPlatform.Steam_Windows
                                       or BuildPlatform.Steam_Linux
                                       or BuildPlatform.Steam_macOS;
        if (isSteam)
        {
            Log("Step 8: Generating Steam app_build.vdf…");
            RunStep("SteamVdf", () => WriteSteamVdf(config, platformOutputDir));
        }
        else
        {
            Log("Step 8: Skipped (not a Steam platform).");
        }

        // -- Step 9: Stage the HTML5 runtime (Web only) -----------------------
        if (config.Platform == BuildPlatform.Web)
        {
            Log("Step 9: Staging the HTML5 runtime…");
            if (!RunStep("WebRuntime", () => StageWebRuntime(config, root, platformOutputDir, version)))
                success = false;
        }
        else
        {
            Log("Step 9: Skipped (not a web platform).");
        }

        // -- Step 10: Publish the game binary (desktop only) ------------------
        string? rid        = RuntimeIdentifiers.For(config.Platform);
        string? executable = null;
        if (options.Publish && rid != null)
        {
            Log($"Step 10: Publishing for {rid}…");
            var publisher = new DesktopPublisher();
            var request   = new PublishRequest(root, config.Platform, config.Configuration, platformOutputDir, config.AppName);
            PublishResult? published = null;

            try
            {
                published = await publisher.PublishAsync(request, new Progress<string>(line => Log("  " + line)), cancellation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _errors.Add($"[Publish] {ex.Message}");
                Log($"ERROR: [Publish] {ex.Message}");
            }

            if (published != null)
            {
                foreach (var error in published.Errors)
                {
                    _errors.Add($"[Publish] {error}");
                    Log($"ERROR: [Publish] {error}");
                }
                if (published.Success)
                {
                    executable = published.ExecutablePath;
                    Log($"  Published: {executable} in {published.Duration.TotalSeconds:F1}s");
                }
            }
            if (published?.Success != true) success = false;
        }
        else if (options.Publish && config.Platform == BuildPlatform.Android)
        {
            // Android is not a RID, so it does not go through DesktopPublisher: an APK is an
            // application project with an Activity and its content as Android assets. Until
            // this existed the target staged some files, skipped the publish and reported
            // "ok" with a 17 KB zip holding no application at all.
            Log("Step 10: Building the APK…");
            var publisher = new AndroidPublisher();
            var request = new PublishRequest(root, config.Platform, config.Configuration, platformOutputDir, config.AppName)
            {
                Version = version,
            };

            PublishResult? built = null;
            try
            {
                built = await publisher.PublishAsync(request, new Progress<string>(line => Log("  " + line)), cancellation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _errors.Add($"[Publish] {ex.Message}");
                Log($"ERROR: [Publish] {ex.Message}");
            }

            if (built != null)
            {
                foreach (var error in built.Errors)
                {
                    _errors.Add($"[Publish] {error}");
                    Log($"ERROR: [Publish] {error}");
                }
                if (built.Success)
                {
                    executable = built.ExecutablePath;
                    Log($"  Built: {executable} in {built.Duration.TotalSeconds:F1}s");
                }
            }
            if (built?.Success != true) success = false;
        }
        else if (rid == null && IsMobile(config.Platform))
        {
            // iOS has no publish path here, and skipping quietly is the dangerous version of
            // that: the run would report "ok" and package an archive holding no application.
            _errors.Add($"[Publish] {config.Platform} cannot be built yet: there is no publish path "
                + "for it, so the archive would hold no application.");
            Log($"Step 10: FAILED ({config.Platform} has no publish path).");
            success = false;
        }
        else
        {
            Log(rid == null ? "Step 10: Skipped (not a desktop platform)." : "Step 10: Skipped (publish disabled).");
        }

        // -- Step 11: Package -------------------------------------------------
        string? archive = null;
        if (options.Package && success && !_errors.Any())
        {
            Log("Step 11: Packaging…");
            RunStep("Package", () => archive = Package(config, platformOutputDir, version, rid, executable));
        }
        else
        {
            Log(options.Package ? "Step 11: Skipped (the export has errors)." : "Step 11: Skipped (packaging disabled).");
        }

        sw.Stop();

        // Final verdict
        bool hasErrors = _errors.Count > 0;
        if (hasErrors) success = false;

        Log($"Export {(success ? "SUCCEEDED" : "FAILED")} in {sw.Elapsed.TotalSeconds:F2}s. " +
            $"Errors: {_errors.Count}");

        return new ExportResult(
            Success:        success,
            OutputPath:     platformOutputDir,
            Log:            new List<string>(_log),
            Errors:         new List<string>(_errors),
            Duration:       sw.Elapsed,
            Archive:        archive,
            ExecutablePath: executable,
            Rid:            rid);
    }

    /// <summary>
    /// Exports every target in turn, uploads the archives when asked, and writes
    /// <see cref="BuildReport.FileName"/> beside them. One target failing does not stop the rest.
    /// </summary>
    public async Task<BuildReport> ExportAllAsync(PlatformConfig template, IReadOnlyList<BuildPlatform> targets, ExportOptions? options = null,
                                                  IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        options ??= new ExportOptions();
        string root    = template.EffectiveProjectRoot;
        string outRoot = ResolveOutputRoot(template);
        string version = options.Version ?? template.Version;

        var targetReports = new List<TargetReport>();

        foreach (var platform in targets)
        {
            cancellation.ThrowIfCancellationRequested();
            var config = template.With(platform);
            progress?.Report($"{platform}: exporting");

            var result = await ExportAsync(config, options, cancellation).ConfigureAwait(false);
            targetReports.Add(new TargetReport
            {
                Platform     = platform.ToString(),
                Rid          = result.Rid,
                Success      = result.Success,
                Seconds      = Math.Round(result.Duration.TotalSeconds, 1),
                OutputPath   = result.OutputPath,
                Executable   = result.ExecutablePath,
                Archive      = result.Archive,
                ArchiveBytes = result.Archive != null && File.Exists(result.Archive) ? new FileInfo(result.Archive).Length : 0,
                Errors       = new List<string>(result.Errors),
            });
            progress?.Report($"{platform}: {(result.Success ? "ok" : "failed")}");
        }

        if (options.Upload)
        {
            var target = UploadTargetFactory(template.Upload, out var why);
            if (target == null)
            {
                progress?.Report($"publish skipped: {why}");
                foreach (var t in targetReports.Where(t => t.Archive != null))
                    t.Errors.Add($"not published: {why}");
            }
            else
            {
                var staged   = new BuildReport { Targets = targetReports };
                var pending  = BuildPublisher.ArtifactsFrom(staged, template.Upload, out var skipped);
                foreach (var note in skipped) progress?.Report($"publish: {note}");

                var metadata = BuildPublisher.MetadataFor(template, version, GitInfo.TryReadHeadSha(root), publish: options.PublishListing);
                var results  = await BuildPublisher.PublishAsync(target, pending, metadata, template.Upload, progress, cancellation).ConfigureAwait(false);

                foreach (var published in results)
                {
                    int index = targetReports.FindIndex(t => t.Archive == published.Artifact);
                    if (index < 0) continue;
                    var t = targetReports[index];
                    targetReports[index] = t with
                    {
                        UploadUrl    = published.Url,
                        UploadStatus = published.Status,
                        UploadId     = published.Id,
                        Success      = t.Success && published.Success,
                        Errors       = published.Success ? t.Errors : t.Errors.Append($"publish failed: {published.Error}").ToList(),
                    };
                }
            }
        }

        var report = new BuildReport
        {
            AppName       = template.AppName,
            Version       = version,
            Configuration = template.Configuration.ToString(),
            GitSha        = GitInfo.TryReadHeadSha(root),
            Targets       = targetReports,
        };

        try { report.Save(Path.Combine(outRoot, BuildReport.FileName)); }
        catch (Exception ex) { progress?.Report($"could not write {BuildReport.FileName}: {ex.Message}"); }

        return report;
    }

    // -------------------------------------------------------------------------
    // Step implementations
    // -------------------------------------------------------------------------

    // Step 1 — Validate
    /// <summary>
    /// A platform whose archive is meaningless without a published application.
    /// Web is deliberately not here: its export IS the artifact.
    /// </summary>
    private static bool IsMobile(BuildPlatform platform)
        => platform is BuildPlatform.Android or BuildPlatform.iOS;

    private void ValidateConfig(PlatformConfig config, string platformOutputDir)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(config.AppName))
            failures.Add("AppName is required.");

        if (string.IsNullOrWhiteSpace(config.Version))
            failures.Add("Version is required.");

        if (string.IsNullOrWhiteSpace(config.OutputDirectory))
            failures.Add("OutputDirectory is required.");

        if (string.IsNullOrWhiteSpace(config.StartScene))
            failures.Add("StartScene is required.");

        if (!Directory.Exists(config.EffectiveProjectRoot))
            failures.Add($"Project root '{config.EffectiveProjectRoot}' does not exist.");

        // Platform-specific checks
        if (config.Platform == BuildPlatform.Android && !string.IsNullOrWhiteSpace(config.AndroidKeystorePath))
        {
            // A keystore is only needed to sign with YOUR key, for Play. Without one the
            // Android SDK signs with its debug key, which installs fine by hand and is what a
            // playtest build wants — so demanding a keystore here just made every Android
            // build fail before it started.
            string keystore = Path.IsPathRooted(config.AndroidKeystorePath)
                ? config.AndroidKeystorePath
                : Path.Combine(config.EffectiveProjectRoot, config.AndroidKeystorePath);
            if (!File.Exists(keystore))
                failures.Add($"AndroidKeystorePath '{config.AndroidKeystorePath}' does not exist.");
        }

        if (config.Platform == BuildPlatform.iOS)
        {
            if (string.IsNullOrWhiteSpace(config.IOSTeamId))
                failures.Add("IOSTeamId is required for iOS builds.");
        }

        // Check output directory is writable
        try
        {
            Directory.CreateDirectory(platformOutputDir);
            string probe = Path.Combine(platformOutputDir, ".write_probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            failures.Add($"Output directory '{platformOutputDir}' is not writable: {ex.Message}");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Config validation failed:\n  " + string.Join("\n  ", failures));

        Log($"Validation passed. Platform={config.Platform} Config={config.Configuration} " +
            $"App='{config.AppName}' v{config.Version} Root='{config.EffectiveProjectRoot}'");
    }

    // Step 2 — Directory structure
    private void CreateDirectoryStructure(string root)
    {
        string[] subdirs = { "Assets", "Scripts", "Scenes", "Logs" };
        foreach (var sub in subdirs)
        {
            string path = Path.Combine(root, sub);
            Directory.CreateDirectory(path);
            Log($"  Created: {path}");
        }
    }

    // Step 3 — Asset cooking
    private void CookAssets(PlatformConfig config, string projectRoot, string platformOutputDir)
    {
        string srcAssets = Path.Combine(projectRoot, "Assets");
        string dstAssets = Path.Combine(platformOutputDir, "Assets");

        if (!config.CookAssets)
        {
            Log("  CookAssets=false — skipping asset cooking, copying raw assets.");
            CopyDirectory(srcAssets, dstAssets);
            return;
        }

        if (!Directory.Exists(srcAssets))
        {
            Log($"  No Assets folder at '{srcAssets}' (skipping).");
            return;
        }

        var cooker = new AssetCooker { IsIncrementalCook = true };
        CookResult result = cooker.Cook(srcAssets, dstAssets, config);

        foreach (var line in cooker.GetLog())
            Log($"  [Cooker] {line}");

        foreach (var err in result.Errors)
        {
            _errors.Add($"[AssetCooker] {err}");
            Log($"  ERROR: {err}");
        }

        Log($"  Assets cooked. Processed={result.FilesProcessed} " +
            $"Skipped={result.FilesSkipped} Errors={result.ErrorCount}");
    }

    // Steps 4+5 — Copy directories
    private void CopyDirectory(string srcDir, string dstDir, Func<string, bool>? keep = null)
    {
        if (!Directory.Exists(srcDir))
        {
            Log($"  Source directory not found (skipping): '{srcDir}'");
            return;
        }

        Directory.CreateDirectory(dstDir);

        foreach (string file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(srcDir, file);
            if (keep != null && !keep(rel.Replace('\\', '/'))) continue;

            string dst = Path.Combine(dstDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
            Log($"  COPY: {rel}");
        }
    }

    // Step 6 — ProjectSettings.json
    /// <summary>
    /// The project's own settings with the build's metadata on top. A shipped game reads
    /// <c>WindowTitle</c>, <c>WindowWidth</c> and <c>VSync</c> from this file exactly as the
    /// editor did, so the project's keys must survive; the build adds its own beside them.
    /// </summary>
    private void WriteProjectSettings(PlatformConfig config, string projectRoot, string platformOutputDir, string version)
    {
        JsonObject settings = new();

        string source = Path.Combine(projectRoot, "ProjectSettings.json");
        if (File.Exists(source))
        {
            try
            {
                settings = JsonNode.Parse(File.ReadAllText(source).TrimStart('﻿')) as JsonObject ?? new JsonObject();
            }
            catch (JsonException ex)
            {
                _errors.Add($"[ProjectSettings] the project's ProjectSettings.json does not parse: {ex.Message}");
            }
        }

        settings["appName"]             = config.AppName;
        settings["version"]             = version;
        settings["bundleId"]            = config.BundleId;
        settings["platform"]            = config.Platform.ToString();
        settings["buildConfiguration"]  = config.Configuration.ToString();
        settings["startScene"]          = config.StartScene;
        settings["scenes"]              = new JsonArray(config.Scenes.Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
        settings["steamAppId"]          = config.SteamAppId;
        settings["includeDebugOverlay"] = config.IncludeDebugOverlay;

        string json = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        string path = Path.Combine(platformOutputDir, "ProjectSettings.json");
        File.WriteAllText(path, json, Utf8NoBom);
        Log($"  Written: {path}");
    }

    // Step 7 — PlatformDefines.json
    private void WritePlatformDefines(PlatformConfig config, string platformOutputDir)
    {
        var defines = new List<string>();

        // Platform defines
        defines.Add(config.Platform switch
        {
            BuildPlatform.Windows_x64   or BuildPlatform.Windows_x86   => "PLATFORM_WINDOWS",
            BuildPlatform.Linux_x64                                      => "PLATFORM_LINUX",
            BuildPlatform.macOS_x64     or BuildPlatform.macOS_ARM64    => "PLATFORM_MACOS",
            BuildPlatform.Android                                        => "PLATFORM_ANDROID",
            BuildPlatform.iOS                                            => "PLATFORM_IOS",
            BuildPlatform.Steam_Windows                                  => "PLATFORM_WINDOWS",
            BuildPlatform.Steam_Linux                                    => "PLATFORM_LINUX",
            BuildPlatform.Steam_macOS                                    => "PLATFORM_MACOS",
            BuildPlatform.Web                                            => "PLATFORM_WEB",
            _                                                            => "PLATFORM_UNKNOWN",
        });

        // Architecture
        if (config.Platform == BuildPlatform.Windows_x86)
            defines.Add("ARCH_X86");
        else if (config.Platform == BuildPlatform.macOS_ARM64)
            defines.Add("ARCH_ARM64");
        else if (config.Platform == BuildPlatform.Web)
            defines.Add("ARCH_WASM");
        else
            defines.Add("ARCH_X64");

        // Configuration
        defines.Add(config.Configuration switch
        {
            BuildConfiguration.Debug       => "BUILD_DEBUG",
            BuildConfiguration.Development => "BUILD_DEVELOPMENT",
            BuildConfiguration.Release     => "BUILD_RELEASE",
            _                              => "BUILD_DEBUG",
        });

        // Steam
        bool isSteam = config.Platform is BuildPlatform.Steam_Windows
                                       or BuildPlatform.Steam_Linux
                                       or BuildPlatform.Steam_macOS;
        if (isSteam)
            defines.Add("STEAMWORKS");

        // Mobile
        if (config.Platform == BuildPlatform.Android) defines.Add("MOBILE");
        if (config.Platform == BuildPlatform.iOS)     defines.Add("MOBILE");

        // Debug overlay
        if (config.IncludeDebugOverlay)
            defines.Add("DEBUG_OVERLAY");

        string json = JsonSerializer.Serialize(defines,
            new JsonSerializerOptions { WriteIndented = true });

        string path = Path.Combine(platformOutputDir, "PlatformDefines.json");
        File.WriteAllText(path, json, Utf8NoBom);
        Log($"  Written: {path} ({defines.Count} defines: {string.Join(", ", defines)})");
    }

    // Step 9 — HTML5 runtime
    /// <summary>
    /// Copies the JavaScript runtime beside the exported project and writes the
    /// page that boots it, and — when the build is installable — the manifest, the
    /// icons and the service worker.
    /// </summary>
    /// <remarks>
    /// A web export stages rather than compiles: the engine is already
    /// JavaScript, and the project's scenes, scripts and assets are already in
    /// the formats it reads. What the pipeline has to do is put the two together
    /// and point a page at them.
    ///
    /// Behaviour written in C# does not come across. Those component types load
    /// as placeholders that preserve their data, so the scene stays intact and
    /// the gap is visible rather than silent — see <c>html5/README.md</c>.
    /// </remarks>
    private void StageWebRuntime(PlatformConfig config, string projectRoot, string platformOutputDir, string version)
    {
        string source = Html5Root();

        // Only the runtime is needed to play a game; the editor, the tests, the
        // examples and the export templates are not part of a shipped build.
        CopyDirectory(Path.Combine(source, "src"), Path.Combine(platformOutputDir, "engine", "src"));
        CopyDirectory(Path.Combine(source, "runtime"), Path.Combine(platformOutputDir, "engine", "runtime"), rel => !rel.StartsWith("export/", StringComparison.Ordinal));
        Log("  Staged: engine/src, engine/runtime");

        WriteWebIndex(config, projectRoot, platformOutputDir, version, Path.Combine(source, "runtime", "export"));
    }

    /// <summary>The engine checkout's <c>html5/</c> folder, or a clear error naming the environment variable.</summary>
    private static string Html5Root()
    {
        string? engineRoot = Code.EngineRepoLocator.Find()?.Root;
        string source = engineRoot != null ? Path.Combine(engineRoot, "html5") : "html5";

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(
                $"Could not find the HTML5 runtime at '{source}'. Set " +
                $"{Code.EngineRepoLocator.EnvironmentVariable} to the engine checkout, or run the " +
                "export from the repository root.");
        }

        return source;
    }

    /// <summary>Writes the page, the installable files and a note on serving, from the shared templates.</summary>
    private void WriteWebIndex(PlatformConfig config, string projectRoot, string platformOutputDir, string version, string templatesDir)
    {
        // The project's own files sit at the root of the export, so the runtime's
        // asset root is "./" and StartScene resolves exactly as it does natively.
        string title = System.Net.WebUtility.HtmlEncode(config.AppName);
        string scene = System.Net.WebUtility.HtmlEncode(config.StartScene ?? string.Empty);
        string stats = config.Configuration == BuildConfiguration.Release ? "false" : "true";
        string slug  = Slugify(config.AppName);

        // One string identifying this build, used as the service worker's cache
        // name AND stamped into the page. A console paste from a player then says
        // which build they are running, which is otherwise unanswerable: a stale
        // service worker serves old files whose line numbers look exactly like
        // today's.
        string? headSha = GitInfo.TryReadHeadSha(projectRoot);
        string buildId = $"{slug}-{version}-{(headSha != null ? headSha.Substring(0, 8) : "local")}";

        string pwaHead = string.Empty;
        string pwaBoot = string.Empty;

        if (config.WebInstallable)
        {
            var (r, g, b, a) = PngWriter.ParseHex(ThemeColour);
            foreach (int size in new[] { 192, 512 })
            {
                string icon = Path.Combine(platformOutputDir, $"icon-{size}.png");
                string? provided = string.IsNullOrWhiteSpace(config.WebIconPath) ? null : Path.Combine(projectRoot, config.WebIconPath);
                if (provided != null && File.Exists(provided)) File.Copy(provided, icon, overwrite: true);
                else PngWriter.WriteSolid(icon, size, r, g, b, a);
            }

            string shortName = config.AppName.Length > 12 ? config.AppName.Substring(0, 12) : config.AppName;
            File.WriteAllText(Path.Combine(platformOutputDir, "manifest.webmanifest"), Fill(Template(templatesDir, "manifest.webmanifest.tmpl"), new()
            {
                ["name"]       = JsonSerializer.Serialize(config.AppName),
                ["shortName"]  = JsonSerializer.Serialize(shortName),
                ["themeColor"] = ThemeColour,
            }), Utf8NoBom);

            pwaHead = "<link rel=\"manifest\" href=\"manifest.webmanifest\">\n<link rel=\"apple-touch-icon\" href=\"icon-192.png\">\n";
            pwaBoot = "\nif ('serviceWorker' in navigator) navigator.serviceWorker.register('./sw.js').catch(() => {});\n";
            Log("  Written: manifest.webmanifest, icon-192.png, icon-512.png");
        }

        // No byte order mark: one before <!doctype> can push a browser into
        // quirks mode, and the runtime's own loader has to strip it back off
        // every JSON file this pipeline writes as it is.
        string indexPath = Path.Combine(platformOutputDir, "index.html");
        File.WriteAllText(indexPath, Fill(Template(templatesDir, "index.html.tmpl"), new()
        {
            ["title"]      = title,
            ["scene"]      = scene,
            ["stats"]      = stats,
            ["themeColor"] = ThemeColour,
            ["build"]      = System.Net.WebUtility.HtmlEncode(buildId),
            ["pwaHead"]    = pwaHead,
            ["pwaBoot"]    = pwaBoot,
        }), Utf8NoBom);
        Log($"  Written: {indexPath}");

        // Browsers refuse to load ES modules from a file:// path, so someone who
        // opens the export by double-clicking gets a blank page and no reason.
        var note = new StringBuilder();
        note.AppendLine($"{config.AppName} {version} — web build");
        note.AppendLine();
        note.AppendLine("Serve this folder over HTTP and open index.html.");
        note.AppendLine("Opening it directly from disk will not work: browsers refuse to load");
        note.AppendLine("ES modules from a file:// path, and a service worker needs HTTPS or localhost.");
        note.AppendLine();
        note.AppendLine("Any static host will do. To try it locally:");
        note.AppendLine("    npx serve .");
        note.AppendLine("or, from an engine checkout:");
        note.AppendLine("    node html5/tools/serve.js 8080 .");

        string notePath = Path.Combine(platformOutputDir, "HOW-TO-RUN.txt");
        File.WriteAllText(notePath, note.ToString(), Utf8NoBom);
        Log($"  Written: {notePath}");

        // The worker is written last: its precache list is every other file.
        if (config.WebInstallable)
        {
            var precache = ListFiles(platformOutputDir)
                .Where(f => f != "sw.js" && f != "HOW-TO-RUN.txt")
                .Select(f => "./" + f)
                .ToList();

            File.WriteAllText(Path.Combine(platformOutputDir, "sw.js"), Fill(Template(templatesDir, "sw.js.tmpl"), new()
            {
                ["cacheName"] = buildId,
                ["precache"]  = JsonSerializer.Serialize(precache, new JsonSerializerOptions { WriteIndented = true }),
            }), Utf8NoBom);
            Log("  Written: sw.js");
        }
    }

    private static string Template(string templatesDir, string name)
    {
        string path = Path.Combine(templatesDir, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"The web export template '{name}' is missing from '{templatesDir}'.", path);
        return File.ReadAllText(path);
    }

    /// <summary>Replaces every <c>{{key}}</c>. A key with no value is an error, not a blank.</summary>
    public static string Fill(string template, Dictionary<string, string> values)
    {
        return System.Text.RegularExpressions.Regex.Replace(template, @"\{\{(\w+)\}\}", match =>
        {
            string key = match.Groups[1].Value;
            if (!values.TryGetValue(key, out var value))
                throw new InvalidOperationException($"template placeholder {{{{{key}}}}} has no value");
            return value;
        });
    }

    /// <summary>Every file under a folder, relative with forward slashes, sorted.</summary>
    public static List<string> ListFiles(string dir)
        => Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToList();

    // Step 8 — Steam app_build.vdf
    private void WriteSteamVdf(PlatformConfig config, string platformOutputDir)
    {
        if (config.SteamAppId == 0)
        {
            Log("  Warning: SteamAppId is 0. The VDF will be written but may be invalid.");
        }

        // Determine depot content path (the platform subfolder itself)
        string contentPath = Path.GetFullPath(platformOutputDir).Replace('\\', '/');

        var sb = new StringBuilder();
        sb.AppendLine("\"AppBuild\"");
        sb.AppendLine("{");
        sb.AppendLine($"\t\"AppID\"\t\t\"{config.SteamAppId}\"");
        sb.AppendLine($"\t\"Desc\"\t\t\"{config.AppName} {config.Version} - {config.SteamBranch}\"");
        sb.AppendLine($"\t\"BuildOutput\"\t\"{contentPath}/steam_build_output\"");
        sb.AppendLine($"\t\"ContentRoot\"\t\"{contentPath}\"");
        sb.AppendLine($"\t\"SetLive\"\t\t\"{config.SteamBranch}\"");
        sb.AppendLine();
        sb.AppendLine("\t\"Depots\"");
        sb.AppendLine("\t{");
        sb.AppendLine($"\t\t\"{config.SteamDepotId}\"");
        sb.AppendLine("\t\t{");
        sb.AppendLine("\t\t\t\"FileMapping\"");
        sb.AppendLine("\t\t\t{");
        sb.AppendLine($"\t\t\t\t\"LocalPath\"\t\"*\"");
        sb.AppendLine($"\t\t\t\t\"DepotPath\"\t\".\"");
        sb.AppendLine("\t\t\t\t\"recursive\"\t\"1\"");
        sb.AppendLine("\t\t\t}");
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t}");
        sb.AppendLine("}");

        string vdfPath = Path.Combine(platformOutputDir, "app_build.vdf");
        File.WriteAllText(vdfPath, sb.ToString(), Encoding.UTF8);
        Log($"  Written: {vdfPath} (AppID={config.SteamAppId} DepotID={config.SteamDepotId} Branch={config.SteamBranch})");
    }

    // Step 11 — Package
    /// <summary>
    /// Archives the platform folder beside itself: a zip for Windows and the web (what a
    /// browser downloads and Explorer opens), a tar.gz for Linux and macOS so the binary keeps
    /// its executable bit.
    /// </summary>
    private string Package(PlatformConfig config, string platformOutputDir, string version, string? rid, string? built)
    {
        string outRoot = Path.GetDirectoryName(platformOutputDir)!;
        string stem    = $"{Slugify(config.AppName)}-{version}-{rid ?? config.Platform.ToString().ToLowerInvariant()}";

        // An APK is already the shippable, installable artifact — and the one thing a phone
        // can open from a download. Zipping it would give a tester a file their phone cannot
        // install, so it is carried out of the head project's output as-is.
        if (config.Platform == BuildPlatform.Android && built != null && File.Exists(built))
        {
            string apk = Path.Combine(outRoot, stem + ".apk");
            if (File.Exists(apk)) File.Delete(apk);
            File.Copy(built, apk);
            Log($"  Packaged: {apk} ({BuildReport.FormatBytes(new FileInfo(apk).Length)})");
            return apk;
        }

        bool useZip    = rid == null || RuntimeIdentifiers.IsWindows(config.Platform);
        string archive = Path.Combine(outRoot, stem + (useZip ? ".zip" : ".tar.gz"));

        if (File.Exists(archive)) File.Delete(archive);

        if (useZip)
        {
            ZipFile.CreateFromDirectory(platformOutputDir, archive, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        else
        {
            using var file = File.Create(archive);
            using var gzip = new GZipStream(file, CompressionLevel.Optimal);
            TarFile.CreateFromDirectory(platformOutputDir, gzip, includeBaseDirectory: false);
        }

        Log($"  Archived: {archive} ({BuildReport.FormatBytes(new FileInfo(archive).Length)})");
        return archive;
    }

    // -------------------------------------------------------------------------
    // Step runner — catches exceptions and records them
    // -------------------------------------------------------------------------

    private bool RunStep(string stepName, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            string msg = $"[{stepName}] {ex.Message}";
            _errors.Add(msg);
            Log($"ERROR: {msg}");
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Logging
    // -------------------------------------------------------------------------

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private void Log(string message)
    {
        _log.Add(message);
        Output?.WriteLine($"[ExportPipeline] {message}");
    }

    // -------------------------------------------------------------------------
    // CLI entry point
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses command-line arguments and runs an export. Returns the exit code.
    /// <para/>
    /// Usage:
    /// <code>sbengine --project Games/Foo --all [--upload] [--config release] [--output dist] [--report path]</code>
    /// See <see cref="ExportCliOptions.Usage"/>.
    /// </summary>
    public static int RunCli(string[] args)
    {
        var options = ExportCliOptions.Parse(args);

        if (options.Help)
        {
            Console.WriteLine(ExportCliOptions.Usage);
            return 0;
        }
        if (options.Errors.Count > 0)
        {
            foreach (var error in options.Errors) Console.Error.WriteLine($"sbengine: {error}");
            Console.Error.WriteLine(ExportCliOptions.Usage);
            return 2;
        }

        var targets = options.ResolvedTargets;

        PlatformConfig template;
        if (!string.IsNullOrWhiteSpace(options.ConfigFile))
        {
            template = PlatformConfig.Load(options.ConfigFile);
            template.ProjectRoot ??= Path.GetDirectoryName(Path.GetFullPath(options.ConfigFile));
            if (!options.Quiet) Console.WriteLine($"[ExportPipeline] Loaded config from '{options.ConfigFile}'.");
        }
        else
        {
            template = PlatformConfig.ForProject(options.ProjectDir ?? Directory.GetCurrentDirectory(), targets[0]);
        }

        if (options.Configuration is { } configuration)
        {
            template.Configuration = configuration;
            // The overlay follows the configuration unless a settings file said otherwise.
            if (string.IsNullOrWhiteSpace(options.ConfigFile) && !File.Exists(Path.Combine(template.EffectiveProjectRoot, PlatformConfig.FileName)))
                template.IncludeDebugOverlay = configuration != BuildConfiguration.Release;
        }
        if (!string.IsNullOrWhiteSpace(options.OutputDirectory)) template.OutputDirectory = options.OutputDirectory;
        if (!string.IsNullOrWhiteSpace(options.Keystore)) template.AndroidKeystorePath = options.Keystore;
        if (options.Depot is { } depot) template.SteamDepotId = depot;
        if (!string.IsNullOrWhiteSpace(options.Version)) template.Version = options.Version;
        if (!string.IsNullOrWhiteSpace(options.Channel))      template.Upload.Channel      = options.Channel;
        if (!string.IsNullOrWhiteSpace(options.Notes))        template.Upload.Notes        = options.Notes;
        if (!string.IsNullOrWhiteSpace(options.Requirements)) template.Upload.Requirements = options.Requirements;
        if (!string.IsNullOrWhiteSpace(options.AppSlug))      template.Upload.AppSlug      = options.AppSlug;
        if (options.NoReplace) template.Upload.Replace = false;

        var pipeline = new ExportPipeline { Output = options.Quiet ? null : Console.Out };
        var progress = new Progress<string>(line => { if (!options.Quiet) Console.WriteLine($"[sbengine] {line}"); });

        BuildReport report;
        try
        {
            report = pipeline.ExportAllAsync(template, targets, new ExportOptions
            {
                Publish = options.Publish,
                Package = options.Package,
                Upload  = options.Upload,
                PublishListing = options.Hidden ? false : null,
            }, progress).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"sbengine: {ex.Message}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"{report.AppName} {report.Version} ({report.Configuration}){(report.GitSha != null ? " @ " + report.GitSha.Substring(0, 8) : "")}");
        foreach (var line in report.ToSummaryLines()) Console.WriteLine(line);

        if (!string.IsNullOrWhiteSpace(options.ReportPath))
        {
            report.Save(options.ReportPath);
            Console.WriteLine($"report: {options.ReportPath}");
        }

        return report.Success ? 0 : 1;
    }
}
