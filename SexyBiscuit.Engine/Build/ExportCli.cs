using System;
using System.Collections.Generic;
using System.Linq;

namespace SexyBiscuit.Engine.Build;

/// <summary>
/// The command line of the build CLI, parsed. Every flag reads its value with a bounds check,
/// so a trailing bare flag is a message rather than silently ignored — the loop that skipped
/// the last argument was a documented gotcha for a long time.
/// </summary>
public sealed record ExportCliOptions
{
    public string?             ProjectDir      { get; init; }
    public List<BuildPlatform> Platforms       { get; init; } = new();
    public bool                All             { get; init; }
    public BuildConfiguration? Configuration   { get; init; }
    public string?             OutputDirectory { get; init; }
    public string?             ConfigFile      { get; init; }
    public string?             Keystore        { get; init; }
    public uint?               Depot           { get; init; }
    public bool                Publish         { get; init; } = true;
    public bool                Package         { get; init; } = true;
    public bool                Upload          { get; init; }
    public string?             Version         { get; init; }
    public string?             ReportPath      { get; init; }
    public bool                Quiet           { get; init; }
    public bool                Help            { get; init; }
    public List<string>        Errors          { get; init; } = new();

    /// <summary>The targets to build: <c>--all</c>'s four, the named ones, or the web build alone.</summary>
    public IReadOnlyList<BuildPlatform> ResolvedTargets
        => All ? RuntimeIdentifiers.DefaultTargets
         : Platforms.Count > 0 ? Platforms.Distinct().ToList()
         : new[] { BuildPlatform.Web };

    public const string Usage = """
        usage: sbengine [build] [--project <dir>] [--platform <name>]... [--all] [options]

          --project <dir>       the game folder (default: the current directory)
          --platform <name>     web | win-x64 | win-x86 | linux-x64 | osx-x64 | osx-arm64 | android | ios
                                | steam-windows | steam-linux | steam-macos  (repeatable, or comma-separated)
          --all                 web, win-x64, osx-arm64 and linux-x64
          --config <c>          debug | development | release (default: the settings file's, else release)
          --output <dir>        where builds go, relative to the project (default: dist)
          --file <path>         a BuildSettings.json to use instead of the project's
          --version <v>         overrides the version for this run
          --no-publish          stage content only; do not run dotnet publish
          --no-zip              do not archive the platform folders
          --upload              send every archive to the configured upload target
          --report <path>       also write build-report.json here
          --keystore <path>     Android keystore (validated, not yet built)
          --depot <id>          Steam depot id
          --quiet               print the summary only
          --help
        """;

    public static ExportCliOptions Parse(string[] args)
    {
        var platforms = new List<BuildPlatform>();
        var errors    = new List<string>();
        string? projectDir = null, output = null, file = null, keystore = null, version = null, report = null;
        uint? depot = null;
        BuildConfiguration? configuration = null;
        bool all = false, publish = true, package = true, upload = false, quiet = false, help = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            string? Next(string flag)
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    errors.Add($"{flag} needs a value");
                    return null;
                }
                return args[++i];
            }

            switch (arg.ToLowerInvariant())
            {
                case "build":
                    break;   // the verb the README shows; harmless

                case "--project":
                    projectDir = Next(arg);
                    break;

                case "--platform":
                case "-p":
                    if (Next(arg) is { } spec)
                    {
                        foreach (var name in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var platform = RuntimeIdentifiers.Parse(name) ?? (Enum.TryParse<BuildPlatform>(name, ignoreCase: true, out var parsed) ? parsed : null);
                            if (platform is { } p) platforms.Add(p);
                            else errors.Add($"unknown platform '{name}'");
                        }
                    }
                    break;

                case "--all":
                    all = true;
                    break;

                case "--config":
                case "--configuration":
                    if (Next(arg) is { } configName)
                    {
                        if (Enum.TryParse<BuildConfiguration>(configName, ignoreCase: true, out var parsedConfig)) configuration = parsedConfig;
                        else errors.Add($"unknown configuration '{configName}' (debug, development or release)");
                    }
                    break;

                case "--output":
                case "-o":
                    output = Next(arg);
                    break;

                case "--file":
                    file = Next(arg);
                    break;

                case "--keystore":
                    keystore = Next(arg);
                    break;

                case "--depot":
                    if (Next(arg) is { } depotText)
                    {
                        if (uint.TryParse(depotText, out var depotId)) depot = depotId;
                        else errors.Add($"--depot expects a number, not '{depotText}'");
                    }
                    break;

                case "--version":
                    version = Next(arg);
                    break;

                case "--report":
                    report = Next(arg);
                    break;

                case "--no-publish": publish = false; break;
                case "--no-zip":     package = false; break;
                case "--upload":     upload  = true;  break;
                case "--quiet":      quiet   = true;  break;
                case "--help":
                case "-h":           help    = true;  break;

                default:
                    errors.Add($"unknown argument '{arg}'");
                    break;
            }
        }

        return new ExportCliOptions
        {
            ProjectDir = projectDir, Platforms = platforms, All = all, Configuration = configuration,
            OutputDirectory = output, ConfigFile = file, Keystore = keystore, Depot = depot,
            Publish = publish, Package = package, Upload = upload, Version = version, ReportPath = report,
            Quiet = quiet, Help = help, Errors = errors,
        };
    }
}
