using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SexyBiscuit.Engine.Build;

/// <summary>Result of a full <see cref="ExportPipeline.Export"/> run.</summary>
public record ExportResult(
    bool          Success,
    string        OutputPath,
    List<string>  Log,
    List<string>  Errors,
    TimeSpan      Duration);

/// <summary>
/// Orchestrates a complete game export:
/// validation → directory structure → asset cooking → scripts → scenes →
/// project settings → platform defines → Steam VDF (if applicable).
/// </summary>
public class ExportPipeline
{
    // -------------------------------------------------------------------------
    // Internal state reset each Export() call
    // -------------------------------------------------------------------------
    private readonly List<string> _log    = new();
    private readonly List<string> _errors = new();

    // -------------------------------------------------------------------------
    // Main entry point
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs all export steps sequentially. Each step catches its own exceptions
    /// so that later steps still run and all errors are collected.
    /// </summary>
    public ExportResult Export(PlatformConfig config)
    {
        _log.Clear();
        _errors.Clear();

        var sw = Stopwatch.StartNew();
        bool success = true;

        string platformOutputDir = Path.Combine(config.OutputDirectory, config.Platform.ToString());

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
        RunStep("CookAssets", () => CookAssets(config, platformOutputDir));
        // Asset cooking failure is non-fatal (we log the errors but continue)

        // -- Step 4: Copy scripts ---------------------------------------------
        Log("Step 4: Copying scripts…");
        RunStep("CopyScripts", () => CopyDirectory("Scripts", Path.Combine(platformOutputDir, "Scripts")));

        // -- Step 5: Copy scenes ----------------------------------------------
        Log("Step 5: Copying scenes…");
        RunStep("CopyScenes", () => CopyDirectory("Scenes", Path.Combine(platformOutputDir, "Scenes")));

        // -- Step 6: Write ProjectSettings.json -------------------------------
        Log("Step 6: Writing ProjectSettings.json…");
        RunStep("ProjectSettings", () => WriteProjectSettings(config, platformOutputDir));

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

        sw.Stop();

        // Final verdict
        bool hasErrors = _errors.Count > 0;
        if (hasErrors) success = false;

        Log($"Export {(success ? "SUCCEEDED" : "FAILED")} in {sw.Elapsed.TotalSeconds:F2}s. " +
            $"Errors: {_errors.Count}");

        return new ExportResult(
            Success:    success,
            OutputPath: platformOutputDir,
            Log:        new List<string>(_log),
            Errors:     new List<string>(_errors),
            Duration:   sw.Elapsed);
    }

    // -------------------------------------------------------------------------
    // Step implementations
    // -------------------------------------------------------------------------

    // Step 1 — Validate
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

        // Platform-specific checks
        if (config.Platform == BuildPlatform.Android)
        {
            if (string.IsNullOrWhiteSpace(config.AndroidKeystorePath))
                failures.Add("AndroidKeystorePath is required for Android builds.");
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
            $"App='{config.AppName}' v{config.Version}");
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
    private void CookAssets(PlatformConfig config, string platformOutputDir)
    {
        string srcAssets = "Assets";
        string dstAssets = Path.Combine(platformOutputDir, "Assets");

        if (!config.CookAssets)
        {
            Log("  CookAssets=false — skipping asset cooking, copying raw assets.");
            CopyDirectory(srcAssets, dstAssets);
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
    private void CopyDirectory(string srcDir, string dstDir)
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
            string dst = Path.Combine(dstDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
            Log($"  COPY: {rel}");
        }
    }

    // Step 6 — ProjectSettings.json
    private void WriteProjectSettings(PlatformConfig config, string platformOutputDir)
    {
        var settings = new
        {
            appName   = config.AppName,
            version   = config.Version,
            bundleId  = config.BundleId,
            platform  = config.Platform.ToString(),
            buildConfiguration = config.Configuration.ToString(),
            startScene = config.StartScene,
            scenes    = config.Scenes,
            steamAppId = config.SteamAppId,
            includeDebugOverlay = config.IncludeDebugOverlay,
        };

        string json = JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { WriteIndented = true });

        string path = Path.Combine(platformOutputDir, "ProjectSettings.json");
        File.WriteAllText(path, json, Encoding.UTF8);
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
            _                                                            => "PLATFORM_UNKNOWN",
        });

        // Architecture
        if (config.Platform == BuildPlatform.Windows_x86)
            defines.Add("ARCH_X86");
        else if (config.Platform == BuildPlatform.macOS_ARM64)
            defines.Add("ARCH_ARM64");
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
        File.WriteAllText(path, json, Encoding.UTF8);
        Log($"  Written: {path} ({defines.Count} defines: {string.Join(", ", defines)})");
    }

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

    private void Log(string message)
    {
        _log.Add(message);
        Console.WriteLine($"[ExportPipeline] {message}");
    }

    // -------------------------------------------------------------------------
    // CLI entry point
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses command-line arguments and runs an export.
    /// <para/>
    /// Usage:
    /// <code>sbengine build --platform windows-x64 --config release --output ./dist
    ///        [--keystore path] [--depot id]</code>
    /// </summary>
    public static void RunCli(string[] args)
    {
        // Defaults
        string platform    = "windows-x64";
        string configName  = "debug";
        string output      = "dist";
        string keystore    = "";
        string depot       = "";
        string configFile  = "";

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--platform": platform   = args[++i]; break;
                case "--config":   configName = args[++i]; break;
                case "--output":   output     = args[++i]; break;
                case "--keystore": keystore   = args[++i]; break;
                case "--depot":    depot      = args[++i]; break;
                case "--file":     configFile = args[++i]; break;
            }
        }

        // Parse or load PlatformConfig
        PlatformConfig config;
        if (!string.IsNullOrWhiteSpace(configFile) && File.Exists(configFile))
        {
            config = PlatformConfig.Load(configFile);
            Console.WriteLine($"[ExportPipeline] Loaded config from '{configFile}'.");
        }
        else
        {
            BuildPlatform bp = platform.ToLowerInvariant() switch
            {
                "windows-x64"   or "windows_x64"   => BuildPlatform.Windows_x64,
                "windows-x86"   or "windows_x86"   => BuildPlatform.Windows_x86,
                "linux-x64"     or "linux_x64"     => BuildPlatform.Linux_x64,
                "macos-x64"     or "macos_x64"     => BuildPlatform.macOS_x64,
                "macos-arm64"   or "macos_arm64"   => BuildPlatform.macOS_ARM64,
                "android"                          => BuildPlatform.Android,
                "ios"                              => BuildPlatform.iOS,
                "steam-windows" or "steam_windows" => BuildPlatform.Steam_Windows,
                "steam-linux"   or "steam_linux"   => BuildPlatform.Steam_Linux,
                "steam-macos"   or "steam_macos"   => BuildPlatform.Steam_macOS,
                _ => throw new ArgumentException($"Unknown platform: '{platform}'")
            };

            BuildConfiguration bc = configName.ToLowerInvariant() switch
            {
                "debug"       => BuildConfiguration.Debug,
                "development" => BuildConfiguration.Development,
                "release"     => BuildConfiguration.Release,
                _ => throw new ArgumentException($"Unknown configuration: '{configName}'")
            };

            config = PlatformConfig.Default(bp);
            config.Configuration   = bc;
            config.OutputDirectory = output;

            if (!string.IsNullOrWhiteSpace(keystore))
                config.AndroidKeystorePath = keystore;

            if (uint.TryParse(depot, out uint depotId))
                config.SteamDepotId = depotId;
        }

        // Run export
        var pipeline = new ExportPipeline();
        ExportResult result = pipeline.Export(config);

        Console.WriteLine();
        Console.WriteLine(result.Success
            ? $"BUILD SUCCEEDED in {result.Duration.TotalSeconds:F2}s"
            : $"BUILD FAILED in {result.Duration.TotalSeconds:F2}s ({result.Errors.Count} error(s))");
        Console.WriteLine($"Output: {result.OutputPath}");

        if (result.Errors.Count > 0)
        {
            Console.WriteLine("\nErrors:");
            foreach (var err in result.Errors)
                Console.WriteLine($"  {err}");
        }

        // Exit code: 0 = success, 1 = failure
        Environment.ExitCode = result.Success ? 0 : 1;
    }
}
