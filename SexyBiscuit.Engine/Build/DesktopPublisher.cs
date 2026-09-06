using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SexyBiscuit.Engine.Code;

namespace SexyBiscuit.Engine.Build;

/// <summary>What to publish and where.</summary>
public sealed record PublishRequest(
    string             ProjectRoot,
    BuildPlatform      Platform,
    BuildConfiguration Configuration,
    string             OutputDirectory,
    string             AppName)
{
    /// <summary>An explicit engine checkout; otherwise <see cref="EngineRepoLocator.Find"/>.</summary>
    public string? EngineRepoRoot { get; init; }

    /// <summary>
    /// The build's version. Desktop does not need it — the binary carries no version — but an
    /// APK does: Android shows the display version and orders upgrades by a derived integer.
    /// </summary>
    public string Version { get; init; } = "1.0.0";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);
}

/// <summary>The outcome of a publish.</summary>
public sealed record PublishResult(
    bool                  Success,
    string?               ExecutablePath,
    IReadOnlyList<string> Errors,
    TimeSpan              Duration,
    IReadOnlyList<string> Log);

/// <summary>
/// Turns a project into a self-contained desktop binary with <c>dotnet publish</c>.
/// </summary>
/// <remarks>
/// A game with a csproj publishes that. A game with no C# — every template, and most
/// prototypes — gets a generated player project under <c>.sexybiscuit/player/</c> whose
/// <c>Program.cs</c> boots the engine from the folder the binary sits in. The engine is
/// referenced as source (<c>-p:SexyBiscuitEngineProject=</c>) so it compiles for the target
/// runtime; the editor-written <c>SexyBiscuit.props</c>, which pins a game to the editor's
/// Debug engine binary, is overridden for the same reason. Nothing is trimmed: Jint and the
/// reflection-based serialiser need the whole engine. The native libraries (SDL, OpenAL) sit
/// beside the single-file binary rather than inside it, because MonoGame's loader looks there.
/// </remarks>
public sealed class DesktopPublisher
{
    /// <summary>The folder a JS-only game's generated player project lives in, under the project's scratch folder.</summary>
    public const string PlayerFolder = "player";

    /// <summary>The <c>dotnet publish</c> arguments, exposed so a test can check them without a build.</summary>
    public static IReadOnlyList<string> PublishArguments(string csprojPath, string configuration, string rid, string outputDirectory, string? engineCsproj)
    {
        var args = new List<string>
        {
            "publish", csprojPath,
            "-c", configuration,
            "-r", rid,
            "--self-contained", "true",
            "-p:PublishSingleFile=true",
            // MonoGame loads SDL and OpenAL through its own loader, which looks beside the
            // executable and not in a single-file bundle's extraction folder — a build with the
            // natives bundled starts and dies on "Failed to load library: libSDL2.dylib". They
            // are published as files next to the binary instead.
            "-p:IncludeNativeLibrariesForSelfExtract=false",
            "-p:PublishTrimmed=false",
            "-p:DebugType=none",
            "-p:GenerateDocumentationFile=false",
            "-p:SexyBiscuitEngineDir=",
            "-p:BuildProjectReferences=true",
            "-nologo", "-v:q", "-tl:off",
            "-clp:NoSummary;ForceNoAlign",
            "-o", outputDirectory,
        };

        if (!string.IsNullOrEmpty(engineCsproj))
            args.Add($"-p:SexyBiscuitEngineProject={engineCsproj}");

        return args;
    }

    /// <summary>
    /// The csproj to publish for a project: its own when it has one, otherwise a generated
    /// player project under <c>.sexybiscuit/player/</c>, rewritten every time so it never goes
    /// stale. It lives below the scratch folder so <see cref="CodeProject.Find"/> keeps
    /// reporting "no game code" and the editor never treats it as the game.
    /// </summary>
    public static string EnsurePlayerProject(string projectRoot, string appName)
    {
        string folder = Path.Combine(projectRoot, ".sexybiscuit", PlayerFolder);
        Directory.CreateDirectory(folder);

        string name = CodeProjectGenerator.SanitiseIdentifier(appName);
        string csproj = Path.Combine(folder, name + ".Player.csproj");

        File.WriteAllText(csproj, CodeProjectGenerator.RenderPlayerCsproj(name));
        File.WriteAllText(Path.Combine(folder, "Program.cs"), CodeProjectGenerator.RenderPlayerProgram(name));
        return csproj;
    }

    /// <summary>The binary a publish produces for a csproj on a platform.</summary>
    public static string ExecutablePathFor(string csprojPath, BuildPlatform platform, string outputDirectory)
    {
        var project = CodeProject.Load(csprojPath);
        string assembly = project.AssemblyName;
        return Path.Combine(outputDirectory, RuntimeIdentifiers.IsWindows(platform) ? assembly + ".exe" : assembly);
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var errors    = new List<string>();

        string? rid = RuntimeIdentifiers.For(request.Platform);
        if (rid == null)
            return Fail($"{request.Platform} is not a desktop platform; nothing to publish.");

        var dotnet = DotnetLocator.Find();
        if (dotnet == null)
            return Fail("the .NET SDK was not found; install it or put dotnet on the PATH.");

        var repo = EngineRepoLocator.Find(request.EngineRepoRoot);
        if (repo == null)
            return Fail($"publishing needs the engine source: set {EngineRepoLocator.EnvironmentVariable} to the SexyBiscuit checkout.");

        string csproj = CodeProject.Find(request.ProjectRoot)?.CsprojPath ?? EnsurePlayerProject(request.ProjectRoot, request.AppName);
        progress?.Report($"publishing {Path.GetFileName(csproj)} for {rid}");

        var args   = PublishArguments(csproj, request.Configuration.ToString(), rid, request.OutputDirectory, repo.EngineCsproj);
        var runner = new DotnetBuildRunner(dotnet);
        var run    = await runner.RunDotnetAsync(args, Path.GetDirectoryName(csproj), request.Timeout, progress, cancellation).ConfigureAwait(false);

        var diagnostics = MsBuildDiagnosticParser.Parse(run.Lines);
        foreach (var diagnostic in diagnostics.Where(d => d.Severity == BuildSeverity.Error))
            errors.Add(diagnostic.ToString());

        bool success = run.ExitCode == 0 && !run.TimedOut && !run.Cancelled;
        if (!success && errors.Count == 0)
        {
            string reason = run.TimedOut ? "the publish timed out" : run.Cancelled ? "the publish was cancelled" : $"dotnet exited with code {run.ExitCode}";
            string tail   = string.Join(" | ", run.Lines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(3));
            errors.Add($"{reason}. {tail}".Trim());
        }

        string? executable = null;
        if (success)
        {
            executable = ExecutablePathFor(csproj, request.Platform, request.OutputDirectory);
            if (!File.Exists(executable))
            {
                errors.Add($"the publish succeeded but '{executable}' is not there.");
                success = false;
            }
        }

        stopwatch.Stop();
        return new PublishResult(success, executable, errors, stopwatch.Elapsed, run.Lines);

        PublishResult Fail(string message)
        {
            errors.Add(message);
            return new PublishResult(false, null, errors, stopwatch.Elapsed, Array.Empty<string>());
        }
    }
}
