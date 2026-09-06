using System.Diagnostics;
using SexyBiscuit.Engine.Code;

namespace SexyBiscuit.Engine.Build;

/// <summary>
/// Turns a staged game into an installable APK.
/// </summary>
/// <remarks>
/// Android is not a runtime identifier, so it cannot go through <see cref="DesktopPublisher"/>:
/// there is no <c>dotnet publish -r android</c>. An APK is an application project with an
/// Activity, a manifest and its content shipped as Android assets, so this writes that head
/// project under <c>.sexybiscuit/android/</c> and publishes it.
///
/// The staged platform folder — the same scenes, scripts and settings a desktop build ships
/// beside its binary — is copied in as the head's <c>content/</c>, and the generated Activity
/// unpacks it to app storage on first run. That is what lets the engine's ordinary file IO
/// work unchanged on a platform where the game's files are not files.
///
/// The engine only grows its Android target framework when asked
/// (<c>-p:SexyBiscuitAndroid=true</c>), so a machine without the `android` workload can still
/// build the engine, run the tests and open the editor.
/// </remarks>
public sealed class AndroidPublisher
{
    /// <summary>Where the generated head project lives, under the game's scratch folder.</summary>
    public const string HeadFolder = "android";

    /// <summary>The launcher icon densities Android expects, in dp.</summary>
    private static readonly (string Folder, int Size)[] IconDensities =
    {
        ("mipmap-mdpi", 48), ("mipmap-hdpi", 72), ("mipmap-xhdpi", 96),
        ("mipmap-xxhdpi", 144), ("mipmap-xxxhdpi", 192),
    };

    /// <summary>
    /// A reverse-DNS package id derived from the app name, e.g. <c>app.darksgames.jake01</c>.
    /// </summary>
    /// <remarks>
    /// Android requires at least one dot and rejects a segment starting with a digit, which a
    /// game called "8Ball" would produce — hence the prefix rather than the bare slug.
    /// </remarks>
    public static string ApplicationIdFor(string appName)
    {
        string slug = new string(ExportPipeline.Slugify(appName).Where(char.IsLetterOrDigit).ToArray());
        if (slug.Length == 0) slug = "game";
        return "app.darksgames." + slug.ToLowerInvariant();
    }

    /// <summary>
    /// Android orders upgrades by an integer, not by the version string, so one is derived
    /// from it: 1.2.3 becomes 10203. A version that does not parse gets 1.
    /// </summary>
    public static int VersionCodeFor(string version)
    {
        var parts = version.Split('.', '-', '+');
        int code = 0, taken = 0;

        foreach (var part in parts)
        {
            if (!int.TryParse(part, out int value)) break;
            code = code * 100 + Math.Clamp(value, 0, 99);
            if (++taken == 3) break;
        }
        return code > 0 ? code : 1;
    }

    // -------------------------------------------------------------------------
    // The head project
    // -------------------------------------------------------------------------

    /// <summary>
    /// Writes the head project and copies the staged game into it. Rewritten every publish,
    /// so it can never go stale against the generator.
    /// </summary>
    public static string EnsureHeadProject(string projectRoot, string appName, string version, string stagedContentDir)
    {
        string folder = Path.Combine(projectRoot, ".sexybiscuit", HeadFolder);
        Directory.CreateDirectory(folder);

        string name = CodeProjectGenerator.SanitiseIdentifier(appName);
        string csproj = Path.Combine(folder, name + ".Android.csproj");

        File.WriteAllText(csproj, CodeProjectGenerator.RenderAndroidCsproj(
            name, ApplicationIdFor(appName), version, VersionCodeFor(version)));
        File.WriteAllText(Path.Combine(folder, "MainActivity.cs"),
            CodeProjectGenerator.RenderAndroidActivity(name, appName));

        StageContent(stagedContentDir, Path.Combine(folder, "content"));
        WriteLauncherIcons(projectRoot, folder);

        return csproj;
    }

    /// <summary>
    /// The staged build becomes the APK's assets. Copied fresh each time — a file deleted
    /// from the game must not survive inside the next APK.
    /// </summary>
    private static void StageContent(string source, string destination)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);
        if (!Directory.Exists(source)) return;

        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            // Skip anything a previous desktop publish left behind: the APK carries the
            // game, not another platform's binaries.
            string relative = Path.GetRelativePath(source, file);
            if (relative.EndsWith(".so") || relative.EndsWith(".dll") || relative.EndsWith(".pdb")) continue;

            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// Launcher icons at the five densities, from the project's <c>icon.png</c> when it has
    /// one. Without an icon Android shows its default robot, which is not worth failing over.
    /// </summary>
    private static void WriteLauncherIcons(string projectRoot, string headFolder)
    {
        string? source = new[] { "icon.png", "Assets/icon.png", "Assets/Textures/icon.png" }
            .Select(candidate => Path.Combine(projectRoot, candidate))
            .FirstOrDefault(File.Exists);

        foreach (var (folder, _) in IconDensities)
        {
            string directory = Path.Combine(headFolder, "Resources", folder);
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "ic_launcher.png");

            // Copied rather than resampled: the engine has no image scaler that does not
            // need a GraphicsDevice, and Android accepts one size in every density bucket.
            if (source != null) File.Copy(source, target, overwrite: true);
            else if (!File.Exists(target)) File.WriteAllBytes(target, PlaceholderIcon);
        }
    }

    /// <summary>A 1x1 opaque PNG, so the manifest's @mipmap/ic_launcher always resolves.</summary>
    private static readonly byte[] PlaceholderIcon = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    // -------------------------------------------------------------------------
    // Publish
    // -------------------------------------------------------------------------

    /// <summary>Builds the APK and returns its path.</summary>
    public async Task<PublishResult> PublishAsync(PublishRequest request, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var log = new List<string>();

        PublishResult Fail(string message) =>
            new(false, null, new[] { message }, stopwatch.Elapsed, log);

        var dotnet = DotnetLocator.Find();
        if (dotnet == null) return Fail("the .NET SDK was not found; install it or put dotnet on the PATH.");

        var repo = EngineRepoLocator.Find(request.EngineRepoRoot);
        if (repo == null)
            return Fail($"an APK needs the engine source: set {EngineRepoLocator.EnvironmentVariable} to the SexyBiscuit checkout.");

        string csproj = EnsureHeadProject(request.ProjectRoot, request.AppName, request.Version, request.OutputDirectory);
        progress?.Report($"publishing {Path.GetFileName(csproj)} for android");

        string apkDirectory = Path.Combine(Path.GetDirectoryName(csproj)!, "out");

        var args = new List<string>
        {
            "publish", csproj,
            "-c", request.Configuration.ToString(),
            "-o", apkDirectory,
            "-p:SexyBiscuitAndroid=true",
            "--nologo",
        };
        if (repo.EngineCsproj != null) args.Add($"-p:SexyBiscuitEngineProject={repo.EngineCsproj}");

        var (exitCode, output) = await RunAsync(dotnet.Path, args, Path.GetDirectoryName(csproj)!, request.Timeout, cancellation)
            .ConfigureAwait(false);
        log.AddRange(output);

        if (exitCode != 0)
        {
            var errors = output.Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)).Distinct().Take(10).ToList();
            if (errors.Count == 0) errors.Add($"dotnet publish exited {exitCode}.");

            // The three ways this fails on a machine that has never built for Android, named
            // rather than left in a wall of MSBuild output.
            if (output.Any(l => l.Contains("XA5300"))) errors.Add("the Android SDK was not found: set ANDROID_HOME, or pass -p:AndroidSdkDirectory=.");
            if (output.Any(l => l.Contains("XA0031") || l.Contains("JavaSdkDirectory"))) errors.Add("a JDK was not found: install one and set JAVA_HOME.");
            if (output.Any(l => l.Contains("NETSDK1147") || l.Contains("workload"))) errors.Add("the android workload is missing: dotnet workload install android.");

            return new PublishResult(false, null, errors, stopwatch.Elapsed, log);
        }

        // Prefer the signed APK: the unsigned one will not install.
        string? apk = Directory.Exists(apkDirectory)
            ? Directory.GetFiles(apkDirectory, "*.apk")
                .OrderByDescending(f => f.Contains("-Signed", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault()
            : null;

        if (apk == null) return Fail("the publish succeeded but produced no .apk.");

        stopwatch.Stop();
        return new PublishResult(true, apk, Array.Empty<string>(), stopwatch.Elapsed, log);
    }

    private static async Task<(int ExitCode, List<string> Output)> RunAsync(
        string dotnet, IReadOnlyList<string> args, string workingDirectory, TimeSpan timeout, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory       = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        var output = new List<string>();
        using var process = new Process { StartInfo = info };
        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);

        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timer.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            output.Add($"the Android publish did not finish within {timeout.TotalMinutes:F0} minutes.");
            return (-1, output);
        }

        output.AddRange((await stdout.ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        output.AddRange((await stderr.ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        return (process.ExitCode, output);
    }
}
